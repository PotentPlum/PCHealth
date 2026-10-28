using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PCHealth.Maintenance.Windows;

/// <summary>
/// Reads events as XML with the Windows Event Log API (wevtapi.dll), newest first (T-58, T-60). Read-only; no event
/// is cleared or written. A log the user may not read throws <see cref="UnauthorizedAccessException"/>.
/// </summary>
public static class WindowsEventLog
{
    private const uint QueryChannelPath = 0x1;
    private const uint QueryReverseDirection = 0x200;
    private const uint RenderEventXml = 1;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorAccessDenied = 5;
    private const int ErrorChannelNotFound = 15007;
    private const int ErrorInsufficientBuffer = 122;

    /// <param name="query">An XPath filter such as <c>*[System[(EventID=100)]]</c>.</param>
    /// <exception cref="UnauthorizedAccessException">The log needs rights the user does not have.</exception>
    /// <exception cref="IOException">The log does not exist on this PC or could not be read.</exception>
    public static IReadOnlyList<string> Query(string channel, string query, int max)
    {
        using var results = EvtQuery(IntPtr.Zero, channel, query, QueryChannelPath | QueryReverseDirection);
        if (results.IsInvalid)
        {
            throw Failure(channel, Marshal.GetLastPInvokeError());
        }

        var events = new List<string>();
        var handles = new IntPtr[16];
        while (events.Count < max)
        {
            if (!EvtNext(results, handles.Length, handles, -1, 0, out var returned))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNoMoreItems)
                {
                    break;
                }

                throw Failure(channel, error);
            }

            for (var i = 0; i < returned; i++)
            {
                try
                {
                    if (events.Count < max)
                    {
                        events.Add(Render(handles[i]));
                    }
                }
                finally
                {
                    EvtClose(handles[i]);
                }
            }
        }

        return events;
    }

    private static string Render(IntPtr handle)
    {
        EvtRender(IntPtr.Zero, handle, RenderEventXml, 0, IntPtr.Zero, out var used, out _);
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorInsufficientBuffer)
        {
            throw new IOException("An event could not be read.", new Win32Exception(error));
        }

        var buffer = Marshal.AllocHGlobal(used);
        try
        {
            if (!EvtRender(IntPtr.Zero, handle, RenderEventXml, used, buffer, out used, out _))
            {
                throw new IOException("An event could not be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Exception Failure(string channel, int error) => error switch
    {
        ErrorAccessDenied => new UnauthorizedAccessException($"Reading {channel} needs administrator rights."),
        ErrorChannelNotFound => new IOException($"This PC has no {channel} log."),
        _ => new IOException($"{channel} could not be read.", new Win32Exception(error)),
    };

    private sealed class EventHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => EvtClose(handle);
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern EventHandle EvtQuery(IntPtr session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtNext(EventHandle resultSet, int eventsSize, [Out] IntPtr[] events, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, uint flags, int bufferSize, IntPtr buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtClose(IntPtr handle);
}
