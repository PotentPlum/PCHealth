using System.Text.RegularExpressions;

namespace PCHealth.Organization;

public enum DependencyKind
{
    /// <summary>A full path such as <c>C:\Tools\x</c>, or one that starts at the drive root.</summary>
    AbsolutePath,

    /// <summary>A <c>\\server\share</c> path.</summary>
    NetworkPath,

    /// <summary>A relative path whose <c>..</c> segments climb above the folder.</summary>
    LeavesFolder,

    /// <summary>A path that starts with an environment variable, a build property or the home folder.</summary>
    Variable,
}

/// <summary>
/// Classifies a path written in a file, with Windows rules on every OS: both separators, drive letters, UNC prefixes
/// and <c>..</c> segments. A reference that stays inside the folder is not a dependency (spec §9 "Correctness").
/// </summary>
internal static partial class PathReferences
{
    /// <summary>A stand-in for a variable in the middle of a path; it names one segment, wherever it points.</summary>
    internal const string Placeholder = "\u0001";

    /// <param name="reference">The path as written, after the parser substituted the variables it understands.</param>
    /// <param name="baseSegments">The referencing file's folder, as segments below the scanned folder.</param>
    /// <returns>The kind of dependency, or null when the reference stays inside the folder or is not a path.</returns>
    public static DependencyKind? Classify(string reference, IReadOnlyList<string> baseSegments)
    {
        var path = reference.Trim().Trim('"', '\'').Trim();
        if (path.Length == 0)
        {
            return null;
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? (path.StartsWith("file://///", StringComparison.OrdinalIgnoreCase) || !path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                    ? DependencyKind.NetworkPath
                    : DependencyKind.AbsolutePath)
                : null;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return DependencyKind.NetworkPath;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return DependencyKind.AbsolutePath;
        }

        if (path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            return DependencyKind.NetworkPath;
        }

        // "C:\x", "C:/x" and the drive-relative "C:x" all name a place outside any folder that moves.
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return DependencyKind.AbsolutePath;
        }

        if (IsSeparator(path[0]))
        {
            return DependencyKind.AbsolutePath;
        }

        if (path == "~" || (path.Length > 1 && path[0] == '~' && IsSeparator(path[1])) || LeadingVariable().IsMatch(path))
        {
            return DependencyKind.Variable;
        }

        var stack = new List<string>(baseSegments);
        foreach (var segment in path.Split(['\\', '/']))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    return DependencyKind.LeavesFolder;
                }

                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(segment);
            }
        }

        return null;
    }

    public static string Describe(DependencyKind kind) => kind switch
    {
        DependencyKind.AbsolutePath => "a full path",
        DependencyKind.NetworkPath => "a network path",
        DependencyKind.LeavesFolder => "a path that leads outside the folder",
        DependencyKind.Variable => "a path that starts with a variable, which may point anywhere",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Replaces variables inside a path (not at its start) with one placeholder segment each.</summary>
    public static string MiddleVariablesToPlaceholders(string path) =>
        AnyVariable().Replace(path, m => m.Index == 0 ? m.Value : Placeholder);

    private static bool IsSeparator(char c) => c is '\\' or '/';

    // %NAME%, $env:NAME, ${env:NAME}, $(Name), $Name and ${Name} at the very start of a path.
    [GeneratedRegex(@"^(?:%[^%\s]+%|\$env:\w+|\$\{[^}]+\}|\$\([^)]+\)|\$\w+)")]
    private static partial Regex LeadingVariable();

    [GeneratedRegex(@"%[^%\s]+%|\$env:\w+|\$\{[^}]+\}|\$\([^)]+\)")]
    private static partial Regex AnyVariable();
}
