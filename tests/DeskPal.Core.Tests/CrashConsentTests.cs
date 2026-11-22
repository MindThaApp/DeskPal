using DeskPal.Core;
using Xunit;

namespace DeskPal.Core.Tests;

public class CrashConsentTests
{
    [Fact]
    public void NewInstallIsOnAndNeedsNoNotice()
    {
        var s = new DeskPalSettings();
        Assert.Equal(CrashReportingMode.Always, s.CrashReports);
        s.Migrate();
        Assert.Equal(CrashReportingMode.Always, s.CrashReports);
        Assert.False(s.CrashNoticePending);
    }

    [Fact]
    public void OldAskDefaultMovesToOnAndIsToldOnce()
    {
        var s = new DeskPalSettings { SettingsVersion = 2, CrashReports = CrashReportingMode.Ask };
        Assert.True(s.Migrate());
        Assert.Equal(CrashReportingMode.Always, s.CrashReports);
        Assert.True(s.CrashNoticePending);
        s.CrashNoticePending = false;
        s.Migrate();
        Assert.False(s.CrashNoticePending);
    }

    [Theory]
    [InlineData(CrashReportingMode.Never)]
    [InlineData(CrashReportingMode.Always)]
    public void ExplicitChoiceIsKept(CrashReportingMode chosen)
    {
        var s = new DeskPalSettings { SettingsVersion = 2, CrashReports = chosen };
        s.Migrate();
        Assert.Equal(chosen, s.CrashReports);
        Assert.False(s.CrashNoticePending);
    }

    [Fact]
    public void GateSendsWhenOnAndDropsWhenOff()
    {
        Assert.Equal(CrashGateDecision.Send, CrashReportGate.Decide(CrashReportingMode.Always, false));
        Assert.Equal(CrashGateDecision.Drop, CrashReportGate.Decide(CrashReportingMode.Never, true));
    }

    private static CrashReportRedactor Redactor() => new(@"C:\Users\jane", "jane", "JANES-PC", @"C:\Apps\DeskPal");

    [Theory]
    [InlineData("Could not open Quarterly-report.docx now", "Could not open <file>.docx now")]
    [InlineData("Cannot read 'My holiday plan.pdf'", "Cannot read '<file>.pdf'")]
    [InlineData(@"Access denied: C:\Users\jane\Documents\secret notes.txt", @"Access denied: %USERPROFILE%\Documents\<file>.txt")]
    public void FileNamesAreStripped(string input, string expected)
    {
        var result = Redactor().Redact(input);
        Assert.Equal(expected, result);
        Assert.Equal(result, Redactor().Redact(result));
    }

    [Theory]
    [InlineData("connect to 192.168.1.20 failed", "connect to <ip> failed")]
    [InlineData("peer 2001:db8::ff00:42:8329 reset", "peer <ip> reset")]
    [InlineData("DeskPal.Core, Version=1.0.0.0, Culture=neutral", "DeskPal.Core, Version=1.0.0.0, Culture=neutral")]
    public void IpAddressesAreStripped(string input, string expected) =>
        Assert.Equal(expected, Redactor().Redact(input));

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string text = "12:30:45 System.IO.File.ReadAllText failed in DeskPal.Core 1.0.2";
        Assert.Equal(text, Redactor().Redact(text));
    }

    [Fact]
    public void UserAndMachineNamesAreStripped() =>
        Assert.Equal("<user> on <machine>", Redactor().Redact("jane on JANES-PC"));
}
