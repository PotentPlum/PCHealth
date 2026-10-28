using PCHealth.Testing;

namespace PCHealth.Maintenance.Tests;

/// <summary>The observation window (T-58) with scripted samples and a hand-advanced clock.</summary>
public sealed class ResourceObserverTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cpu_time_and_working_set_are_measured_per_program_over_the_window()
    {
        var clock = new ManualClock(Start);
        var sampler = new ScriptedSampler(
        [
            [P(1, "chat", @"C:\Chat\chat.exe", 10, 100), P(2, "chat", @"C:\Chat\chat.exe", 1, 50), P(3, "idle", @"C:\Idle\idle.exe", 5, 10), Hidden(4)],
            [P(1, "chat", @"C:\Chat\chat.exe", 16, 120), P(2, "chat", @"C:\Chat\chat.exe", 2, 50), P(3, "idle", @"C:\Idle\idle.exe", 5, 10), Hidden(4)],
            [P(1, "chat", @"C:\Chat\chat.exe", 22, 140), P(3, "idle", @"C:\Idle\idle.exe", 5, 10), P(5, "late", null, 3, 30), Hidden(4)],
        ]);
        var observer = new ResourceObserver(sampler, clock, (step, _) => { clock.Advance(step); return Task.CompletedTask; });

        var result = await observer.ObserveAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Window);
        Assert.False(result.Cancelled);
        var chat = result.Processes[0];
        Assert.Equal(@"C:\Chat\chat.exe", chat.ImagePath);
        Assert.Equal([1, 2], chat.ProcessIds);
        Assert.Equal(TimeSpan.FromSeconds(13), chat.CpuTime);
        Assert.Equal(140, chat.PeakWorkingSetBytes);
        Assert.Equal(TimeSpan.Zero, result.Processes.Single(p => p.Name == "idle").CpuTime);
        Assert.Equal(TimeSpan.Zero, result.Processes.Single(p => p.Name == "late").CpuTime);
        Assert.Equal(["protected (4)"], result.Unmeasured);
        Assert.Contains(result.Caveats, c => c.StartsWith("Measured over 10 seconds", StringComparison.Ordinal));
        Assert.Contains(result.Caveats, c => c.Contains("could not be measured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_keeps_what_was_seen_and_says_the_window_was_shorter()
    {
        var clock = new ManualClock(Start);
        var sampler = new ScriptedSampler([[P(1, "a", @"C:\a.exe", 1, 10)], [P(1, "a", @"C:\a.exe", 2, 10)]]);
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var observer = new ResourceObserver(sampler, clock, (step, token) =>
        {
            clock.Advance(step);
            if (++calls == 2)
            {
                cancel.Cancel();
            }

            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        var result = await observer.ObserveAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(5), cancel.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(2, result.SampleCount);
        Assert.Contains("The observation was stopped early, so the window is shorter than asked.", result.Caveats);
    }

    [Fact]
    public async Task The_window_is_bounded()
    {
        var observer = new ResourceObserver(new ScriptedSampler([]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => observer.ObserveAsync(TimeSpan.FromHours(1), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromMinutes(2), ResourceObserver.DefaultWindow);
    }

    [Fact]
    public void Startup_items_are_matched_by_program_path_and_shared_hosts_are_not_attributed()
    {
        var chat = new ProcessObservation("chat", @"C:\Chat\chat.exe", [1], TimeSpan.FromSeconds(3), 0.01, 100, 90, 3);
        var result = new ObservationResult(Start, TimeSpan.FromMinutes(2), 3, false, [chat], []);
        StartupItem Item(string name, string? image) =>
            new(name, StartupMechanism.RunKey, "HKCU", image, image, true, Signature.NotChecked, StartupState.Enabled, "test");

        var mapped = ResourceObserver.Map([Item("Chat", @"c:\chat\CHAT.exe"), Item("Gone", @"C:\Gone\gone.exe"), Item("Svc", @"C:\Windows\system32\svchost.exe"), Item("Unknown", null)], result);

        Assert.Same(chat, mapped[0].Observed);
        Assert.Equal("Seen running during the window (3 of 3 samples).", mapped[0].Note);
        Assert.Equal("Not seen running during the window.", mapped[1].Note);
        Assert.StartsWith("It runs inside svchost.exe", mapped[2].Note);
        Assert.Null(mapped[3].Observed);
    }

    [Fact]
    public void Boot_timing_is_read_from_the_diagnostics_event()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><EventID>100</EventID><TimeCreated SystemTime="2026-10-27T07:58:12.1234567Z" /></System>
              <EventData>
                <Data Name="BootTime">48210</Data>
                <Data Name="MainPathBootTime">21450</Data>
                <Data Name="BootPostBootTime">26760</Data>
              </EventData>
            </Event>
            """;

        var timing = BootTiming.FromEventXml(xml);

        Assert.Null(timing.NotAvailable);
        Assert.Equal(TimeSpan.FromMilliseconds(48210), timing.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(21450), timing.MainPath);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 7, 58, 12, 123, TimeSpan.Zero).AddTicks(4567), timing.BootedUtc);
        Assert.Contains("(as Windows measured it)", timing.Describe());
        Assert.Equal("Boot timing is not available without administrator rights.", BootTiming.Unavailable("Boot timing is not available without administrator rights.").Describe());
        Assert.NotNull(BootTiming.FromEventXml("<broken").NotAvailable);
    }

    private static ProcessSample P(int id, string name, string? image, int cpuSeconds, long memory) =>
        new(id, name, image, TimeSpan.FromSeconds(cpuSeconds), memory);

    private static ProcessSample Hidden(int id) => new(id, "protected", null, null, null);

    private sealed class ScriptedSampler(IReadOnlyList<IReadOnlyList<ProcessSample>> script) : IProcessSampler
    {
        private int next;

        public IReadOnlyList<ProcessSample> Sample() => next < script.Count ? script[next++] : script.Count > 0 ? script[^1] : [];
    }
}
