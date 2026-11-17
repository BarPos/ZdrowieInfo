using System.Xml.Linq;
using ZdrowieInfo.Server.Common;

namespace ZdrowieInfo.Server.Tests;

public class AppVersionTests
{
    [Fact]
    public void AppVersion_matches_Directory_Build_props()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var version = XDocument.Load(Path.Combine(dir.FullName, "Directory.Build.props"))
            .Descendants("Version").Single().Value;
        Assert.Equal(AppVersion.Current, version);
    }
}
