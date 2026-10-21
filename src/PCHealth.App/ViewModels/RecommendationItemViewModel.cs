using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCHealth.Core;
using PCHealth.Policy.Recommendations;

namespace PCHealth.App.ViewModels;

/// <summary>
/// One recommendation as the overview shows it: the six questions of spec §7, with confidence, impact and recovery
/// as three separate lines (spec §3.3). Hashes, paths, checks and rationale are in the details, shown on demand.
/// </summary>
public sealed class RecommendationItemViewModel : ObservableObject
{
    private bool showDetails;

    public RecommendationItemViewModel(Recommendation recommendation)
    {
        Recommendation = recommendation;
        ToggleDetailsCommand = new RelayCommand(() => ShowDetails = !ShowDetails);
    }

    public Recommendation Recommendation { get; }

    public string WhatWeFound => Recommendation.WhatWeFound;

    public string WhyItMatters => Recommendation.WhyItMatters;

    public string Evidence => string.Join(Environment.NewLine, Recommendation.Evidence.Take(3).Select(e => e.Description))
        + (Recommendation.Evidence.Count > 3 ? $"{Environment.NewLine}…and {Recommendation.Evidence.Count - 3} more in the details." : "");

    public string WhatWillChange => Recommendation.WhatWillChange;

    public string WhatCouldHappenAfterward => Recommendation.WhatCouldHappenAfterward;

    public string CanIRecover => Recommendation.CanIRecover;

    public string Confidence => $"Confidence: {Recommendation.Confidence.Level}";

    public string Impact => $"Impact: {SizeText.Estimated(Recommendation.Impact.PotentiallyReclaimable)}";

    public string Recovery => $"Recovery: {Describe(Recommendation.Recovery.Method)}";

    public bool IsAction => Recommendation.Disposition == Disposition.Act;

    public string? ReviewReason => Recommendation.ReviewReason is { } reason ? $"For review: {reason}" : null;

    public bool ShowDetails
    {
        get => showDetails;
        set => SetProperty(ref showDetails, value);
    }

    public IRelayCommand ToggleDetailsCommand { get; }

    public IReadOnlyList<string> Details =>
    [
        $"Confidence rationale: {Recommendation.Confidence.Rationale}",
        $"Recovery limits: {Recommendation.Recovery.Limitations}",
        $"Reference: {Recommendation.Key}",
        .. Recommendation.Evidence.Select(e => $"Evidence: {e.Description}"),
        .. Recommendation.Operations.Select(o => $"Would {Verb(o.Kind)}: {o.Path} ({SizeText.Measured(o.ExpectedSize)})"),
        .. Recommendation.Blocked.Select(b => $"Left alone: {b.Operation.Path} ({b.Reason})"),
    ];

    private static string Describe(RecoveryMethod method) => method switch
    {
        RecoveryMethod.RecycleBin => "Recycle Bin",
        RecoveryMethod.Quarantine => "quarantine",
        _ => "none",
    };

    private static string Verb(OperationKind kind) => kind == OperationKind.MoveToQuarantine ? "quarantine" : "move to the Recycle Bin";
}
