using NAudio.CoreAudioApi;
using RecBox.Models;

namespace RecBox.Services;

public sealed class AudioDeviceService
{
    public const string DefaultDeviceId = "default";

    public IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new()
            {
                Id = DefaultDeviceId,
                Name = "Default Windows microphone",
                IsDefault = true
            }
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in collection)
            {
                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsDefault = false
                });
            }
        }
        catch
        {
            // Enumeration can fail if audio subsystem is unavailable; keep Default entry.
        }

        return devices;
    }

    public string ResolveWasapiDeviceName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == DefaultDeviceId)
            return "default";

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            return device.FriendlyName;
        }
        catch
        {
            return "default";
        }
    }
}
