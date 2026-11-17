using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZdrowieInfo.Server.Common;
using ZdrowieInfo.Server.Tests.Infrastructure;
using ZdrowieInfo.Server.Updates;

namespace ZdrowieInfo.Server.Tests.Updates;

public sealed class FakeReleaseSource : IReleaseSource
{
    public bool IsConfigured { get; set; } = true;
    public Release? Latest { get; set; }
    public Dictionary<string, byte[]> Files { get; } = new();
    public int Calls { get; private set; }

    public Task<Release?> GetLatestAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Latest);
    }

    public Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(Files[asset.Name]));

    /// <summary>Wydanie z paczką dla x64 i SHA256SUMS.txt (opcjonalnie z błędnym skrótem).</summary>
    public void Publish(string version, byte[] tarball, bool wrongHash = false)
    {
        var name = ReleaseAssetNames.ServerTarball(version, "x64");
        var hash = Convert.ToHexStringLower(SHA256.HashData(wrongHash ? [1, 2, 3] : tarball));
        Files[name] = tarball;
        Files[ReleaseAssetNames.Sums] = Encoding.UTF8.GetBytes($"{hash}  {name}\n{new string('0', 64)}  install.sh\n");
        Latest = new Release(new ReleaseInfo(version, "v" + version, $"ZdrowieInfo {version}", "Zmiany.", DateTimeOffset.UtcNow, false, null),
            [new ReleaseAsset(name, tarball.Length, "https://example.invalid/" + name), new ReleaseAsset(ReleaseAssetNames.Sums, 100, "https://example.invalid/sums")]);
    }
}

public sealed class FakeInstallation : IServerInstallation
{
    public string? Architecture { get; set; } = "x64";
    public bool SelfUpdateSupported { get; set; } = true;
    public string? UnsupportedReason { get; set; }
}

public class UpdatesTests(PostgresFixture postgres)
{
    private static async Task<(AppFactory App, FakeReleaseSource Releases)> StartAsync(PostgresFixture postgres)
    {
        var releases = new FakeReleaseSource();
        var app = await AppFactory.CreateAsync(postgres, services: s =>
        {
            s.AddSingleton<IReleaseSource>(releases);
            s.AddSingleton<IServerInstallation>(new FakeInstallation());
        });
        return (app, releases);
    }

    private static ServerUpdateService Service(AppFactory app) => app.Services.GetRequiredService<ServerUpdateService>();

    private static string Next() => $"{int.Parse(AppVersion.Current.Split('.')[0]) + 1}.0.0";

    [Fact]
    public void Versions_are_plain_triplets_compared_numerically()
    {
        Assert.True(AppVersion.IsPlainTriplet("1.2.10"));
        foreach (var bad in new[] { "1.2", "1.2.3.4", "v1.2.3", "1.2.3-rc", "1.2.3 ", "", null })
            Assert.False(AppVersion.IsPlainTriplet(bad));
        Assert.Equal(1, AppVersion.Compare("1.2.10", "1.2.9"));
        Assert.Equal(0, AppVersion.Compare("2.0.0", "2.0.0"));
        Assert.Equal(-1, AppVersion.Compare("1.99.99", "2.0.0"));
        Assert.NotNull(ServerUpdateService.CheckTargetVersion(AppVersion.Current));
        Assert.NotNull(ServerUpdateService.CheckTargetVersion("v9.0.0"));
        Assert.Null(ServerUpdateService.CheckTargetVersion(Next()));
    }

    [Fact]
    public void Sums_file_parses_sha256sum_output()
    {
        var sums = Sha256Sums.Parse($"{new string('A', 64)}  ZdrowieInfo.Server-1.0.0-linux-x64.tar.gz\r\n{new string('b', 64)} *./install.sh\nzły wiersz\n");
        Assert.Equal(new string('a', 64), sums["ZdrowieInfo.Server-1.0.0-linux-x64.tar.gz"]);
        Assert.Equal(new string('b', 64), sums["install.sh"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public void GitHub_release_json_is_parsed_and_only_https_assets_kept()
    {
        var source = new GitHubReleaseSource(new NoHttp(), Options.Create(new UpdatesOptions { GitHubRepository = "owner/repo" }), NullLogger<GitHubReleaseSource>.Instance);
        using var doc = JsonDocument.Parse("""
            {"tag_name":"v1.4.0","name":"ZdrowieInfo 1.4.0","body":"Nowości","published_at":"2026-11-20T10:00:00Z","prerelease":false,
             "html_url":"https://github.com/owner/repo/releases/tag/v1.4.0",
             "assets":[{"name":"SHA256SUMS.txt","size":120,"browser_download_url":"https://github.com/owner/repo/releases/download/v1.4.0/SHA256SUMS.txt"},
                       {"name":"evil","size":1,"browser_download_url":"http://example.com/evil"}]}
            """);
        var release = source.Parse(doc.RootElement)!;
        Assert.Equal("1.4.0", release.Info.Version);
        Assert.Equal("Nowości", release.Info.Notes);
        Assert.Equal(["SHA256SUMS.txt"], release.Assets.Select(a => a.Name));
        Assert.True(source.IsConfigured);
        Assert.False(new GitHubReleaseSource(new NoHttp(), Options.Create(new UpdatesOptions { GitHubRepository = "owner/repo/extra" }), NullLogger<GitHubReleaseSource>.Instance).IsConfigured);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Test nie łączy się z siecią.");
    }

    [Fact]
    public async Task Health_endpoint_reports_the_running_version()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var response = await app.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"\"version\":\"{AppVersion.Current}\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_repository_updates_are_off_and_nothing_is_contacted()
    {
        await using var app = await AppFactory.CreateAsync(postgres); // domyślnie bez repozytorium – prawdziwe źródło GitHub
        var status = await Service(app).CheckNowAsync();
        Assert.False(status.Configured);
        Assert.False((await Service(app).UpdateNowAsync("admin")).Ok);
        Assert.Equal(AppVersion.Current, status.CurrentVersion);
    }

    [Fact]
    public async Task Newer_release_is_downloaded_verified_and_staged_for_update_sh()
    {
        var (app, releases) = await StartAsync(postgres);
        await using var _ = app;
        var tarball = RandomNumberGenerator.GetBytes(4096);
        releases.Publish(Next(), tarball);

        var status = await Service(app).CheckNowAsync();
        Assert.True(status.UpdateAvailable);
        var result = await Service(app).UpdateNowAsync("szef");

        Assert.True(result.Ok, result.Error);
        var dir = Service(app).UpdatesDirectory;
        var name = ReleaseAssetNames.ServerTarball(Next(), "x64");
        Assert.Equal(tarball, await File.ReadAllBytesAsync(Path.Combine(dir, Next(), name)));
        Assert.True(File.Exists(Path.Combine(dir, Next(), ReleaseAssetNames.Sums)));
        var request = UpdateRequestFile.ParseKeyValues(await File.ReadAllTextAsync(Path.Combine(dir, UpdateRequestFile.RequestFileName)));
        Assert.Equal(Next(), request["version"]);
        Assert.Equal(AppVersion.Current, request["from_version"]);
        Assert.Equal(name, request["file"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(tarball)), request["sha256"]);
        Assert.Empty(Directory.GetFiles(dir, ".*.tmp"));
        var run = Assert.Single((await Service(app).GetStatusAsync()).Runs);
        Assert.Equal((ServerUpdateState.Staged, "szef"), (run.State, run.RequestedBy));

        // Drugie zlecenie, zanim update.sh skończy – odmowa.
        Assert.False((await Service(app).UpdateNowAsync("szef")).Ok);
    }

    [Fact]
    public async Task Hash_mismatch_or_old_version_is_refused_without_a_request()
    {
        var (app, releases) = await StartAsync(postgres);
        await using var _ = app;
        releases.Publish(Next(), RandomNumberGenerator.GetBytes(100), wrongHash: true);
        var bad = await Service(app).UpdateNowAsync("szef");
        Assert.False(bad.Ok);
        Assert.Contains("SHA-256", bad.Error);

        releases.Publish(AppVersion.Current, RandomNumberGenerator.GetBytes(100));
        Assert.Contains("nie jest nowsze", (await Service(app).UpdateNowAsync("szef")).Error);
        Assert.False(File.Exists(Path.Combine(Service(app).UpdatesDirectory, UpdateRequestFile.RequestFileName)));
    }

    [Fact]
    public async Task Result_left_by_update_sh_finishes_the_run()
    {
        var (app, releases) = await StartAsync(postgres);
        await using var _ = app;
        releases.Publish(Next(), RandomNumberGenerator.GetBytes(100));
        Assert.True((await Service(app).UpdateNowAsync("szef")).Ok);
        var runId = (await Service(app).GetStatusAsync()).Runs.Single().Id;
        var dir = Service(app).UpdatesDirectory;
        await File.WriteAllTextAsync(Path.Combine(dir, UpdateRequestFile.ResultFileName),
            $"format=1\nrun_id={runId}\nversion={Next()}\nfrom_version={AppVersion.Current}\nstate=RolledBack\nmessage=Nowa wersja nie odpowiedziała; przywrócono {AppVersion.Current}.\nfinished_at=2026-11-20T10:00:00Z\n");

        var run = (await Service(app).GetStatusAsync()).Runs.Single();

        Assert.Equal(ServerUpdateState.RolledBack, run.State);
        Assert.StartsWith("Nowa wersja nie odpowiedziała", run.Message);
        Assert.False(File.Exists(Path.Combine(dir, UpdateRequestFile.ResultFileName)));
        Assert.False(Directory.Exists(Path.Combine(dir, Next())));

        // Wynik install.sh --upgrade (bez zlecenia z panelu) też trafia do historii.
        await File.WriteAllTextAsync(Path.Combine(dir, UpdateRequestFile.ResultFileName),
            $"format=1\nrun_id={Guid.NewGuid()}\nversion={Next()}\nfrom_version={AppVersion.Current}\nstate=Succeeded\nmessage=OK\n");
        Assert.Equal(2, (await Service(app).GetStatusAsync()).Runs.Count);
    }
}
