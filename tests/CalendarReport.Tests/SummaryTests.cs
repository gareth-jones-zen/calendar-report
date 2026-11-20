using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

public sealed class SummaryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-summary-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();

    public SummaryTests() => Directory.CreateDirectory(dir);

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

    private const string Me = "alex@example.com";

    /// <summary>A meeting with people (addresses; "room@example.com" is a room), organised by the first unless given.</summary>
    private static Event With(string start, string end, string subject, string[] people, string? organizer = null,
        string showAs = "busy", string sensitivity = "normal", string[]? categories = null, string? organizerName = "Organiser", int? attendees = null)
    {
        var invitees = people.Select(a => new Attendee(a.Split('@')[0], a, a.StartsWith("room", StringComparison.Ordinal) ? "resource" : "required", "accepted")).ToList();
        return new Event(DateTime.Parse(start), DateTime.Parse(end), false, showAs, subject, null, organizerName,
            attendees ?? invitees.Count, true, sensitivity, categories ?? [], false, "accepted", invitees, organizer ?? Me);
    }

    private static Event Block(string start, string end, string subject, string showAs = "busy", string sensitivity = "normal") =>
        With(start, end, subject, [], Me, showAs, sensitivity);

    private const string Settings = """
        time_zone = "GMT Standard Time"
        [groups]
        team = ["ann@example.com", "bob@example.com"]
        [summary]
        default_view = "me"
        order = ["Me", "Team", "Task time", "Other"]
        [summary.work_week]
        "ann@example.com" = 30
        [view.me]
        rules = "mine"
        depth = "buckets"
        [view.team]
        rules = "mine"
        people = ["team"]
        team_total = true
        [[rules.common]]
        show_as = "free"
        count = "skip"
        [[rules.common]]
        min_hours = 20
        count = "leave"
        [[rules.common]]
        private = true
        subject = '^private appointment$'
        starts_between = "11:30-14:00"
        max_minutes = 60
        count = "not-working"
        [[rules.common]]
        subject = '\btravel\b'
        count = "not-working"
        [[rules.mine]]
        use = "common"
        [[rules.mine]]
        group = "Me"
        bucket = "1:1 with my manager"
        one_to_one_with = "manager"
        [[rules.mine]]
        group = "Team"
        bucket = "1:1s"
        one_to_one_with = "team"
        [[rules.mine]]
        group = "L&D"
        organizer = 'docebo'
        [[rules.mine]]
        group = "Projects"
        own_block = false
        organizer_title = 'project'
        [[rules.mine]]
        group = "Team"
        bucket = "Team meetings"
        mostly = "team"
        [[rules.mine]]
        group = "Task time"
        bucket = "Blocked time"
        own_block = true
        """;

    private (int Code, string Out, string Err) Run(string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Source = _ => source, Today = () => new DateTime(2026, 11, 19), Env = _ => null,
        });
        return (code, output.ToString(), errors.ToString());
    }

    private JsonObject Json(params string[] args)
    {
        var (code, output, error) = Run(Settings, ["summary", "--format", "json", .. args]);
        Assert.True(code == 0, error);
        return JsonNode.Parse(output)!.AsObject();
    }

    private static int Minutes(JsonNode week, string group, string? bucket = null)
    {
        var g = week["groups"]!.AsArray().FirstOrDefault(x => (string)x!["name"]! == group);
        if (g is null)
            return 0;
        if (bucket is null)
            return (int)g["minutes"]!;
        return (int?)g["buckets"]!.AsArray().FirstOrDefault(x => (string)x!["name"]! == bucket)?["minutes"] ?? 0;
    }

    [Fact]
    public void Shortened_meetings_count_as_booked_and_overlaps_count_once_in_the_earlier_rule()
    {
        source.Managers["me"] = "john@example.com";
        source.Calendars["me"] = [
            With("2026-11-10T10:30", "2026-11-10T10:55", "Weekly 1-2-1", ["john@example.com", "room3@example.com"], "john@example.com"),
            With("2026-11-10T14:00", "2026-11-10T14:50", "Ann 1:1", ["ann@example.com"]),
            Block("2026-11-10T14:30", "2026-11-10T15:10", "Prep"),   // overlaps Ann's 1:1 (rounded to 15:00) by half an hour
        ];
        var week = Json("--from", "2026-11-09", "--weeks", "1")["people"]![0]!["weeks"]![0]!;
        Assert.Equal(30, Minutes(week, "Me", "1:1 with my manager"));   // 25 -> 30; the room isn't a person
        Assert.Equal(60, Minutes(week, "Team", "1:1s"));                // 50 -> 60
        Assert.Equal(15, Minutes(week, "Task time"));                   // 40 -> 45, less the 30 the 1:1 took
        Assert.Equal(105, (int)week["busy"]!);
        Assert.Equal(2250, (int)week["base"]!);                         // 37.5 h
        Assert.Equal(2250 - 105, (int)week["available"]!);
    }

    [Fact]
    public void Leave_takes_a_fifth_off_the_week_and_a_busier_week_is_its_own_total()
    {
        source.Calendars["ann@example.com"] = [
            Block("2026-11-09T00:00", "2026-11-10T00:00", "Holiday") with { OrganizerAddress = "ann@example.com" },
            .. Enumerable.Range(10, 4).Select(d =>
                Block($"2026-11-{d}T08:00", $"2026-11-{d}T17:00", "Focus") with { OrganizerAddress = "ann@example.com" }),
        ];
        var person = Json("ann@example.com", "--from", "2026-11-09")["people"]![0]!;
        Assert.Equal("2026-11-09", (string)person["leave"]![0]!);
        var week = person["weeks"]![0]!;
        Assert.Equal(4, (int)week["days"]!);
        Assert.Equal(1440, (int)week["base"]!);    // her 30 h week, four days of it
        Assert.Equal(2160, (int)week["busy"]!);    // 4 x 9 h
        Assert.Equal(2160, (int)week["total"]!);
        Assert.Equal(0, (int)week["available"]!);
    }

    [Fact]
    public void Lunch_and_travel_are_neither_busy_nor_free_and_free_blocks_are_counted_between_entries()
    {
        source.Calendars["me"] = [
            Block("2026-11-10T07:00", "2026-11-10T08:00", "Travel"),
            Block("2026-11-10T12:00", "2026-11-10T13:00", "Private Appointment", sensitivity: "private"),
            Block("2026-11-10T15:00", "2026-11-10T17:00", "Private Appointment", sensitivity: "private"),
            Block("2026-11-10T10:00", "2026-11-10T11:00", "Focus"),
        ];
        var person = Json("--from", "2026-11-09")["people"]![0]!;
        var week = person["weeks"]![0]!;
        Assert.Equal(60 + 120, (int)week["busy"]!);   // the 15:00 private appointment is too late and long for lunch
        Assert.Equal(180, Minutes(week, "Task time"));
        var blocks = week["freeBlocks"]!.AsObject();
        Assert.Equal(1, (int)blocks["1h-2h"]!);       // 11:00-12:00
        Assert.Equal(2, (int)blocks["2h-2h 30m"]!);   // 08:00-10:00 after travel, and 13:00-15:00 after lunch
        Assert.Equal(0, (int)blocks["30m-1h"]!);
        var events = person["events"]!.AsArray();
        Assert.Equal("not-working", (string)events.First(e => (string)e!["subject"]! == "Travel")!["count"]!);
    }

    [Fact]
    public void Team_meetings_need_half_the_room_and_project_meetings_go_by_the_organisers_title()
    {
        source.Titles["pm@example.com"] = "Senior Project Manager";
        source.Calendars["me"] = [
            With("2026-11-11T09:00", "2026-11-11T10:00", "Arch sync", ["ann@example.com", "bob@example.com", "carol@example.com"]),
            With("2026-11-11T10:00", "2026-11-11T11:00", "Big one", ["ann@example.com", "x@example.com", "y@example.com", "z@example.com"]),
            With("2026-11-11T11:00", "2026-11-11T12:00", "Phase review", ["pm@example.com", "x@example.com"], "pm@example.com"),
            With("2026-11-11T13:00", "2026-11-11T15:00", "Navigating change", ["x@example.com"], "x@example.com", organizerName: "SRV Docebo"),
        ];
        var json = Json("--from", "2026-11-09", "--depth", "events");
        var week = json["people"]![0]!["weeks"]![0]!;
        Assert.Equal(60, Minutes(week, "Team", "Team meetings"));   // 3 of 4 people are team or me
        Assert.Equal(60, Minutes(week, "Other"));                    // 2 of 5
        Assert.Equal(60, Minutes(week, "Projects"));
        Assert.Equal(120, Minutes(week, "L&D"));
    }

    [Fact]
    public void The_table_shows_hours_and_minutes_and_a_team_total()
    {
        source.Calendars["ann@example.com"] = [
            With("2026-11-10T10:00", "2026-11-10T10:25", "Sync", ["bob@example.com", "carol@example.com"], "ann@example.com"),
        ];
        source.Calendars["bob@example.com"] = [
            With("2026-11-10T10:00", "2026-11-10T10:25", "Sync", ["bob@example.com", "carol@example.com"], "ann@example.com"),
        ];
        var (code, output, error) = Run(Settings, "summary", "--view", "team");
        Assert.True(code == 0, error);
        Assert.Contains("Ann Smith  (30 h week; leave: none)", output);
        Assert.Contains("Team total (2 people)", output);
        var line = output.Split('\n').First(l => l.StartsWith("Week total", StringComparison.Ordinal));
        Assert.Contains("30h 00m", line);   // Ann's, last week (w/c 9 Nov: the default period)
        Assert.Contains("w/c 09 Nov", output);
        Assert.Contains("Team ", output);
        Assert.Contains("30m", output);     // 25 -> 30
    }

    [Theory]
    [InlineData("[[rules.x]]\ngroup = \"A\"\nsubjekt = 'a'\n", "unknown setting 'subjekt' in rules.x (rule 1)")]
    [InlineData("[[rules.x]]\ncount = \"later\"\n", "count should be one of")]
    [InlineData("[[rules.x]]\nuse = \"y\"\n[[rules.y]]\nuse = \"x\"\n", "uses itself")]
    [InlineData("[[rules.x]]\nuse = \"nope\"\n", "there's no rule list 'nope'")]
    [InlineData("[[rules.x]]\ngroup = \"A\"\nmostly = \"nobody\"\n", "there's no group 'nobody'")]
    [InlineData("[[rules.x]]\ngroup = \"A\"\nstarts_between = \"noon\"\n", "starts_between should be")]
    [InlineData("[view.v]\nrules = \"missing\"\n", "view.v.rules: there's no rule list 'missing'")]
    [InlineData("[summary]\nfree_bands = [60, 30]\n", "free_bands should go up")]
    public void Settings_are_checked_strictly(string settings, string message)
    {
        var (code, _, error) = Run(settings, "summary");
        Assert.Equal(2, code);
        Assert.Contains(message, error);
    }

    [Fact]
    public void With_no_rules_the_built_in_ones_give_meetings_and_task_time()
    {
        source.Calendars["me"] = [
            With("2026-11-10T10:00", "2026-11-10T11:00", "Sync", ["ann@example.com"]),
            Block("2026-11-10T11:00", "2026-11-10T12:00", "Focus"),
            Block("2026-11-10T00:00", "2026-11-11T00:00", "Deadline", showAs: "free"),
        ];
        var (code, output, error) = Run("", "summary");
        Assert.True(code == 0, error);
        Assert.Contains("Meetings ", output);
        Assert.Contains("Task time ", output);
        Assert.DoesNotContain("Deadline", output);
    }
}
