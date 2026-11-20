using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;
using Tomlyn.Model;

namespace CalendarReport;

/// <summary>A ring of people to keep in touch with: flagged after GapDays without a meeting, or (if set) when
/// LostOccurrences of a recurring meeting's last few were lost.</summary>
public sealed record Ring(string Name, int GapDays, int? LostOccurrences, IReadOnlyList<string> People);

/// <summary>[contacts] and [[contacts.ring]] (0.5.0): who `contacts` checks, and how often they should meet you.</summary>
public sealed class ContactSettings
{
    private static readonly string[] Keys =
        ["max_attendees", "cadence_max_attendees", "lookback_days", "lookahead_days", "recent_occurrences", "ring", "why"];

    private static readonly string[] RingKeys = ["name", "gap_days", "lost_occurrences", "people"];

    /// <summary>A meeting counts as contact only if it has this many people or fewer, you included (an all-hands
    /// isn't contact).</summary>
    public int MaxAttendees { get; init; } = 6;

    /// <summary>A recurring meeting is checked for lost occurrences only if it has this many people or fewer (2: a
    /// 1:1). A group meeting losing a week doesn't break contact with each person in it.</summary>
    public int CadenceMaxAttendees { get; init; } = 2;

    public int LookbackDays { get; init; } = 120;
    public int LookaheadDays { get; init; } = 60;

    /// <summary>How many of a recurring meeting's most recent occurrences are checked for being lost.</summary>
    public int RecentOccurrences { get; init; } = 4;

    public IReadOnlyList<Ring> Rings { get; init; } = [];

    /// <summary>One line each on why someone matters, keyed (lower case) by an address, a name, or a word in a
    /// ring's people.</summary>
    public IReadOnlyDictionary<string, string> Why { get; init; } = new Dictionary<string, string>();

    internal static ContactSettings Parse(TomlTable raw, string file)
    {
        if (!raw.TryGetValue("contacts", out var value))
            return new ContactSettings();
        UsageException Bad(string message) => new($"{file}: {message}");
        if (value is not TomlTable table)
            throw Bad("[contacts] should be a table.");
        foreach (var key in table.Keys)
            if (!Keys.Contains(key))
                throw Bad($"unknown setting '{key}' in [contacts] (known: {string.Join(", ", Keys)}).");

        int Whole(TomlTable t, string key, string where, int fallback, int min, int max)
        {
            if (!t.TryGetValue(key, out var v))
                return fallback;
            return v is long l && l >= min && l <= max ? (int)l : throw Bad($"{where}.{key} should be a whole number from {min} to {max}.");
        }

        var rings = new List<Ring>();
        if (table.TryGetValue("ring", out var r))
        {
            if (r is not TomlTableArray array)
                throw Bad("contacts.ring should be [[contacts.ring]] tables.");
            foreach (var ring in array)
            {
                foreach (var key in ring.Keys)
                    if (!RingKeys.Contains(key))
                        throw Bad($"unknown setting '{key}' in [[contacts.ring]] (known: {string.Join(", ", RingKeys)}).");
                var name = ring.TryGetValue("name", out var n) && n is string { Length: > 0 } s ? s
                    : throw Bad("each [[contacts.ring]] needs a name.");
                if (rings.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw Bad($"two [[contacts.ring]] are called '{name}'.");
                var where = $"contacts.ring '{name}'";
                if (!ring.ContainsKey("gap_days"))
                    throw Bad($"{where} needs gap_days.");
                var people = ring.TryGetValue("people", out var p) && p is TomlArray list && list.Count > 0
                                                                   && list.All(x => x is string { Length: > 0 })
                    ? list.Select(x => ((string)x!).Trim()).ToList()
                    : throw Bad($"{where}: people should be a list of addresses, names or groups.");
                rings.Add(new Ring(name, Whole(ring, "gap_days", where, 0, 1, 366),
                    ring.ContainsKey("lost_occurrences") ? Whole(ring, "lost_occurrences", where, 0, 1, 52) : null, people));
            }
        }

        var why = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (table.TryGetValue("why", out var w))
        {
            if (w is not TomlTable whyTable)
                throw Bad("[contacts.why] should be a table of \"person\" = \"why\".");
            foreach (var (who, line) in whyTable)
                why[who.Trim()] = line is string { Length: > 0 } text ? text.Trim() : throw Bad($"contacts.why.\"{who}\" should be a string.");
        }

        var settings = new ContactSettings
        {
            MaxAttendees = Whole(table, "max_attendees", "contacts", 6, 2, 1000),
            CadenceMaxAttendees = Whole(table, "cadence_max_attendees", "contacts", 2, 2, 1000),
            LookbackDays = Whole(table, "lookback_days", "contacts", 120, 7, 366),
            LookaheadDays = Whole(table, "lookahead_days", "contacts", 60, 0, 366),
            RecentOccurrences = Whole(table, "recent_occurrences", "contacts", 4, 1, 52),
            Rings = rings,
            Why = why,
        };
        if (rings.Count > 0 && rings.Max(x => x.GapDays) > settings.LookbackDays)
            throw Bad($"a ring's gap_days ({rings.Max(x => x.GapDays)}) is longer than contacts.lookback_days ({settings.LookbackDays}), so it could never be seen.");
        return settings;
    }
}

/// <summary>A meeting with someone: when, what, how many people.</summary>
public sealed record Contact(DateTime Start, string Subject, int People);

/// <summary>A recurring meeting with someone: its pattern, how many of its recent occurrences were checked, and the
/// dates of those lost (cancelled, removed, or declined by either of you).</summary>
public sealed record Cadence(string Subject, string Pattern, int Checked, IReadOnlyList<DateTime> Lost);

/// <summary>Where you stand with one person.</summary>
public sealed record ContactStatus(Person Person, string? Why, Contact? Last, int? DaysSince, Contact? Next,
    IReadOnlyList<Cadence> Recurring, IReadOnlyList<string> Reasons)
{
    public bool Flagged => Reasons.Count > 0;
}

public sealed record RingStatus(Ring Ring, IReadOnlyList<ContactStatus> People);

public sealed record ContactsReport(DateTime Now, DateTime From, DateTime To, string TimeZone, int MaxAttendees,
    int RecentOccurrences, IReadOnlyList<RingStatus> Rings);

/// <summary>`contacts`: for each person in the rings, the last meeting you both attended, the next one booked, and how
/// many of a recurring meeting's recent occurrences were lost. Reads only your own calendar.</summary>
public static class Contacts
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public const string Asked = "asked";

    /// <summary>The rings, each with its people resolved, the first ring winning for anyone named twice. PEOPLE
    /// narrow them (anyone named who isn't in a ring is put in a ring of their own, "asked", which is never flagged);
    /// ring narrows to one ring.</summary>
    public static List<(Ring Ring, List<(Person Person, string? Why)> People)> Resolve(
        Config config, ISource source, IReadOnlyList<string> asked, string? ring)
    {
        var settings = config.Contacts;
        if (ring is not null && !settings.Rings.Any(r => string.Equals(r.Name, ring, StringComparison.OrdinalIgnoreCase)))
            throw new UsageException(settings.Rings.Count == 0
                ? $"There are no [[contacts.ring]] in {config.Path}."
                : $"There's no ring '{ring}' in {config.Path} (rings: {string.Join(", ", settings.Rings.Select(r => r.Name))}).");
        if (settings.Rings.Count == 0 && asked.Count == 0)
            throw new UsageException($"There are no [[contacts.ring]] in {config.Path}: add some (see the README, \"Contacts\"), or name people.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(Ring, List<(Person, string?)>)>();
        foreach (var r in settings.Rings)
        {
            var people = new List<(Person, string?)>();
            foreach (var word in r.People)
            {
                var words = config.Groups.TryGetValue(word, out var members)
                    ? members.Select(m => (Member: m, Group: (string?)word)).ToList()
                    : [(word, null)];
                foreach (var (member, group) in words)
                {
                    var person = source.Resolve(member);
                    if (!seen.Add(person.Address))
                        continue;
                    people.Add((person, WhyFor(settings, person, member, group)));
                }
            }
            result.Add((r, people));
        }

        if (asked.Count > 0)
        {
            var wanted = Reports.People(asked, config, source);
            foreach (var (_, people) in result)
                people.RemoveAll(p => !wanted.Any(w => string.Equals(w.Address, p.Item1.Address, StringComparison.OrdinalIgnoreCase)));
            var others = wanted.Where(w => !seen.Contains(w.Address)).Select(w => (w, WhyFor(settings, w, w.Address, null))).ToList();
            if (others.Count > 0)
                result.Add((new Ring(Asked, 0, null, []), others));
        }
        if (ring is not null)
            result.RemoveAll(x => !string.Equals(x.Item1.Name, ring, StringComparison.OrdinalIgnoreCase));
        result.RemoveAll(x => x.Item2.Count == 0);
        return result;
    }

    private static string? WhyFor(ContactSettings settings, Person person, string word, string? group)
    {
        foreach (var key in new[] { person.Address, person.Name, word, group })
            if (key is not null && settings.Why.TryGetValue(key, out var line))
                return line;
        return null;
    }

    public static ContactsReport Build(Config config, ISource source, Person me,
        List<(Ring Ring, List<(Person Person, string? Why)> People)> rings, DateTime now)
    {
        var settings = config.Contacts;
        var from = now.Date.AddDays(-settings.LookbackDays);
        var to = now.Date.AddDays(settings.LookaheadDays + 1);
        var events = source.Events(me, from, to);
        var series = new Dictionary<string, Recurrence?>();
        Recurrence? Series(string id)
        {
            if (!series.TryGetValue(id, out var r))
                series[id] = r = source.Series(me, id);
            return r;
        }

        var result = rings.Select(x => new RingStatus(x.Ring,
            x.People.Select(p => Status(p.Person, p.Why, x.Ring, events, settings, Series, from, to, now)).ToList())).ToList();
        return new ContactsReport(now, from, to, config.TimeZone.Id, settings.MaxAttendees, settings.RecentOccurrences, result);
    }

    /// <summary>Everyone at a meeting, by address: the organiser and the people invited, rooms left out.</summary>
    internal static int PeopleAt(Event e) =>
        (e.Invitees ?? []).Where(a => a.Type != "resource").Select(a => (a.Address ?? a.Name ?? "").ToLowerInvariant())
            .Append((e.OrganizerAddress ?? e.Organizer ?? "").ToLowerInvariant())
            .Where(a => a.Length > 0).Distinct().Count();

    /// <summary>Whether they're at it, as organiser or invited, and haven't declined. By address, or by display name
    /// (someone can be invited by an alias of their address).</summary>
    internal static bool With(Event e, Person person)
    {
        bool Is(string? address, string? name) =>
            string.Equals(address, person.Address, StringComparison.OrdinalIgnoreCase)
            || (name is not null && string.Equals(name, person.Name, StringComparison.OrdinalIgnoreCase));
        if (Is(e.OrganizerAddress, e.Organizer))
            return true;
        return (e.Invitees ?? []).Any(a => a.Type != "resource" && Is(a.Address, a.Name) && a.Response != "declined");
    }

    private static ContactStatus Status(Person person, string? why, Ring ring, IReadOnlyList<Event> events,
        ContactSettings settings, Func<string, Recurrence?> series, DateTime from, DateTime to, DateTime now)
    {
        var meetings = events.Where(e => !e.AllDay && !e.Cancelled && PeopleAt(e) <= settings.MaxAttendees && With(e, person)).ToList();
        // Contact: you accepted it or organised it. Booked: anything ahead you haven't declined.
        var last = meetings.Where(e => e.Start < now && e.Response is "accepted" or "organizer").MaxBy(e => e.Start);
        var next = meetings.Where(e => e.Start >= now && e.Response != "declined").MinBy(e => e.Start);
        int? daysSince = last is null ? null : (now.Date - last.Start.Date).Days;

        var recurring = new List<Cadence>();
        foreach (var id in meetings.Where(e => e.SeriesMasterId is not null && PeopleAt(e) <= settings.CadenceMaxAttendees)
                     .Select(e => e.SeriesMasterId!).Distinct())
        {
            // A series with nothing due from today on has ended (its end date passed, or its count reached): it isn't
            // a cadence any more.
            if (series(id) is not { } pattern || !Recurrences.Dates(pattern, now.Date, to.AddDays(-1)).Any())
                continue;
            var expected = Recurrences.Dates(pattern, from, now)
                .Where(d => d.Date + pattern.StartTime < now).TakeLast(settings.RecentOccurrences).ToList();
            if (expected.Count == 0)
                continue;
            var held = events.Where(e => e.SeriesMasterId == id && !e.Cancelled && e.Response != "declined"
                                         && With(e, person))
                .Select(e => (e.OriginalStart ?? e.Start).Date).ToHashSet();
            recurring.Add(new Cadence(pattern.Subject.Length > 0 ? pattern.Subject : meetings.First(e => e.SeriesMasterId == id).Subject,
                Recurrences.Describe(pattern), expected.Count, expected.Where(d => !held.Contains(d.Date)).ToList()));
        }

        var reasons = new List<string>();
        if (ring.Name != Asked)
        {
            if (last is null)
                reasons.Add($"no meeting in the last {(now.Date - from).Days} days");
            else if (daysSince > ring.GapDays)
                reasons.Add($"{daysSince} days since the last meeting (more than {ring.GapDays})");
            if (ring.LostOccurrences is { } limit)
                foreach (var c in recurring.Where(c => c.Lost.Count >= limit))
                    reasons.Add($"\"{c.Subject}\": {c.Lost.Count} of the last {c.Checked} lost");
        }
        return new ContactStatus(person, why, last is null ? null : new Contact(last.Start, last.Subject, PeopleAt(last)), daysSince,
            next is null ? null : new Contact(next.Start, next.Subject, PeopleAt(next)), recurring, reasons);
    }

    // ---- table --------------------------------------------------------------------------------------------------

    public static string Table(ContactsReport report, bool flaggedOnly)
    {
        var text = new StringBuilder();
        text.Append($"Contacts as of {report.Now.ToString("ddd dd MMM yyyy HH:mm", En)}: meetings of {report.MaxAttendees} people or fewer, "
                    + $"{report.From.ToString("dd MMM", En)} to {report.To.AddDays(-1).ToString("dd MMM", En)}. ! = flagged.\n");
        var width = report.Rings.SelectMany(r => r.People).Select(p => p.Person.Name.Length).DefaultIfEmpty(10).Max();
        foreach (var ring in report.Rings)
        {
            var people = ring.People.Where(p => !flaggedOnly || p.Flagged).ToList();
            if (people.Count == 0)
                continue;
            text.Append('\n').Append(ring.Ring.Name);
            if (ring.Ring.Name != Asked)
                text.Append($"  (flagged after {ring.Ring.GapDays} days"
                            + (ring.Ring.LostOccurrences is { } n ? $", or {n} of a recurring meeting's last {report.RecentOccurrences} lost" : "") + ")");
            text.Append('\n');
            foreach (var p in people)
            {
                var last = p.Last is null ? $"none since {report.From.ToString("dd MMM", En)}"
                    : $"last {p.Last.Start.ToString("ddd dd MMM", En)} ({Ago(p.DaysSince!.Value)})";
                var next = p.Next is null ? "nothing booked" : $"next {p.Next.Start.ToString("ddd dd MMM", En)}";
                text.Append(p.Flagged ? "! " : "  ").Append(p.Person.Name.PadRight(width)).Append("  ")
                    .Append(last.PadRight(30)).Append(next).Append('\n');
                var indent = new string(' ', width + 4);
                foreach (var c in p.Recurring.Where(c => c.Lost.Count > 0))
                    text.Append(indent).Append($"\"{c.Subject}\" ({c.Pattern}): {c.Lost.Count} of the last {c.Checked} lost ("
                                               + string.Join(", ", c.Lost.Select(d => d.ToString("dd MMM", En))) + ")\n");
                if (p.Flagged && p.Why is not null)
                    text.Append(indent).Append("why: ").Append(p.Why).Append('\n');
            }
        }
        if (flaggedOnly && report.Rings.All(r => r.People.All(p => !p.Flagged)))
            text.Append("\nNo one is flagged.\n");
        return text.ToString();
    }

    /// <summary>"3 days", "1 week", "6 weeks": days under a fortnight, whole weeks after.</summary>
    internal static string Ago(int days) =>
        days == 0 ? "today" : days == 1 ? "yesterday" : days < 14 ? $"{days} days" : $"{days / 7} weeks";

    // ---- JSON ---------------------------------------------------------------------------------------------------

    public static string Json(ContactsReport report, bool flaggedOnly)
    {
        JsonNode? Meeting(Contact? c) => c is null ? null : new JsonObject
        {
            ["start"] = Iso(c.Start), ["subject"] = c.Subject, ["people"] = c.People,
        };
        var rings = new JsonArray();
        foreach (var ring in report.Rings)
        {
            var people = new JsonArray();
            foreach (var p in ring.People.Where(p => !flaggedOnly || p.Flagged))
                people.Add((JsonNode)new JsonObject
                {
                    ["name"] = p.Person.Name, ["address"] = p.Person.Address, ["why"] = p.Why,
                    ["last"] = Meeting(p.Last), ["daysSince"] = p.DaysSince, ["next"] = Meeting(p.Next),
                    ["recurring"] = new JsonArray(p.Recurring.Select(c => (JsonNode?)new JsonObject
                    {
                        ["subject"] = c.Subject, ["pattern"] = c.Pattern, ["checked"] = c.Checked,
                        ["lost"] = new JsonArray(c.Lost.Select(d => (JsonNode?)JsonValue.Create(d.ToString("yyyy-MM-dd", En))).ToArray()),
                    }).ToArray()),
                    ["flagged"] = p.Flagged,
                    ["reasons"] = new JsonArray(p.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                });
            rings.Add((JsonNode)new JsonObject
            {
                ["name"] = ring.Ring.Name, ["gapDays"] = ring.Ring.Name == Asked ? null : ring.Ring.GapDays,
                ["lostOccurrences"] = ring.Ring.LostOccurrences, ["people"] = people,
            });
        }
        var root = new JsonObject
        {
            ["asOf"] = Iso(report.Now), ["from"] = Iso(report.From), ["to"] = Iso(report.To), ["timeZone"] = report.TimeZone,
            ["maxAttendees"] = report.MaxAttendees, ["recentOccurrences"] = report.RecentOccurrences, ["rings"] = rings,
        };
        return PyJson.Dump(root, 2, false) + "\n";
    }

    private static string Iso(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss", En);
}

/// <summary>The dates a recurring meeting is due on, from its pattern (Outlook's: daily, weekly, monthly or yearly,
/// absolute or relative).</summary>
public static class Recurrences
{
    /// <summary>The dates it's due on from `from` up to and including `until`'s date.</summary>
    public static IEnumerable<DateTime> Dates(Recurrence r, DateTime from, DateTime until)
    {
        var last = until.Date;
        if (r.EndDate is { } end && end < last)
            last = end;
        var count = 0;
        for (var day = r.StartDate.Date; day <= last; day = day.AddDays(1))
        {
            if (!Due(r, day))
                continue;
            if (r.Count is { } n && ++count > n)
                yield break;
            if (day >= from.Date)
                yield return day;
        }
    }

    internal static bool Due(Recurrence r, DateTime day)
    {
        var months = (day.Year - r.StartDate.Year) * 12 + day.Month - r.StartDate.Month;
        switch (r.Type)
        {
            case "daily":
                return (day - r.StartDate.Date).Days % r.Interval == 0;
            case "weekly":
            {
                if (!r.DaysOfWeek.Contains(day.DayOfWeek))
                    return false;
                var weeks = (WeekStart(day, r.FirstDayOfWeek) - WeekStart(r.StartDate, r.FirstDayOfWeek)).Days / 7;
                return weeks % r.Interval == 0;
            }
            case "absoluteMonthly":
                return months % r.Interval == 0 && day.Day == Math.Min(r.DayOfMonth, DateTime.DaysInMonth(day.Year, day.Month));
            case "relativeMonthly":
                return months % r.Interval == 0 && Relative(r, day);
            case "absoluteYearly":
                return day.Month == r.Month && (day.Year - r.StartDate.Year) % r.Interval == 0
                       && day.Day == Math.Min(r.DayOfMonth, DateTime.DaysInMonth(day.Year, day.Month));
            case "relativeYearly":
                return day.Month == r.Month && (day.Year - r.StartDate.Year) % r.Interval == 0 && Relative(r, day);
            default:
                return false;
        }
    }

    /// <summary>The first, second, third, fourth or last of its days of the week in the month.</summary>
    private static bool Relative(Recurrence r, DateTime day)
    {
        if (!r.DaysOfWeek.Contains(day.DayOfWeek))
            return false;
        var nth = (day.Day - 1) / 7;
        return r.Index switch
        {
            "first" => nth == 0,
            "second" => nth == 1,
            "third" => nth == 2,
            "fourth" => nth == 3,
            "last" => day.AddDays(7).Month != day.Month,
            _ => false,
        };
    }

    private static DateTime WeekStart(DateTime day, DayOfWeek first) =>
        day.Date.AddDays(-(((int)day.DayOfWeek - (int)first + 7) % 7));

    public static string Describe(Recurrence r) => (r.Type, r.Interval) switch
    {
        ("daily", 1) => "daily",
        ("daily", var n) => $"every {n} days",
        ("weekly", 1) => "weekly",
        ("weekly", 2) => "fortnightly",
        ("weekly", var n) => $"every {n} weeks",
        ("absoluteMonthly" or "relativeMonthly", 1) => "monthly",
        ("absoluteMonthly" or "relativeMonthly", var n) => $"every {n} months",
        (_, 1) => "yearly",
        (_, var n) => $"every {n} years",
    };
}
