using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Core;
using PCHealth.Execution;
using PCHealth.Inventory;
using PCHealth.Policy;
using PCHealth.Policy.Catalog;
using PCHealth.Policy.Residue;
using PCHealth.Testing;

namespace PCHealth.App.Tests;

/// <summary>
/// Observed network behavior (spec §3.5, §9; T-36): every app flow except the user-started update check runs while a
/// listener records all .NET network activity (name lookups, socket connects, HTTP requests). Fixture files only.
/// </summary>
[Collection(NetworkObservation.Name)]
public sealed class PrivacyTests : IDisposable
{
    private readonly TempRoot root = new();

    [Fact]
    public void The_listener_sees_a_connection_when_there_is_one()
    {
        using var network = new NetworkObservation();
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();

        using (var client = new TcpClient())
        {
            client.Connect(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port);
        }

        Assert.Contains(network.Events, e => e.StartsWith("System.Net.Sockets/ConnectStart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_first_run_discovery_changes_undo_settings_and_storage_make_no_network_calls()
    {
        using var network = new NetworkObservation();

        // Startup with the real composition, including the real update service and the Windows platform layer.
        using (var real = AppServices.Build(new AppPaths(root.Resolve("real/PCHealth")), s => s.AddSingleton(FixtureDiscovery())))
        {
            real.GetRequiredService<StartupTasks>().Run();
            var main = real.GetRequiredService<MainViewModel>();
            await main.FirstRun.StartCommand.ExecuteAsync(null);
            foreach (var page in main.Pages)
            {
                main.Current = page;
            }

            var updates = main.Pages.OfType<UpdatesViewModel>().Single();
            Assert.False(updates.CheckCommand.CanExecute(null));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new VelopackUpdates().CheckAsync(CancellationToken.None));
            await main.Pages.OfType<StorageViewModel>().Single().MeasureCommand.ExecuteAsync(null);
        }

        // A change and its Undo, on fixture files.
        using (var services = TestServices.Build(root))
        {
            var file = root.CreateFile("data/a.tmp", "aa");
            var recommendation = PreviewTests.Recommend(new ExclusionList(), file);
            var gateway = services.GetRequiredService<ExecutionGateway>();
            var move = gateway.Execute(recommendation, recommendation.Operations);
            Assert.Equal(ExecutionStatus.Completed, move.Status);
            gateway.Undo(move.TransactionId!.Value);
            var settings = services.GetRequiredService<SettingsViewModel>();
            settings.RetentionDays = "7";
            settings.SaveRetentionCommand.Execute(null);
            settings.ClearConfirmed = true;
            settings.ClearCommand.Execute(null);
        }

        Assert.Empty(network.Events);
    }

    public void Dispose() => root.Dispose();

    private IDiscovery FixtureDiscovery()
    {
        var profile = root.CreateDirectory("profile");
        var temp = root.CreateDirectory("profile/AppData/Local/Temp");
        root.CreateFile("profile/AppData/Local/Temp/a.tmp", "t");
        root.CreateFile("profile/Documents/report.pdf", "r");
        return new Discovery(
            new Locations(temp, root.Resolve("profile/AppData/Local")), new NoRegistrations(), new RecursiveScanner(), null,
            new ProtectionPolicy([]), new ExclusionList(), profile, TimeProvider.System);
    }

    private sealed class Locations(string temp, string localAppData) : IKnownLocations
    {
        public string? Resolve(string token) => token switch
        {
            "Temp" => temp,
            "LocalAppData" => localAppData,
            _ => null,
        };
    }

    private sealed class NoRegistrations : IAppRegistrations
    {
        public RegistrationSnapshot Read() => new([], []);
    }
}

/// <summary>Records every event from the .NET networking stack while it is alive.</summary>
public sealed class NetworkObservation : EventListener
{
    public const string Name = "Network observation";

    private static readonly string[] Sources = ["System.Net.Sockets", "System.Net.NameResolution", "System.Net.Http", "System.Net.Security"];

    private readonly ConcurrentQueue<string> events = new();

    public IReadOnlyCollection<string> Events => events.ToArray();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (Sources.Contains(eventSource.Name))
        {
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // Counters are periodic statistics, not activity.
        if (eventData.EventName is not ("EventCounters" or null))
        {
            events?.Enqueue($"{eventData.EventSource.Name}/{eventData.EventName}: {string.Join(", ", eventData.Payload ?? [])}");
        }
    }
}

/// <summary>The listener is process-wide, so these tests never run alongside each other.</summary>
[CollectionDefinition(NetworkObservation.Name, DisableParallelization = true)]
public sealed class NetworkObservationCollection;
