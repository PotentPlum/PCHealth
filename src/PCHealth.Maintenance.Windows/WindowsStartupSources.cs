using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace PCHealth.Maintenance.Windows;

/// <summary>
/// Reads startup items on Windows without elevation (T-57): the Run and RunOnce keys of both hives and both registry
/// views, Task Manager's StartupApproved records, the Startup folders, the Task Scheduler and the service list. Every
/// read is read-only. Signatures are checked with WinVerifyTrust from the local cache only, so no network is used.
/// </summary>
public sealed class WindowsStartupSources : IStartupSources
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const long MaxShortcutBytes = 1024 * 1024;

    public IReadOnlyList<(string Name, string Command)> RunValues(RunKey key)
    {
        using var root = Root(key.Hive);
        using var run = root.OpenSubKey(key.Path[5..]);
        return run is null
            ? []
            : [.. run.GetValueNames()
                .Select(name => (Name: name.Length == 0 ? "(Default)" : name, Command: run.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string))
                .Where(v => !string.IsNullOrWhiteSpace(v.Command))
                .Select(v => (v.Name, v.Command!))];
    }

    IReadOnlyDictionary<string, byte[]> IStartupSources.Approved(Hive hive, string subkey)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(Approved + subkey);
        return key is null
            ? new Dictionary<string, byte[]>()
            : key.GetValueNames().Select(n => (n, key.GetValue(n) as byte[])).Where(v => v.Item2 is not null).ToDictionary(v => v.n, v => v.Item2!, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<(string Folder, Hive Hive)> StartupFolders() =>
    [
        .. new[] { (Environment.GetFolderPath(Environment.SpecialFolder.Startup), Hive.CurrentUser), (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Hive.LocalMachine) }
            .Where(f => f.Item1.Length > 0 && Directory.Exists(f.Item1)),
    ];

    public IReadOnlyList<string> FilesIn(string folder) => Directory.GetFiles(folder);

    public byte[] ReadAllBytes(string path) =>
        new FileInfo(path).Length > MaxShortcutBytes ? throw new IOException("The shortcut is too large to be one.") : File.ReadAllBytes(path);

    public TaskList Tasks()
    {
        var tasks = new List<TaskDefinition>();
        var notRead = new List<UnreadSource>();
        dynamic service;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or UnauthorizedAccessException)
        {
            throw new IOException($"The Task Scheduler could not be read: {ex.Message}", ex);
        }

        Walk(service.GetFolder(@"\"), tasks, notRead);
        return new TaskList(tasks, notRead);
    }

    public ServiceList Services()
    {
        var services = new List<ServiceDefinition>();
        var notRead = new List<UnreadSource>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
            ?? throw new IOException("The service list could not be opened.");
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("Start") is not int start || key.GetValue("Type") is not int type)
                {
                    continue;
                }

                var display = key.GetValue("DisplayName") as string;
                services.Add(new ServiceDefinition(
                    name,
                    display is null || display.StartsWith('@') ? null : display,
                    key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    start,
                    key.GetValue("DelayedAutostart") is 1,
                    type));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                notRead.Add(new UnreadSource($@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", ex.Message));
            }
        }

        return new ServiceList(services, notRead);
    }

    public string ExpandEnvironment(string text) => Environment.ExpandEnvironmentVariables(text);

    public bool Exists(string path) => File.Exists(path);

    public Signature SignatureOf(string path)
    {
        var state = Verify(path) switch
        {
            0 => SignatureState.Valid,
            TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureState.Unsigned,
            _ => SignatureState.Invalid,
        };
        return new Signature(state, state == SignatureState.Unsigned ? null : Publisher(path));
    }

    private static void Walk(dynamic folder, List<TaskDefinition> tasks, List<UnreadSource> notRead)
    {
        string path = folder.Path;
        try
        {
            foreach (var task in folder.GetTasks(1))
            {
                try
                {
                    tasks.Add(new TaskDefinition((string)task.Path, (string)task.Xml));
                }
                catch (COMException ex)
                {
                    notRead.Add(new UnreadSource((string)task.Path, ex.Message));
                }
            }

            foreach (var child in folder.GetFolders(0))
            {
                Walk(child, tasks, notRead);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            notRead.Add(new UnreadSource($"Task Scheduler folder {path}", ex.Message));
        }
    }

    private static RegistryKey Root(Hive hive) =>
        RegistryKey.OpenBaseKey(hive == Hive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    private static string? Publisher(string path)
    {
        try
        {
            // X509CertificateLoader has no way to read an Authenticode signer; the signature itself is checked by WinVerifyTrust.
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signer);
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>WinVerifyTrust on the file's own signature, with revocation checks off and URL retrieval from cache only.</summary>
    private static int Verify(string path)
    {
        var file = new WintrustFileInfo { Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WintrustData
            {
                Size = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                File = filePointer,
                StateAction = 0,
                ProviderFlags = 0x00000010 | 0x00001000,
            };
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WintrustData data);
}
