using DeskPal.Core;

namespace DeskPal;

// Single owner of the live settings object: saves it, restarts the timers and tells every view to refresh.
public sealed class SettingsHub(DeskPalSettings settings, NudgeScheduler scheduler)
{
    public DeskPalSettings Settings { get; } = settings;

    public event Action? Changed;

    public void Commit(bool reminders)
    {
        try { JsonFileStore.Save(AppData.SettingsPath, Settings); }
        catch (Exception ex) { Logger.Log("settings save failed: " + ex.Message); }
        if (reminders) scheduler.ApplySettings();
        Changed?.Invoke();
    }
}
