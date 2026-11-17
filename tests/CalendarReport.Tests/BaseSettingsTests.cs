using CalendarReport;

namespace CalendarReport.Tests;

/// <summary>Settings that name a shared base file (base = "base-config.toml").</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-base-" + Guid.NewGuid().ToString("N"));

    private const string Base = """
        login_hint = "me@example.com"
        tenant = "contoso"
        msal_cache = "%EA_LOCAL%/msal-cache.json"
        time_zone = "GMT Standard Time"
        not_ours = "ignored"

        [paths]
        EA_LOCAL = "C:/Users/me/AppData/Local/claude-ea"

        [groups]
        team = ["base@example.com"]

        [git]
        push = true
        """;

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), Base);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string? Env(string name) => name == "LOCALAPPDATA" ? @"C:\Users\me\AppData\Local" : null;

    private Config Load(string text)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, text);
        return Config.Load(path, Env);
    }

    [Fact]
    public void Shared_sign_in_and_paths_come_from_the_base()
    {
        var config = Load("base = \"base-config.toml\"\nlog_file = \"%EA_LOCAL%/calendar-report.log\"\n");
        Assert.Equal("me@example.com", config.LoginHint);
        Assert.Equal("contoso", config.Tenant);
        Assert.Equal("C:/Users/me/AppData/Local/claude-ea/msal-cache.json".Replace('/', '\\'), config.MsalCache);
        Assert.Equal("C:/Users/me/AppData/Local/claude-ea/calendar-report.log".Replace('/', '\\'), config.LogFile);
        Assert.Equal("GMT Standard Time", config.TimeZone.Id);
    }

    [Fact]
    public void The_tools_own_settings_win()
    {
        var config = Load("base = \"base-config.toml\"\nlogin_hint = \"own@example.com\"\ntenant = \"own\"\n");
        Assert.Equal("own@example.com", config.LoginHint);
        Assert.Equal("own", config.Tenant);
    }

    [Fact]
    public void A_table_applies_only_where_the_tool_has_the_section()
    {
        Assert.Empty(Load("base = \"base-config.toml\"\n").Groups);
        var config = Load("base = \"base-config.toml\"\n[groups]\nmine = [\"ann@example.com\"]\n");
        Assert.Equal(["ann@example.com"], config.Groups["mine"]);
        Assert.Equal(["base@example.com"], config.Groups["team"]);
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<UsageException>(() => Load("base = \"nope.toml\"\n"));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Contains("nope.toml", error.Message);
    }
}
