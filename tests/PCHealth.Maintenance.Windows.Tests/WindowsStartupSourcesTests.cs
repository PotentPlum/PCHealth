using System.Runtime.InteropServices;
using PCHealth.Testing;

namespace PCHealth.Maintenance.Windows.Tests;

/// <summary>The real startup sources on the CI runner, read-only (T-57).</summary>
public sealed class WindowsStartupSourcesTests : IDisposable
{
    private readonly TempRoot root = new();

    public void Dispose() => root.Dispose();

    [Fact]
    public void The_runner_s_startup_items_are_read_with_their_evidence()
    {
        var inventory = new StartupInventoryReader(new WindowsStartupSources()).Read();

        Assert.Contains(inventory.Items, i => i.Mechanism == StartupMechanism.Service && i.ImagePath is not null);
        Assert.Contains(inventory.Items, i => i.Mechanism == StartupMechanism.ScheduledTask);
        Assert.All(inventory.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.StateEvidence)));
        Assert.All(inventory.NotRead, n => Assert.False(string.IsNullOrWhiteSpace(n.Reason)));
    }

    [Fact]
    public void A_signed_runtime_file_names_its_publisher_and_a_plain_file_is_unsigned()
    {
        var sources = new WindowsStartupSources();
        var runtime = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        var plain = root.CreateFile("tool.exe", "MZ");

        var signed = sources.SignatureOf(runtime);

        Assert.Equal(SignatureState.Valid, signed.State);
        Assert.Equal("Microsoft Corporation", signed.Publisher);
        Assert.Equal(SignatureState.Unsigned, sources.SignatureOf(plain).State);
    }

    [Fact]
    public void Run_keys_and_task_manager_records_are_read_from_both_hives()
    {
        var sources = new WindowsStartupSources();

        foreach (var key in RunKey.All)
        {
            Assert.NotNull(sources.RunValues(key));
        }

        Assert.NotNull(((IStartupSources)sources).Approved(Hive.CurrentUser, "Run"));
        Assert.NotEmpty(sources.Tasks().Tasks);
    }
}
