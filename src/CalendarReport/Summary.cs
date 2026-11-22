using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace CalendarReport;

/// <summary>An event and where its rules put it. <see cref="Counted"/> is the minutes it ended up with: rounded up,
/// less any taken by an overlapping event whose rule comes first, on working days only.</summary>
public sealed class Placed(Event e, int priority, string? group, string? bucket, string? count, DateTime countedEnd)
{
    public Event Event { get; } = e;
    public int Priority { get; } = priority;
    public string? Group { get; } = group;
    public string? Bucket { get; } = bucket;
    public string? Count { get; } = count;
    public DateTime CountedEnd { get; } = countedEnd;
    public int Counted { get; set; }
}

public sealed class WeekSummary
{
    public required DateTime WeekOf { get; init; }
    public int Days { get; set; }
    public int Base { get; set; }
    public int Busy { get; set; }
    public int Total => Math.Max(Base, Busy);
    public int Available => Total - Busy;
    public Dictionary<(string Group, string Bucket), int> Buckets { get; } = [];
    public required int[] Blocks { get; init; }
}

public sealed record PersonSummary(Person Person, double WeekHours, IReadOnlyList<DateTime> Leave,
    IReadOnlyList<WeekSummary> Weeks, IReadOnlyList<Placed> Events);

public sealed record SummaryReport(DateTime From, DateTime To, string TimeZone, View View, string Depth,
    IReadOnlyList<(string Group, IReadOnlyList<string> Buckets)> Order, IReadOnlyList<int> Bands,
    IReadOnlyList<PersonSummary> People);

/// <summary>What the rules need to know beyond the event: who the person is, their manager, the groups' addresses and
/// organisers' job titles, each looked up once.</summary>
public sealed class RuleContext(ISource source, Config config)
{
    private readonly Dictionary<string, HashSet<string>> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> titles = new(StringComparer.OrdinalIgnoreCase);

    public string AddressOf(Person person) => (person.Address == "me" ? source.MyAddress() : person.Address).ToLowerInvariant();

    public HashSet<string> Group(string name)
    {
        if (!groups.TryGetValue(name, out var set))
            groups[name] = set = config.Groups[name]   // an address is taken as it is; a name is looked up
                .Select(w => w.Contains('@') ? w.ToLowerInvariant() : AddressOf(source.Resolve(w))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set;
    }

    public string? Manager(Person person)
    {
        if (!managers.TryGetValue(person.Address, out var manager))
            managers[person.Address] = manager = source.Manager(person)?.ToLowerInvariant();
        return manager;
    }

    public string? Title(string address)
    {
        if (!titles.TryGetValue(address, out var title))
            titles[address] = title = source.JobTitle(address);
        return title;
    }

    /// <summary>The first rule the event meets, and its place in the list (lower comes first in an overlap).</summary>
    public (int Priority, Rule? Rule) Classify(Event e, IReadOnlyList<Rule> rules, Person person)
    {
        var self = AddressOf(person);
        var organizer = e.OrganizerAddress?.ToLowerInvariant() ?? "";
        var others = (e.Invitees ?? []).Where(a => a.Type != "resource" && !string.IsNullOrEmpty(a.Address))
            .Select(a => a.Address!.ToLowerInvariant()).ToHashSet();
        if (organizer.Length > 0)
            others.Add(organizer);
        others.Remove(self);
        for (var i = 0; i < rules.Count; i++)
            if (Matches(rules[i], e, person, self, organizer, others))
                return (i, rules[i]);
        return (rules.Count, null);
    }

    private string? owner;

    /// <summary>The owner: the signed-in user (0.7.0), lower case.</summary>
    public string Owner => owner ??= source.MyAddress().ToLowerInvariant();

    /// <summary>The organisation's domain, for <c>external</c>: [slots] org_domain, or the owner's own.</summary>
    public string OrgDomain => config.Slots.OrgDomain?.ToLowerInvariant() ?? (Owner.Contains('@') ? Owner[(Owner.IndexOf('@') + 1)..] : "");

    /// <summary>Whether anyone invited (rooms aside), or the organiser, is outside the organisation.</summary>
    public bool IsExternal(Event e)
    {
        var domain = OrgDomain;
        if (domain.Length == 0)
            return false;
        bool Outside(string? address) => address is { } a && a.Contains('@')
            && !a[(a.IndexOf('@') + 1)..].Equals(domain, StringComparison.OrdinalIgnoreCase)
            && !a.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        return Outside(e.OrganizerAddress) || (e.Invitees ?? []).Any(a => a.Type != "resource" && Outside(a.Address));
    }

    /// <summary>The person's own response to an event, in the rules' words: accepted, tentative, none, organiser or
    /// declined.</summary>
    public static string ResponseOf(Event e) => (e.Response ?? "none").ToLowerInvariant() switch
    {
        "accepted" => "accepted",
        "tentativelyaccepted" or "tentative" => "tentative",
        "organizer" or "organiser" => "organiser",
        "declined" => "declined",
        _ => "none",
    };

    private bool Matches(Rule r, Event e, Person person, string self, string organizer, HashSet<string> others)
    {
        var minutes = (e.End - e.Start).TotalMinutes;
        if (r.Subject is { } subject && !subject.IsMatch(e.Subject)) return false;
        if (r.NotSubject is { } not && not.IsMatch(e.Subject)) return false;
        if (r.Category is { } category && !e.Categories.Any(category.IsMatch)) return false;
        if (r.Organizer is { } org && !org.IsMatch(e.Organizer ?? "")) return false;
        if (r.ShowAs is { } showAs && !string.Equals(e.ShowAs, showAs, StringComparison.OrdinalIgnoreCase)) return false;
        if (r.Private == true && e.Sensitivity != "private") return false;
        if (r.Private == false && e.Sensitivity == "private") return false;
        if (r.MinHours is { } minHours && minutes < minHours * 60) return false;
        if (r.MaxMinutes is { } maxMinutes && minutes > maxMinutes) return false;
        if (r.MinAttendees is { } minAttendees && e.Attendees < minAttendees) return false;
        if (r.StartsFrom is { } from && r.StartsTo is { } to && (e.Start.TimeOfDay < from || e.Start.TimeOfDay > to)) return false;
        if (r.OwnBlock is { } own && own != ((organizer.Length == 0 || organizer == self) && others.Count == 0)) return false;
        if (r.OneToOneWith is { } with)
        {
            if (others.Count != 1)
                return false;
            var other = others.Single();
            var match = with switch
            {
                "manager" => other == Manager(person),
                "owner" => other == Owner,
                _ => Group(with).Contains(other),
            };
            if (!match)
                return false;
        }
        if (r.OwnerOrganizes is { } organizes && organizes != ((organizer.Length == 0 ? self : organizer) == Owner)) return false;
        if (r.OwnerAttends is { } attends
            && attends != (self == Owner || organizer == Owner || (e.Invitees ?? []).Any(a => string.Equals(a.Address, Owner, StringComparison.OrdinalIgnoreCase))))
            return false;
        if (r.External is { } external && external != IsExternal(e)) return false;
        if (r.Recurring is { } recurring && recurring != (e.SeriesMasterId is not null || e.Type is "occurrence" or "exception")) return false;
        if (r.Response is { } responses && !responses.Contains(ResponseOf(e))) return false;
        if (r.Mostly is { } mostly)
        {
            if (others.Count < 2)
                return false;
            var group = Group(mostly);
            var everyone = others.Count + 1;
            if ((others.Count(group.Contains) + 1) * 2 < everyone)
                return false;
        }
        if (r.OrganizerTitle is { } title && !(organizer.Length > 0 && title.IsMatch(Title(organizer) ?? "")))
            return false;
        return true;
    }
}

public static class Summaries
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>An event's length rounded up to the setting's minutes: Outlook's shortened meetings (25 or 50 minutes)
    /// count as the half hour or hour they were booked as.</summary>
    public static DateTime Rounded(DateTime start, DateTime end, int roundUpTo)
    {
        var minutes = (int)Math.Ceiling((end - start).TotalMinutes - 1e-9);
        if (minutes <= 0 || roundUpTo <= 1)
            return end;
        return start.AddMinutes((minutes + roundUpTo - 1) / roundUpTo * roundUpTo);
    }

    public static PersonSummary Summarise(Person person, IReadOnlyList<Event> events, SummarySettings settings,
        IReadOnlyList<Rule> rules, RuleContext context, DateTime from, DateTime to)
    {
        var address = context.AddressOf(person);
        var weekHours = settings.WorkWeek.TryGetValue(person.Address == "me" ? "me" : address, out var h) ? h
            : settings.WorkWeek.TryGetValue(address, out h) ? h : settings.WorkWeekHours;
        var placed = new List<Placed>();
        var leave = new SortedSet<DateTime>();
        var owner = new Dictionary<DateTime, int[]>();   // per day, per minute: the placed event counting it, or -1

        foreach (var e in events)
        {
            if (e.Cancelled || (!settings.TentativeBusy && e.ShowAs == "tentative"))
                continue;
            var (priority, rule) = context.Classify(e, rules, person);
            if (rule?.Count == "skip")
                continue;
            if (rule?.Count == "leave")
            {
                for (var d = e.Start.Date; d < e.End.Date || (d == e.End.Date && e.End.TimeOfDay > TimeSpan.FromHours(12)); d = d.AddDays(1))
                    leave.Add(d);
                continue;
            }
            var end = Rounded(e.Start, e.End, settings.RoundUpTo);
            var p = new Placed(e, priority, rule is null ? "Other" : rule.Group, rule is null ? "Everything else" : rule.Bucket,
                rule?.Count, end);
            var index = placed.Count;
            placed.Add(p);
            var start = e.Start.AddSeconds(-e.Start.Second).AddMilliseconds(-e.Start.Millisecond);
            for (var t = start; t < end; t = t.AddMinutes(1))
            {
                if (t < from || t >= to)
                    continue;
                if (!owner.TryGetValue(t.Date, out var minutes))
                    owner[t.Date] = minutes = Enumerable.Repeat(-1, 1440).ToArray();
                var m = (int)(t - t.Date).TotalMinutes;
                if (minutes[m] < 0 || priority < placed[minutes[m]].Priority)
                    minutes[m] = index;
            }
        }

        var weeks = new List<WeekSummary>();
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || leave.Contains(day))
                continue;
            var monday = Monday(day);
            if (weeks.Count == 0 || weeks[^1].WeekOf != monday)
                weeks.Add(new WeekSummary { WeekOf = monday, Blocks = new int[settings.FreeBands.Count] });
            var week = weeks[^1];
            week.Days++;
            if (!owner.TryGetValue(day, out var minutes))
                continue;
            var last = -1;
            for (var m = 0; m < 1440; m++)
            {
                if (minutes[m] < 0)
                    continue;
                var p = placed[minutes[m]];
                p.Counted++;
                if (p.Count != "not-working")
                {
                    week.Busy++;
                    var key = (p.Group!, p.Bucket!);
                    week.Buckets[key] = week.Buckets.GetValueOrDefault(key) + 1;
                }
                var gap = m - last - 1;
                if (last >= 0 && gap >= settings.FreeBands[0])
                    week.Blocks[settings.FreeBands.Select((b, i) => (b, i)).Last(x => gap >= x.b).i]++;
                last = m;
            }
        }
        foreach (var week in weeks)
            week.Base = (int)Math.Round(weekHours * 60 * week.Days / 5);
        return new PersonSummary(person, weekHours, leave.Where(d => d >= from && d < to && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList(),
            weeks, placed);
    }

    /// <summary>The groups and their buckets in showing order: [summary] order first, then as the rules name them,
    /// then anything else that turned up.</summary>
    public static List<(string Group, IReadOnlyList<string> Buckets)> Order(SummarySettings settings, IReadOnlyList<Rule> rules,
        IEnumerable<PersonSummary> people)
    {
        var buckets = new List<(string Group, string Bucket)>();
        foreach (var r in rules)
            if (r.Group is not null && !buckets.Contains((r.Group, r.Bucket!)))
                buckets.Add((r.Group, r.Bucket!));
        foreach (var key in people.SelectMany(p => p.Weeks).SelectMany(w => w.Buckets.Keys))
            if (!buckets.Contains(key))
                buckets.Add(key);
        var groups = buckets.Select(b => b.Group).Distinct().ToList();
        var ordered = settings.Order.Where(groups.Contains).Concat(groups.Where(g => !settings.Order.Contains(g))).ToList();
        return ordered.Select(g => (g, (IReadOnlyList<string>)buckets.Where(b => b.Group == g).Select(b => b.Bucket).ToList())).ToList();
    }

    // ---- formatting ---------------------------------------------------------------------------------------------

    /// <summary>3h 05m; 25m; "-" for nothing.</summary>
    public static string Hm(int minutes)
    {
        if (minutes == 0)
            return "-";
        var (h, m) = (Math.Abs(minutes) / 60, Math.Abs(minutes) % 60);
        var sign = minutes < 0 ? "-" : "";
        return h > 0 ? $"{sign}{h}h {m:00}m" : $"{sign}{m}m";
    }

    private static string Short(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";

    public static IReadOnlyList<string> BandNames(IReadOnlyList<int> bands) =>
        bands.Select((b, i) => i + 1 < bands.Count ? $"{Short(b)}-{Short(bands[i + 1])}" : $"{Short(b)}+").ToList();

    /// <summary>Everyone's weeks added together, for a team total.</summary>
    public static PersonSummary Team(IReadOnlyList<PersonSummary> people, int bands)
    {
        var weeks = new SortedDictionary<DateTime, WeekSummary>();
        foreach (var w in people.SelectMany(p => p.Weeks))
        {
            if (!weeks.TryGetValue(w.WeekOf, out var t))
                weeks[w.WeekOf] = t = new WeekSummary { WeekOf = w.WeekOf, Blocks = new int[bands] };
            t.Days += w.Days;
            t.Base += w.Total;   // each person's own total, so a busy week's extra isn't lost
            t.Busy += w.Busy;
            foreach (var (key, m) in w.Buckets)
                t.Buckets[key] = t.Buckets.GetValueOrDefault(key) + m;
            for (var i = 0; i < bands; i++)
                t.Blocks[i] += w.Blocks[i];
        }
        return new PersonSummary(new Person("team", $"Team total ({people.Count} people)"), people.Sum(p => p.WeekHours), [],
            weeks.Values.ToList(), people.SelectMany(p => p.Events).ToList());
    }

    public static string Table(SummaryReport report)
    {
        var text = new StringBuilder();
        var names = BandNames(report.Bands);
        var shown = report.People.ToList();
        if (report.View.TeamTotal && shown.Count > 1)
            shown.Add(Team(report.People, report.Bands.Count));
        var width = Math.Max(44, report.Order.SelectMany(o => o.Buckets.Select(b => b.Length + 2).Append(o.Group.Length)).DefaultIfEmpty(0).Max() + 2);
        foreach (var person in shown)
        {
            if (text.Length > 0)
                text.Append('\n');
            var team = person.Person.Address == "team";
            var leave = person.Leave.Count == 0 ? "none" : string.Join(", ", person.Leave.Select(d => d.ToString("ddd dd MMM", En)));
            text.Append(person.Person.Name).Append(team ? "\n" : $"  ({person.WeekHours.ToString("0.##", En)} h week; leave: {leave})\n");
            if (person.Weeks.Count == 0)
            {
                text.Append("  No working days in the period.\n");
                continue;
            }
            var many = person.Weeks.Count > 1;
            var total = person.Weeks.Sum(w => w.Total);
            text.Append(new string(' ', width)).Append(string.Concat(person.Weeks.Select(w => $"{"w/c " + w.WeekOf.ToString("dd MMM", En),12}")));
            text.Append(many ? $"{"total",12}" : "").Append($"{"%",6}\n");

            void Row(string label, IReadOnlyList<int> values, bool percent)
            {
                text.Append(label.PadRight(width)).Append(string.Concat(values.Select(v => $"{Hm(v),12}")));
                if (many)
                    text.Append($"{Hm(values.Sum()),12}");
                if (percent && total > 0)
                    text.Append($"{Math.Round(100.0 * values.Sum() / total),5:0}%");
                text.Append('\n');
            }

            foreach (var (group, buckets) in report.Order)
            {
                var values = person.Weeks.Select(w => w.Buckets.Where(b => b.Key.Group == group).Sum(b => b.Value)).ToList();
                if (values.All(v => v == 0))
                    continue;
                Row(group, values, true);
                if (report.Depth == "groups")
                    continue;
                foreach (var bucket in buckets)
                {
                    var bv = person.Weeks.Select(w => w.Buckets.GetValueOrDefault((group, bucket))).ToList();
                    if (bv.All(v => v == 0))
                        continue;
                    Row("  " + bucket, bv, false);
                    var mine = person.Events.Where(p => p.Group == group && p.Bucket == bucket && p.Counted > 0).ToList();
                    if (report.Depth == "events" && !team)
                    {
                        foreach (var p in mine.OrderBy(p => p.Event.Start))
                            text.Append($"      {p.Event.Start.ToString("ddd dd HH:mm", En)}-{p.Event.End.ToString("HH:mm", En)}  "
                                        + $"{Clip(p.Event.Subject, 50),-50} {Hm(p.Counted),8}{Note(p)}\n");
                    }
                    else if (report.View.Split.TryGetValue($"{group} / {bucket}", out var by) || report.View.Split.TryGetValue(group, out by))
                    {
                        foreach (var (key, m) in mine.GroupBy(p => by == "subject" ? p.Event.Subject : People(p.Event))
                                     .Select(g => (g.Key, g.Sum(p => p.Counted))).OrderByDescending(x => x.Item2))
                            text.Append($"      {Clip(key, width - 6).PadRight(width - 6)}{new string(' ', 12 * (person.Weeks.Count - (many ? 0 : 1)))}{Hm(m),12}\n");
                    }
                }
            }
            Row("Available", person.Weeks.Select(w => w.Available).ToList(), true);
            Row("Week total (the % base)", person.Weeks.Select(w => w.Total).ToList(), true);
            if (report.Depth == "events" && !team)
            {
                var outside = person.Events.Where(p => p.Count == "not-working" && p.Counted > 0).OrderBy(p => p.Event.Start).ToList();
                if (outside.Count > 0)
                {
                    text.Append("Not working (not counted)\n");
                    foreach (var p in outside)
                        text.Append($"      {p.Event.Start.ToString("ddd dd HH:mm", En)}-{p.Event.End.ToString("HH:mm", En)}  {Clip(p.Event.Subject, 50)}\n");
                }
            }
            text.Append("Free blocks between meetings (how many)\n");
            for (var i = 0; i < names.Count; i++)
            {
                var counts = person.Weeks.Select(w => w.Blocks[i]).ToList();
                text.Append(("  " + names[i]).PadRight(width)).Append(string.Concat(counts.Select(c => $"{(c == 0 ? "-" : c.ToString(En)),12}")));
                if (many)
                    text.Append($"{(counts.Sum() == 0 ? "-" : counts.Sum().ToString(En)),12}");
                text.Append('\n');
            }
        }
        if (text.Length == 0)
            text.Append("No one to show.\n");
        return text.ToString();
    }

    private static string Note(Placed p)
    {
        var booked = (int)Math.Round((p.Event.End - p.Event.Start).TotalMinutes);
        var rounded = (int)Math.Round((p.CountedEnd - p.Event.Start).TotalMinutes);
        var notes = new List<string>();
        if (rounded != booked)
            notes.Add($"booked {Hm(booked)}");
        if (p.Counted < rounded)
            notes.Add("overlap");
        return notes.Count == 0 ? "" : $"  ({string.Join(", ", notes)})";
    }

    private static string People(Event e) =>
        string.Join(", ", (e.Invitees ?? []).Where(a => a.Type != "resource").Select(a => a.Name ?? a.Address ?? "").Order());

    private static string Clip(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    public static string Json(SummaryReport report)
    {
        var names = BandNames(report.Bands);

        JsonObject Week(WeekSummary w) => new()
        {
            ["weekOf"] = w.WeekOf.ToString("yyyy-MM-dd", En), ["days"] = w.Days, ["base"] = w.Base, ["busy"] = w.Busy,
            ["total"] = w.Total, ["available"] = w.Available,
            ["groups"] = new JsonArray(report.Order.Where(o => w.Buckets.Keys.Any(k => k.Group == o.Group)).Select(o => (JsonNode?)new JsonObject
            {
                ["name"] = o.Group, ["minutes"] = w.Buckets.Where(b => b.Key.Group == o.Group).Sum(b => b.Value),
                ["buckets"] = new JsonArray(o.Buckets.Where(b => w.Buckets.ContainsKey((o.Group, b))).Select(b => (JsonNode?)new JsonObject
                {
                    ["name"] = b, ["minutes"] = w.Buckets[(o.Group, b)],
                }).ToArray()),
            }).ToArray()),
            ["freeBlocks"] = new JsonObject(names.Select((n, i) => KeyValuePair.Create(n, (JsonNode?)w.Blocks[i]))),
        };

        JsonObject Person(PersonSummary p, bool events)
        {
            var item = new JsonObject
            {
                ["address"] = p.Person.Address, ["name"] = p.Person.Name, ["workWeekHours"] = p.WeekHours,
                ["leave"] = new JsonArray(p.Leave.Select(d => (JsonNode?)d.ToString("yyyy-MM-dd", En)).ToArray()),
                ["weeks"] = new JsonArray(p.Weeks.Select(w => (JsonNode?)Week(w)).ToArray()),
            };
            if (events)
                item["events"] = new JsonArray(p.Events.OrderBy(e => e.Event.Start).Select(e => (JsonNode?)new JsonObject
                {
                    ["start"] = e.Event.Start.ToString("yyyy-MM-ddTHH:mm:ss", En), ["end"] = e.Event.End.ToString("yyyy-MM-ddTHH:mm:ss", En),
                    ["subject"] = e.Event.Subject, ["group"] = e.Group, ["bucket"] = e.Bucket, ["count"] = e.Count,
                    ["counted"] = e.Counted,
                }).ToArray());
            return item;
        }

        var root = new JsonObject
        {
            ["from"] = report.From.ToString("yyyy-MM-ddTHH:mm:ss", En), ["to"] = report.To.ToString("yyyy-MM-ddTHH:mm:ss", En),
            ["timeZone"] = report.TimeZone, ["view"] = report.View.Name,
            ["people"] = new JsonArray(report.People.Select(p => (JsonNode?)Person(p, true)).ToArray()),
        };
        if (report.View.TeamTotal && report.People.Count > 1)
            root["team"] = Person(Team(report.People, report.Bands.Count), false);
        return PyJson.Dump(root, 2, false) + "\n";
    }
}
