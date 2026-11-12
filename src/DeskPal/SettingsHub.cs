using DeskPal.Core;

namespace DeskPal;

// Single owner of the live settings object: saves it, restarts the timers and tells every view to refresh.
public sealed class SettingsHub(DeskPalSettings settings, NudgeScheduler scheduler)
{
    public DeskPalSettings Settings { get; } = settings;

    public event Action? Changed;

    // Set once the Automation Portal link is running; the settings page reads its skipped-events count.
    public AutomationPortalLink? Link { get; set; }

    // Tells every view to refresh without saving anything.
    public void RaiseChanged() => Changed?.Invoke();

    public void Commit(bool reminders)
    {
        try { JsonFileStore.Save(AppData.SettingsPath, Settings); }
        catch (Exception ex) { Logger.Log("settings save failed: " + ex.Message); }
        if (reminders) scheduler.ApplySettings();
        Changed?.Invoke();
    }
}
