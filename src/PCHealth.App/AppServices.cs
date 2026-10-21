using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PCHealth.App.ViewModels;
using PCHealth.Execution;
using PCHealth.Execution.Windows;
using PCHealth.Inventory;
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
        services.AddSingleton(sp => WindowsProtectedLocations.CreateDefaultPolicy(sp.GetRequiredService<IPathResolver>()));
        services.AddSingleton(_ => ExclusionList.Load(paths.Exclusions));
        services.AddSingleton(_ => TransactionLedger.Open(paths.Ledger));
        services.AddSingleton(sp => new QuarantineStore(paths.Quarantine, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => new GlobalPause(paths.PauseState));
        services.AddSingleton(sp => new ExecutionGateway(
            sp.GetRequiredService<IPathResolver>(),
            sp.GetRequiredService<IPlatformOperations>(),
            sp.GetRequiredService<ProtectionPolicy>(),
            sp.GetRequiredService<ExclusionList>(),
            sp.GetRequiredService<TransactionLedger>(),
            sp.GetRequiredService<QuarantineStore>(),
            sp.GetRequiredService<GlobalPause>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new Restorer(
            sp.GetRequiredService<TransactionLedger>(), sp.GetRequiredService<QuarantineStore>(), new HandleMove(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new InterruptedOperations(sp.GetRequiredService<TransactionLedger>(), sp.GetRequiredService<QuarantineStore>()));
        services.AddSingleton<StartupTasks>();
        services.AddSingleton<FindingsStore>();
        services.AddSingleton(_ => DeclinedRecommendations.Load(paths.Declines));
        services.AddSingleton<Func<Recommendation, PreviewViewModel>>(sp => recommendation => new PreviewViewModel(
            recommendation,
            sp.GetRequiredService<ExecutionGateway>(),
            sp.GetRequiredService<DeclinedRecommendations>(),
            paths,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDiscovery>(sp => new Discovery(
            new WindowsKnownLocations(sp.GetRequiredService<IPathResolver>()),
            new WindowsAppRegistrations(),
            new RecursiveScanner(),
            new WindowsFileIdentityProvider(),
            sp.GetRequiredService<ProtectionPolicy>(),
            sp.GetRequiredService<ExclusionList>(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<PauseViewModel>();
        services.AddSingleton<FirstRunViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<MainViewModel>();

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
