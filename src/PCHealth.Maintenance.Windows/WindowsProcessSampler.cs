using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PCHealth.Maintenance.Windows;

/// <summary>
/// Samples running processes with limited-information access, which needs no elevation for most processes (T-58).
/// Processes Windows protects come back without CPU time or memory instead of being guessed.
/// </summary>
public sealed class WindowsProcessSampler : IProcessSampler
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public IReadOnlyList<ProcessSample> Sample()
    {
        var samples = new List<ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                TimeSpan? cpu = null;
                long? memory = null;
                try
                {
                    cpu = process.TotalProcessorTime;
                    memory = process.WorkingSet64;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Protected or already exited: reported as unmeasured, not as zero.
                }

                samples.Add(new ProcessSample(process.Id, process.ProcessName, ImagePath(process.Id), cpu, memory));
            }
        }

        return samples;
    }

    private static string? ImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder exeName, ref uint size);
}
