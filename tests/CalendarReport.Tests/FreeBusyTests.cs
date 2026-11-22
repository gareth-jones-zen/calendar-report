using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

/// <summary>0.8.0, from the first live trial of `slots`: a calendar refused in full is read through free/busy
/// (titles only); a 15-minute grid, with adjacent identical starts collapsed into one; --topic lists related meetings.</summary>
public sealed class FreeBusyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-freebusy-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();
    private static readonly DateTime Now = new(2026, 11, 22, 18, 0, 0);

    private const string Ann = "ann@example.com";
    private const string Pat = "pat@example.com";

    public FreeBusyTests()
    {
        Directory.CreateDirectory(dir);
        source.People[Pat] = new Person(Pat, "Pat North");
        source.Calendars["me"] = [];
        source.Calendars[Ann] = [];
        source.Refused.Add(Pat);
        source.FreeBusyCalendars[Pat] =
        [
            FakeSource.Item("2026-11-26T10:00", "2026-11-26T11:00", "ExCo weekly catch up"),
            FakeSource.Item("2026-11-26T13:00", "2026-11-26T13:30", "Partner call", "tentative"),
            FakeSource.Item("2026-11-26T15:00", "2026-11-26T16:00", ""),
            FakeSource.Item("2026-11-27T09:00", "2026-11-27T09:30", "Pricing sync"),
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

    private Config Settings(string text = "")
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n" + text);
        return Config.Load(path, _ => null);
    }

    private SlotsReport Build(Config config, string[] people, int minutes = 30, string day = "2026-11-26", string? topic = null,
        string until = "")
    {
        var everyone = new List<Person> { source.Resolve("me") };
        everyone.AddRange(people.Select(source.Resolve));
        var request = new SlotRequest(everyone, [], minutes, DateTime.Parse(day), DateTime.Parse(day),
            DateTime.Parse(until.Length > 0 ? until : day), "either", 100, topic);
        return Slots.Build(config, source, request, Now);
    }

    private static SlotCandidate Covering(SlotsReport report, string start) =>
        report.Candidates.SingleOrDefault(c => c.Start <= DateTime.Parse(start) && (c.LatestStart ?? c.Start) >= DateTime.Parse(start))
        ?? throw new Xunit.Sdk.XunitException($"no candidate covers {start}: {string.Join(", ", report.Candidates.Select(c => $"{c.Start:HH:mm}-{c.LatestStart:HH:mm}"))}");

    private const string Wide = "[slots]\nper_day = 50\n";

    [Fact]
    public void A_refused_calendar_is_read_through_free_busy_as_titles_only()
    {
        var report = Build(Settings(Wide), [Pat]);
        Assert.Contains(source.Asked, a => a.StartsWith("freebusy pat@example.com", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("Pat North: titles only (free/busy)", StringComparison.Ordinal));
        Assert.Equal(["Pat North"], report.TitlesOnly);
        var pat = Covering(report, "2026-11-26T13:00").People.Single(p => p.Person.Address == Pat);
        Assert.True(pat.Readable);
        Assert.True(pat.TitlesOnly);
        var item = Assert.Single(pat.Items);
        Assert.Equal(("Partner call", "soft", "shown as tentative (titles only)"), (item.Subject, item.Label, item.Why));
    }

    [Fact]
    public void A_busy_titles_only_meeting_is_fixed_and_one_with_no_subject_says_no_details()
    {
        var report = Build(Settings(Wide), [Pat]);
        Assert.DoesNotContain(report.Candidates, c => c.Start <= DateTime.Parse("2026-11-26T10:30") && (c.LatestStart ?? c.Start) >= DateTime.Parse("2026-11-26T10:00"));
        Assert.DoesNotContain(report.Candidates, c => c.Start <= DateTime.Parse("2026-11-26T15:30") && (c.LatestStart ?? c.Start) >= DateTime.Parse("2026-11-26T15:00"));
        Assert.True(report.RuledOut > 0);
    }

    [Fact]
    public void Rules_about_what_free_busy_cant_show_dont_apply_but_subject_rules_do()
    {
        var config = Settings(Wide + """
            [[rules.slots]]
            subject = '\bExCo\b'
            label = "soft"
            why = "test: ExCo by subject"
            [[rules.slots]]
            own_block = true
            label = "movable"
            why = "own block"
            [[rules.slots]]
            response = "tentative"
            label = "movable"
            why = "tentative response"
            """);
        var report = Build(config, [Pat]);
        var exco = Covering(report, "2026-11-26T10:00").People.Single(p => p.Person.Address == Pat).Items.Single();
        Assert.Equal(("soft", "test: ExCo by subject"), (exco.Label, exco.Why));
        // Without the guard a free/busy item (no organiser, no one else) would look like Pat's own block.
        var partner = Covering(report, "2026-11-26T13:00").People.Single(p => p.Person.Address == Pat).Items.Single();
        Assert.Equal(("soft", "shown as tentative (titles only)"), (partner.Label, partner.Why));
    }

    [Fact]
    public void Someone_refused_both_ways_is_not_visible_and_the_note_says_so()
    {
        source.FreeBusyCalendars.Remove(Pat);
        var report = Build(Settings(Wide), [Pat]);
        Assert.Contains(report.Notes, n => n.Contains("neither their calendar nor their free/busy can be read", StringComparison.Ordinal));
        Assert.All(report.Candidates, c => Assert.False(c.People.Single(p => p.Person.Address == Pat).Readable));
        Assert.Contains("not visible (neither calendar nor free/busy)", Slots.Table(report), StringComparison.Ordinal);
    }

    [Fact]
    public void The_fifteen_minute_grid_finds_a_slot_that_starts_at_a_quarter_past()
    {
        source.Calendars[Ann] =
        [
            new(DateTime.Parse("2026-11-26T09:00"), DateTime.Parse("2026-11-26T13:15"), false, "busy", "Workshop", null, "dee", 3, true,
                "normal", [], false, "accepted", [new Attendee("Ann", Ann, "required", "accepted")], "dee@example.com"),
            new(DateTime.Parse("2026-11-26T14:00"), DateTime.Parse("2026-11-26T17:00"), false, "busy", "Offsite", null, "dee", 3, true,
                "normal", [], false, "accepted", [new Attendee("Ann", Ann, "required", "accepted")], "dee@example.com"),
        ];
        var report = Build(Settings(Wide), [Ann], minutes: 45);
        var c = Assert.Single(report.Candidates);
        Assert.Equal(DateTime.Parse("2026-11-26T13:15"), c.Start);
        Assert.All(c.People, p => Assert.True(p.Free));

        var thirty = Build(Settings(Wide + "step_minutes = 30\n"), [Ann], minutes: 45);
        Assert.Empty(thirty.Candidates);
    }

    [Fact]
    public void Adjacent_starts_with_the_same_things_in_the_way_become_one_candidate()
    {
        var report = Build(Settings(Wide), [Ann], minutes: 60, day: "2026-11-27");
        var free = Assert.Single(report.Candidates);
        Assert.Equal((DateTime.Parse("2026-11-27T09:00"), DateTime.Parse("2026-11-27T16:00")), (free.Start, free.LatestStart!.Value));
        Assert.Contains("(or start up to 16:00)", Slots.Table(report), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_splits_where_what_is_in_the_way_changes()
    {
        var report = Build(Settings(Wide), [Pat], minutes: 30, day: "2026-11-27");
        // 09:00 and 09:15 overlap Pat's 09:00-09:30 (fixed: ruled out); from 09:30 Pat is free all day.
        var first = report.Candidates.OrderBy(c => c.Start).First();
        Assert.Equal(DateTime.Parse("2026-11-27T09:30"), first.Start);
        Assert.Equal(DateTime.Parse("2026-11-27T16:30"), first.LatestStart);
        Assert.Equal(2, report.RuledOut);
    }

    [Fact]
    public void Topic_lists_related_meetings_already_in_anyones_calendar()
    {
        source.Calendars[Ann] =
        [
            new(DateTime.Parse("2026-11-25T13:30"), DateTime.Parse("2026-11-25T14:00"), false, "tentative",
                "ITS Availability in Fibre Hub with link to LLQT to get quote", null, "Martin Smith", 5, true, "normal", [], false, "notResponded",
                [new Attendee("Ann", Ann, "required", "none")], "martin.smith@example.com"),
            FakeSource.Meeting("2026-11-26T09:00", "2026-11-26T09:30", "Kits review") with { Attendees = 2 },
        ];
        source.FreeBusyCalendars[Pat].Add(FakeSource.Item("2026-11-26T16:00", "2026-11-26T16:30", "Fibre Hub ITS follow-up"));
        var report = Build(Settings(Wide), [Ann, Pat], day: "2026-11-25", until: "2026-11-27", topic: "ITS in FibreHub");
        Assert.NotNull(report.Related);
        Assert.Equal(["ITS Availability in Fibre Hub with link to LLQT to get quote", "Fibre Hub ITS follow-up"],
            report.Related!.Select(m => m.Subject));
        var its = report.Related![0];
        Assert.Equal(("Martin Smith", "Ann Smith"), (its.Organizer, Assert.Single(its.People)));
        Assert.Null(report.Related![1].Organizer);   // free/busy doesn't give the organiser
        Assert.DoesNotContain(report.Related!, m => m.Subject == "Kits review");
        var json = JsonNode.Parse(Slots.Json(report))!.AsObject();
        Assert.Equal("ITS in FibreHub", json["topic"]!.GetValue<string>());
        Assert.Equal(2, json["related"]!.AsArray().Count);
        Assert.Contains("Related meetings (\"ITS in FibreHub\")", Slots.Table(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Topic_words_drop_stopwords_but_keep_acronyms()
    {
        Assert.Equal(["its", "fibrehub"], Slots.TopicWords("ITS in FibreHub"));
        Assert.Equal(["status"], Slots.TopicWords("discuss its status"));
        Assert.True(Slots.SharesTopic("ITS Availability in Fibre Hub", ["its", "fibrehub"]));
        Assert.False(Slots.SharesTopic("Kits review", ["its"]));
    }

    [Fact]
    public void The_json_says_who_is_titles_only_and_the_latest_start()
    {
        var report = Build(Settings(Wide), [Pat], minutes: 30, day: "2026-11-27");
        var json = JsonNode.Parse(Slots.Json(report))!.AsObject();
        Assert.Equal("Pat North", json["titlesOnly"]![0]!.GetValue<string>());
        var candidate = json["candidates"]![0]!.AsObject();
        Assert.Equal("2026-11-27T16:30", candidate["latestStart"]!.GetValue<string>());
        Assert.True(candidate["people"]!.AsArray().Single(p => p!["address"]!.GetValue<string>() == Pat)!["titlesOnly"]!.GetValue<bool>());
    }

    [Fact]
    public void Events_shows_a_refused_calendar_through_free_busy()
    {
        var report = Reports.Read([source.Resolve(Pat)], source, DateTime.Parse("2026-11-26"), DateTime.Parse("2026-11-27"),
            "GMT Standard Time", events: true, workPlan: false, cancelled: false);
        var pat = Assert.Single(report.People);
        Assert.True(pat.TitlesOnly);
        Assert.Equal(3, pat.Events!.Count);
        var table = Reports.Table(report);
        Assert.Contains("titles only (free/busy)", table, StringComparison.Ordinal);
        Assert.Contains("ExCo weekly catch up", table, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schedule_item_reads_as_a_free_busy_event()
    {
        var item = JsonNode.Parse("""
            {"isPrivate": true, "status": "tentative", "subject": "Private Appointment", "location": "MTG Room 2.B",
             "start": {"dateTime": "2026-11-26T12:00:00.0000000", "timeZone": "GMT Standard Time"},
             "end": {"dateTime": "2026-11-26T13:00:00.0000000", "timeZone": "GMT Standard Time"}}
            """)!.AsObject();
        var e = GraphSource.ReadScheduleItem(item);
        Assert.True(e.FreeBusy);
        Assert.Equal(("tentative", "Private Appointment", "MTG Room 2.B", "private"), (e.ShowAs, e.Subject, e.Location, e.Sensitivity));
        Assert.Equal((DateTime.Parse("2026-11-26T12:00"), DateTime.Parse("2026-11-26T13:00"), false), (e.Start, e.End, e.AllDay));
        Assert.Null(e.Organizer);
        Assert.Null(e.Invitees);
    }
}
