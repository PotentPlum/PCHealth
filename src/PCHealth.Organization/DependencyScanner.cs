using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PCHealth.Core;

namespace PCHealth.Organization;

/// <summary>A path written in a file that would break, or point elsewhere, if the folder moved.</summary>
/// <param name="SourceFile">The file that holds the path.</param>
/// <param name="Line">The line it is on, for text formats.</param>
/// <param name="Reference">The path as written.</param>
public sealed record PathDependency(string SourceFile, int? Line, string Reference, DependencyKind Kind)
{
    public string Reason =>
        $"{Path.GetFileName(SourceFile)}{(Line is { } line ? $" line {line}" : "")} refers to {PathReferences.Describe(Kind)}: {Reference}";
}

/// <summary>A file that may hold paths but was not checked, and why.</summary>
public sealed record UncheckedFile(string Path, string Reason);

/// <summary>
/// What the scanner found for one folder. A folder with any dependency is move-restricted. An empty result is never a
/// guarantee: <see cref="NotChecked"/> lists what was not read, and other programs may know the path too (spec §5.2).
/// </summary>
public sealed record DependencyReport(string Folder, IReadOnlyList<PathDependency> Dependencies, IReadOnlyList<UncheckedFile> NotChecked)
{
    public bool MoveRestricted => Dependencies.Count > 0;

    public IReadOnlyList<string> Reasons => Dependencies.Select(d => d.Reason).ToArray();

    public string Summary => (MoveRestricted, NotChecked.Count) switch
    {
        (true, _) => $"Can't be moved here: {Dependencies.Count} {(Dependencies.Count == 1 ? "path points" : "paths point")} outside the folder or to a fixed place.",
        (false, 0) => "No paths outside the folder were found in the files that were checked. Programs and settings elsewhere may still refer to this folder.",
        (false, var n) => $"No paths outside the folder were found in the files that were checked, but {n} {(n == 1 ? "file was" : "files were")} not checked. Programs and settings elsewhere may still refer to this folder.",
    };
}

/// <summary>
/// Looks for path dependencies in the formats T-50 names: solutions, C# projects, <c>package.json</c>,
/// <c>.code-workspace</c>, batch and PowerShell scripts, and shortcuts. Read-only; files above a size bound and
/// formats it can't read are listed as not checked.
/// </summary>
public static partial class DependencyScanner
{
    /// <summary>Text files larger than this are not read.</summary>
    internal const long MaxTextBytes = 4 * 1024 * 1024;

    internal const long MaxShortcutBytes = 1024 * 1024;

    private static readonly HashSet<string> Unreadable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vcxproj", ".vbproj", ".fsproj", ".props", ".targets", ".proj", ".sh", ".py", ".psm1", ".vbs",
        ".prproj", ".aep", ".blend", ".als", ".flp", ".psd", ".ai", ".indd",
    };

    // Properties that point at the build tools or the package cache, never at the user's own files.
    private static readonly HashSet<string> ToolProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64", "MSBuildToolsPath", "MSBuildBinPath",
        "MSBuildSDKsPath", "VCTargetsPath", "NuGetPackageRoot", "MSBuildFrameworkToolsPath",
    };

    private static readonly HashSet<string> NotSearched = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", ".venv", "venv", "__pycache__", "target", "packages", ".tox",
    };

    private static readonly string[] PackageSections = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];

    public static DependencyReport Scan(DetectedProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Scan(project.Root, project.Files);
    }

    /// <param name="folder">The folder that would move.</param>
    /// <param name="files">The files below it, from a read-only scan.</param>
    public static DependencyReport Scan(string folder, IEnumerable<FileEntry> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(files);
        var root = Path.TrimEndingDirectorySeparator(folder);
        var dependencies = new List<PathDependency>();
        var notChecked = new List<UncheckedFile>();
        foreach (var file in files.Where(f => !f.IsReparsePoint).OrderBy(f => f.FullPath, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file.FullPath);
            var segments = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments[0] == ".." || segments[..^1].Any(NotSearched.Contains))
            {
                continue;
            }

            var reader = ReaderFor(file.Name);
            if (reader is null)
            {
                if (Unreadable.Contains(Path.GetExtension(file.Name)))
                {
                    notChecked.Add(new UncheckedFile(file.FullPath, $"The scanner can't read {Path.GetExtension(file.Name)} files, so paths in it were not checked."));
                }

                continue;
            }

            var limit = Path.GetExtension(file.Name).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? MaxShortcutBytes : MaxTextBytes;
            if (file.Size.Bytes > limit)
            {
                notChecked.Add(new UncheckedFile(file.FullPath, "The file is too large to read for paths."));
                continue;
            }

            try
            {
                var context = new FileContext(file.FullPath, segments[..^1], segments.Length - 1);
                foreach (var (reference, line, resolved) in reader(context))
                {
                    if (PathReferences.Classify(resolved, context.Folder) is { } kind)
                    {
                        dependencies.Add(new PathDependency(file.FullPath, line, reference, kind));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notChecked.Add(new UncheckedFile(file.FullPath, $"The file could not be read: {ex.Message}"));
            }
            catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException)
            {
                notChecked.Add(new UncheckedFile(file.FullPath, $"The file is not valid {Path.GetExtension(file.Name)} and was not checked: {ex.Message}"));
            }
        }

        return new DependencyReport(root, dependencies, notChecked);
    }

    private delegate IEnumerable<(string Reference, int? Line, string Resolved)> Reader(FileContext file);

    private sealed record FileContext(string Path, IReadOnlyList<string> Folder, int Depth)
    {
        /// <summary>From this file's folder back up to the scanned folder, e.g. <c>..\..\</c>.</summary>
        public string UpToRoot => string.Concat(Enumerable.Repeat(@"..\", Depth));
    }

    private static Reader? ReaderFor(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".sln" => Solution,
        ".slnx" => SolutionXml,
        ".csproj" => CSharpProject,
        ".code-workspace" => Workspace,
        ".bat" or ".cmd" => Batch,
        ".ps1" => PowerShell,
        ".lnk" => Shortcut,
        _ when name.Equals("package.json", StringComparison.OrdinalIgnoreCase) => Package,
        _ => null,
    };

    private static IEnumerable<(string, int?, string)> Solution(FileContext file)
    {
        var line = 0;
        foreach (var text in File.ReadLines(file.Path))
        {
            line++;
            var match = SolutionProject().Match(text);
            if (match.Success)
            {
                var path = match.Groups["path"].Value;
                yield return (path, line, PathReferences.MiddleVariablesToPlaceholders(path));
            }
        }
    }

    private static IEnumerable<(string, int?, string)> SolutionXml(FileContext file)
    {
        var document = LoadXml(file.Path);
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName is "Project" or "File"))
        {
            if (element.Attribute("Path")?.Value is { Length: > 0 } path)
            {
                yield return (path, Line(element), PathReferences.MiddleVariablesToPlaceholders(path));
            }
        }
    }

    private static IEnumerable<(string, int?, string)> CSharpProject(FileContext file)
    {
        var document = LoadXml(file.Path);
        foreach (var element in document.Descendants())
        {
            var values = element.Name.LocalName switch
            {
                "ProjectReference" => Split(element.Attribute("Include")?.Value),
                "Import" when element.Attribute("Sdk") is null => Split(element.Attribute("Project")?.Value),
                "HintPath" => Split(element.Value),
                _ => [],
            };
            foreach (var value in values)
            {
                if (MsBuild(value, file) is { } resolved)
                {
                    yield return (value, Line(element), resolved);
                }
            }
        }
    }

    private static IEnumerable<(string, int?, string)> Package(FileContext file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file.Path), JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var section in PackageSections)
        {
            if (!document.RootElement.TryGetProperty(section, out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                var value = dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString()! : "";
                foreach (var prefix in new[] { "file:", "link:" })
                {
                    if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !value.Contains("://", StringComparison.Ordinal))
                    {
                        yield return ($"{dependency.Name}: {value}", null, value[prefix.Length..]);
                    }
                }
            }
        }
    }

    private static IEnumerable<(string, int?, string)> Workspace(FileContext file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file.Path), JsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("folders", out var folders) || folders.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var folder in folders.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object))
        {
            foreach (var key in new[] { "path", "uri" })
            {
                if (folder.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } path)
                {
                    // VS Code expands ${workspaceFolder}-style variables only in settings, not in folder paths.
                    yield return (path, null, path);
                }
            }
        }
    }

    private static IEnumerable<(string, int?, string)> Batch(FileContext file)
    {
        var lines = File.ReadAllLines(file.Path);
        var assigned = lines.Select(l => BatchSet().Match(l)).Where(m => m.Success).Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.StartsWith("::", StringComparison.Ordinal) || BatchRemark().IsMatch(text))
            {
                continue;
            }

            foreach (var token in Tokens(text))
            {
                // Switches such as /d and /s, and loop variables such as %%i, are not paths.
                if ((token.StartsWith('/') && token.Length <= 3) || token.StartsWith("%%", StringComparison.Ordinal))
                {
                    continue;
                }

                // %~dp0 is the script's own folder, with a trailing backslash.
                var resolved = BatchScriptFolder().Replace(token, "");
                var leading = LeadingPercent().Match(resolved);
                if (leading.Success && assigned.Contains(leading.Groups["name"].Value))
                {
                    continue;
                }

                if (LooksLikePath(resolved) || (leading.Success && HasSeparator(resolved)))
                {
                    yield return (token, i + 1, PathReferences.MiddleVariablesToPlaceholders(resolved));
                }
            }
        }
    }

    private static IEnumerable<(string, int?, string)> PowerShell(FileContext file)
    {
        var lines = File.ReadAllLines(file.Path);
        var inComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (inComment || text.StartsWith("<#", StringComparison.Ordinal))
            {
                inComment = !text.Contains("#>", StringComparison.Ordinal);
                continue;
            }

            if (text.StartsWith('#'))
            {
                continue;
            }

            foreach (var token in Tokens(text))
            {
                if (token.StartsWith('-'))
                {
                    continue;
                }

                var resolved = PowerShellScriptFolder().Replace(token, ".");
                var environment = resolved.StartsWith("$env:", StringComparison.OrdinalIgnoreCase)
                    || resolved.StartsWith("${env:", StringComparison.OrdinalIgnoreCase)
                    || resolved.StartsWith("$HOME", StringComparison.OrdinalIgnoreCase);

                // Other $variables hold values the script assigns itself; those literals are checked where they are written.
                if (resolved.StartsWith('$') && !environment)
                {
                    continue;
                }

                if (LooksLikePath(resolved) || (environment && HasSeparator(resolved)))
                {
                    yield return (token, i + 1, PathReferences.MiddleVariablesToPlaceholders(resolved));
                }
            }
        }
    }

    private static IEnumerable<(string, int?, string)> Shortcut(FileContext file)
    {
        var link = ShellLink.Parse(File.ReadAllBytes(file.Path));
        if (link.Target is null && link.RelativePath is null)
        {
            throw new InvalidDataException("The shortcut points at a shell item that has no path.");
        }

        // Windows follows the stored full path first, so a shortcut breaks or points elsewhere once its folder moves.
        if (link.Target is not null)
        {
            yield return (link.Target, null, link.Target);
        }
        else
        {
            yield return (link.RelativePath!, null, link.RelativePath!);
        }

        if (link.WorkingDirectory is { Length: > 0 } workingDirectory)
        {
            yield return (workingDirectory, null, workingDirectory);
        }
    }

    /// <summary>A path from an MSBuild item, with the folder properties MSBuild defines replaced; null for build-tool paths.</summary>
    private static string? MsBuild(string value, FileContext file)
    {
        var property = MsBuildProperty().Match(value);
        if (property.Success && property.Index == 0)
        {
            var name = property.Groups["name"].Value;
            if (ToolProperties.Contains(name))
            {
                return null;
            }

            var rest = value[property.Length..];
            value = name.ToLowerInvariant() switch
            {
                "msbuildthisfiledirectory" or "msbuildprojectdirectory" or "projectdir" => @".\" + rest,

                // Assumed to be the scanned folder, where a solution that owns the project usually sits.
                "solutiondir" => file.UpToRoot + rest,
                _ => value,
            };
        }

        return PathReferences.MiddleVariablesToPlaceholders(value);
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    private static int? Line(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;

    /// <summary>Quoted strings, and unquoted words, of a script line; single characters such as <c>/</c> or <c>\</c> are operators, not paths.</summary>
    private static IEnumerable<string> Tokens(string line) =>
        ScriptToken().Matches(line).Select(m => m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["d"].Success ? m.Groups["d"].Value : m.Value)
            .Select(t => t.TrimEnd(';', ',', ')').TrimStart('('))
            .Where(t => t.Length > 1);

    /// <summary>A token that is plainly a path: it starts like one, or has a separator and no URL scheme.</summary>
    private static bool LooksLikePath(string token) =>
        !token.Contains("://", StringComparison.Ordinal)
        && (HasSeparator(token) || (char.IsAsciiLetter(token[0]) && token[1] == ':') || token == "..");

    private static bool HasSeparator(string token) => token.Contains('\\') || token.Contains('/');

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""")]
    private static partial Regex SolutionProject();

    [GeneratedRegex(@"\$\((?<name>[\w.]+)\)")]
    private static partial Regex MsBuildProperty();

    [GeneratedRegex(@"""(?<q>[^""]*)""|'(?<q>[^']*)'|(?<d>[^\s""'=|&<>]+)")]
    private static partial Regex ScriptToken();

    [GeneratedRegex(@"^(?:@?\s*)set\s+(?:/[ap]\s+)?""?(?<name>[^=\s""]+)=", RegexOptions.IgnoreCase)]
    private static partial Regex BatchSet();

    [GeneratedRegex(@"^@?rem(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BatchRemark();

    [GeneratedRegex(@"%~dp[0-9]", RegexOptions.IgnoreCase)]
    private static partial Regex BatchScriptFolder();

    [GeneratedRegex(@"^%(?<name>[^%~\s]+)%")]
    private static partial Regex LeadingPercent();

    [GeneratedRegex(@"\$PSScriptRoot|\$\{PSScriptRoot\}", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellScriptFolder();
}
