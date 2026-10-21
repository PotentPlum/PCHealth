using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PCHealth.Execution.Windows;

/// <summary>
/// Finds running processes that would be affected by changing an item (spec §6.3): Restart Manager reports processes
/// holding any of its files open, and for a folder, processes started from a program inside it are reported too.
/// A Restart Manager failure throws, so the gateway refuses the item rather than assuming it is free.
/// </summary>
public static class InUseCheck
{
    // Restart Manager sessions take file lists in batches; very large folders are registered in chunks.
    private const int Batch = 1000;

    /// <exception cref="IOException">The check could not be made.</exception>
    public static IReadOnlyList<string> ProcessesUsing(string finalPath, bool isDirectory)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = isDirectory
            ? Directory.EnumerateFiles(finalPath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            : [finalPath];
        foreach (var chunk in files.Chunk(Batch))
        {
            foreach (var name in Holders(chunk))
            {
                names.Add(name);
            }
        }

        if (isDirectory)
        {
            foreach (var name in ProgramsStartedFrom(finalPath))
            {
                names.Add(name);
            }
        }

        return names.ToArray();
    }

    private static IEnumerable<string> Holders(string[] files)
    {
        var key = new StringBuilder(SessionKeyLength + 1);
        var error = RmStartSession(out var session, 0, key);
        if (error != 0)
        {
            throw Failure("A Restart Manager session could not be started", error);
        }

        try
        {
            error = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (error != 0)
            {
                throw Failure("The files could not be registered with the Restart Manager", error);
            }

            uint count = 0;
            RmProcessInfo[] processes = [];
            while (true)
            {
                var capacity = (uint)processes.Length;
                uint reasons = 0;
                error = RmGetList(session, out var needed, ref capacity, processes, ref reasons);
                if (error == 0)
                {
                    count = capacity;
                    break;
                }

                if (error != ErrorMoreData)
                {
                    throw Failure("The Restart Manager could not list the processes using the files", error);
                }

                processes = new RmProcessInfo[needed + 4];
            }

            return processes.Take((int)count).Select(p => Describe(p)).ToArray();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    private static string Describe(RmProcessInfo process)
    {
        var name = string.IsNullOrWhiteSpace(process.AppName) ? $"process {process.Process.ProcessId}" : process.AppName;
        return string.IsNullOrWhiteSpace(process.ServiceShortName) ? name : $"{name} (service {process.ServiceShortName})";
    }

    /// <summary>Processes whose program file is inside the folder. Processes this account cannot inspect are not listed.</summary>
    private static IEnumerable<string> ProgramsStartedFrom(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } image && image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add($"{Path.GetFileName(image)} (process {process.Id})");
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or exited processes; another user's program would not run from this user's folder.
                }
            }
        }

        return found;
    }

    private static IOException Failure(string what, int error) => new($"{what}: {new Win32Exception(error).Message} ({error}).");

    private const int SessionKeyLength = 32;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        RmUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RmProcessInfo[] rgAffectedApps,
        ref uint lpdwRebootReasons);
}
