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

    private Config LoadWithMonths(string text)
    {
        File.WriteAllText(Path.Combine(dir, "months-base.toml"), "calendar_months_ahead = 3\n");
        return Load("base = \"months-base.toml\"\n" + text);
    }

    [Fact]
    public void Contacts_look_ahead_follows_the_bases_calendar_months_ahead()
    {
        var config = LoadWithMonths("[contacts]\nmax_attendees = 6\n");
        Assert.Null(config.Contacts.LookaheadDays);
        Assert.Equal(3, config.Contacts.LookaheadMonths);
        Assert.Equal(new DateTime(2027, 2, 23), config.Contacts.LookaheadEnd(new DateTime(2026, 11, 22, 9, 30, 0)));
        // With no [contacts] section at all, the base still applies.
        Assert.Equal(new DateTime(2027, 2, 23), LoadWithMonths("").Contacts.LookaheadEnd(new DateTime(2026, 11, 22)));
    }

    [Fact]
    public void Contacts_own_lookahead_days_wins_over_the_base()
    {
        var config = LoadWithMonths("[contacts]\nlookahead_days = 10\n");
        Assert.Equal(10, config.Contacts.LookaheadDays);
        Assert.Equal(new DateTime(2026, 12, 3), config.Contacts.LookaheadEnd(new DateTime(2026, 11, 22)));
    }

    [Fact]
    public void Contacts_look_ahead_is_sixty_days_when_neither_is_set()
    {
        var config = Load("base = \"base-config.toml\"\n[contacts]\nmax_attendees = 6\n");
        Assert.Null(config.Contacts.LookaheadMonths);
        Assert.Equal(new DateTime(2026, 11, 22).AddDays(61), config.Contacts.LookaheadEnd(new DateTime(2026, 11, 22)));
    }

    [Fact]
    public void A_bad_calendar_months_ahead_is_a_settings_error()
    {
        var error = Assert.Throws<UsageException>(() => Load("calendar_months_ahead = 0\n"));
        Assert.Contains("calendar_months_ahead", error.Message);
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        var error = Assert.Throws<UsageException>(() => Load("base = \"nope.toml\"\n"));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Contains("nope.toml", error.Message);
    }
}
