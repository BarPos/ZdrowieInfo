using System.ComponentModel.DataAnnotations;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Tests;

public class OptionsTests
{
    private static List<ValidationResult> Validate(ZdrowieInfoOptions o)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Https_public_url_is_valid() =>
        Assert.Empty(Validate(new() { PublicUrl = "https://zdrowieinfo.example.pl", DataDirectory = "/var/lib/zdrowieinfo" }));

    [Theory]
    [InlineData("http://zdrowieinfo.example.pl")]
    [InlineData("https://example.pl/zdrowieinfo")]
    [InlineData("zdrowieinfo.example.pl")]
    public void Invalid_public_urls_are_rejected(string url) =>
        Assert.NotEmpty(Validate(new() { PublicUrl = url, DataDirectory = "/tmp" }));

    [Fact]
    public void Http_allowed_when_explicitly_disabled() =>
        Assert.Empty(Validate(new() { PublicUrl = "http://localhost:5180", DataDirectory = "/tmp", RequireHttpsPublicUrl = false }));
}
