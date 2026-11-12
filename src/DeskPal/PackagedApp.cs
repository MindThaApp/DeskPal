using System.Runtime.InteropServices;
using DeskPal.Core.Integration;
using Microsoft.Windows.AppLifecycle;

namespace DeskPal;

// Store (MSIX) build vs portable build. The package manifest declares the deskpal: scheme and the toast activator, so a
// packaged copy registers nothing itself.
public static class PackagedApp
{
    private const int AppModelErrorNoPackage = 15700;

    public static readonly bool IsPackaged = DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            var length = 0u;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // The deskpal: link this launch was started with: from the command line, or (packaged) from the protocol activation.
    public static string? StartLinkArg()
    {
        var arg = DeskPalLink.FindArg(Environment.GetCommandLineArgs().Skip(1));
        if (arg is not null || !IsPackaged) return arg;
        try
        {
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activated?.Kind == ExtendedActivationKind.Protocol
                && activated.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol)
                return DeskPalLink.FindArg([protocol.Uri.OriginalString]);
        }
        catch (Exception ex)
        {
            Logger.Log("activation args failed: " + ex.Message);
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
