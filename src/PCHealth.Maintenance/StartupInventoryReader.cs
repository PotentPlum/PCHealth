using System.Xml;
using System.Xml.Linq;
using PCHealth.Inventory;

namespace PCHealth.Maintenance;

public enum Hive
{
    CurrentUser,
    LocalMachine,
}

/// <summary>A Run or RunOnce key: for this user or all users, and in the native or the 32-bit (WOW6432Node) view.</summary>
public sealed record RunKey(Hive Hive, bool Wow6432, bool Once)
{
    public string Path =>
        $@"{(Hive == Hive.CurrentUser ? "HKCU" : "HKLM")}\Software\{(Wow6432 ? @"WOW6432Node\" : "")}Microsoft\Windows\CurrentVersion\{(Once ? "RunOnce" : "Run")}";

    /// <summary>The StartupApproved key that records whether Task Manager disabled an entry of this key.</summary>
    public string ApprovedKey => Wow6432 ? "Run32" : "Run";

    public static IReadOnlyList<RunKey> All { get; } =
    [
        .. from hive in new[] { Hive.CurrentUser, Hive.LocalMachine }
           from wow in new[] { false, true }
           from once in new[] { false, true }
           select new RunKey(hive, wow, once),
    ];
}

/// <summary>A scheduled task as Windows stores it: its path and its XML definition.</summary>
public sealed record TaskDefinition(string Path, string Xml);

/// <summary>A service as registered: its start type (2 is automatic) and its type (drivers are 1 and 2).</summary>
public sealed record ServiceDefinition(string Name, string? DisplayName, string? ImagePath, int Start, bool DelayedStart, int Type);

/// <summary>The tasks that could be read, and the folders or tasks that could not.</summary>
public sealed record TaskList(IReadOnlyList<TaskDefinition> Tasks, IReadOnlyList<UnreadSource> NotRead);

/// <summary>The services that could be read, and the ones that could not.</summary>
public sealed record ServiceList(IReadOnlyList<ServiceDefinition> Services, IReadOnlyList<UnreadSource> NotRead);

/// <summary>
/// Where startup items are registered (T-57). Windows reads the registry, the Startup folders, the Task Scheduler and
/// the service list without elevation; tests use fakes. Every member only reads. A source that can't be read throws
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>; the reader records it as not read.
/// </summary>
public interface IStartupSources
{
    /// <summary>The values of a Run or RunOnce key; empty when the key does not exist.</summary>
    IReadOnlyList<(string Name, string Command)> RunValues(RunKey key);

    /// <summary>The StartupApproved values of <paramref name="subkey"/> (Run, Run32 or StartupFolder); empty when there are none.</summary>
    IReadOnlyDictionary<string, byte[]> Approved(Hive hive, string subkey);

    /// <summary>This user's Startup folder and the all-users one, where they exist.</summary>
    IReadOnlyList<(string Folder, Hive Hive)> StartupFolders();

    IReadOnlyList<string> FilesIn(string folder);

    byte[] ReadAllBytes(string path);

    TaskList Tasks();

    ServiceList Services();

    string ExpandEnvironment(string text);

    bool Exists(string path);

    Signature SignatureOf(string path);
}

/// <summary>Reads every startup item from the sources into one list, and lists what could not be read (T-57). Read-only.</summary>
public sealed class StartupInventoryReader(IStartupSources sources)
{
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly string[] Programs = [".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msc", ".lnk", ".dll"];

    public StartupInventory Read()
    {
        var items = new List<StartupItem>();
        var notRead = new List<UnreadSource>();
        foreach (var key in RunKey.All)
        {
            Try(key.Path, notRead, () => items.AddRange(RunItems(key, notRead)));
        }

        foreach (var (folder, hive) in Try("Startup folders", notRead, sources.StartupFolders) ?? [])
        {
            Try(folder, notRead, () => items.AddRange(FolderItems(folder, hive, notRead)));
        }

        if (Try("Task Scheduler", notRead, sources.Tasks) is { } tasks)
        {
            notRead.AddRange(tasks.NotRead);
            foreach (var task in tasks.Tasks)
            {
                Try(task.Path, notRead, () =>
                {
                    if (TaskItem(task) is { } item)
                    {
                        items.Add(item);
                    }
                });
            }
        }

        var services = Try(@"HKLM\SYSTEM\CurrentControlSet\Services", notRead, sources.Services);
        notRead.AddRange(services?.NotRead ?? []);
        foreach (var service in services?.Services ?? [])
        {
            // Drivers (types 1 and 2) are not programs a user starts or stops from Services; only Win32 services count.
            if (service.Start == 2 && (service.Type & 0x30) != 0)
            {
                var command = service.ImagePath is null ? null : ServicePath(service.ImagePath);
                items.Add(Item(
                    service.DisplayName ?? service.Name, StartupMechanism.Service, service.Name, command,
                    StartupState.Enabled, service.DelayedStart ? "Start type: automatic (delayed start)." : "Start type: automatic."));
            }
        }

        return new StartupInventory(items, notRead);
    }

    /// <summary>The program a command starts: the quoted part, or the shortest unquoted prefix that names a program.</summary>
    internal static string? ImagePath(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        var space = -1;
        while ((space = text.IndexOf(' ', space + 1)) > 0)
        {
            var prefix = text[..space];
            if (Programs.Any(p => prefix.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return prefix;
            }
        }

        return Programs.Any(p => text.EndsWith(p, StringComparison.OrdinalIgnoreCase)) || !text.Contains(' ', StringComparison.Ordinal)
            ? text
            : text[..text.IndexOf(' ', StringComparison.Ordinal)];
    }

    /// <summary>Task Manager's StartupApproved record: an odd first byte means disabled; a missing record means enabled.</summary>
    internal static (StartupState State, string Evidence) ApprovedState(IReadOnlyDictionary<string, byte[]> approved, string name)
    {
        if (!approved.TryGetValue(name, out var value))
        {
            return (StartupState.Enabled, "No Task Manager record, so it is enabled.");
        }

        if (value.Length == 0)
        {
            return (StartupState.Unknown, "Task Manager's record for it is empty.");
        }

        return (value[0] & 1) == 1
            ? (StartupState.Disabled, "Disabled in Task Manager (StartupApproved).")
            : (StartupState.Enabled, "Enabled in Task Manager (StartupApproved).");
    }

    /// <summary>A task that runs at boot or at sign-in, with its first program action; null for any other task.</summary>
    internal StartupItem? TaskItem(TaskDefinition task)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(task.Xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Its definition could not be read: {ex.Message}", ex);
        }

        XName Name(string local) => XName.Get(local, TaskNamespace);
        var triggers = document.Descendants(Name("Triggers")).Elements()
            .Where(t => t.Name.LocalName is "BootTrigger" or "LogonTrigger")
            .ToArray();
        if (triggers.Length == 0)
        {
            return null;
        }

        var exec = document.Descendants(Name("Exec")).FirstOrDefault();
        var command = exec?.Element(Name("Command"))?.Value.Trim();
        var arguments = exec?.Element(Name("Arguments"))?.Value.Trim();
        var full = command is null ? null : $"{(command.Contains(' ', StringComparison.Ordinal) ? $"\"{command}\"" : command)}{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}";
        var kinds = string.Join(" and ", triggers.Select(t => t.Name.LocalName == "BootTrigger" ? "at startup" : "at sign-in").Distinct());
        var taskEnabled = !string.Equals(document.Descendants(Name("Settings")).Elements(Name("Enabled")).FirstOrDefault()?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        var triggerEnabled = triggers.Any(t => !string.Equals(t.Element(Name("Enabled"))?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
        var (state, evidence) = (taskEnabled, triggerEnabled) switch
        {
            (false, _) => (StartupState.Disabled, $"The task is disabled. It would run {kinds}."),
            (true, false) => (StartupState.Disabled, $"The task is enabled, but its trigger ({kinds}) is disabled."),
            _ => (StartupState.Enabled, $"The task is enabled and runs {kinds}."),
        };
        return Item(task.Path.Split('\\').Last(), StartupMechanism.ScheduledTask, task.Path, full, state, evidence);
    }

    private IEnumerable<StartupItem> RunItems(RunKey key, List<UnreadSource> notRead)
    {
        var values = sources.RunValues(key);
        var approved = key.Once
            ? new Dictionary<string, byte[]>()
            : Try($@"{key.Hive}\StartupApproved\{key.ApprovedKey}", notRead, () => sources.Approved(key.Hive, key.ApprovedKey));
        foreach (var (name, command) in values)
        {
            var (state, evidence) = key.Once
                ? (StartupState.Enabled, "RunOnce entries run once at the next sign-in; Task Manager does not list them.")
                : approved is null ? (StartupState.Unknown, "Task Manager's records could not be read.") : ApprovedState(approved, name);
            yield return Item(name, key.Once ? StartupMechanism.RunOnceKey : StartupMechanism.RunKey, key.Path, command, state, evidence);
        }
    }

    private IEnumerable<StartupItem> FolderItems(string folder, Hive hive, List<UnreadSource> notRead)
    {
        var approved = Try($@"{hive}\StartupApproved\StartupFolder", notRead, () => sources.Approved(hive, "StartupFolder"));
        foreach (var file in sources.FilesIn(folder).Where(f => !FileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var name = FileName(file);
            var command = file;
            if (Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var link = ShellLink.Parse(sources.ReadAllBytes(file));
                    command = link.Target is null ? file : $"\"{link.Target}\"";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    notRead.Add(new UnreadSource(file, $"The shortcut could not be read: {ex.Message}"));
                }
            }

            var (state, evidence) = approved is null ? (StartupState.Unknown, "Task Manager's records could not be read.") : ApprovedState(approved, name);
            yield return Item(Path.GetFileNameWithoutExtension(name), StartupMechanism.StartupFolder, folder, command, state, evidence);
        }
    }

    private StartupItem Item(string name, StartupMechanism mechanism, string location, string? command, StartupState state, string evidence)
    {
        var image = command is null ? null : ImagePath(sources.ExpandEnvironment(command));
        var rooted = image is not null && image.Length >= 3 && char.IsAsciiLetter(image[0]) && image[1] == ':' && image[2] is '\\' or '/';
        var exists = rooted ? sources.Exists(image!) : (bool?)null;
        var signature = exists == true ? sources.SignatureOf(image!) : Signature.NotChecked;
        return new StartupItem(name, mechanism, location, command, image, exists, signature, state, evidence);
    }

    /// <summary>The last segment of a Windows path, on any OS.</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>A service image path in the forms the service list uses: <c>\SystemRoot\…</c> and <c>system32\…</c> are under Windows.</summary>
    private static string ServicePath(string imagePath)
    {
        var path = imagePath.Trim();
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            return "%SystemRoot%" + path[@"\SystemRoot".Length..];
        }

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase) ? @"%SystemRoot%\" + path : path;
    }

    private static T? Try<T>(string source, List<UnreadSource> notRead, Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException)
        {
            notRead.Add(new UnreadSource(source, ex.Message));
            return null;
        }
    }

    private static void Try(string source, List<UnreadSource> notRead, Action read) =>
        Try<object>(source, notRead, () =>
        {
            read();
            return new object();
        });
}
