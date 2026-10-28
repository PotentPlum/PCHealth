namespace PCHealth.Organization;

/// <summary>
/// When a project is offered for archiving (spec §5.2, T-53): nothing in it changed for a number of months the user
/// can change. Archiving moves the whole folder to an archive folder the user chose; it never deletes anything, and the
/// wording never suggests the project is no longer needed.
/// </summary>
public static class ProjectArchive
{
    public const int DefaultMonths = 12;

    /// <summary>True when the newest file in the project is at least <paramref name="months"/> months old.</summary>
    public static bool IsInactive(DetectedProject project, DateTimeOffset nowUtc, int months)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfLessThan(months, 1);
        return project.LastActivityUtc is { } last && last <= nowUtc.AddMonths(-months);
    }

    /// <summary>What the page says about an inactive project: facts, then what archiving does.</summary>
    public static string Describe(DetectedProject project, int months)
    {
        ArgumentNullException.ThrowIfNull(project);
        return $"{project.Activity} Nothing in it changed for {months} {(months == 1 ? "month" : "months")} or more. "
            + "Archiving moves the whole folder into your archive folder, where it stays as it is; Undo in Activity moves it back.";
    }
}
