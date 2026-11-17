using ZdrowieInfo.Server.Sources.Fetching;

namespace ZdrowieInfo.Server.Tests.Sources;

public class RobotsTxtTests
{
    // robots.txt Mazowieckiego OW NFZ (17.11.2026).
    private const string NfzWarszawa = """
        User-agent: *
        Disallow: /gfx/
        Disallow: /pdf/
        Allow: /*.pdf$
        Allow: /*.doc$
        User-agent: Fasterfox
        Disallow: /
        """;

    [Fact]
    public void Other_bots_group_does_not_apply_to_us()
    {
        var robots = RobotsTxt.Parse(NfzWarszawa);
        Assert.True(robots.IsAllowed("/dla-swiadczeniodawcow/aktualnosci/"));
        Assert.False(robots.IsAllowed("/gfx/logo.png"));
        Assert.True(robots.IsAllowed("/pdf/komunikat.pdf"));
    }

    [Fact]
    public void Our_own_group_wins_over_star()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /\n\nUser-agent: ZdrowieInfo\nAllow: /\n");
        Assert.True(robots.IsAllowed("/cokolwiek"));
    }

    [Fact]
    public void Longest_match_wins_and_empty_disallow_allows()
    {
        var robots = RobotsTxt.Parse("User-agent: *\nDisallow: /a/\nAllow: /a/b/\nDisallow:\n");
        Assert.False(robots.IsAllowed("/a/x"));
        Assert.True(robots.IsAllowed("/a/b/x"));
        Assert.True(robots.IsAllowed("/z"));
    }
}
