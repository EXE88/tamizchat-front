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
