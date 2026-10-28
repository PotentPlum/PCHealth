using System.Text;

namespace PCHealth.Maintenance.Tests;

/// <summary>The startup inventory (T-57) from fake sources: every mechanism, its enabled state, and what could not be read.</summary>
public sealed class StartupInventoryTests
{
    private static readonly byte[] Enabled = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Disabled = [0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public void Run_keys_are_read_with_their_task_manager_state()
    {
        var sources = new FakeSources();
        sources.Run[new RunKey(Hive.CurrentUser, false, false)] =
        [
            ("OneDrive", "\"C:\\Program Files\\Microsoft OneDrive\\OneDrive.exe\" /background"),
            ("Chat", @"C:\Users\me\AppData\Local\Chat\chat.exe --autostart"),
        ];
        sources.Run[new RunKey(Hive.LocalMachine, true, false)] = [("Vendor", @"C:\Program Files (x86)\Vendor\tool.exe -min")];
        sources.Run[new RunKey(Hive.CurrentUser, false, true)] = [("FinishSetup", @"C:\Setup\finish.cmd")];
        sources.ApprovedValues[(Hive.CurrentUser, "Run")] = new() { ["OneDrive"] = Enabled, ["Chat"] = Disabled };
        sources.Existing.Add(@"C:\Program Files\Microsoft OneDrive\OneDrive.exe");
        sources.Existing.Add(@"C:\Program Files (x86)\Vendor\tool.exe");

        var inventory = new StartupInventoryReader(sources).Read();

        var oneDrive = inventory.Items.Single(i => i.Name == "OneDrive");
        Assert.Equal((StartupMechanism.RunKey, StartupState.Enabled), (oneDrive.Mechanism, oneDrive.State));
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", oneDrive.Location);
        Assert.Equal(@"C:\Program Files\Microsoft OneDrive\OneDrive.exe", oneDrive.ImagePath);
        Assert.True(oneDrive.FileExists);
        Assert.Equal("Signed by Fake Publisher.", oneDrive.Signature.Describe());

        var chat = inventory.Items.Single(i => i.Name == "Chat");
        Assert.Equal(StartupState.Disabled, chat.State);
        Assert.Equal("Disabled in Task Manager (StartupApproved).", chat.StateEvidence);
        Assert.False(chat.FileExists);
        Assert.Equal(SignatureState.NotChecked, chat.Signature.State);

        var vendor = inventory.Items.Single(i => i.Name == "Vendor");
        Assert.Equal(@"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", vendor.Location);
        Assert.Equal(@"C:\Program Files (x86)\Vendor\tool.exe", vendor.ImagePath);
        Assert.Equal("No Task Manager record, so it is enabled.", vendor.StateEvidence);

        var once = inventory.Items.Single(i => i.Name == "FinishSetup");
        Assert.Equal(StartupMechanism.RunOnceKey, once.Mechanism);
        Assert.StartsWith("RunOnce entries run once", once.StateEvidence);
        Assert.True(inventory.IsComplete);
    }

    [Fact]
    public void Startup_folder_shortcuts_are_followed_to_their_target()
    {
        var sources = new FakeSources();
        sources.Folders.Add((@"C:\Users\me\Startup", Hive.CurrentUser));
        sources.Files[@"C:\Users\me\Startup"] = [@"C:\Users\me\Startup\Notes.lnk", @"C:\Users\me\Startup\sync.bat", @"C:\Users\me\Startup\desktop.ini", @"C:\Users\me\Startup\Broken.lnk"];
        sources.Bytes[@"C:\Users\me\Startup\Notes.lnk"] = Shortcut(@"C:\Tools\notes.exe");
        sources.Bytes[@"C:\Users\me\Startup\Broken.lnk"] = Encoding.ASCII.GetBytes("not a shortcut");
        sources.ApprovedValues[(Hive.CurrentUser, "StartupFolder")] = new() { ["sync.bat"] = Disabled };

        var inventory = new StartupInventoryReader(sources).Read();

        Assert.Equal(["Notes", "sync", "Broken"], inventory.Items.Select(i => i.Name));
        Assert.Equal(@"C:\Tools\notes.exe", inventory.Items[0].ImagePath);
        Assert.Equal(StartupState.Disabled, inventory.Items[1].State);
        Assert.Equal(@"C:\Users\me\Startup\sync.bat", inventory.Items[1].ImagePath);
        Assert.Contains(inventory.NotRead, n => n.Source == @"C:\Users\me\Startup\Broken.lnk");
    }

    [Fact]
    public void Only_tasks_with_logon_or_boot_triggers_are_listed()
    {
        var sources = new FakeSources();
        sources.TaskDefinitions.Add(new TaskDefinition(@"\Vendor\Updater", Task("<LogonTrigger><Enabled>true</Enabled></LogonTrigger>", @"C:\Program Files\Vendor\update.exe", "/silent")));
        sources.TaskDefinitions.Add(new TaskDefinition(@"\Backup", Task("<BootTrigger />", @"C:\Backup\run.exe", null, enabled: false)));
        sources.TaskDefinitions.Add(new TaskDefinition(@"\Nightly", Task("<CalendarTrigger />", @"C:\Nightly\run.exe", null)));
        sources.TaskDefinitions.Add(new TaskDefinition(@"\Off", Task("<LogonTrigger><Enabled>false</Enabled></LogonTrigger>", @"C:\Off\run.exe", null)));
        sources.TaskDefinitions.Add(new TaskDefinition(@"\Broken", "<Task"));
        sources.TaskErrors.Add(new UnreadSource(@"\Microsoft\Secret", "Access is denied."));

        var inventory = new StartupInventoryReader(sources).Read();

        Assert.Equal(["Updater", "Backup", "Off"], inventory.Items.Select(i => i.Name));
        var updater = inventory.Items[0];
        Assert.Equal("\"C:\\Program Files\\Vendor\\update.exe\" /silent", updater.Command);
        Assert.Equal(@"C:\Program Files\Vendor\update.exe", updater.ImagePath);
        Assert.Equal("The task is enabled and runs at sign-in.", updater.StateEvidence);
        Assert.Equal((StartupState.Disabled, "The task is disabled. It would run at startup."), (inventory.Items[1].State, inventory.Items[1].StateEvidence));
        Assert.Equal((StartupState.Disabled, "The task is enabled, but its trigger (at sign-in) is disabled."), (inventory.Items[2].State, inventory.Items[2].StateEvidence));
        Assert.Equal([@"\Microsoft\Secret", @"\Broken"], inventory.NotRead.Select(n => n.Source));
    }

    [Fact]
    public void Automatic_services_are_listed_but_drivers_and_manual_services_are_not()
    {
        var sources = new FakeSources();
        sources.ServiceDefinitions.AddRange(
        [
            new ServiceDefinition("VendorSvc", "Vendor Service", "\"C:\\Program Files\\Vendor\\svc.exe\" -service", 2, DelayedStart: true, Type: 0x10),
            new ServiceDefinition("Shared", null, @"system32\svchost.exe -k netsvcs", 2, false, 0x20),
            new ServiceDefinition("Disk", null, @"\SystemRoot\System32\drivers\disk.sys", 2, false, 0x1),
            new ServiceDefinition("Manual", null, @"C:\Manual\svc.exe", 3, false, 0x10),
        ]);

        var inventory = new StartupInventoryReader(sources).Read();

        Assert.Equal(["Vendor Service", "Shared"], inventory.Items.Select(i => i.Name));
        Assert.Equal("Start type: automatic (delayed start).", inventory.Items[0].StateEvidence);
        Assert.Equal(@"C:\Windows\system32\svchost.exe", inventory.Items[1].ImagePath);
        Assert.All(inventory.Items, i => Assert.Equal(StartupMechanism.Service, i.Mechanism));
    }

    [Fact]
    public void A_source_that_cannot_be_read_is_reported_and_the_rest_is_still_read()
    {
        var sources = new FakeSources();
        sources.Run[new RunKey(Hive.CurrentUser, false, false)] = [("App", @"C:\App\app.exe")];
        sources.Unreadable.Add(new RunKey(Hive.LocalMachine, false, false));
        sources.ApprovedFails = true;

        var inventory = new StartupInventoryReader(sources).Read();

        Assert.False(inventory.IsComplete);
        Assert.Contains(inventory.NotRead, n => n.Source == @"HKLM\Software\Microsoft\Windows\CurrentVersion\Run" && n.Reason == "Access is denied.");
        var app = Assert.Single(inventory.Items);
        Assert.Equal(StartupState.Unknown, app.State);
        Assert.Equal("Task Manager's records could not be read.", app.StateEvidence);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\A\\a.exe\" --x", @"C:\Program Files\A\a.exe")]
    [InlineData(@"C:\Program Files\A\a.exe --x", @"C:\Program Files\A\a.exe")]
    [InlineData(@"C:\A\a.exe", @"C:\A\a.exe")]
    [InlineData(@"rundll32.exe C:\x.dll,Entry", "rundll32.exe")]
    [InlineData(@"C:\Tools\run", @"C:\Tools\run")]
    [InlineData("   ", null)]
    public void The_program_is_found_in_a_command(string command, string? expected)
    {
        Assert.Equal(expected, StartupInventoryReader.ImagePath(command));
    }

    private static string Task(string trigger, string command, string? arguments, bool enabled = true) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers>{trigger}</Triggers>
          <Settings><Enabled>{(enabled ? "true" : "false")}</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>{command}</Command>{(arguments is null ? "" : $"<Arguments>{arguments}</Arguments>")}</Exec></Actions>
        </Task>
        """;

    /// <summary>A minimal shortcut whose LinkInfo holds a local base path.</summary>
    private static byte[] Shortcut(string target)
    {
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        new Guid("00021401-0000-0000-c000-000000000046").ToByteArray().CopyTo(header, 4);
        BitConverter.GetBytes(0x2u).CopyTo(header, 0x14);
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        var info = new byte[0x1C + basePath.Length + 1];
        BitConverter.GetBytes(info.Length).CopyTo(info, 0);
        BitConverter.GetBytes(0x1C).CopyTo(info, 4);
        BitConverter.GetBytes(1).CopyTo(info, 8);
        BitConverter.GetBytes(0x1C).CopyTo(info, 16);
        BitConverter.GetBytes(0x1C + basePath.Length).CopyTo(info, 24);
        basePath.CopyTo(info, 0x1C);
        return [.. header, .. info];
    }

    private sealed class FakeSources : IStartupSources
    {
        public Dictionary<RunKey, List<(string, string)>> Run { get; } = [];

        public HashSet<RunKey> Unreadable { get; } = [];

        public Dictionary<(Hive, string), Dictionary<string, byte[]>> ApprovedValues { get; } = [];

        public bool ApprovedFails { get; set; }

        public List<(string, Hive)> Folders { get; } = [];

        public Dictionary<string, List<string>> Files { get; } = [];

        public Dictionary<string, byte[]> Bytes { get; } = [];

        public List<TaskDefinition> TaskDefinitions { get; } = [];

        public List<UnreadSource> TaskErrors { get; } = [];

        public List<ServiceDefinition> ServiceDefinitions { get; } = [];

        public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<(string Name, string Command)> RunValues(RunKey key) =>
            Unreadable.Contains(key) ? throw new UnauthorizedAccessException("Access is denied.") : Run.GetValueOrDefault(key) ?? [];

        public IReadOnlyDictionary<string, byte[]> Approved(Hive hive, string subkey) =>
            ApprovedFails ? throw new IOException("The key is damaged.") : ApprovedValues.GetValueOrDefault((hive, subkey)) ?? [];

        public IReadOnlyList<(string Folder, Hive Hive)> StartupFolders() => Folders;

        public IReadOnlyList<string> FilesIn(string folder) => Files.GetValueOrDefault(folder) ?? [];

        public byte[] ReadAllBytes(string path) => Bytes[path];

        public TaskList Tasks() => new(TaskDefinitions, TaskErrors);

        public ServiceList Services() => new(ServiceDefinitions, []);

        public string ExpandEnvironment(string text) => text.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase);

        public bool Exists(string path) => Existing.Contains(path);

        public Signature SignatureOf(string path) => new(SignatureState.Valid, "Fake Publisher");
    }
}
