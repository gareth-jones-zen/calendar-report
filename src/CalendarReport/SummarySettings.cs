using System.Globalization;
using System.Text.RegularExpressions;
using Tomlyn.Model;

namespace CalendarReport;

/// <summary>One rule of a summary's rule list: its tests (all must hold) and where a matching event's time goes, a
/// group and bucket or a <see cref="Count"/> ("skip", "leave", "not-working"). A rule with <see cref="Use"/> stands
/// for another list, put in at that point; lists are expanded when the settings are read.</summary>
public sealed class Rule
{
    public required string Where { get; init; }
    public string? Use { get; init; }
    public string? Group { get; init; }
    public string? Bucket { get; init; }
    public string? Count { get; init; }
    public Regex? Subject { get; init; }
    public Regex? NotSubject { get; init; }
    public Regex? Category { get; init; }
    public Regex? Organizer { get; init; }
    public Regex? OrganizerTitle { get; init; }
    public string? OneToOneWith { get; init; }
    public string? Mostly { get; init; }
    public bool? OwnBlock { get; init; }
    public bool? Private { get; init; }
    public string? ShowAs { get; init; }
    public int? MinAttendees { get; init; }
    public double? MinHours { get; init; }
    public int? MaxMinutes { get; init; }
    public TimeSpan? StartsFrom { get; init; }
    public TimeSpan? StartsTo { get; init; }
}

/// <summary>A named way of summarising: which rule list, for whom, and how much detail.</summary>
public sealed record View(string Name, string Rules, IReadOnlyList<string> People, string Depth,
    IReadOnlyDictionary<string, string> Split, bool TeamTotal);

/// <summary>The summary's settings: [summary], [view.NAME] and [rules.NAME] (see the README, "Summaries").</summary>
public sealed partial class SummarySettings
{
    public static readonly string[] Depths = ["groups", "buckets", "events"];
    public static readonly string[] Counts = ["skip", "leave", "not-working"];

    private static readonly string[] SummaryKeys =
        ["default_view", "free_bands", "tentative", "round_up_to", "work_week_hours", "work_week", "order"];

    private static readonly string[] ViewKeys = ["rules", "people", "depth", "split", "team_total"];

    private static readonly string[] RuleKeys =
    [
        "use", "group", "bucket", "count", "subject", "not_subject", "category", "organizer", "organizer_title",
        "one_to_one_with", "mostly", "own_block", "private", "show_as", "min_attendees", "min_hours", "max_minutes",
        "starts_between",
    ];

    public string DefaultView { get; init; } = "default";
    public IReadOnlyList<int> FreeBands { get; init; } = [30, 60, 120, 150];
    public bool TentativeBusy { get; init; } = true;
    public int RoundUpTo { get; init; } = 15;
    public double WorkWeekHours { get; init; } = 37.5;

    /// <summary>Per person (an address, or "me"), lower case: their week's hours.</summary>
    public IReadOnlyDictionary<string, double> WorkWeek { get; init; } = new Dictionary<string, double>();

    /// <summary>Groups to show first, in this order; the rest follow in the order the rules name them.</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    public IReadOnlyDictionary<string, View> Views { get; init; } = new Dictionary<string, View>();

    /// <summary>Each rule list, with its <c>use</c>s expanded.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Rule>> Rules { get; init; } = new Dictionary<string, IReadOnlyList<Rule>>();

    /// <summary>Used when the settings have no rule list called "default": free time skipped, your own blocks as
    /// task time, everything else as meetings.</summary>
    public static IReadOnlyList<Rule> BuiltInRules =>
    [
        new() { Where = "built-in", ShowAs = "free", Count = "skip" },
        new() { Where = "built-in", OwnBlock = true, Group = "Task time", Bucket = "Blocked time" },
        new() { Where = "built-in", Group = "Meetings", Bucket = "Meetings" },
    ];

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d-([01]\d|2[0-3]):[0-5]\d\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRange();

    internal static SummarySettings Parse(TomlTable raw, string file, IReadOnlyDictionary<string, IReadOnlyList<string>> groups)
    {
        UsageException Bad(string message) => new($"{file}: {message}");

        TomlTable Table(object value, string where) =>
            value as TomlTable ?? throw Bad($"{where} should be a table.");

        void Strict(TomlTable table, string[] known, string where)
        {
            foreach (var key in table.Keys)
                if (!known.Contains(key))
                    throw Bad($"unknown setting '{key}' in {where} (known: {string.Join(", ", known)}).");
        }

        string Str(object value, string where) =>
            value as string is { Length: > 0 } s ? s : throw Bad($"{where} should be a non-empty string.");

        double Number(object value, string where) => value switch
        {
            long l => l,
            double d => d,
            _ => throw Bad($"{where} should be a number."),
        };

        int Whole(object value, string where) =>
            value is long l && l is >= 0 and <= int.MaxValue ? (int)l : throw Bad($"{where} should be a whole number, 0 or more.");

        IReadOnlyList<string> Strings(object value, string where) =>
            value is TomlArray a && a.All(x => x is string { Length: > 0 }) ? a.Select(x => (string)x!).ToList()
                : throw Bad($"{where} should be a list of strings.");

        Regex Pattern(object value, string where)
        {
            var text = Str(value, where);
            try
            {
                return new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException error)
            {
                throw Bad($"{where} isn't a valid pattern: {error.Message}");
            }
        }

        // ---- [summary] ----
        var summary = raw.TryGetValue("summary", out var s) ? Table(s, "[summary]") : [];
        Strict(summary, SummaryKeys, "[summary]");
        var bands = new List<int> { 30, 60, 120, 150 };
        if (summary.TryGetValue("free_bands", out var fb))
        {
            if (fb is not TomlArray a || a.Count == 0 || a.Any(x => x is not long l || l < 1))
                throw Bad("summary.free_bands should be a list of minutes, such as [30, 60, 120].");
            bands = a.Select(x => (int)(long)x!).ToList();
            if (bands.Zip(bands.Skip(1)).Any(p => p.Second <= p.First))
                throw Bad("summary.free_bands should go up: [30, 60, 120].");
        }
        var tentative = summary.TryGetValue("tentative", out var t) ? Str(t, "summary.tentative") : "busy";
        if (tentative is not ("busy" or "free"))
            throw Bad("summary.tentative should be \"busy\" or \"free\".");
        var week = summary.TryGetValue("work_week_hours", out var wh) ? Number(wh, "summary.work_week_hours") : 37.5;
        if (week is <= 0 or > 168)
            throw Bad("summary.work_week_hours should be more than 0 and at most 168.");
        var perPerson = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (summary.TryGetValue("work_week", out var ww))
            foreach (var (who, hours) in Table(ww, "[summary.work_week]"))
            {
                var h = Number(hours, $"summary.work_week.\"{who}\"");
                if (h is <= 0 or > 168)
                    throw Bad($"summary.work_week.\"{who}\" should be more than 0 and at most 168.");
                perPerson[who.ToLowerInvariant()] = h;
            }

        // ---- [rules.NAME] ----
        var lists = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        if (raw.TryGetValue("rules", out var r))
        {
            foreach (var (name, value) in Table(r, "[rules]"))
            {
                if (value is not TomlTableArray array)
                    throw Bad($"rules.{name} should be a list of rules: [[rules.{name}]].");
                var list = new List<Rule>();
                var n = 0;
                foreach (var rule in array)
                {
                    var where = $"rules.{name} (rule {++n})";
                    Strict(rule, RuleKeys, where);
                    list.Add(ReadRule(rule, where));
                }
                lists[name] = list;
            }
        }

        Rule ReadRule(TomlTable rule, string where)
        {
            string? Text(string key) => rule.TryGetValue(key, out var v) ? Str(v, $"{where} {key}") : null;
            Regex? Rx(string key) => rule.TryGetValue(key, out var v) ? Pattern(v, $"{where} {key}") : null;
            bool? Flag(string key) => rule.TryGetValue(key, out var v)
                ? v as bool? ?? throw Bad($"{where} {key} should be true or false.") : null;

            if (Text("use") is { } use)
            {
                if (rule.Count > 1)
                    throw Bad($"{where}: a rule with use = \"{use}\" can't have anything else.");
                return new Rule { Where = where, Use = use };
            }
            var count = Text("count");
            if (count is not null && !Counts.Contains(count))
                throw Bad($"{where} count should be one of {string.Join(", ", Counts.Select(c => $"\"{c}\""))}.");
            var group = Text("group");
            if (count is null == group is null)
                throw Bad($"{where} needs a group (and bucket), or a count, but not both.");
            foreach (var key in new[] { "one_to_one_with", "mostly" })
                if (Text(key) is { } g && !(key == "one_to_one_with" && g == "manager") && !groups.ContainsKey(g))
                    throw Bad($"{where} {key}: there's no group '{g}' in [groups].");
            TimeSpan? from = null, to = null;
            if (Text("starts_between") is { } between)
            {
                if (!TimeRange().IsMatch(between))
                    throw Bad($"{where} starts_between should be \"hh:mm-hh:mm\", such as \"11:30-14:00\".");
                from = TimeSpan.ParseExact(between[..5], @"hh\:mm", CultureInfo.InvariantCulture);
                to = TimeSpan.ParseExact(between[6..], @"hh\:mm", CultureInfo.InvariantCulture);
            }
            return new Rule
            {
                Where = where, Group = group, Bucket = group is null ? null : Text("bucket") ?? group, Count = count,
                Subject = Rx("subject"), NotSubject = Rx("not_subject"), Category = Rx("category"), Organizer = Rx("organizer"),
                OrganizerTitle = Rx("organizer_title"), OneToOneWith = Text("one_to_one_with"), Mostly = Text("mostly"),
                OwnBlock = Flag("own_block"), Private = Flag("private"), ShowAs = Text("show_as"),
                MinAttendees = rule.TryGetValue("min_attendees", out var ma) ? Whole(ma, $"{where} min_attendees") : null,
                MinHours = rule.TryGetValue("min_hours", out var mh) ? Number(mh, $"{where} min_hours") : null,
                MaxMinutes = rule.TryGetValue("max_minutes", out var mm) ? Whole(mm, $"{where} max_minutes") : null,
                StartsFrom = from, StartsTo = to,
            };
        }

        var expanded = new Dictionary<string, IReadOnlyList<Rule>>(StringComparer.Ordinal);
        List<Rule> Expand(string name, List<string> path)
        {
            if (path.Contains(name))
                throw Bad($"rules.{name} uses itself: {string.Join(" -> ", path.Append(name))}.");
            if (!lists.TryGetValue(name, out var list))
            {
                if (name == "default")
                    return [.. BuiltInRules];
                throw Bad($"{(path.Count == 0 ? "" : $"rules.{path[^1]}: ")}there's no rule list '{name}' ([[rules.{name}]]).");
            }
            var output = new List<Rule>();
            foreach (var rule in list)
                output.AddRange(rule.Use is { } use ? Expand(use, [.. path, name]) : [rule]);
            return output;
        }
        foreach (var name in lists.Keys)
            expanded[name] = Expand(name, []);
        if (!expanded.ContainsKey("default"))
            expanded["default"] = BuiltInRules;

        // ---- [view.NAME] ----
        var views = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new("default", "default", ["me"], "buckets", new Dictionary<string, string>(), false),
        };
        if (raw.TryGetValue("view", out var v))
        {
            foreach (var (name, value) in Table(v, "[view]"))
            {
                var where = $"view.{name}";
                var view = Table(value, $"[{where}]");
                Strict(view, ViewKeys, $"[{where}]");
                var rules = view.TryGetValue("rules", out var vr) ? Str(vr, $"{where}.rules") : "default";
                if (!expanded.ContainsKey(rules))
                    throw Bad($"{where}.rules: there's no rule list '{rules}' ([[rules.{rules}]]).");
                var depth = view.TryGetValue("depth", out var d) ? Str(d, $"{where}.depth") : "groups";
                if (!Depths.Contains(depth))
                    throw Bad($"{where}.depth should be one of {string.Join(", ", Depths.Select(x => $"\"{x}\""))}.");
                var split = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (view.TryGetValue("split", out var sp))
                    foreach (var (key, how) in Table(sp, $"{where}.split"))
                    {
                        var by = Str(how, $"{where}.split.\"{key}\"");
                        if (by is not ("subject" or "person"))
                            throw Bad($"{where}.split.\"{key}\" should be \"subject\" or \"person\".");
                        split[key] = by;
                    }
                views[name] = new View(name, rules, view.TryGetValue("people", out var p) ? Strings(p, $"{where}.people") : ["me"],
                    depth, split, view.TryGetValue("team_total", out var tt) && (tt as bool? ?? throw Bad($"{where}.team_total should be true or false.")));
            }
        }
        var defaultView = summary.TryGetValue("default_view", out var dv) ? Str(dv, "summary.default_view") : "default";
        if (!views.ContainsKey(defaultView))
            throw Bad($"summary.default_view: there's no view '{defaultView}' ([view.{defaultView}]).");

        return new SummarySettings
        {
            DefaultView = defaultView,
            FreeBands = bands,
            TentativeBusy = tentative == "busy",
            RoundUpTo = summary.TryGetValue("round_up_to", out var ru) ? Math.Max(1, Whole(ru, "summary.round_up_to")) : 15,
            WorkWeekHours = week,
            WorkWeek = perPerson,
            Order = summary.TryGetValue("order", out var o) ? Strings(o, "summary.order") : [],
            Views = views,
            Rules = expanded,
        };
    }
}
