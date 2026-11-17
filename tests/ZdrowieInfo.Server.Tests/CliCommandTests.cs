using ZdrowieInfo.Server.Hosting;

namespace ZdrowieInfo.Server.Tests;

public class CliCommandTests
{
    [Fact]
    public void No_arguments_runs_the_app() => Assert.Equal(CliCommandKind.Run, CliCommand.Parse([]).Kind);

    [Fact]
    public void Host_arguments_are_ignored() =>
        Assert.Equal(CliCommandKind.Run, CliCommand.Parse(["--urls", "http://0.0.0.0:5000"]).Kind);

    [Theory]
    [InlineData("--create-user", "CreateUser")]
    [InlineData("--set-password", "SetPassword")]
    [InlineData("--reset-mfa", "ResetMfa")]
    public void Commands_with_login(string flag, string kind)
    {
        var command = CliCommand.Parse([flag, "bartek"]);
        Assert.Equal(kind, command.Kind.ToString());
        Assert.Equal("bartek", command.Login);
    }

    [Fact]
    public void Missing_login_is_invalid() => Assert.Equal(CliCommandKind.Invalid, CliCommand.Parse(["--create-user"]).Kind);

    [Fact]
    public void Two_commands_are_invalid() =>
        Assert.Equal(CliCommandKind.Invalid, CliCommand.Parse(["--migrate-only", "--reset-mfa", "x"]).Kind);

    [Fact]
    public void Migrate_only() => Assert.Equal(CliCommandKind.MigrateOnly, CliCommand.Parse(["--migrate-only"]).Kind);
}
