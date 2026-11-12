using DeskPal.Core.Integration;
using Xunit;

namespace DeskPal.Core.Tests;

public sealed class DeskPalLinkTests
{
    [Theory]
    [InlineData("deskpal://open?page=today", "today")]
    [InlineData("DESKPAL://OPEN?PAGE=Bloop", "bloop")]
    [InlineData("deskpal:open?page=garden", "garden")]
    [InlineData("deskpal://open/?page=about", "about")]
    [InlineData("deskpal://open?page=settings", "settings")]
    [InlineData("deskpal://open?page=home", "home")]
    [InlineData("deskpal://open?page=nope", "home")]
    [InlineData("deskpal://open", "home")]
    [InlineData("deskpal://open?page=to%64ay", "today")]
    public void Open_maps_pages(string uri, string page)
    {
        var l = DeskPalLink.Parse(uri);
        Assert.True(l.Supported);
        Assert.Equal(page, l.Page);
    }

    [Theory]
    [InlineData("deskpal://water?ml=250")]
    [InlineData("deskpal://pause?minutes=30")]
    [InlineData("deskpal://")]
    [InlineData("deskpal://openx?page=today")]
    [InlineData("https://example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_verbs_are_unsupported_and_open_home(string? uri)
    {
        var l = DeskPalLink.Parse(uri);
        Assert.False(l.Supported);
        Assert.Equal("home", l.Page);
        Assert.Null(l.Section);
    }

    [Fact]
    public void Settings_section_automation_only_on_settings()
    {
        Assert.Equal("automation", DeskPalLink.Parse("deskpal://open?page=settings&section=automation").Section);
        Assert.Null(DeskPalLink.Parse("deskpal://open?page=today&section=automation").Section);
        Assert.Null(DeskPalLink.Parse("deskpal://open?page=settings&section=other").Section);
    }

    [Fact]
    public void FindArg_picks_the_link()
    {
        Assert.Equal("deskpal://open?page=today", DeskPalLink.FindArg(["--x", "deskpal://open?page=today"]));
        Assert.Null(DeskPalLink.FindArg(["--x"]));
    }

    [Fact]
    public void Pipe_open_is_read_only_and_normalised()
    {
        var p = ControlParser.Parse("{\"v\":1,\"cmd\":\"open\",\"args\":{\"page\":\"Garden\"}}");
        Assert.Equal("garden", p.Command!.Page);
        Assert.True(p.Command.IsReadOnly);
        Assert.Equal("home", ControlParser.Parse("{\"cmd\":\"open\",\"args\":{\"page\":\"zzz\"}}").Command!.Page);
        Assert.Equal("home", ControlParser.Parse("{\"cmd\":\"open\"}").Command!.Page);
    }
}
