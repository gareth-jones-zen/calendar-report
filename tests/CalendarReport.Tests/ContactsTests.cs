using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

public sealed class ContactsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-contacts-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0);   // a Friday

    private const string Me = "pat@example.com";

    private const string Settings = """
        time_zone = "GMT Standard Time"
        [groups]
        team = ["ann@example.com", "bob@example.com"]
        [contacts]
        max_attendees = 6
        [[contacts.ring]]
        name = "core"
        gap_days = 10
        lost_occurrences = 2
        people = ["team"]
        [[contacts.ring]]
        name = "wider"
        gap_days = 42
        people = ["Cara Lee", "dev@example.com", "ann@example.com"]
        [contacts.why]
        "Cara Lee" = "Head of Delivery: plans and architecture in step"
        """;

    public ContactsTests()
    {
        Directory.CreateDirectory(dir);
        source.People["me"] = new("me", "Pat Example");
        source.People["cara@example.com"] = new("cara@example.com", "Cara Lee");
        source.People["Cara Lee"] = new("cara@example.com", "Cara Lee");
        source.People["dev@example.com"] = new("dev@example.com", "Dev Rana");
        source.People["eve@example.com"] = new("eve@example.com", "Eve North");

        // Ann: a weekly 1:1 on Tuesdays at 10:00. 27 Oct moved to the Wednesday; 3 and 10 Nov lost (cancelled for leave,
        // so gone from the calendar); 17 and 24 Nov held or booked.
        source.Masters["s-ann"] = Weekly("Ann / Pat 1:1", new DateTime(2026, 9, 1), DayOfWeek.Tuesday, new TimeSpan(10, 0, 0));
        var ann = new List<Event>();
        for (var day = new DateTime(2026, 9, 1); day <= new DateTime(2026, 11, 24); day = day.AddDays(7))
        {
            if (day.Day is 3 or 10 && day.Month == 11)
                continue;
            var start = day.Day == 27 && day.Month == 10 ? day.AddDays(1).AddHours(14) : day.AddHours(10);
            ann.Add(With(start, "Ann / Pat 1:1", ["ann@example.com"]) with
            {
                Type = "occurrence", SeriesMasterId = "s-ann", OriginalStart = day.AddHours(10),
            });
        }

        source.Calendars["me"] = [
            .. ann,
            With(new DateTime(2026, 11, 2, 15, 0, 0), "Design review", ["bob@example.com", "eve@example.com", "room1@example.com"]),
            With(new DateTime(2026, 11, 18, 9, 0, 0), "All hands", Enumerable.Range(1, 40).Select(i => $"p{i}@example.com").Append("bob@example.com").ToArray()),
            With(new DateTime(2026, 11, 19, 9, 0, 0), "Declined by me", ["bob@example.com"]) with { Response = "declined" },
            With(new DateTime(2026, 11, 12, 9, 0, 0), "Declined by Bob", ["bob@example.com"], declined: "bob@example.com"),
            With(new DateTime(2026, 11, 13, 9, 0, 0), "Cancelled", ["bob@example.com"]) with { Cancelled = true },
            With(new DateTime(2026, 9, 30, 11, 0, 0), "Catch-up", ["cara@example.com"], organizer: "cara@example.com"),
            With(new DateTime(2026, 11, 5, 11, 0, 0), "Dev and Pat", ["dev@example.com"]),
            With(new DateTime(2026, 12, 1, 11, 0, 0), "Dev and Pat again", ["dev@example.com"], organizer: "dev@example.com") with
            {
                Response = "tentativelyAccepted",
            },
        ];
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

    private static Recurrence Weekly(string subject, DateTime start, DayOfWeek day, TimeSpan time, int interval = 1) =>
        new(subject, "weekly", interval, [day], 0, 0, "first", DayOfWeek.Monday, start, null, null, time);

    /// <summary>A half-hour meeting with people (addresses; "room..." is a room), organised by Pat unless given.</summary>
    private static Event With(DateTime start, string subject, string[] people, string? organizer = null, string? declined = null)
    {
        var invitees = people.Select(a => new Attendee(a.Split('@')[0], a,
            a.StartsWith("room", StringComparison.Ordinal) ? "resource" : "required", a == declined ? "declined" : "accepted")).ToList();
        if (organizer is not null)
            invitees.Add(new Attendee("pat", Me, "required", "accepted"));
        return new Event(start, start.AddMinutes(30), false, "busy", subject, null, organizer ?? "Pat Example", invitees.Count,
            true, "normal", [], false, organizer is null ? "organizer" : "accepted", invitees, organizer ?? Me);
    }

    private (int Code, string Out, string Err) Run(string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Source = _ => source, Now = _ => Now, Today = () => Now.Date, Env = _ => null,
        });
        return (code, output.ToString(), errors.ToString());
    }

    private JsonObject Json(params string[] args)
    {
        var (code, output, error) = Run(Settings, ["contacts", "--format", "json", .. args]);
        Assert.True(code == 0, error);
        return JsonNode.Parse(output)!.AsObject();
    }

    private static JsonNode Person(JsonObject json, string name) =>
        json["rings"]!.AsArray().SelectMany(r => r!["people"]!.AsArray()).Single(p => (string)p!["name"]! == name)!;

    [Fact]
    public void Reads_your_calendar_once_over_the_lookback_and_lookahead()
    {
        var json = Json();
        Assert.Equal(["events me 2026-07-23 2027-01-20"], source.Asked);
        Assert.Equal("2026-11-20T12:00:00", (string)json["asOf"]!);
        Assert.Equal(["core", "wider"], json["rings"]!.AsArray().Select(r => (string)r!["name"]!));
    }

    [Fact]
    public void A_recurring_meeting_with_two_of_its_last_four_lost_is_flagged_and_a_moved_one_is_held()
    {
        var ann = Person(Json(), "Ann Smith");
        Assert.Equal("2026-11-17T10:00:00", (string)ann["last"]!["start"]!);
        Assert.Equal(3, (int)ann["daysSince"]!);
        Assert.Equal("2026-11-24T10:00:00", (string)ann["next"]!["start"]!);
        var cadence = ann["recurring"]![0]!;
        Assert.Equal("weekly", (string)cadence["pattern"]!);
        Assert.Equal(4, (int)cadence["checked"]!);   // 27 Oct (moved to the 28th, so held), 3, 10 and 17 Nov
        Assert.Equal(["2026-11-03", "2026-11-10"], cadence["lost"]!.AsArray().Select(d => (string)d!));
        Assert.True((bool)ann["flagged"]!);
        Assert.Contains("\"Ann / Pat 1:1\": 2 of the last 4 lost", (string)ann["reasons"]![0]!);
    }

    [Fact]
    public void Someone_in_two_rings_is_in_the_first_only()
    {
        var json = Json();
        var wider = json["rings"]![1]!["people"]!.AsArray().Select(p => (string)p!["address"]!).ToList();
        Assert.Equal(["cara@example.com", "dev@example.com"], wider);
    }

    [Fact]
    public void Big_meetings_declined_and_cancelled_ones_are_not_contact()
    {
        var bob = Person(Json(), "Bob Jones");
        Assert.Equal("Design review", (string)bob["last"]!["subject"]!);
        Assert.Equal(3, (int)bob["last"]!["people"]!);   // Pat, Bob and Eve; the room isn't a person
        Assert.Equal(18, (int)bob["daysSince"]!);
        Assert.Null(bob["next"]);
        Assert.Equal(["18 days since the last meeting (more than 10)"], bob["reasons"]!.AsArray().Select(r => (string)r!));
    }

    [Fact]
    public void Only_live_one_to_one_series_are_checked_for_lost_occurrences_unless_the_settings_widen_it()
    {
        // A weekly group meeting with Bob on Thursdays, every occurrence lost; and a 1:1 series with him that ended in October.
        source.Masters["s-group"] = Weekly("Team sync", new DateTime(2026, 9, 3), DayOfWeek.Thursday, new TimeSpan(9, 0, 0));
        source.Masters["s-old"] = Weekly("Old 1:1", new DateTime(2026, 9, 1), DayOfWeek.Monday, new TimeSpan(9, 0, 0)) with
        {
            EndDate = new DateTime(2026, 10, 12),
        };
        source.Calendars["me"].AddRange([
            With(new DateTime(2026, 9, 3, 9, 0, 0), "Team sync", ["bob@example.com", "eve@example.com"]) with { SeriesMasterId = "s-group" },
            With(new DateTime(2026, 9, 7, 9, 0, 0), "Old 1:1", ["bob@example.com"]) with { SeriesMasterId = "s-old" },
        ]);
        var bob = Person(Json(), "Bob Jones");
        Assert.Empty(bob["recurring"]!.AsArray());

        var (code, output, error) = Run(Settings.Replace("max_attendees = 6", "max_attendees = 6\ncadence_max_attendees = 3"), "contacts", "--format", "json");
        Assert.True(code == 0, error);
        bob = Person(JsonNode.Parse(output)!.AsObject(), "Bob Jones");
        Assert.Equal("Team sync", (string)bob["recurring"]![0]!["subject"]!);
        Assert.Equal(4, bob["recurring"]![0]!["lost"]!.AsArray().Count);
        Assert.Single(bob["recurring"]!.AsArray());   // the ended one still isn't
    }

    [Fact]
    public void Past_the_wider_rings_gap_with_nothing_booked_is_flagged_with_why()
    {
        var json = Json();
        var cara = Person(json, "Cara Lee");
        Assert.Equal(51, (int)cara["daysSince"]!);
        Assert.True((bool)cara["flagged"]!);
        Assert.Equal("Head of Delivery: plans and architecture in step", (string)cara["why"]!);

        var dev = Person(json, "Dev Rana");
        Assert.False((bool)dev["flagged"]!);
        Assert.Equal("2026-12-01T11:00:00", (string)dev["next"]!["start"]!);   // tentative still counts as booked
    }

    [Fact]
    public void The_table_marks_the_flagged_and_says_why()
    {
        var (code, output, error) = Run(Settings, "contacts");
        Assert.True(code == 0, error);
        Assert.Contains("core  (flagged after 10 days, or 2 of a recurring meeting's last 4 lost)", output);
        Assert.Contains("! Cara Lee   last Wed 30 Sep (7 weeks)", output);
        Assert.Contains("nothing booked", output);
        Assert.Contains("why: Head of Delivery: plans and architecture in step", output);
        Assert.Contains("\"Ann / Pat 1:1\" (weekly): 2 of the last 4 lost (03 Nov, 10 Nov)", output);
        Assert.Contains("  Dev Rana   last Thu 05 Nov (2 weeks)", output);   // 15 days: whole weeks after a fortnight
    }

    [Fact]
    public void Flagged_and_ring_narrow_the_list()
    {
        var wider = Json("--ring", "wider", "--flagged");
        Assert.Equal(["Cara Lee"], wider["rings"]!.AsArray().SelectMany(r => r!["people"]!.AsArray()).Select(p => (string)p!["name"]!));
        var (code, _, error) = Run(Settings, "contacts", "--ring", "nobody");
        Assert.Equal(2, code);
        Assert.Contains("There's no ring 'nobody'", error);
    }

    [Fact]
    public void People_named_outside_the_rings_are_shown_but_never_flagged()
    {
        var json = Json("eve@example.com", "Cara Lee");
        var rings = json["rings"]!.AsArray();
        Assert.Equal(["wider", "asked"], rings.Select(r => (string)r!["name"]!));
        var eve = rings[1]!["people"]![0]!;
        Assert.Equal(18, (int)eve["daysSince"]!);
        Assert.False((bool)eve["flagged"]!);
    }

    [Fact]
    public void Settings_are_read_strictly()
    {
        var (code, _, error) = Run(Settings.Replace("max_attendees = 6", "max_people = 6"), "contacts");
        Assert.Equal(2, code);
        Assert.Contains("unknown setting 'max_people' in [contacts]", error);

        (code, _, error) = Run(Settings.Replace("gap_days = 42\n", ""), "contacts");
        Assert.Equal(2, code);
        Assert.Contains("contacts.ring 'wider' needs gap_days", error);

        (code, _, error) = Run(Settings.Replace("gap_days = 42", "gap_days = 400"), "contacts");
        Assert.Equal(2, code);

        (code, _, error) = Run("time_zone = \"GMT Standard Time\"\n", "contacts");
        Assert.Equal(2, code);
        Assert.Contains("no [[contacts.ring]]", error);

        (code, _, error) = Run(Settings, "contacts", "--days", "7");
        Assert.Equal(2, code);
    }

    [Fact]
    public void Recurrence_patterns_give_the_dates_they_are_due()
    {
        var fortnightly = Weekly("F", new DateTime(2026, 10, 6), DayOfWeek.Tuesday, TimeSpan.Zero, interval: 2);
        Assert.Equal([new DateTime(2026, 10, 6), new DateTime(2026, 10, 20), new DateTime(2026, 11, 3), new DateTime(2026, 11, 17)],
            Recurrences.Dates(fortnightly, new DateTime(2026, 10, 1), new DateTime(2026, 11, 20)));
        Assert.Equal("fortnightly", Recurrences.Describe(fortnightly));

        var lastFriday = new Recurrence("L", "relativeMonthly", 1, [DayOfWeek.Friday], 0, 0, "last", DayOfWeek.Sunday,
            new DateTime(2026, 1, 1), null, null, TimeSpan.Zero);
        Assert.Equal([new DateTime(2026, 9, 25), new DateTime(2026, 10, 30)],
            Recurrences.Dates(lastFriday, new DateTime(2026, 9, 1), new DateTime(2026, 11, 20)));

        var numbered = new Recurrence("N", "daily", 1, [], 0, 0, "first", DayOfWeek.Sunday,
            new DateTime(2026, 11, 1), null, 3, TimeSpan.Zero);
        Assert.Equal(3, Recurrences.Dates(numbered, new DateTime(2026, 10, 1), new DateTime(2026, 11, 20)).Count());

        var ended = fortnightly with { EndDate = new DateTime(2026, 10, 25) };
        Assert.Equal(2, Recurrences.Dates(ended, new DateTime(2026, 10, 1), new DateTime(2026, 11, 20)).Count());

        var monthly = new Recurrence("M", "absoluteMonthly", 1, [], 31, 0, "first", DayOfWeek.Sunday,
            new DateTime(2026, 8, 31), null, null, TimeSpan.Zero);
        Assert.Contains(new DateTime(2026, 9, 30), Recurrences.Dates(monthly, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)));
    }

    [Fact]
    public void A_series_master_is_read_from_graphs_recurrence()
    {
        var master = JsonNode.Parse("""
            {"subject": "Weekly", "start": {"dateTime": "2026-09-01T10:00:00.0000000", "timeZone": "GMT Standard Time"},
             "recurrence": {"pattern": {"type": "weekly", "interval": 2, "month": 0, "dayOfMonth": 0, "daysOfWeek": ["tuesday"],
                                        "firstDayOfWeek": "monday", "index": "first"},
                            "range": {"type": "endDate", "startDate": "2026-09-01", "endDate": "2026-12-31", "numberOfOccurrences": 0}}}
            """)!.AsObject();
        var r = GraphSource.ReadRecurrence(master)!;
        Assert.Equal(("weekly", 2, DayOfWeek.Tuesday, DayOfWeek.Monday), (r.Type, r.Interval, r.DaysOfWeek[0], r.FirstDayOfWeek));
        Assert.Equal(new DateTime(2026, 12, 31), r.EndDate);
        Assert.Equal(new TimeSpan(10, 0, 0), r.StartTime);

        var e = GraphSource.ReadEvent(JsonNode.Parse("""
            {"subject": "x", "start": {"dateTime": "2026-11-11T14:00:00"}, "end": {"dateTime": "2026-11-11T14:30:00"},
             "type": "exception", "seriesMasterId": "AAA", "originalStart": "2026-07-14T09:00:00Z"}
            """)!.AsObject(), TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"));
        Assert.Equal(("exception", "AAA"), (e.Type, e.SeriesMasterId));
        Assert.Equal(new DateTime(2026, 7, 14, 10, 0, 0), e.OriginalStart);   // UTC to BST
    }
}
