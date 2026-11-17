using ZdrowieInfo.Server.Sources.Extraction;

namespace ZdrowieInfo.Server.Tests.Sources;

public class PolishDateTests
{
    [Theory]
    [InlineData("16.11.2026", 2026, 11, 16)]
    [InlineData(" 03-11-2026 ", 2026, 11, 3)]
    [InlineData("2026-11-17", 2026, 11, 17)]
    [InlineData("16 listopada 2026", 2026, 11, 16)]
    [InlineData("1 października 2026 r.", 2026, 10, 1)]
    [InlineData("2026-11-16T23:30:00Z", 2026, 11, 17)] // północ w Warszawie
    public void Parses_polish_formats(string text, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), PolishDate.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wczoraj")]
    [InlineData("31.02.2026")]
    public void Returns_null_for_unparseable(string? text) => Assert.Null(PolishDate.Parse(text));
}
