using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

/// <summary>`free` (0.12.0): someone's gaps for a quick call, with the owner's side, Teams status and the fallbacks.
/// Today is Tue 24 Nov 2026; the owner (me) is alex@example.com.</summary>
public sealed class FreeTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-free-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();
    private static readonly DateTime Today = new(2026, 11, 24);
    private static readonly DateTime OneOFive = new(2026, 11, 24, 13, 5, 37);

    private const string Me = "alex@example.com";
    private const string Ann = "ann@example.com";
    private const string Bob = "bob@example.com";

    private const string Rules = """
        [[rules.slots]]
        show_as = "free"
        count = "skip"
        [[rules.slots]]
        subject = '^\s*(lunch|travel)\b'
        count = "not-working"
        [[rules.slots]]
        own_block = true
        label = "soft"
        why = "their own blocked time"

        """;

    public FreeTests()
    {
        Directory.CreateDirectory(dir);
        source.Calendars[Ann] =
        [
            With("2026-11-24T09:00", "2026-11-24T09:30", "Stand-up", [Ann, Bob], Bob),
            Own("2026-11-24T12:00", "2026-11-24T12:45", "Lunch with Pat", Ann),
            With("2026-11-24T13:00", "2026-11-24T13:30", "Design review", [Ann, Bob], Bob),
            With("2026-11-24T14:00", "2026-11-24T15:00", "Squad planning", [Ann, Bob], Bob),
            With("2026-11-24T15:05", "2026-11-24T16:00", "Supplier call", [Ann, Bob], Bob),
            Own("2026-11-24T16:00", "2026-11-24T17:00", "Focus time", Ann),
            With("2026-11-24T16:15", "2026-11-24T16:45", "Optional demo", [Ann, Bob], Bob, response: "declined"),
            With("2026-11-25T10:00", "2026-11-25T11:00", "Wednesday thing", [Ann, Bob], Bob),
        ];
        source.Hours[Ann] = new WorkingHours(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], TimeSpan.FromHours(8.5), TimeSpan.FromHours(17.5));
        source.Calendars["me"] =
        [
            With("2026-11-24T13:00", "2026-11-24T13:55", "Owner's meeting", [Me, Bob], Me),
            Own("2026-11-24T15:30", "2026-11-24T16:00", "Prep", Me),
            With("2026-11-24T16:30", "2026-11-24T17:00", "Owner's other meeting", [Me, Bob], Bob),
        ];
        source.Calendars[Bob] =
        [
            Own("2026-11-24T00:00", "2026-11-25T00:00", "Annual leave", Bob, allDay: true, showAs: "oof"),
            Own("2026-11-25T00:00", "2026-11-26T00:00", "Annual leave", Bob, allDay: true, showAs: "oof"),
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

    private static Event With(string start, string end, string subject, string[] people, string organizer, string response = "accepted") =>
        new(DateTime.Parse(start), DateTime.Parse(end), false, "busy", subject, null, organizer.Split('@')[0], people.Length, true,
            "normal", [], false, response,
            people.Where(p => p != organizer).Select(p => new Attendee(p.Split('@')[0], p, "required", "accepted")).ToList(), organizer);

    /// <summary>Someone's own entry: no one else invited.</summary>
    private static Event Own(string start, string end, string subject, string owner, bool allDay = false, string showAs = "busy") =>
        new(DateTime.Parse(start), DateTime.Parse(end), allDay, showAs, subject, null, owner.Split('@')[0], 0, false, "normal", [], false,
            "organizer", [], owner);

    private Config Settings(string text = "")
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n" + Rules + text);
        return Config.Load(path, _ => null);
    }

    private FreeReport Build(DateTime now, string who = Ann, DateTime? day = null, bool lunch = false, int min = 10, bool presence = true,
        bool nowOnly = false, string? chat = null, Config? config = null) =>
        Free.Build(config ?? Settings(), source, new FreeRequest([source.Resolve(who)], day ?? now.Date, nowOnly, lunch, min, presence, chat), now);

    private static List<(string, string)> Spans(FreePerson p) => [.. p.Gaps.Select(g => ($"{g.Start:HH:mm}", $"{g.End:HH:mm}"))];

    [Fact]
    public void Today_lists_gaps_from_now_within_their_working_hours()
    {
        var ann = Build(OneOFive).People[0];
        Assert.Equal([("13:30", "14:00"), ("16:00", "17:30")], Spans(ann));
        Assert.True(ann.HoursFromOutlook);
        Assert.Equal(("busy", "13:30"), (ann.Now!.State, $"{ann.Now.Until:HH:mm}"));
    }

    [Fact]
    public void A_five_minute_gap_is_dropped_and_the_minimum_can_be_raised()
    {
        var ann = Build(OneOFive).People[0];
        Assert.DoesNotContain(ann.Gaps, g => g.Start == DateTime.Parse("2026-11-24T15:00"));
        Assert.Contains(Build(OneOFive, min: 5).People[0].Gaps, g => g.Start == DateTime.Parse("2026-11-24T15:00"));
        Assert.Equal([("16:00", "17:30")], Spans(Build(OneOFive, min: 45).People[0]));
    }

    [Fact]
    public void A_gap_under_way_starts_at_now()
    {
        var ann = Build(new DateTime(2026, 11, 24, 10, 0, 20)).People[0];
        Assert.Equal(("10:00", "12:00"), Spans(ann)[0]);
        Assert.Equal("free until 12:00", ann.Now!.Text);
    }

    [Fact]
    public void Another_day_lists_the_whole_working_day_with_no_status()
    {
        var report = Build(OneOFive, day: new DateTime(2026, 11, 25));
        var ann = report.People[0];
        Assert.False(report.Today);
        Assert.Equal([("08:30", "10:00"), ("11:00", "17:30")], Spans(ann));
        Assert.Null(ann.Now);
        Assert.Null(ann.Presence);
        Assert.DoesNotContain(source.Asked, a => a.StartsWith("presence", StringComparison.Ordinal));
    }

    [Fact]
    public void Before_their_day_starts_the_gaps_start_with_it()
    {
        var ann = Build(new DateTime(2026, 11, 24, 8, 0, 0)).People[0];
        Assert.Equal(("08:30", "09:00"), Spans(ann)[0]);
        Assert.Equal("before-hours", ann.Now!.State);
    }

    [Fact]
    public void Without_their_outlook_hours_the_settings_hours_are_used()
    {
        source.Hours.Clear();
        var ann = Build(new DateTime(2026, 11, 24, 8, 0, 0)).People[0];
        Assert.False(ann.HoursFromOutlook);
        Assert.Equal(("09:30", "12:00"), Spans(ann)[0]);
        Assert.Equal(("16:00", "17:00"), Spans(ann)[^1]);
    }

    [Fact]
    public void After_their_day_ends_there_are_no_gaps()
    {
        var ann = Build(new DateTime(2026, 11, 24, 17, 40, 0)).People[0];
        Assert.Empty(ann.Gaps);
        Assert.Equal("after-hours", ann.Now!.State);
    }

    [Fact]
    public void A_day_outside_their_working_days_says_so()
    {
        var ann = Build(OneOFive, day: new DateTime(2026, 11, 27)).People[0];
        Assert.Empty(ann.Gaps);
        Assert.Contains("Not one of their working days", ann.DayNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Lunch_is_away_unless_asked_for()
    {
        var now = new DateTime(2026, 11, 24, 9, 40, 0);
        Assert.Equal([("09:40", "12:00"), ("12:45", "13:00"), ("13:30", "14:00"), ("16:00", "17:30")], Spans(Build(now).People[0]));
        var withLunch = Build(now, lunch: true).People[0];
        Assert.Equal(("09:40", "13:00"), Spans(withLunch)[0]);
        Assert.True(withLunch.Gaps[0].OverLunch);
        Assert.Equal("at lunch until 12:45", Build(new DateTime(2026, 11, 24, 12, 10, 0)).People[0].Now!.Text);
    }

    [Fact]
    public void Their_own_focus_block_doesnt_close_a_gap_but_is_marked()
    {
        var gap = Build(OneOFive).People[0].Gaps[^1];
        var soft = Assert.Single(gap.Soft);
        Assert.Equal(("own-block", "16:00", "17:00"), (soft.Kind, $"{soft.Start:HH:mm}", $"{soft.End:HH:mm}"));
        var now = Build(new DateTime(2026, 11, 24, 16, 10, 0)).People[0].Now!;
        Assert.Equal("own-block", now.State);
        Assert.Contains("could be interrupted", now.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declined_meeting_isnt_in_the_way()
    {
        Assert.Equal(90, Build(OneOFive).People[0].Gaps[^1].Minutes);   // 16:00-17:30, past the declined 16:15 demo
    }

    [Fact]
    public void Each_gap_says_where_the_owner_is_free_their_own_block_counting_as_free()
    {
        var gaps = Build(OneOFive).People[0].Gaps;
        Assert.Equal("you're in something until 13:55", Free.OwnerText(gaps[0]));
        Assert.Equal("you're free 16:00-16:30, 17:00-17:30", Free.OwnerText(gaps[1]));
        var early = Build(new DateTime(2026, 11, 24, 15, 0, 0), min: 5).People[0].Gaps[0];   // 15:00-15:05; the owner's Prep at 15:30 is his own
        Assert.Equal("you're free too", Free.OwnerText(early));
    }

    [Fact]
    public void Someone_on_leave_is_said_to_be_and_when_they_are_back()
    {
        var bob = Build(OneOFive, who: Bob).People[0];
        Assert.Empty(bob.Gaps);
        Assert.Equal("On leave today (back Thu 26 Nov).", bob.DayNote);
        Assert.Equal("leave", bob.Now!.State);
    }

    [Fact]
    public void Titles_only_still_finds_gaps_and_spots_focus_time_by_subject()
    {
        source.Refused.Add(Ann);
        source.FreeBusyCalendars[Ann] =
        [
            FakeSource.Item("2026-11-24T13:00", "2026-11-24T13:30", "Design review"),
            FakeSource.Item("2026-11-24T14:00", "2026-11-24T16:00", ""),
            FakeSource.Item("2026-11-24T16:00", "2026-11-24T17:00", "Focus time"),
        ];
        var ann = Build(OneOFive).People[0];
        Assert.True(ann.TitlesOnly);
        Assert.Equal([("13:30", "14:00"), ("16:00", "17:30")], Spans(ann));
        Assert.Single(ann.Gaps[^1].Soft);
    }

    [Fact]
    public void A_calendar_that_cant_be_seen_at_all_still_gives_status_and_contact()
    {
        source.Refused.Add(Ann);
        var ann = Build(OneOFive).People[0];
        Assert.False(ann.Readable);
        Assert.Contains("Can't see their calendar", ann.Notes[0], StringComparison.Ordinal);
        Assert.NotNull(ann.CallLink);
    }

    [Fact]
    public void Presence_not_granted_says_waiting_on_it_and_the_auto_reply_comes_from_mail_tips()
    {
        source.AutoReplies[Ann] = "I'm at a conference until Thursday.";
        var report = Build(OneOFive);
        var ann = report.People[0];
        Assert.Equal("not-granted", ann.Presence!.State);
        Assert.Equal("I'm at a conference until Thursday.", ann.AutoReply);
        var table = Free.Table(report);
        Assert.Contains("Teams:    status waiting on IT", table, StringComparison.Ordinal);
        Assert.Contains("Auto-reply: on: \"I'm at a conference until Thursday.\"", table, StringComparison.Ordinal);
        Assert.Contains("13:30-14:00   30 min", table, StringComparison.Ordinal);
    }

    [Fact]
    public void Presence_granted_is_shown_with_its_message_and_its_own_out_of_office()
    {
        source.Presences[Ann] = new PresenceRead("shown",
            new PresenceInfo("Busy", "InAMeeting", "Back at 2", true, "<p>Away until Friday.</p><p>Call Bob.</p>", "remote"));
        var report = Build(OneOFive);
        var ann = report.People[0];
        Assert.Equal("Away until Friday.", ann.AutoReply);
        Assert.DoesNotContain(source.Asked, a => a.StartsWith("auto-reply", StringComparison.Ordinal));
        Assert.Contains("Teams:    shows Busy, in a meeting · \"Back at 2\" · working remotely", Free.Table(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Presence_refused_for_one_person_says_so()
    {
        source.Presences[Ann] = new PresenceRead("not-available", null, "404");
        Assert.Contains("Teams:    status not available for them", Free.Table(Build(OneOFive)), StringComparison.Ordinal);
    }

    [Fact]
    public void No_presence_is_asked_for_when_told_not_to()
    {
        Assert.Null(Build(OneOFive, presence: false).People[0].Presence);
        Assert.DoesNotContain(source.Asked, a => a.StartsWith("presence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Available", "Available", "busy", "may have ended")]
    [InlineData("Busy", "InACall", "free", "On a call now")]
    [InlineData("DoNotDisturb", "Presenting", "busy", "a chat rather than a call")]
    [InlineData("Away", "Away", "free", "Away from their desk")]
    [InlineData("Offline", "OffWork", "free", "Offline in Teams")]
    public void Status_and_calendar_together(string availability, string activity, string state, string expected)
    {
        var note = Free.PresenceNote(new PresenceInfo(availability, activity, null, false, null, null),
            new NowState(state, DateTime.Parse("2026-11-24T14:30"), ""));
        Assert.Contains(expected, note, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_values_are_read_from_either_field()
    {
        Assert.Equal("Busy, in a meeting", Free.StatusText(new PresenceInfo("Busy", "InAMeeting", null, false, null, null)));
        Assert.Equal("Busy, in a meeting", Free.StatusText(new PresenceInfo("Busy", "inameeting", null, false, null, null)));
        Assert.Equal("Available", Free.StatusText(new PresenceInfo("Available", "Available", null, false, null, null)));
        Assert.Equal("Do not disturb, presenting", Free.StatusText(new PresenceInfo("DoNotDisturb", "Presenting", null, false, null, null)));
    }

    [Fact]
    public void No_meeting_titles_are_printed()
    {
        source.Presences[Ann] = new PresenceRead("shown", new PresenceInfo("Available", "Available", null, false, null, null));
        foreach (var text in new[] { Free.Table(Build(OneOFive)), Free.Json(Build(OneOFive)), Free.Table(Build(OneOFive, nowOnly: true)) })
            foreach (var title in new[] { "Stand-up", "Design review", "Squad planning", "Supplier call", "Focus time", "Lunch with Pat", "Owner's" })
                Assert.DoesNotContain(title, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Now_shows_status_now_and_the_next_gap_only()
    {
        var table = Free.Table(Build(OneOFive, nowOnly: true));
        Assert.Contains("Now:      busy (in a meeting or busy) until 13:30", table, StringComparison.Ordinal);
        Assert.Contains("Next gap: 13:30-14:00", table, StringComparison.Ordinal);
        Assert.DoesNotContain("16:00-17:30", table, StringComparison.Ordinal);
    }

    [Fact]
    public void The_call_link_phone_and_chat_link()
    {
        source.Contacts[Ann] = new DirectoryContact("id-1", ["+44 1706 000000"], null);
        var ann = Build(OneOFive, chat: "about the IPv4 plan").People[0];
        Assert.Equal("https://teams.microsoft.com/l/call/0/0?users=ann@example.com", ann.CallLink);
        Assert.Equal("https://teams.microsoft.com/l/chat/0/0?users=ann@example.com&message="
                     + "Have%20you%20got%205%20minutes%20for%20a%20quick%20call%20about%20the%20IPv4%20plan%3F", ann.ChatLink);
        Assert.Equal([("+44 1706 000000", "work")], ann.Phones);
        Assert.Null(Build(OneOFive).People[0].ChatLink);
        Assert.Empty(Build(OneOFive, config: Settings("[free]\nshow_phone = false\n")).People[0].Phones);
    }

    [Theory]
    [InlineData("the IPv4 plan", "Have you got 5 minutes for a quick call about the IPv4 plan?")]
    [InlineData("about X?", "Have you got 5 minutes for a quick call about X?")]
    [InlineData("", "Have you got 5 minutes for a quick call?")]
    public void The_chat_message(string topic, string expected) => Assert.Equal(expected, Free.ChatMessage(topic));

    [Theory]
    [InlineData("<html><head><style>p{}</style></head><body><p>Out until Monday &amp; back Tuesday.</p><p>More</p></body></html>",
        "Out until Monday & back Tuesday.")]
    [InlineData("Plain text\r\nsecond line", "Plain text")]
    [InlineData("<div><br></div><div>  After a blank  </div>", "After a blank")]
    [InlineData("   ", null)]
    public void An_auto_replys_first_line(string message, string? expected) => Assert.Equal(expected, Free.FirstLine(message));

    [Fact]
    public void Working_hours_in_another_zone_are_converted()
    {
        var hours = JsonNode.Parse("""
            {"daysOfWeek":["monday","tuesday"],"startTime":"09:00:00.0000000","endTime":"17:00:00.0000000",
             "timeZone":{"name":"W. Europe Standard Time"}}
            """)!.AsObject();
        var read = GraphSource.ReadWorkingHours(hours, Today, TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"))!;
        Assert.Equal((TimeSpan.FromHours(8), TimeSpan.FromHours(16)), (read.Start, read.End));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], read.Days);
    }

    [Fact]
    public void A_presence_resource_is_read()
    {
        var o = JsonNode.Parse("""
            {"availability":"Away","activity":"Away","statusMessage":{"message":{"content":"Walking the dog","contentType":"text"}},
             "outOfOfficeSettings":{"message":null,"isOutOfOffice":false},"workLocation":{"workLocationType":"office"}}
            """)!.AsObject();
        Assert.Equal(new PresenceInfo("Away", "Away", "Walking the dog", false, null, "office"), GraphSource.ReadPresence(o));
    }

    [Fact]
    public void Mail_tips_auto_reply_off_is_null()
    {
        Assert.Null(GraphSource.ReadAutoReply(JsonNode.Parse("""{"automaticReplies":{"message":""}}""")!.AsObject()));
        Assert.Equal("Back soon", GraphSource.ReadAutoReply(JsonNode.Parse("""{"automaticReplies":{"message":"<p>Back soon</p>"}}""")!.AsObject()));
    }

    [Theory]
    [InlineData("[free]\nmin_minutes = 0\n", "free.min_minutes")]
    [InlineData("[free]\nminutes = 10\n", "unknown setting 'minutes' in [free]")]
    [InlineData("[free]\nshow_phone = \"yes\"\n", "free.show_phone")]
    [InlineData("[free]\nrules = \"nope\"\n", "no rule list 'nope'")]
    public void Bad_settings_are_named(string text, string message)
    {
        var error = Assert.Throws<UsageException>(() => Build(OneOFive, config: Settings(text)));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private (int Code, string Out, string Err, List<string> Log) Run(params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n" + Rules);
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var log = new List<string>();
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Source = _ => source, Today = () => Today, Now = _ => OneOFive, Env = _ => null,
            Log = _ => log.Add,
        });
        return (code, output.ToString(), errors.ToString(), log);
    }

    [Fact]
    public void The_command_prints_the_answer_and_logs_no_names()
    {
        var (code, output, error, log) = Run("free", "Ann Smith");
        Assert.True(code == 0, error);
        Assert.StartsWith("Ann Smith · Tue 24 Nov, 13:05\n", output, StringComparison.Ordinal);
        Assert.Contains("  Now:      busy (in a meeting or busy) until 13:30", output, StringComparison.Ordinal);
        Assert.Contains("  Call:     https://teams.microsoft.com/l/call/0/0?users=ann@example.com", output, StringComparison.Ordinal);
        Assert.Equal(["free: 1 named, done"], log);
    }

    [Fact]
    public void The_command_prints_json()
    {
        var (code, output, error, _) = Run("free", Ann, "--json", "--chat", "X");
        Assert.True(code == 0, error);
        var person = JsonNode.Parse(output)!["people"]![0]!.AsObject();
        foreach (var key in new[] { "now", "presence", "workingHours", "gaps", "phones", "callLink", "chatLink", "autoReply" })
            Assert.True(person.ContainsKey(key), key);
        var gap = person["gaps"]![0]!.AsObject();
        Assert.Equal(30, gap["minutes"]!.GetValue<int>());
        Assert.Equal("2026-11-24T13:55", gap["ownerFree"]![0]!["start"]!.GetValue<string>());
        Assert.Equal("not-granted", person["presence"]!["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(new[] { "free" }, "people (who you'd call)")]
    [InlineData(new[] { "free", "ann@example.com", "--now", "--day", "tomorrow" }, "--now is about today")]
    [InlineData(new[] { "free", "ann@example.com", "--days", "3" }, "free takes --day")]
    [InlineData(new[] { "free", "ann@example.com", "--day", "2026-11-20" }, "has gone")]
    [InlineData(new[] { "free", "ann@example.com", "--min", "0" }, "--min")]
    public void Bad_requests_are_explained(string[] args, string message)
    {
        var (code, _, error, _) = Run(args);
        Assert.Equal(2, code);
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Sign_in_for_presence_is_its_own_request_and_says_what_to_do_when_refused()
    {
        var asked = new List<string>();
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n");
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", path, "sign-in", "--presence"], new CliHost
        {
            Out = output, Err = errors, Env = _ => null,
            SignIn = _ => asked.Add("usual"),
            SignInPresence = c => throw new M365Graph.SignInNeededException("Sign-in failed: AADSTS90094: admin approval required"),
        });
        Assert.Equal(2, code);
        Assert.Empty(asked);   // the usual sign-in isn't touched
        Assert.Contains("Approval required", errors.ToString(), StringComparison.Ordinal);
    }
}
