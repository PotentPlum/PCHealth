using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Execution;
using PCHealth.Execution.Windows;
using PCHealth.Inventory;
using PCHealth.Inventory.Compression;
using PCHealth.Inventory.Windows;
using PCHealth.Ledger;
using PCHealth.Policy;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App;

/// <summary>The app's composition root. Everything that changes files is reachable only through the one gateway.</summary>
public static class AppServices
{
    /// <param name="configure">Lets tests replace registrations, e.g. the platform layer.</param>
    public static ServiceProvider Build(AppPaths paths, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataRoot);

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPathResolver, WindowsPathResolver>();
        services.AddSingleton<IPlatformOperations, WindowsPlatformOperations>();
        services.AddSingleton<IFileCompression, WofCompression>();
        services.AddSingleton(sp => new ProtectionPolicy(WindowsProtectedLocations.Discover(sp.GetRequiredService<IPathResolver>()).Concat(OwnData(paths))));
        services.AddSingleton(_ => ExclusionList.Load(paths.Exclusions));
        services.AddSingleton(_ => TransactionLedger.Open(paths.Ledger));
        services.AddSingleton(_ => AppSettings.Load(paths.Settings));
        services.AddSingleton(sp => new QuarantineStore(
            paths.Quarantine, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AppSettings>().QuarantineRetention));
        services.AddSingleton<Func<RecommendationBuilder>>(sp => () => new RecommendationBuilder(
            sp.GetRequiredService<ProtectionPolicy>(), sp.GetRequiredService<ExclusionList>(), sp.GetRequiredService<QuarantineStore>().Retention));
        services.AddSingleton(_ => new GlobalPause(paths.PauseState));
        services.AddSingleton(sp => new ExecutionGateway(
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IPlatformOperations>(),
            sp.GetRequiredService<ProtectionPolicy>(),
            sp.GetRequiredService<ExclusionList>(),
            sp.GetRequiredService<TransactionLedger>(),
            sp.GetRequiredService<QuarantineStore>(),
            sp.GetRequiredService<GlobalPause>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Restorer>(),
            sp.GetRequiredService<IFileCompression>()));
        services.AddSingleton(sp => new Restorer(
            sp.GetRequiredService<TransactionLedger>(),
            sp.GetRequiredService<QuarantineStore>(),
            new HandleMove(),
            sp.GetRequiredService<TimeProvider>(),
            new WindowsRecycleBinRestore(),
            sp.GetRequiredService<IFileCompression>()));
        services.AddSingleton(sp => new InterruptedOperations(
            sp.GetRequiredService<TransactionLedger>(), sp.GetRequiredService<QuarantineStore>(), sp.GetRequiredService<IFileCompression>()));
        services.AddSingleton<StartupTasks>();
        services.AddSingleton<FindingsStore>();
        services.AddSingleton(_ => DeclinedRecommendations.Load(paths.Declines));
        services.AddSingleton<Func<Recommendation, PreviewViewModel>>(sp => recommendation => new PreviewViewModel(
            recommendation,
            sp.GetRequiredService<ExecutionGateway>(),
            sp.GetRequiredService<DeclinedRecommendations>(),
            paths,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Func<IReadOnlyList<Recommendation>, BatchPreviewViewModel>>(sp => recommendations => new BatchPreviewViewModel(
            recommendations, sp.GetRequiredService<ExecutionGateway>()));
        services.AddSingleton<IDiscovery>(sp => new Discovery(
            new WindowsKnownLocations(sp.GetRequiredService<IPathResolver>()),
            new WindowsAppRegistrations(),
            new RecursiveScanner(),
            new WindowsFileIdentityProvider(),
            sp.GetRequiredService<ProtectionPolicy>(),
            sp.GetRequiredService<ExclusionList>(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            sp.GetRequiredService<TimeProvider>(),
            () => sp.GetRequiredService<QuarantineStore>().Retention));

        services.AddTransient(sp => new DiscoveryProgressViewModel(sp.GetRequiredService<AppSettings>(), paths, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<PauseViewModel>();
        services.AddSingleton<FirstRunViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<IChunkCompressor>(_ => new WofCompressor());
        services.AddSingleton(sp => new CompressionEstimates(new RecursiveScanner(), sp.GetRequiredService<IChunkCompressor>()));
        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<DuplicatesViewModel>();
        services.AddSingleton<IOrganizeFolders>(sp => new WindowsOrganizeFolders(sp.GetRequiredService<IPathResolver>()));
        services.AddSingleton(sp => new OrganizeAnalysis(sp.GetRequiredService<IOrganizeFolders>(), new RecursiveScanner(), sp.GetRequiredService<ExclusionList>()));
        services.AddSingleton<IFolderPicker, WpfFolderPicker>();
        services.AddSingleton<OrganizeViewModel>();
        services.AddSingleton<Maintenance.IStartupSources, Maintenance.Windows.WindowsStartupSources>();
        services.AddSingleton<Maintenance.IProcessSampler, Maintenance.Windows.WindowsProcessSampler>();
        services.AddSingleton<Func<Maintenance.ResourceObserver>>(sp => () => new Maintenance.ResourceObserver(
            sp.GetRequiredService<Maintenance.IProcessSampler>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Func<Maintenance.BootTiming>>(_ => Maintenance.Windows.WindowsBootTiming.Read);
        services.AddSingleton<MaintenanceViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp => new BenchmarkViewModel(BenchmarkViewModel.DefaultReportsFolder(), clock: sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IUpdates, VelopackUpdates>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<MainViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>The app never suggests changing its own data, nor the folder an earlier version kept it in (D-021).</summary>
    private static IEnumerable<ProtectedLocation> OwnData(AppPaths paths)
    {
        yield return new ProtectedLocation(paths.DataRoot, ProtectionKind.AppData, "it holds this app's own history, settings and quarantine.");
        if (Path.GetDirectoryName(paths.DataRoot) is { } parent)
        {
            yield return new ProtectedLocation(Path.Combine(parent, "PCHealth"), ProtectionKind.AppData, "this app or its earlier data is installed there.");
        }
    }
}
