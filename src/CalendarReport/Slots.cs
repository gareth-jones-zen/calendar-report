using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CalendarDays;
using Tomlyn.Model;

namespace CalendarReport;

/// <summary>[slots]: how `slots` finds and sorts candidate times for a meeting (0.7.0). The labels come from a rule
/// list (<see cref="Rules"/>, default "slots"; the built-in list without one).</summary>
public sealed partial class SlotSettings
{
    private static readonly string[] Keys =
        ["rules", "hours", "lunch", "step_minutes", "max_candidates", "per_day", "past_by_days", "org_domain"];

    public string Rules { get; init; } = "slots";
    public TimeSpan HoursFrom { get; init; } = TimeSpan.FromHours(9);
    public TimeSpan HoursTo { get; init; } = TimeSpan.FromHours(17);
    public TimeSpan? LunchFrom { get; init; }
    public TimeSpan? LunchTo { get; init; }
    public int StepMinutes { get; init; } = 30;
    public int MaxCandidates { get; init; } = 15;
    public int PerDay { get; init; } = 3;
    public int PastByDays { get; init; } = 5;

    /// <summary>The organisation's domain, for the <c>external</c> test; by default the owner's own.</summary>
    public string? OrgDomain { get; init; }

    /// <summary>The starting labels (assistant/designs/meeting-slots, decision 1), used when the settings have no
    /// [[rules.slots]]: free time isn't in the way; out of office and outside attendees are fixed; a 1:1 with you
    /// and what you organised are movable; sprint ceremonies, tentative meetings, people's own blocked time and large
    /// meetings are soft; anything else is fixed.</summary>
    public static IReadOnlyList<Rule> BuiltInRules =>
    [
        new() { Where = "built-in slots (1)", ShowAs = "free", Count = "skip" },
        new() { Where = "built-in slots (2)", ShowAs = "oof", Label = "fixed", Why = "away (shown as out of office)" },
        new() { Where = "built-in slots (3)", External = true, Label = "fixed", Why = "with people outside the organisation" },
        new() { Where = "built-in slots (4)", OneToOneWith = "owner", Label = "movable", Why = "a 1:1 with you" },
        new() { Where = "built-in slots (5)", OwnerOrganizes = true, Label = "movable", Why = "you organised it" },
        new() { Where = "built-in slots (6)", Subject = new Regex(SprintCeremonies, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                Label = "soft", Why = "a sprint ceremony" },
        new() { Where = "built-in slots (7)", Response = ["tentative"], Label = "soft", Why = "they're tentative" },
        new() { Where = "built-in slots (8)", OwnBlock = true, Label = "soft", Why = "their own blocked time" },
        new() { Where = "built-in slots (9)", MinAttendees = 12, Label = "soft", Why = "a large meeting, where they're one of many" },
    ];

    /// <summary>Sprint ceremonies, by subject: stand-ups, refinement, sprint planning, sprint reviews and retros.
    /// "Planning" and "review" alone are left out: too many other meetings use them.</summary>
    public const string SprintCeremonies =
        @"\b(stand-?up|daily scrum|(backlog )?refinement|sprint planning|sprint review|retro|retrospective)\b";

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d-(([01]\d|2[0-3]):[0-5]\d|24:00)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Window();

    internal static SlotSettings Parse(TomlTable raw, string file)
    {
        if (!raw.TryGetValue("slots", out var s))
            return new SlotSettings();
        UsageException Bad(string message) => new($"{file}: {message}");
        if (s is not TomlTable table)
            throw Bad("[slots] should be a table.");
        foreach (var key in table.Keys)
            if (!Keys.Contains(key))
                throw Bad($"unknown setting '{key}' in [slots] (known: {string.Join(", ", Keys)}).");

        int Whole(string key, int fallback, int min, int max)
        {
            if (!table.TryGetValue(key, out var v))
                return fallback;
            return v is long l && l >= min && l <= max ? (int)l : throw Bad($"slots.{key} should be a whole number from {min} to {max}.");
        }

        (TimeSpan From, TimeSpan To)? Range(string key)
        {
            if (!table.TryGetValue(key, out var v))
                return null;
            if (v is not string text || !Window().IsMatch(text))
                throw Bad($"slots.{key} should be \"hh:mm-hh:mm\", such as \"09:00-17:00\".");
            var from = TimeSpan.ParseExact(text[..5], @"hh\:mm", CultureInfo.InvariantCulture);
            var to = text[6..] == "24:00" ? TimeSpan.FromHours(24) : TimeSpan.ParseExact(text[6..], @"hh\:mm", CultureInfo.InvariantCulture);
            return to > from ? (from, to) : throw Bad($"slots.{key} should end after it starts.");
        }

        string? Text(string key) => table.TryGetValue(key, out var v)
            ? v as string is { Length: > 0 } t ? t : throw Bad($"slots.{key} should be a non-empty string.")
            : null;

        var hours = Range("hours");
        var lunch = Range("lunch");
        return new SlotSettings
        {
            Rules = Text("rules") ?? "slots",
            HoursFrom = hours?.From ?? TimeSpan.FromHours(9),
            HoursTo = hours?.To ?? TimeSpan.FromHours(17),
            LunchFrom = lunch?.From,
            LunchTo = lunch?.To,
            StepMinutes = Whole("step_minutes", 30, 5, 240),
            MaxCandidates = Whole("max_candidates", 15, 1, 100),
            PerDay = Whole("per_day", 3, 1, 50),
            PastByDays = Whole("past_by_days", 5, 0, 30),
            OrgDomain = Text("org_domain"),
        };
    }
}

/// <summary>What `slots` was asked for: the people (the owner always among the essential), how long, and when.</summary>
public sealed record SlotRequest(
    IReadOnlyList<Person> Essential, IReadOnlyList<Person> Optional, int Minutes, DateTime From, DateTime By,
    DateTime Until, string Place, int MaxCandidates);

/// <summary>Something in the way for one person at a candidate time: its subject (when visible), its time, how
/// easily it gives (fixed, soft or movable) and why, and which rule said so.</summary>
public sealed record SlotItem(string? Subject, DateTime Start, DateTime End, string Label, string Why, string Rule, bool DetailsVisible);

/// <summary>One person at a candidate time: what's in the way (nothing: free), or that their calendar can't be read.</summary>
public sealed record SlotPerson(Person Person, bool Essential, bool Readable, IReadOnlyList<SlotItem> Items)
{
    public bool Free => Readable && Items.Count == 0;

    /// <summary>0 free, 1 movable, 2 soft, 3 fixed.</summary>
    public int Worst => Items.Count == 0 ? 0 : Items.Max(i => Slots.Rank(i.Label));
}

public sealed record SlotCandidate(DateTime Start, DateTime End, bool PastBy, string? OwnerLocation, bool Lunch,
    IReadOnlyList<SlotPerson> People)
{
    /// <summary>What would have to give, a line each.</summary>
    public IReadOnlyList<string> Gives =>
        [
            .. People.SelectMany(p => p.Items.Select(i =>
                $"{p.Person.Name}{(p.Essential ? "" : " (optional)")}: {i.Subject ?? "busy (no details visible)"} ({i.Label}: {i.Why})")),
            .. Lunch ? new[] { "everyone: over lunch (soft)" } : [],
        ];
}

public sealed record SlotsReport(SlotRequest Request, string TimeZone, int Considered, int RuledOut,
    IReadOnlyList<SlotCandidate> Candidates, IReadOnlyList<string> Notes);

/// <summary>`slots`: candidate times for a meeting with several people, and for each person what's in the way,
/// labelled fixed, soft or movable by rules (0.7.0; assistant/designs/meeting-slots). It only reads, and only prints:
/// nothing about anyone's calendar is written to disk.</summary>
public static class Slots
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static int Rank(string label) => label switch { "movable" => 1, "soft" => 2, "fixed" => 3, _ => 0 };

    /// <summary>The day n working days (Monday to Friday) after this one.</summary>
    public static DateTime AddWorkingDays(DateTime day, int n)
    {
        var d = day.Date;
        while (n > 0)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                n--;
        }
        return d;
    }

    /// <summary>The rule list the labels come from: [slots] rules, or the built-in list when that's "slots" and the
    /// settings have none.</summary>
    public static IReadOnlyList<Rule> RulesFor(Config config)
    {
        var name = config.Slots.Rules;
        if (config.Summary.Rules.TryGetValue(name, out var rules))
            return rules;
        if (name == "slots")
            return SlotSettings.BuiltInRules;
        throw new UsageException($"{config.Path}: slots.rules: there's no rule list '{name}' ([[rules.{name}]]).");
    }

    public static SlotsReport Build(Config config, ISource source, SlotRequest request, DateTime now)
    {
        var settings = config.Slots;
        var rules = RulesFor(config);
        var context = new RuleContext(source, config);
        var from = request.From.Date;
        var to = request.Until.Date.AddDays(1);
        var notes = new List<string>();

        var everyone = request.Essential.Select(p => (Person: p, Essential: true))
            .Concat(request.Optional.Select(p => (Person: p, Essential: false))).ToList();
        var calendars = new Dictionary<string, IReadOnlyList<Event>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (person, _) in everyone)
        {
            try
            {
                calendars[person.Address] = source.Events(person, from, to).Where(e => !e.Cancelled).ToList();
            }
            catch (M365Graph.GraphRequestException error) when ((int)error.Status is 403 or 404)
            {
                calendars[person.Address] = null;
                notes.Add($"{person.Name}'s calendar can't be read (is it shared with you?): shown as not visible.");
            }
        }
        var me = request.Essential[0];
        var plan = source.WorkPlan(me, from, to);

        DayOff DayOffFor(Person person, DateTime day)
        {
            var events = calendars[person.Address] ?? [];
            var self = context.AddressOf(person);
            var onDay = events.Where(e => e.Start < day.AddDays(1) && e.End > day).Select(e =>
            {
                var organizer = e.OrganizerAddress?.ToLowerInvariant();
                var mine = (organizer is null || organizer == self) && (e.Invitees ?? []).All(a => a.Type == "resource");
                return new DayEvent(e.Subject, e.Start, e.End, e.AllDay, e.ShowAs, e.Categories, mine, e.Attendees);
            }).ToList();
            return DayOffRules.Classify(config.Leave, onDay, DateOnly.FromDateTime(day));
        }

        var considered = 0;
        var ruledOut = 0;
        var kept = new List<SlotCandidate>();
        var skippedDays = new List<string>();
        for (var day = from; day < to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            var mine = DayOffFor(me, day);
            if (mine.Kind is DayOffKind.Leave or DayOffKind.BankHoliday)
            {
                skippedDays.Add($"{day:ddd d MMM} ({(mine.Kind == DayOffKind.BankHoliday ? "bank holiday" : "your leave")})");
                continue;
            }
            var earliest = day + settings.HoursFrom;
            var latest = day + settings.HoursTo;
            var midday = day + DayOffRules.Midday.ToTimeSpan();
            if (mine.Kind == DayOffKind.HalfDay && mine.Half == HalfOfDay.Am)
                earliest = earliest < midday ? midday : earliest;
            if (mine.Kind == DayOffKind.HalfDay && mine.Half == HalfOfDay.Pm)
                latest = latest > midday ? midday : latest;
            var dayOffs = everyone.ToDictionary(p => p.Person.Address, p => p.Person == me ? mine : DayOffFor(p.Person, day),
                StringComparer.OrdinalIgnoreCase);

            for (var start = earliest; start.AddMinutes(request.Minutes) <= latest; start = start.AddMinutes(settings.StepMinutes))
            {
                if (start <= now)
                    continue;
                var end = start.AddMinutes(request.Minutes);
                considered++;
                var people = everyone.Select(p => Person(p.Person, p.Essential, start, end, dayOffs[p.Person.Address],
                    calendars[p.Person.Address], rules, context, p.Person == me ? plan : null)).ToList();
                if (people.Any(p => p.Essential && p.Worst == 3))
                {
                    ruledOut++;
                    continue;
                }
                var lunch = settings.LunchFrom is { } lf && settings.LunchTo is { } lt && start < day + lt && end > day + lf;
                var location = plan?.FirstOrDefault(s => s.Start <= start && s.End > start)?.Location;
                kept.Add(new SlotCandidate(start, end, day > request.By.Date, location, lunch, people));
            }
        }
        if (skippedDays.Count > 0)
            notes.Insert(0, $"No candidates on {string.Join(", ", skippedDays)}.");

        var inPerson = request.Place == "in-person";
        var sorted = kept
            .OrderBy(c => Math.Max(c.People.Where(p => p.Essential).Select(p => p.Worst).DefaultIfEmpty(0).Max(), c.Lunch ? 2 : 0))
            .ThenBy(c => c.People.Count(p => p.Essential && p.Items.Count > 0))
            .ThenBy(c => c.PastBy)
            .ThenBy(c => c.People.Count(p => !p.Essential && p.Worst == 3))
            .ThenBy(c => c.People.Sum(p => p.Items.Count(i => i.Label == "soft")))
            .ThenBy(c => inPerson && c.OwnerLocation != "office")
            .ThenBy(c => c.Start)
            .ToList();
        var chosen = new List<SlotCandidate>();
        var perDay = new Dictionary<DateTime, int>();
        foreach (var c in sorted)
        {
            if (chosen.Count >= request.MaxCandidates)
                break;
            var n = perDay.GetValueOrDefault(c.Start.Date);
            if (n >= settings.PerDay)
                continue;
            perDay[c.Start.Date] = n + 1;
            chosen.Add(c);
        }
        return new SlotsReport(request, config.TimeZone.Id, considered, ruledOut, chosen, notes);
    }

    private static SlotPerson Person(Person person, bool essential, DateTime start, DateTime end, DayOff dayOff,
        IReadOnlyList<Event>? events, IReadOnlyList<Rule> rules, RuleContext context, IReadOnlyList<WorkSegment>? plan)
    {
        if (events is null)
            return new SlotPerson(person, essential, false, []);
        var items = new List<SlotItem>();
        var midday = start.Date + DayOffRules.Midday.ToTimeSpan();
        switch (dayOff.Kind)
        {
            case DayOffKind.Leave or DayOffKind.BankHoliday:
                return new SlotPerson(person, essential, true,
                [
                    new SlotItem(dayOff.Entry?.Subject, start.Date, start.Date.AddDays(1), "fixed",
                        dayOff.Kind == DayOffKind.BankHoliday ? "a bank holiday" : "on leave", "leave", dayOff.Entry is not null),
                ]);
            case DayOffKind.HalfDay when dayOff.Half == HalfOfDay.Unknown:
                items.Add(new SlotItem(dayOff.Entry?.Subject, start.Date, start.Date.AddDays(1), "soft",
                    "half a day's leave, which half isn't known", "leave", dayOff.Entry is not null));
                break;
            case DayOffKind.HalfDay when (dayOff.Half == HalfOfDay.Am && start < midday) || (dayOff.Half == HalfOfDay.Pm && end > midday):
                items.Add(new SlotItem(dayOff.Entry?.Subject, start.Date, start.Date.AddDays(1), "fixed",
                    $"half a day's leave ({(dayOff.Half == HalfOfDay.Am ? "morning" : "afternoon")})", "leave", dayOff.Entry is not null));
                break;
        }
        if (plan?.FirstOrDefault(s => s.Location == "timeOff" && s.Start < end && s.End > start) is { } off)
            items.Add(new SlotItem("Time off (work plan)", off.Start, off.End, "fixed", "time off in your work plan", "work plan", true));

        foreach (var e in events.Where(e => e.Start < end && e.End > start))
        {
            if (RuleContext.ResponseOf(e) == "declined")
                continue;
            if (string.IsNullOrWhiteSpace(e.Subject))
            {
                if (e.ShowAs.Equals("free", StringComparison.OrdinalIgnoreCase))
                    continue;
                var soft = e.ShowAs.Equals("tentative", StringComparison.OrdinalIgnoreCase);
                items.Add(new SlotItem(null, e.Start, e.End, soft ? "soft" : "fixed",
                    soft ? "shown as tentative; no details visible" : "busy; no details visible", "no details", false));
                continue;
            }
            var (_, rule) = context.Classify(e, rules, person);
            if (rule is null)
                items.Add(new SlotItem(e.Subject, e.Start, e.End, "fixed", "someone else's meeting (no rule matched)", "none", true));
            else if (rule.Label is { } label)
                items.Add(new SlotItem(e.Subject, e.Start, e.End, label, rule.Why ?? label, rule.Where, true));
            else if (rule.Count == "not-working")
                items.Add(new SlotItem(e.Subject, e.Start, e.End, "soft", "not working time (lunch, travel)", rule.Where, true));
            else if (rule.Count == "leave")
                items.Add(new SlotItem(e.Subject, e.Start, e.End, "fixed", "leave", rule.Where, true));
            else if (rule.Count is null)   // a summary rule (a group) from a borrowed list: no label, so as unmatched
                items.Add(new SlotItem(e.Subject, e.Start, e.End, "fixed", "someone else's meeting (no label for it)", rule.Where, true));
            // count = "skip": not in the way (free time)
        }
        return new SlotPerson(person, essential, true, items);
    }

    private static string Time(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm", En);

    public static string Json(SlotsReport report)
    {
        var r = report.Request;
        var root = new JsonObject
        {
            ["minutes"] = r.Minutes,
            ["from"] = r.From.ToString("yyyy-MM-dd", En),
            ["by"] = r.By.ToString("yyyy-MM-dd", En),
            ["until"] = r.Until.ToString("yyyy-MM-dd", En),
            ["place"] = r.Place,
            ["timeZone"] = report.TimeZone,
            ["people"] = new JsonArray([
                .. r.Essential.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["address"] = p.Address, ["essential"] = true }),
                .. r.Optional.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["address"] = p.Address, ["essential"] = false }),
            ]),
            ["considered"] = report.Considered,
            ["ruledOut"] = report.RuledOut,
            ["notes"] = new JsonArray([.. report.Notes.Select(n => (JsonNode)JsonValue.Create(n))]),
            ["candidates"] = new JsonArray([.. report.Candidates.Select(c => (JsonNode)new JsonObject
            {
                ["start"] = Time(c.Start),
                ["end"] = Time(c.End),
                ["pastBy"] = c.PastBy,
                ["ownerLocation"] = c.OwnerLocation,
                ["lunch"] = c.Lunch,
                ["people"] = new JsonArray([.. c.People.Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.Person.Name,
                    ["address"] = p.Person.Address,
                    ["essential"] = p.Essential,
                    ["readable"] = p.Readable,
                    ["free"] = p.Free,
                    ["items"] = new JsonArray([.. p.Items.Select(i => (JsonNode)new JsonObject
                    {
                        ["subject"] = i.Subject,
                        ["start"] = Time(i.Start),
                        ["end"] = Time(i.End),
                        ["label"] = i.Label,
                        ["why"] = i.Why,
                        ["rule"] = i.Rule,
                        ["detailsVisible"] = i.DetailsVisible,
                    })]),
                })]),
                ["gives"] = new JsonArray([.. c.Gives.Select(g => (JsonNode)JsonValue.Create(g))]),
            })]),
        };
        return PyCompat.PyJson.Dump(root, 2, false) + "\n";
    }

    public static string Table(SlotsReport report)
    {
        var r = report.Request;
        var b = new StringBuilder();
        string Names(IEnumerable<Person> people) => string.Join(", ", people.Select(p => p.Name));
        b.Append(En, $"Slots for {r.Minutes} minutes with {Names(r.Essential)}");
        if (r.Optional.Count > 0)
            b.Append(En, $" (optional: {Names(r.Optional)})");
        b.Append(En, $", {r.From:ddd d MMM} to {r.Until:ddd d MMM} (by {r.By:ddd d MMM}; {r.Place}).\n");
        b.Append(En, $"{report.Considered} times looked at; {report.RuledOut} ruled out by a fixed meeting for someone essential; ");
        b.Append(En, $"{report.Candidates.Count} shown.\n");
        foreach (var note in report.Notes)
            b.Append(note).Append('\n');
        var n = 0;
        foreach (var c in report.Candidates)
        {
            b.Append('\n').Append(En, $"{++n}. {c.Start:ddd d MMM HH:mm}-{c.End:HH:mm}");
            if (c.OwnerLocation is { } where)
                b.Append(En, $"  you: {where}");
            if (c.PastBy)
                b.Append("  (past the by date)");
            if (c.Lunch)
                b.Append("  (over lunch)");
            b.Append('\n');
            foreach (var p in c.People)
            {
                b.Append(En, $"   {p.Person.Name}{(p.Essential ? "" : " (optional)")}: ");
                if (!p.Readable)
                    b.Append("calendar not visible\n");
                else if (p.Items.Count == 0)
                    b.Append("free\n");
                else
                    b.Append(string.Join("; ", p.Items.Select(i =>
                        $"{i.Label}: {i.Subject ?? "busy"} {i.Start:HH:mm}-{i.End:HH:mm} ({i.Why})"))).Append('\n');
            }
        }
        if (report.Candidates.Count == 0)
            b.Append("\nNo time works without moving a fixed meeting for someone essential: try a later --until, or make someone optional.\n");
        return b.ToString();
    }
}
