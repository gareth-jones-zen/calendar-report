using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

/// <summary>A source with fixed people, events and work plans. Only "me" has a work plan, as with Graph.</summary>
internal sealed class FakeSource : ISource
{
    public Dictionary<string, Person> People { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["me"] = new("me", "Alex Morgan"),
        ["ann@example.com"] = new("ann@example.com", "Ann Smith"),
        ["Ann Smith"] = new("ann@example.com", "Ann Smith"),
        ["bob@example.com"] = new("bob@example.com", "Bob Jones"),
    };

    public Dictionary<string, List<Event>> Calendars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WorkSegment> MyPlan { get; } = [];
    public List<string> Asked { get; } = [];

    public Person Resolve(string who) =>
        People.TryGetValue(who, out var p) ? p : throw new UsageException($"No one in the directory is called '{who}'.");

    /// <summary>People whose calendar itself is refused (403), as Graph does when it isn't shared in full.</summary>
    public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What free/busy (getSchedule) gives for someone; anyone not here is refused there too.</summary>
    public Dictionary<string, List<Event>> FreeBusyCalendars { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Event> Events(Person person, DateTime from, DateTime to)
    {
        Asked.Add($"events {person.Address} {from:yyyy-MM-dd} {to:yyyy-MM-dd}");
        if (Refused.Contains(person.Address))
            throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.Forbidden, "GET", "/users/x/calendarView", "ErrorAccessDenied");
        return Calendars.GetValueOrDefault(person.Address, []).Where(e => e.Start < to && e.End > from).ToList();
    }

    public IReadOnlyList<Event> FreeBusy(Person person, DateTime from, DateTime to)
    {
        Asked.Add($"freebusy {person.Address} {from:yyyy-MM-dd} {to:yyyy-MM-dd}");
        if (!FreeBusyCalendars.TryGetValue(person.Address, out var events))
            throw new M365Graph.GraphRequestException(System.Net.HttpStatusCode.Forbidden, "POST", "/me/calendar/getSchedule", "refused");
        return events.Where(e => e.Start < to && e.End > from).Select(e => e with { FreeBusy = true }).ToList();
    }

    /// <summary>A free/busy item: times, show-as and subject (empty when not shown), as getSchedule gives it.</summary>
    public static Event Item(string start, string end, string subject, string showAs = "busy", bool isPrivate = false) =>
        new(DateTime.Parse(start), DateTime.Parse(end), false, showAs, subject, null, null, 0, false,
            isPrivate ? "private" : "normal", [], false, null, null, null, FreeBusy: true);

    public IReadOnlyList<WorkSegment>? WorkPlan(Person person, DateTime from, DateTime to) =>
        person.Address == "me" ? MyPlan.Where(s => s.Start < to && s.End > from).ToList() : null;

    public Dictionary<string, string> Managers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string MyAddress() => "alex@example.com";
    public string? Manager(Person person) => Managers.GetValueOrDefault(person.Address);
    public string? JobTitle(string address) => Titles.GetValueOrDefault(address);

    /// <summary>Recurring meetings' patterns, by series master id.</summary>
    public Dictionary<string, Recurrence> Masters { get; } = [];
    public Recurrence? Series(Person person, string seriesMasterId) => Masters.GetValueOrDefault(seriesMasterId);

    public static Event Meeting(string start, string end, string subject, int attendees = 3, bool online = true,
        string showAs = "busy", bool cancelled = false, string? response = "accepted", bool allDay = false) =>
        new(DateTime.Parse(start), DateTime.Parse(end), allDay, showAs, subject, null, "Organiser", attendees, online, "normal", [],
            cancelled, response);
}

public sealed class ReportTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();

    public ReportTests()
    {
        Directory.CreateDirectory(dir);
        source.Calendars["me"] = [
            FakeSource.Meeting("2026-11-17T09:00", "2026-11-17T09:30", "Stand-up", attendees: 8),
            FakeSource.Meeting("2026-11-17T00:00", "2026-11-18T00:00", "Deadline", attendees: 0, online: false, showAs: "free", allDay: true),
            FakeSource.Meeting("2026-11-18T14:00", "2026-11-18T15:00", "Gone", cancelled: true),
        ];
        source.Calendars["ann@example.com"] = [
            FakeSource.Meeting("2026-11-17T10:00", "2026-11-17T11:00", "Review, \"part 2\"", response: "declined"),
        ];
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-17T09:00"), DateTime.Parse("2026-11-17T17:00"), "office"));
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-18T09:00"), DateTime.Parse("2026-11-18T12:00"), "remote"));
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-18T13:00"), DateTime.Parse("2026-11-18T17:00"), "timeOff"));
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
        File.WriteAllText(path, text);
        return Config.Load(path);
    }

    private (int Code, string Out, string Err) Run(string settings, params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, settings);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Source = _ => source, Today = () => new DateTime(2026, 11, 17), Env = _ => null,
        });
        return (code, output.ToString(), errors.ToString());
    }

    private const string Groups = "time_zone = \"GMT Standard Time\"\n[groups]\nteam = [\"Ann Smith\", \"bob@example.com\"]\n";

    [Fact]
    public void Groups_expand_names_resolve_and_each_person_is_listed_once()
    {
        var people = Reports.People(["team,ann@example.com", "me"], Settings(Groups), source);
        Assert.Equal(["ann@example.com", "bob@example.com", "me"], people.Select(p => p.Address));
        Assert.Equal("me", Reports.People([], Settings(Groups), source).Single().Address);
    }

    [Fact]
    public void Events_with_the_work_plan_as_a_table()
    {
        var (code, output, _) = Run(Groups, "events", "me", "ann@example.com", "--days", "2", "--work-plan");
        Assert.Equal(0, code);
        Assert.Equal(
            "Alex Morgan\n" +
            "  Tue 17 Nov   office 09:00-17:00\n" +
            "    00:00-24:00  free      Deadline\n".Replace("00:00-24:00", "all day    ") +
            "    09:00-09:30  busy      Stand-up  · 8 people · online\n" +
            "  Wed 18 Nov   remote 09:00-12:00, time off 13:00-17:00\n" +
            "\n" +
            "Ann Smith <ann@example.com>\n" +
            "  (work plan: only your own work plan can be read)\n" +
            "  Tue 17 Nov\n" +
            "    10:00-11:00  busy      Review, \"part 2\"  · 3 people · online · declined\n",
            output);
        Assert.DoesNotContain("Gone", output);   // cancelled, so left out without --cancelled
    }

    [Fact]
    public void Cancelled_meetings_only_when_asked()
    {
        var (_, output, _) = Run(Groups, "events", "--days", "2", "--cancelled");
        Assert.Contains("14:00-15:00  busy      Gone  · 3 people · online · cancelled", output);
    }

    [Fact]
    public void A_work_plan_on_its_own_shows_every_day()
    {
        var (_, output, _) = Run(Groups, "work-plan", "--from", "2026-11-17", "--to", "2026-11-19");
        Assert.Equal(
            "Alex Morgan\n  Tue 17 Nov   office 09:00-17:00\n  Wed 18 Nov   remote 09:00-12:00, time off 13:00-17:00\n" +
            "  Thu 19 Nov   no work plan\n", output);
    }

    [Fact]
    public void Csv_quotes_what_needs_it_and_carries_the_days_work_plan()
    {
        var (_, output, _) = Run(Groups, "events", "me", "Ann Smith", "--days", "1", "--work-plan", "--format", "csv");
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("address,name,date,start,end,all_day,show_as,subject,", lines[0]);
        Assert.EndsWith(",work_plan", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("\"Review, \"\"part 2\"\"\"", lines[3]);
        Assert.EndsWith(",office 09:00-17:00", lines[1]);
        Assert.EndsWith(",(not readable)", lines[3]);
    }

    [Fact]
    public void Json_has_the_range_the_people_and_a_note_for_an_unreadable_work_plan()
    {
        var (_, output, _) = Run(Groups, "events", "me", "ann@example.com", "--days", "1", "--work-plan", "--format", "json");
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("2026-11-17T00:00:00", root["from"]!.GetValue<string>());
        Assert.Equal("2026-11-18T00:00:00", root["to"]!.GetValue<string>());
        Assert.Equal("GMT Standard Time", root["timeZone"]!.GetValue<string>());
        var people = root["people"]!.AsArray();
        Assert.Equal(2, people[0]!["events"]!.AsArray().Count);
        Assert.Equal("office", people[0]!["workPlan"]![0]!["location"]!.GetValue<string>());
        Assert.Null(people[1]!["workPlan"]);
        Assert.Equal("only your own work plan can be read", people[1]!["workPlanNote"]!.GetValue<string>());
    }

    [Fact]
    public void Out_writes_a_file()
    {
        var file = Path.Combine(dir, "out", "week.csv");
        var (code, output, _) = Run(Groups, "events", "--days", "1", "--format", "csv", "--out", file);
        Assert.Equal(0, code);
        Assert.Contains("Wrote", output);
        Assert.StartsWith("address,", File.ReadAllText(file).TrimStart('﻿'));
    }

    [Theory]
    [InlineData(new[] { "--days", "3" }, "2026-11-17", "2026-11-20")]
    [InlineData(new[] { "--from", "tomorrow" }, "2026-11-18", "2026-11-25")]
    [InlineData(new[] { "--from", "monday", "--to", "2026-11-20" }, "2026-11-16", "2026-11-21")]
    public void Ranges_are_from_midnight_to_midnight_after_the_last_day(string[] args, string from, string to)
    {
        var (_, _, _) = Run(Groups, ["events", .. args]);
        Assert.Equal($"events me {from} {to}", source.Asked.Single());
    }

    [Fact]
    public void Bad_arguments_and_settings_say_what_is_wrong()
    {
        Assert.Contains("isn't a date", Run(Groups, "events", "--from", "17/11/2026").Err);
        Assert.Contains("is before --from", Run(Groups, "events", "--from", "2026-11-20", "--to", "2026-11-19").Err);
        Assert.Contains("not allowed with argument --to", Run(Groups, "events", "--to", "2026-11-20", "--days", "2").Err);
        Assert.Contains("invalid choice: 'xml'", Run(Groups, "events", "--format", "xml").Err);
        Assert.Contains("No one in the directory is called 'Zed'", Run(Groups, "events", "Zed").Err);
        Assert.Contains("unknown setting 'colour'", Run("colour = \"red\"\n", "events").Err);
        Assert.Contains("isn't a time zone", Run("time_zone = \"Mars\"\n", "events").Err);
        Assert.Contains("should be a list", Run("[groups]\nteam = \"ann@example.com\"\n", "events").Err);
        Assert.Equal(2, Run(Groups, "events", "--days", "0").Code);
    }

    [Fact]
    public void Who_and_groups()
    {
        Assert.Equal("Ann Smith <ann@example.com>\nBob Jones <bob@example.com>\n", Run(Groups, "who", "team").Out);
        Assert.Equal("team: Ann Smith, bob@example.com\n", Run(Groups, "groups").Out);
    }

    [Fact]
    public void A_graph_event_reads_into_the_record()
    {
        var e = GraphSource.ReadEvent(JsonNode.Parse("""
            {"subject":"Planning","start":{"dateTime":"2026-11-17T10:00:00.0000000","timeZone":"GMT Standard Time"},
             "end":{"dateTime":"2026-11-17T11:30:00.0000000","timeZone":"GMT Standard Time"},"isAllDay":false,"showAs":"tentative",
             "location":{"displayName":"Room 2"},"organizer":{"emailAddress":{"name":"Ann Smith","address":"ann@example.com"}},
             "attendees":[{},{},{}],"isOnlineMeeting":true,"sensitivity":"private","categories":["Planned"],"isCancelled":false,
             "responseStatus":{"response":"tentativelyAccepted"}}
            """)!.AsObject());
        Assert.Equal(new DateTime(2026, 11, 17, 10, 0, 0), e.Start);
        Assert.Equal(new DateTime(2026, 11, 17, 11, 30, 0), e.End);
        Assert.Equal(("tentative", "Planning", "Room 2", "Ann Smith", 3, true, "private"),
            (e.ShowAs, e.Subject, e.Location, e.Organizer, e.Attendees, e.Online, e.Sensitivity));
        Assert.Equal(["Planned"], e.Categories);
        Assert.Equal("tentativelyAccepted", e.Response);
        Assert.Equal("ann@example.com", e.OrganizerAddress);
        Assert.All(e.Invitees!, a => Assert.Equal(new Attendee(null, null, "required", null), a));   // Graph left them bare
    }

    [Fact]
    public void Attendees_are_read_with_who_they_are()
    {
        var e = GraphSource.ReadEvent(JsonNode.Parse("""
            {"subject":"1:1","start":{"dateTime":"2026-11-17T10:00:00"},"end":{"dateTime":"2026-11-17T10:30:00"},
             "organizer":{"emailAddress":{"name":"Ann Smith","address":"ann@example.com"}},
             "attendees":[{"type":"required","status":{"response":"accepted"},"emailAddress":{"name":"Bob Jones","address":"bob@example.com"}},
                          {"type":"resource","status":{"response":"none"},"emailAddress":{"name":"Room 2","address":"room2@example.com"}}]}
            """)!.AsObject());
        Assert.Equal(2, e.Attendees);
        Assert.Equal([new Attendee("Bob Jones", "bob@example.com", "required", "accepted"), new Attendee("Room 2", "room2@example.com", "resource", "none")],
            e.Invitees);
    }
}
