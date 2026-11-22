using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

/// <summary>`slots` (0.7.0): candidate times for a meeting, and what's in the way for each person, labelled fixed,
/// soft or movable. The owner (me) is alex@example.com; Ann and Cat are essential, Bob optional.</summary>
public sealed class SlotsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-slots-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();
    private static readonly DateTime Today = new(2026, 11, 22);   // a Sunday
    private static readonly DateTime Now = new(2026, 11, 22, 18, 0, 0);

    private const string Me = "alex@example.com";
    private const string Ann = "ann@example.com";
    private const string Bob = "bob@example.com";
    private const string Cat = "cat@example.com";

    public SlotsTests()
    {
        Directory.CreateDirectory(dir);
        source.People[Cat] = new Person(Cat, "Cat Lee");
        source.People["Cat Lee"] = new Person(Cat, "Cat Lee");
        source.Calendars["me"] =
        [
            With("2026-11-23T15:00", "2026-11-23T16:00", "Architecture team meeting", [Ann, Bob], Me),
            Holiday("2026-11-25", "Bank holiday (England and Wales)", "Bank holiday"),
        ];
        source.Calendars[Ann] =
        [
            With("2026-11-23T10:00", "2026-11-23T10:30", "Ann / Alex 1:1", [Ann], Me),
            With("2026-11-23T11:00", "2026-11-23T11:30", "Design chat", [Ann, "dee@example.com"], "dee@example.com", response: "tentativelyAccepted", showAs: "tentative"),
            With("2026-11-23T14:00", "2026-11-23T15:00", "Squad Pi sprint review", [Ann, "dee@example.com", "eve@example.com"], "dee@example.com"),
            With("2026-11-24T09:00", "2026-11-24T12:00", "Supplier workshop", [Ann, "sam@supplier.co.uk"], "sam@supplier.co.uk"),
        ];
        source.Calendars[Bob] =
        [
            With("2026-11-24T13:00", "2026-11-24T14:00", "Customer call", [Bob, "kim@customer.com"], "kim@customer.com"),
        ];
        source.Calendars[Cat] =
        [
            Hidden("2026-11-23T09:00", "2026-11-23T09:30", "tentative"),
            Hidden("2026-11-23T16:00", "2026-11-23T17:00", "busy"),
        ];
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-23T09:00"), DateTime.Parse("2026-11-23T17:00"), "office"));
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-24T09:00"), DateTime.Parse("2026-11-24T17:00"), "remote"));
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

    /// <summary>A meeting, organised by <paramref name="organizer"/>, with these people invited (the organiser not among them).</summary>
    private static Event With(string start, string end, string subject, string[] people, string organizer,
        string response = "accepted", string showAs = "busy") =>
        new(DateTime.Parse(start), DateTime.Parse(end), false, showAs, subject, null, organizer.Split('@')[0],
            people.Length, true, "normal", [], false, response,
            people.Where(p => p != organizer).Select(p => new Attendee(p.Split('@')[0], p, "required", "accepted")).ToList(), organizer);

    private static Event Holiday(string day, string subject, string category) =>
        new(DateTime.Parse(day), DateTime.Parse(day).AddDays(1), true, "free", subject, null, null, 0, false, "normal", [category], false,
            "organizer", [], null);

    /// <summary>A block whose details can't be seen: no subject, only its show-as.</summary>
    private static Event Hidden(string start, string end, string showAs) =>
        new(DateTime.Parse(start), DateTime.Parse(end), false, showAs, "", null, null, 0, false, "normal", [], false, null, [], null);

    private Config Settings(string text = "")
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n" + text);
        return Config.Load(path, _ => null);
    }

    private SlotsReport Build(Config config, int minutes = 30, bool cat = false, string by = "2026-11-23", string until = "2026-11-25")
    {
        var people = new List<Person> { source.Resolve("me"), source.Resolve(Ann) };
        if (cat)
            people.Add(source.Resolve(Cat));
        var request = new SlotRequest(people, [source.Resolve(Bob)], minutes, Today.AddDays(1), DateTime.Parse(by), DateTime.Parse(until),
            "either", 100);
        return Slots.Build(config, source, request, Now);
    }

    private static SlotCandidate At(SlotsReport report, string start) =>
        report.Candidates.SingleOrDefault(c => c.Start == DateTime.Parse(start))
        ?? throw new Xunit.Sdk.XunitException($"no candidate at {start}: {string.Join(", ", report.Candidates.Select(c => c.Start.ToString("ddd HH:mm")))}");

    private static SlotPerson Who(SlotCandidate c, string address) =>
        c.People.Single(p => p.Person.Address == address || (address == Me && p.Person.Address == "me"));

    private const string AllOfThem = "[slots]\nper_day = 50\n";

    [Fact]
    public void A_one_to_one_with_the_owner_is_movable()
    {
        var item = Assert.Single(Who(At(Build(Settings(AllOfThem)), "2026-11-23T10:00"), Ann).Items);
        Assert.Equal("movable", item.Label);
        Assert.Equal("a 1:1 with you", item.Why);
    }

    [Fact]
    public void The_owners_own_team_meeting_is_movable()
    {
        var item = Assert.Single(Who(At(Build(Settings(AllOfThem)), "2026-11-23T15:00"), Me).Items);
        Assert.Equal(("movable", "you organised it"), (item.Label, item.Why));
    }

    [Fact]
    public void A_sprint_review_and_a_tentative_meeting_are_soft()
    {
        var report = Build(Settings(AllOfThem));
        Assert.Equal(("soft", "a sprint ceremony"), (Who(At(report, "2026-11-23T14:00"), Ann).Items[0].Label, Who(At(report, "2026-11-23T14:00"), Ann).Items[0].Why));
        Assert.Equal(("soft", "they're tentative"), (Who(At(report, "2026-11-23T11:00"), Ann).Items[0].Label, Who(At(report, "2026-11-23T11:00"), Ann).Items[0].Why));
    }

    [Fact]
    public void An_external_meeting_for_someone_essential_rules_the_time_out_but_not_for_someone_optional()
    {
        var report = Build(Settings(AllOfThem));
        Assert.DoesNotContain(report.Candidates, c => c.Start.Date == new DateTime(2026, 11, 24) && c.Start.Hour < 12);
        Assert.True(report.RuledOut >= 6);
        var bob = Who(At(report, "2026-11-24T13:00"), Bob);
        Assert.Equal(("fixed", "with people outside the organisation"), (bob.Items[0].Label, bob.Items[0].Why));
        Assert.False(bob.Essential);
    }

    [Fact]
    public void A_bank_holiday_has_no_candidates_and_says_so()
    {
        var report = Build(Settings(AllOfThem));
        Assert.DoesNotContain(report.Candidates, c => c.Start.Date == new DateTime(2026, 11, 25));
        Assert.Contains(report.Notes, n => n.Contains("Wed 25 Nov (bank holiday)", StringComparison.Ordinal));
    }

    [Fact]
    public void Times_past_the_by_date_are_marked()
    {
        var report = Build(Settings(AllOfThem));
        Assert.True(At(report, "2026-11-24T13:00").PastBy);
        Assert.False(At(report, "2026-11-23T09:00").PastBy);
    }

    [Fact]
    public void A_person_whose_details_cant_be_seen_is_labelled_from_show_as()
    {
        var report = Build(Settings(AllOfThem), cat: true);
        var cat = Who(At(report, "2026-11-23T09:00"), Cat);
        Assert.Equal(("soft", false), (cat.Items[0].Label, cat.Items[0].DetailsVisible));
        Assert.Contains("no details visible", cat.Items[0].Why, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Candidates, c => c.Start == DateTime.Parse("2026-11-23T16:00"));   // busy, hidden: fixed
    }

    [Fact]
    public void The_best_comes_first_free_inside_by_and_lunch_counts_as_soft()
    {
        var report = Build(Settings(AllOfThem + "lunch = \"12:00-13:30\"\n"));
        var first = report.Candidates[0];
        Assert.All(first.People.Where(p => p.Essential), p => Assert.True(p.Free));
        Assert.False(first.PastBy);
        Assert.False(first.Lunch);
        Assert.True(At(report, "2026-11-23T12:00").Lunch);
        Assert.Equal("office", first.OwnerLocation);
    }

    [Fact]
    public void Per_day_spreads_the_candidates()
    {
        var report = Build(Settings("[slots]\nper_day = 2\n"));
        Assert.All(report.Candidates.GroupBy(c => c.Start.Date), g => Assert.True(g.Count() <= 2));
    }

    [Fact]
    public void A_rule_list_in_the_settings_replaces_the_built_in_one()
    {
        var config = Settings("""
            [[rules.slots]]
            subject = 'sprint review'
            label = "fixed"
            why = "Ann's squad needs her there"
            """);
        var report = Build(config);
        Assert.DoesNotContain(report.Candidates, c => c.Start == DateTime.Parse("2026-11-23T14:00"));
        // No 1:1 rule in this list: Ann's 1:1 is someone else's meeting, fixed, so 10:00 is ruled out too.
        Assert.DoesNotContain(report.Candidates, c => c.Start == DateTime.Parse("2026-11-23T10:00"));
        Assert.Contains(report.Candidates, c => c.Start == DateTime.Parse("2026-11-23T09:00"));
    }

    [Fact]
    public void The_new_rule_tests_work()
    {
        var config = Settings("""
            [slots]
            per_day = 50
            [[rules.slots]]
            owner_attends = true
            recurring = false
            label = "movable"
            why = "you're in it"
            [[rules.slots]]
            external = true
            response = ["accepted", "none"]
            label = "fixed"
            why = "outside"
            """);
        var report = Build(config);
        Assert.Equal("you're in it", Who(At(report, "2026-11-23T10:00"), Ann).Items[0].Why);
        Assert.Equal("outside", Who(At(report, "2026-11-24T13:00"), Bob).Items[0].Why);
    }

    [Fact]
    public void Org_domain_decides_whats_external()
    {
        Assert.Contains(Build(Settings(AllOfThem)).Candidates, c => c.Start == DateTime.Parse("2026-11-23T10:00"));
        // With the organisation as supplier.co.uk, Ann's 1:1 with alex@example.com is external, so fixed, and rules the time out.
        var report = Build(Settings(AllOfThem + "org_domain = \"supplier.co.uk\"\n"));
        Assert.DoesNotContain(report.Candidates, c => c.Start == DateTime.Parse("2026-11-23T10:00"));
    }

    [Theory]
    [InlineData("[[rules.slots]]\nlabel = \"maybe\"\n", "label should be one of")]
    [InlineData("[[rules.slots]]\nlabel = \"soft\"\ngroup = \"X\"\n", "only one of them")]
    [InlineData("[[rules.slots]]\nwhy = \"x\"\ngroup = \"X\"\n", "why goes with a label")]
    [InlineData("[[rules.slots]]\nresponse = \"maybe\"\nlabel = \"soft\"\n", "response 'maybe'")]
    [InlineData("[slots]\nlunch = \"13:00-12:00\"\n", "should end after it starts")]
    [InlineData("[slots]\nstep = 30\n", "unknown setting 'step' in [slots]")]
    [InlineData("[[rules.mine]]\nlabel = \"soft\"\n[view.me]\nrules = \"mine\"\n", "which is for `slots`")]
    public void Bad_settings_are_named(string text, string message)
    {
        var error = Assert.Throws<UsageException>(() => Settings(text));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_to_one_with_owner_is_allowed_without_a_group()
    {
        var config = Settings("[[rules.slots]]\none_to_one_with = \"owner\"\nlabel = \"movable\"\n");
        Assert.Equal("owner", Assert.Single(config.Summary.Rules["slots"]).OneToOneWith);
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n[slots]\nper_day = 50\n");
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", path, .. args], new CliHost
        {
            Out = output, Err = errors, Source = _ => source, Today = () => Today, Now = _ => Now, Env = _ => null,
        });
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public void The_command_prints_json_for_the_skill()
    {
        var (code, output, error) = Run("slots", Ann, "--optional", Bob, "--minutes", "30", "--by", "2026-11-23", "--until", "2026-11-25", "--json");
        Assert.True(code == 0, error);
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(30, root["minutes"]!.GetValue<int>());
        Assert.Equal(3, root["people"]!.AsArray().Count);
        var first = root["candidates"]!.AsArray()[0]!.AsObject();
        foreach (var key in new[] { "start", "end", "pastBy", "ownerLocation", "lunch", "people", "gives" })
            Assert.True(first.ContainsKey(key), key);
        var person = first["people"]!.AsArray()[0]!.AsObject();
        foreach (var key in new[] { "name", "address", "essential", "readable", "free", "items" })
            Assert.True(person.ContainsKey(key), key);
    }

    [Fact]
    public void The_command_prints_a_table()
    {
        var (code, output, error) = Run("slots", Ann, "--minutes", "30", "--by", "2026-11-23", "--until", "2026-11-24", "--max", "100");
        Assert.True(code == 0, error);
        Assert.Contains("Slots for 30 minutes with Alex Morgan, Ann Smith", output, StringComparison.Ordinal);
        Assert.Contains("movable: Ann / Alex 1:1 10:00-10:30 (a 1:1 with you)", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "slots", "ann@example.com" }, "needs --minutes")]
    [InlineData(new[] { "slots", "--minutes", "30" }, "people (who the meeting is with)")]
    [InlineData(new[] { "slots", "ann@example.com", "--minutes", "30", "--out", "x.txt" }, "only prints")]
    [InlineData(new[] { "slots", "ann@example.com", "--minutes", "30", "--place", "pub" }, "invalid choice: 'pub'")]
    [InlineData(new[] { "slots", "ann@example.com", "--minutes", "30", "--by", "2026-11-30", "--until", "2026-11-27" }, "is before --by")]
    public void Bad_requests_are_explained(string[] args, string message)
    {
        var (code, _, error) = Run(args);
        Assert.Equal(2, code);
        Assert.Contains(message, error, StringComparison.Ordinal);
    }
}
