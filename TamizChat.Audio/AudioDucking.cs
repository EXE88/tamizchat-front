using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace TamizChat.Audio;

/// <summary>
/// Turns off Windows' "communications" auto-ducking for this application.
///
/// Windows has a setting — Sound → Communications — that quietens every other
/// application by 80% while it believes a call is in progress, and the default
/// is on. A voice client is exactly what triggers it: the moment a stream is
/// opened as a communications stream, music, games and everything else drop
/// away and the call is suddenly much louder than the rest of the machine. It
/// is a real feature and some people want it; nobody wants it to happen without
/// having asked for it.
///
/// The documented way out is <c>IAudioSessionControl2::SetDuckingPreference</c>
/// with TRUE, called by the communications application itself: the system then
/// leaves everyone else's volume alone and lets the application handle the
/// mixing, which is precisely what TamizChat already does — it has a per-person
/// volume control of its own.
///
/// Declared here rather than reached for through NAudio: NAudio wraps
/// <c>IAudioSessionControl2</c> but never exposes this call, and the interface
/// is short enough to declare that reaching into its private fields would be
/// the more fragile of the two options.
///
/// Everything here is best effort. Losing this costs a preference; throwing
/// would cost the call.
/// </summary>
public static class AudioDucking
{
    /// <summary>
    /// Asks the system not to attenuate other applications because of the
    /// session this process has on the given endpoint.
    ///
    /// Call it before opening the stream. The preference belongs to the session,
    /// and the session for a process on an endpoint outlives any one stream, so
    /// setting it early covers the stream that follows.
    /// </summary>
    public static void OptOut(MMDevice device)
    {
        try
        {
            OptOutCore(device.ID);
        }
        catch (Exception)
        {
            // An endpoint that vanished, a session manager that refuses, an
            // older Windows: none of them are worth failing a call over.
        }
    }

    private static void OptOutCore(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        try
        {
            if (enumerator.GetDevice(deviceId, out var device) != 0 || device is null)
            {
                return;
            }

            try
            {
                var managerId = typeof(IAudioSessionManager2).GUID;

                // CLSCTX_ALL. There is no property store to pass, so the
                // activation parameters are null.
                if (device.Activate(ref managerId, 23, IntPtr.Zero, out var raw) != 0 || raw is null)
                {
                    return;
                }

                var manager = (IAudioSessionManager2)raw;

                try
                {
                    // A null session id is this process's own default session on
                    // the endpoint — the one every stream we open lands in.
                    if (manager.GetAudioSessionControl(IntPtr.Zero, 0, out var control) != 0 || control is null)
                    {
                        return;
                    }

                    try
                    {
                        control.SetDuckingPreference(true);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // Only GetDevice is used, but every method before it has to be declared
        // or the vtable slots do not line up.
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            int context,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr sessionId, int streamFlags, out IAudioSessionControl2? control);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr sessionId, int crossProcess, out IntPtr volume);

        [PreserveSig]
        int GetSessionEnumerator(out IntPtr sessions);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr notification);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr notification);
    }

    /// <summary>
    /// The nine methods of IAudioSessionControl come first, then the five of
    /// IAudioSessionControl2. Only the last one is called, and it is the last
    /// one on purpose — every slot above it has to exist for the offset to be
    /// right, whatever the signatures are used for.
    /// </summary>
    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid group);

        [PreserveSig]
        int SetGroupingParam(ref Guid group, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr notification);

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetProcessId(out int processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
}
