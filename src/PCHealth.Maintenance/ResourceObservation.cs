using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PCHealth.Maintenance;

/// <summary>One process at one moment. CPU time and memory are null when Windows would not tell us (protected processes).</summary>
public sealed record ProcessSample(int ProcessId, string Name, string? ImagePath, TimeSpan? TotalCpu, long? WorkingSetBytes);

/// <summary>Reads the processes running now. Windows uses limited-information handles; tests use scripted samples.</summary>
public interface IProcessSampler
{
    IReadOnlyList<ProcessSample> Sample();
}

/// <summary>What one program did during the window, measured: CPU time used and working set seen. Never a score.</summary>
/// <param name="CpuShare">CPU time as a share of all processors over the window, from 0 to 1.</param>
public sealed record ProcessObservation(
    string Name,
    string? ImagePath,
    IReadOnlyList<int> ProcessIds,
    TimeSpan CpuTime,
    double CpuShare,
    long PeakWorkingSetBytes,
    long AverageWorkingSetBytes,
    int Samples);

/// <param name="Window">How long the observation actually ran.</param>
/// <param name="Unmeasured">Processes Windows would not report CPU time or memory for.</param>
public sealed record ObservationResult(
    DateTimeOffset StartedUtc,
    TimeSpan Window,
    int SampleCount,
    bool Cancelled,
    IReadOnlyList<ProcessObservation> Processes,
    IReadOnlyList<string> Unmeasured)
{
    /// <summary>What the numbers can and can't tell, shown with them (spec §6.1, rule 9).</summary>
    public IReadOnlyList<string> Caveats =>
    [
        $"Measured over {Window.TotalSeconds:0} seconds from {StartedUtc.ToLocalTime():t}, every few seconds. Programs that ran briefly between samples may be missed.",
        "This is one window on this PC at this moment, not a typical day: what you were doing changes the numbers.",
        "Working set is memory in use by the program; part of it can be shared with other programs.",
        .. Cancelled ? ["The observation was stopped early, so the window is shorter than asked."] : Array.Empty<string>(),
        .. Unmeasured.Count > 0 ? [$"{Unmeasured.Count} processes could not be measured, because Windows protects them."] : Array.Empty<string>(),
    ];
}

/// <summary>A startup item and what its program did during an observation, if it ran.</summary>
public sealed record StartupObservation(StartupItem Item, ProcessObservation? Observed, string Note);

/// <summary>
/// Samples per-process CPU time and working set over a bounded window the user asked for (T-58, spec §5.4, §6.5):
/// two minutes by default, cancellable, on a low-priority thread. Read-only.
/// </summary>
public sealed class ResourceObserver(IProcessSampler sampler, TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(30);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? ((d, t) => Task.Delay(d, t));

    /// <summary>Observes until the window ends or <paramref name="cancellationToken"/> is cancelled; what was seen until then is kept.</summary>
    public async Task<ObservationResult> ObserveAsync(TimeSpan? window = null, TimeSpan? interval = null, CancellationToken cancellationToken = default)
    {
        var length = window ?? DefaultWindow;
        var step = interval ?? DefaultInterval;
        if (length <= TimeSpan.Zero || length > MaxWindow || step <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), $"An observation lasts up to {MaxWindow.TotalMinutes:0} minutes.");
        }

        var started = clock.GetUtcNow();
        var samples = new List<(DateTimeOffset At, IReadOnlyList<ProcessSample> Processes)>();
        var cancelled = false;
        while (true)
        {
            samples.Add((clock.GetUtcNow(), await Task.Run(SampleAtLowPriority, CancellationToken.None).ConfigureAwait(false)));
            if (clock.GetUtcNow() - started >= length)
            {
                break;
            }

            try
            {
                await delay(step, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
        }

        return Summarize(started, clock.GetUtcNow() - started, samples, cancelled);
    }

    /// <summary>Maps startup items to what their programs did, by program path. A shared host such as svchost is not attributed.</summary>
    public static IReadOnlyList<StartupObservation> Map(IReadOnlyList<StartupItem> items, ObservationResult result)
    {
        var byPath = result.Processes.Where(p => p.ImagePath is not null)
            .GroupBy(p => p.ImagePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return items.Select(item =>
        {
            if (item.ImagePath is null)
            {
                return new StartupObservation(item, null, "Its program is not known, so it can't be matched to a process.");
            }

            var name = item.ImagePath[(item.ImagePath.LastIndexOfAny(['\\', '/']) + 1)..];
            if (name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
            {
                return new StartupObservation(item, null, $"It runs inside {name}, which hosts other things too, so its share can't be told apart.");
            }

            return byPath.TryGetValue(item.ImagePath, out var observed)
                ? new StartupObservation(item, observed, $"Seen running during the window ({observed.Samples} of {result.SampleCount} samples).")
                : new StartupObservation(item, null, "Not seen running during the window.");
        }).ToArray();
    }

    private IReadOnlyList<ProcessSample> SampleAtLowPriority()
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.Lowest;
        try
        {
            return sampler.Sample();
        }
        finally
        {
            thread.Priority = priority;
        }
    }

    private static ObservationResult Summarize(DateTimeOffset started, TimeSpan window, List<(DateTimeOffset At, IReadOnlyList<ProcessSample> Processes)> samples, bool cancelled)
    {
        var unmeasured = new List<string>();
        var measured = new List<(string Name, string? ImagePath, int Id, TimeSpan Cpu, long[] Memory)>();
        foreach (var process in samples.SelectMany(s => s.Processes.Select(p => (s.At, Sample: p))).GroupBy(x => (x.Sample.ProcessId, x.Sample.Name)))
        {
            var seen = process.OrderBy(x => x.At).Select(x => x.Sample).ToArray();
            var cpu = seen.Where(s => s.TotalCpu is not null).Select(s => s.TotalCpu!.Value).ToArray();
            var memory = seen.Where(s => s.WorkingSetBytes is not null).Select(s => s.WorkingSetBytes!.Value).ToArray();
            if (cpu.Length == 0 && memory.Length == 0)
            {
                unmeasured.Add($"{process.Key.Name} ({process.Key.ProcessId})");
                continue;
            }

            // CPU time used between the first and the last sample the process was seen in.
            measured.Add((process.Key.Name, seen[^1].ImagePath, process.Key.ProcessId, cpu.Length >= 2 ? cpu[^1] - cpu[0] : TimeSpan.Zero, memory));
        }

        var capacity = window.TotalSeconds * Environment.ProcessorCount;
        var programs = measured
            .GroupBy(m => m.ImagePath ?? m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cpu = g.Aggregate(TimeSpan.Zero, (sum, m) => sum + m.Cpu);
                var memory = g.SelectMany(m => m.Memory).ToArray();
                return new ProcessObservation(
                    g.First().Name,
                    g.First().ImagePath,
                    [.. g.Select(m => m.Id)],
                    cpu,
                    capacity <= 0 ? 0 : Math.Min(1, cpu.TotalSeconds / capacity),
                    memory.Length == 0 ? 0 : memory.Max(),
                    memory.Length == 0 ? 0 : (long)memory.Average(),
                    g.Max(m => m.Memory.Length));
            })
            .OrderByDescending(p => p.CpuTime)
            .ThenByDescending(p => p.PeakWorkingSetBytes)
            .ToArray();
        return new ObservationResult(started, window, samples.Count, cancelled, programs, [.. unmeasured.Order(StringComparer.OrdinalIgnoreCase)]);
    }
}

/// <summary>How long the last boot took, from the Diagnostics-Performance log; or why it is not available.</summary>
public sealed record BootTiming(DateTimeOffset? BootedUtc, TimeSpan? Total, TimeSpan? MainPath, TimeSpan? PostBoot, string? NotAvailable)
{
    public static BootTiming Unavailable(string reason) => new(null, null, null, null, reason);

    public string Describe() => NotAvailable ?? string.Create(
        CultureInfo.CurrentCulture,
        $"The boot on {BootedUtc?.ToLocalTime():g} took {Total?.TotalSeconds:0.0} s: {MainPath?.TotalSeconds:0.0} s until the desktop, then {PostBoot?.TotalSeconds:0.0} s until Windows was idle (as Windows measured it).");

    /// <summary>Reads the newest boot event (ID 100) of Microsoft-Windows-Diagnostics-Performance/Operational from its XML.</summary>
    public static BootTiming FromEventXml(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            return Unavailable($"The boot record could not be read: {ex.Message}");
        }

        var data = document.Descendants().Where(e => e.Name.LocalName == "Data")
            .ToDictionary(e => (string?)e.Attribute("Name") ?? "", e => e.Value, StringComparer.Ordinal);
        var created = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
        TimeSpan? Milliseconds(string name) =>
            data.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
        var total = Milliseconds("BootTime");
        return total is null
            ? Unavailable("The boot record has no boot time.")
            : new BootTiming(
                DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null,
                total,
                Milliseconds("MainPathBootTime"),
                Milliseconds("BootPostBootTime"),
                null);
    }
}
