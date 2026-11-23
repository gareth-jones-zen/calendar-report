using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

/// <summary>How `slots` chooses what to show (0.9.0): the best of each day, sooner days first, then clean
/// alternatives. The case behind it: clean times next week used to fill the list and push out this week's fair
/// trade-offs (a 1:1 with the owner that could move; a sprint ceremony that could be gone over).</summary>
public sealed class SlotsRankingTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-ranking-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();
    private static readonly DateTime Now = new(2026, 11, 22, 18, 0, 0);   // a Sunday evening

    private const string Me = "alex@example.com";
    private const string Ann = "ann@example.com";

    public SlotsRankingTests()
    {
        Directory.CreateDirectory(dir);
        source.Calendars["me"] = [];
        source.Calendars[Ann] =
        [
            // Mon 23: away with a supplier all day except her 1:1 with the owner (movable) at 10:00.
            External("2026-11-23T09:00", "2026-11-23T10:00"),
            With("2026-11-23T10:00", "2026-11-23T10:30", "Ann / Alex 1:1", [Ann], Me),
            External("2026-11-23T10:30", "2026-11-23T17:00"),
            // Tue 24: the same, except a sprint review (soft) at 14:00.
            External("2026-11-24T09:00", "2026-11-24T14:00"),
            With("2026-11-24T14:00", "2026-11-24T14:30", "Squad Pi sprint review", [Ann, "dee@example.com"], "dee@example.com"),
            External("2026-11-24T14:30", "2026-11-24T17:00"),
            // Wed 25 onwards: nothing at all, so every day after is clean.
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

    private static Event With(string start, string end, string subject, string[] people, string organizer) =>
        new(DateTime.Parse(start), DateTime.Parse(end), false, "busy", subject, null, organizer.Split('@')[0],
            people.Length, true, "normal", [], false, "accepted",
            people.Where(p => p != organizer).Select(p => new Attendee(p.Split('@')[0], p, "required", "accepted")).ToList(), organizer);

    private static Event External(string start, string end) =>
        With(start, end, "Supplier workshop", [Ann, "sam@supplier.co.uk"], "sam@supplier.co.uk");

    private Config Settings(string text = "")
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n[slots]\nstep_minutes = 30\n" + text);
        return Config.Load(path, _ => null);
    }

    private SlotsReport Build(Config config, int max = 5, string by = "2026-11-27", string until = "2026-12-04")
    {
        var request = new SlotRequest([source.Resolve("me"), source.Resolve(Ann)], [], 30, new DateTime(2026, 11, 23),
            DateTime.Parse(by), DateTime.Parse(until), "either", max);
        return Slots.Build(config, source, request, Now);
    }

    [Fact]
    public void Sooner_days_with_a_fair_trade_off_come_first_and_are_not_pushed_out_by_clean_later_days()
    {
        var report = Build(Settings());
        Assert.Equal(DateTime.Parse("2026-11-23T10:00"), report.Candidates[0].Start);   // the movable 1:1
        Assert.Equal("movable", report.Candidates[0].People.Single(p => p.Person.Address == Ann).Items[0].Label);
        Assert.Equal(DateTime.Parse("2026-11-24T14:00"), report.Candidates[1].Start);   // the soft sprint review
        Assert.Equal("soft", report.Candidates[1].People.Single(p => p.Person.Address == Ann).Items[0].Label);
        Assert.All(report.Candidates.Take(2), c => Assert.Equal("by-day", c.Section));
    }

    [Fact]
    public void Clean_alternatives_follow_sooner_first_within_the_cap()
    {
        var report = Build(Settings());
        Assert.Equal(5, report.Candidates.Count);
        var clean = report.Candidates.Where(c => c.Section == "clean").ToList();
        Assert.Equal(3, clean.Count);
        Assert.All(clean, c => Assert.True(c.Clean));
        Assert.Equal([25, 26, 27], clean.Select(c => c.Start.Day));
        Assert.Equal(report.Candidates.Skip(2), clean);
    }

    [Fact]
    public void Each_day_inside_by_comes_before_the_days_past_it_and_in_date_order()
    {
        var report = Build(Settings("clean_extra = 0\n"), max: 15, by: "2026-11-25");
        var days = report.Candidates.Select(c => c.Start.Date).ToList();
        Assert.Equal(days.Distinct().Count(), days.Count);   // per_day 1: one each
        var inside = report.Candidates.TakeWhile(c => !c.PastBy).Select(c => c.Start.Date).ToList();
        Assert.Equal(inside.OrderBy(d => d), inside);
        Assert.Equal(new DateTime(2026, 11, 25), inside[^1]);
        var past = report.Candidates.SkipWhile(c => !c.PastBy).ToList();
        Assert.All(past, c => Assert.True(c.PastBy));
        Assert.Equal(past.Select(c => c.Start).OrderBy(d => d), past.Select(c => c.Start));
    }

    [Fact]
    public void Max_candidates_caps_the_whole_list_and_leaves_room_for_clean_ones()
    {
        var report = Build(Settings(), max: 3);
        Assert.Equal(3, report.Candidates.Count);
        Assert.Equal("by-day", report.Candidates[0].Section);
        Assert.Equal(DateTime.Parse("2026-11-23T10:00"), report.Candidates[0].Start);
        Assert.All(report.Candidates.Skip(1), c => Assert.Equal("clean", c.Section));

        Assert.Single(Build(Settings(), max: 1).Candidates);
    }

    [Fact]
    public void Per_day_keeps_that_many_of_each_days_best()
    {
        var report = Build(Settings("per_day = 2\nclean_extra = 0\n"), max: 50);
        Assert.All(report.Candidates.GroupBy(c => c.Start.Date), g => Assert.True(g.Count() <= 2));
        Assert.Contains(report.Candidates, c => c.Start.Date == new DateTime(2026, 11, 25) && c.Start.Hour >= 9);
    }

    [Fact]
    public void Json_says_which_section_each_candidate_is_in_and_the_table_heads_them()
    {
        var report = Build(Settings());
        var json = JsonNode.Parse(Slots.Json(report))!;
        var sections = json["candidates"]!.AsArray().Select(c => (string)c!["section"]!).ToList();
        Assert.Equal(["by-day", "by-day", "clean", "clean", "clean"], sections);

        var table = Slots.Table(report);
        var days = table.IndexOf("Best per day, sooner days first:", StringComparison.Ordinal);
        var clean = table.IndexOf("Clean alternatives (nothing in anyone's way):", StringComparison.Ordinal);
        Assert.True(days >= 0 && clean > days);
    }

    [Fact]
    public void Clean_extra_is_a_setting()
    {
        Assert.Equal(3, Settings().Slots.CleanExtra);
        Assert.Equal(1, Settings().Slots.PerDay);
        Assert.Equal(0, Settings("clean_extra = 0\n").Slots.CleanExtra);
        Assert.Throws<UsageException>(() => Settings("clean_extra = 99\n"));
    }
}
