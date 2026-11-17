namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>Zapisane strony źródeł: tests/fixtures/sources/&lt;slug&gt;/{a,b,detail}.html.</summary>
public static class Fixtures
{
    public static readonly string Root = FindRoot();

    public static string SourcePath(string slug, string file) => Path.Combine(Root, "tests", "fixtures", "sources", slug, file);

    public static bool Exists(string slug, string file) => File.Exists(SourcePath(slug, file));

    public static string Read(string slug, string file) => File.ReadAllText(SourcePath(slug, file));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ZdrowieInfo.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Nie znaleziono katalogu repozytorium.");
    }
}
