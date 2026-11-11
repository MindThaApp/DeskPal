using DeskPal.Core;
using Microsoft.Win32;

namespace DeskPal;

// Read-only: reads the Windows capability consent store, which stamps start/stop times while an app holds the device.
public sealed class RegistryCallDetector : ICallDetector
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static readonly string[] Devices = ["microphone", "webcam"];

    public bool IsInCall()
    {
        foreach (var device in Devices)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root + device, writable: false);
            if (key is null) continue;
            if (AnyInUse(key)) return true;
        }
        return false;
    }

    private static bool AnyInUse(RegistryKey deviceKey)
    {
        foreach (var name in deviceKey.GetSubKeyNames())
        {
            using var sub = deviceKey.OpenSubKey(name, writable: false);
            if (sub is null) continue;
            if (name == "NonPackaged")
            {
                foreach (var app in sub.GetSubKeyNames())
                {
                    if (IsSelf(app)) continue;
                    using var appKey = sub.OpenSubKey(app, writable: false);
                    if (appKey is not null && InUse(appKey)) return true;
                }
            }
            else if (!IsSelf(name) && InUse(sub))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSelf(string name) => name.Contains("deskpal", StringComparison.OrdinalIgnoreCase);

    private static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start > 0
        && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
}
