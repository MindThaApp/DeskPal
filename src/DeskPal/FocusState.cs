using System.Runtime.InteropServices;

namespace DeskPal;

public interface IBusyStateProvider
{
    bool IsBusy();
}

public sealed class ShellBusyStateProvider : IBusyStateProvider
{
    // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE, QUNS_QUIET_TIME
    public bool IsBusy() =>
        SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4 or 6;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
