namespace PCHealth.Execution;

public enum ScopeRejectionReason
{
    /// <summary>The allowed scope itself could not be resolved to a folder.</summary>
    ScopeUnresolvable,

    /// <summary>The target could not be resolved; see <see cref="ScopeDecision.PathRejection"/>.</summary>
    TargetUnresolvable,

    /// <summary>The target really lives outside the scope, e.g. through <c>..</c> or a link in a parent folder.</summary>
    OutsideScope,

    /// <summary>The target is a link whose destination is outside the scope or cannot be resolved.</summary>
    LinkLeadsOutsideScope,
}

/// <summary>Whether a target may be acted on within a scope, and why not.</summary>
public sealed record ScopeDecision
{
    private ScopeDecision(string? finalPath, ScopeRejectionReason? reason, PathRejectionReason? pathRejection, string message)
    {
        FinalPath = finalPath;
        Reason = reason;
        PathRejection = pathRejection;
        Message = message;
    }

    public bool IsAllowed => Reason is null;

    /// <summary>The resolved target to act on; null when rejected.</summary>
    public string? FinalPath { get; }

    public ScopeRejectionReason? Reason { get; }

    /// <summary>The resolver's reason, when the target or scope could not be resolved.</summary>
    public PathRejectionReason? PathRejection { get; }

    /// <summary>A user-readable explanation; empty when allowed.</summary>
    public string Message { get; }

    internal static ScopeDecision Allow(string finalPath) => new(finalPath, null, null, "");

    internal static ScopeDecision Reject(ScopeRejectionReason reason, string message, PathRejectionReason? pathRejection = null) =>
        new(null, reason, pathRejection, message);
}

/// <summary>
/// Checks that a target really lies inside an allowed scope once every link is taken into account (spec §6.3).
/// Use the returned <see cref="ScopeDecision.FinalPath"/>, not the requested path, and recheck immediately before acting.
/// </summary>
/// <remarks>
/// This checks one target. An operation that recurses into a folder must check each entry it reaches.
/// </remarks>
public sealed class ScopeValidator(IPathResolver resolver)
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ScopeDecision Check(string target, string scope)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(scope);

        // The scope is wherever its links finally lead; that real folder is what the target must stay inside.
        var scopeResolution = resolver.Resolve(scope, LinkHandling.Follow);
        if (!scopeResolution.IsResolved || !scopeResolution.IsDirectory)
        {
            return ScopeDecision.Reject(
                ScopeRejectionReason.ScopeUnresolvable,
                scopeResolution.IsResolved ? $"The allowed scope '{scope}' is not a folder." : scopeResolution.Message,
                scopeResolution.Rejection);
        }

        var scopeRoot = scopeResolution.FinalPath!;
        var resolved = resolver.Resolve(target, LinkHandling.DoNotFollow);
        if (!resolved.IsResolved)
        {
            return ScopeDecision.Reject(ScopeRejectionReason.TargetUnresolvable, resolved.Message, resolved.Rejection);
        }

        if (!IsWithin(resolved.FinalPath!, scopeRoot))
        {
            return ScopeDecision.Reject(
                ScopeRejectionReason.OutsideScope,
                $"'{target}' really is '{resolved.FinalPath}', which is outside '{scopeRoot}'.");
        }

        if (resolved.IsReparsePoint)
        {
            var destination = resolver.Resolve(target, LinkHandling.Follow);
            if (!destination.IsResolved)
            {
                return ScopeDecision.Reject(
                    ScopeRejectionReason.LinkLeadsOutsideScope,
                    $"'{target}' is a link whose destination cannot be verified: {destination.Message}",
                    destination.Rejection);
            }

            if (!IsWithin(destination.FinalPath!, scopeRoot))
            {
                return ScopeDecision.Reject(
                    ScopeRejectionReason.LinkLeadsOutsideScope,
                    $"'{target}' is a link to '{destination.FinalPath}', which is outside '{scopeRoot}'.");
            }
        }

        return ScopeDecision.Allow(resolved.FinalPath!);
    }

    private static bool IsWithin(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var trimmedPath = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(trimmedPath, trimmedRoot, PathComparison))
        {
            return true;
        }

        var prefix = trimmedRoot.EndsWith(Path.DirectorySeparatorChar) ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        return trimmedPath.StartsWith(prefix, PathComparison);
    }
}
