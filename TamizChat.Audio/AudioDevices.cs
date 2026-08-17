using NAudio.CoreAudioApi;

namespace TamizChat.Audio;

/// <summary>One selectable microphone or speaker.</summary>
public sealed class AudioDeviceInfo(string id, string name)
{
    /// <summary>Empty means "whatever Windows is using", which is the default.</summary>
    public string Id { get; } = id;

    public string Name { get; } = name;
}

/// <summary>
/// The microphones and speakers the user can choose between.
///
/// Devices are looked up by id at the moment they are opened rather than held
/// on to: a USB headset that is unplugged and plugged back in is a different
/// object, and a stale reference throws when it is used.
/// </summary>
public static class AudioDevices
{
    /// <summary>The entry meaning "follow the Windows default".</summary>
    public const string SystemDefaultId = "";

    public static IReadOnlyList<AudioDeviceInfo> Inputs() => List(DataFlow.Capture);

    public static IReadOnlyList<AudioDeviceInfo> Outputs() => List(DataFlow.Render);

    /// <summary>
    /// Resolves a saved id to a live device, or null for the system default.
    ///
    /// Returns null rather than throwing when the id no longer matches anything
    /// — a headset that is currently unplugged should fall back to the default,
    /// not stop the call from starting.
    /// </summary>
    public static MMDevice? Resolve(string id, bool input)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                input ? DataFlow.Capture : DataFlow.Render,
                DeviceState.Active))
            {
                if (device.ID == id)
                {
                    return device;
                }
            }
        }
        catch (Exception)
        {
            // No audio subsystem at all, which is rare but survivable.
        }

        return null;
    }

    /// <summary>
    /// The name of the device that would be opened right now for a saved id.
    ///
    /// It answers the question the device lists cannot: a saved id that is not
    /// currently active resolves to nothing and the system default is used
    /// instead, so what the list shows as chosen and what would actually open
    /// are two different things — and the gap between them is invisible.
    /// </summary>
    public static string NameInUse(string id, bool input)
    {
        try
        {
            if (Resolve(id, input) is { } chosen)
            {
                return chosen.FriendlyName;
            }

            using var enumerator = new MMDeviceEnumerator();

            // Console, matching what SpeakerPlayback and MicrophoneCapture open.
            return enumerator.GetDefaultAudioEndpoint(
                input ? DataFlow.Capture : DataFlow.Render, Role.Console).FriendlyName;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        var devices = new List<AudioDeviceInfo> { new(SystemDefaultId, "System default") };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // Leave just the default entry.
        }

        return devices;
    }
}
