using System.Diagnostics;

namespace PCHealth.Maintenance.Windows.Tests;

/// <summary>Observing a fixture process on the CI runner (T-58), and boot timing as far as the runner allows.</summary>
public sealed class ResourceObservationWindowsTests
{
    [Fact]
    public async Task A_busy_fixture_process_is_seen_with_its_cpu_time_and_memory()
    {
        using var busy = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "for /L %i in (1,1,200000000) do @rem" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var result = await new ResourceObserver(new WindowsProcessSampler())
                .ObserveAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

            var seen = result.Processes.Single(p => p.ProcessIds.Contains(busy.Id));
            Assert.EndsWith(@"\cmd.exe", seen.ImagePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(seen.CpuTime > TimeSpan.Zero, $"cpu {seen.CpuTime}");
            Assert.True(seen.PeakWorkingSetBytes > 0);
            Assert.True(result.SampleCount >= 4);
        }
        finally
        {
            busy.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void Boot_timing_is_read_or_its_absence_is_explained()
    {
        var timing = WindowsBootTiming.Read();

        Assert.False(string.IsNullOrWhiteSpace(timing.Describe()));
        if (timing.NotAvailable is null)
        {
            Assert.NotNull(timing.Total);
        }
    }
}
