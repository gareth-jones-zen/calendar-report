using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PyCompat;

namespace CalendarReport;

/// <summary>One person's part of a report: their events, their work plan, or both (null where it wasn't asked for, or
/// can't be read: WorkPlanUnreadable says which).</summary>
public sealed record PersonSchedule(Person Person, IReadOnlyList<Event>? Events, IReadOnlyList<WorkSegment>? WorkPlan,
    bool WorkPlanUnreadable = false)
{
    public const string Unreadable = "only your own work plan can be read";
}

/// <summary>What was read: from (inclusive) to (exclusive), in the report's time zone.</summary>
public sealed record Report(DateTime From, DateTime To, string TimeZone, IReadOnlyList<PersonSchedule> People);

public static class Reports
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>The people named (addresses, names, "me" or group names from the settings), each once, in order.</summary>
    public static List<Person> People(IReadOnlyList<string> who, Config config, ISource source)
    {
        var words = new List<string>();
        foreach (var word in who.Count == 0 ? ["me"] : who)
        {
            foreach (var part in word.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (config.Groups.TryGetValue(part, out var members))
                    words.AddRange(members);
                else
                    words.Add(part);
            }
        }
        var people = new List<Person>();
        foreach (var word in words)
        {
            var person = source.Resolve(word);
            if (!people.Any(p => string.Equals(p.Address, person.Address, StringComparison.OrdinalIgnoreCase)))
                people.Add(person);
        }
        return people;
    }

    public static Report Read(IReadOnlyList<Person> people, ISource source, DateTime from, DateTime to, string zone,
        bool events, bool workPlan, bool cancelled) =>
        new(from, to, zone, people.Select(p =>
        {
            var plan = workPlan ? source.WorkPlan(p, from, to) : null;
            return new PersonSchedule(p, events ? source.Events(p, from, to).Where(e => cancelled || !e.Cancelled).ToList() : null,
                plan, workPlan && plan is null);
        }).ToList());

    // ---- table --------------------------------------------------------------------------------------------------

    public static string Table(Report report)
    {
        var text = new StringBuilder();
        var days = Days(report).ToList();
        foreach (var person in report.People)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append(person.Person.Address == "me" ? person.Person.Name : $"{person.Person.Name} <{person.Person.Address}>").Append('\n');
            if (person.WorkPlanUnreadable)
                text.Append($"  (work plan: {PersonSchedule.Unreadable})\n");
            if (person.WorkPlanUnreadable && person.Events is null)
                continue;
            foreach (var day in days)
            {
                var plan = person.WorkPlan is null ? null : Where(person.WorkPlan, day);
                var events = person.Events?.Where(e => On(e, day)).OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ToList();
                if (person.Events is not null && events!.Count == 0 && plan is null or "")
                    continue;   // a day with nothing to show (a weekend, usually)
                text.Append($"  {day.ToString("ddd dd MMM", En)}");
                if (plan is not null)
                    text.Append("   ").Append(plan.Length == 0 ? "no work plan" : plan);
                text.Append('\n');
                foreach (var e in events ?? [])
                    text.Append("    ").Append(Line(e, day)).Append('\n');
            }
        }
        if (text.Length == 0)
            text.Append("No one to show.\n");
        return text.ToString();
    }

    private static string Line(Event e, DateTime day)
    {
        var when = e.AllDay ? "all day    " : $"{Clock(e.Start, day)}-{Clock(e.End, day)}";
        var notes = new List<string>();
        if (e.Attendees > 1)
            notes.Add($"{e.Attendees} people");
        if (e.Online)
            notes.Add("online");
        if (e.Location is { } place && !(e.Online && place.Contains("Teams", StringComparison.OrdinalIgnoreCase)))
            notes.Add(place);
        if (e.Response is "declined" or "tentativelyAccepted" or "notResponded")
            notes.Add(e.Response == "tentativelyAccepted" ? "tentative" : e.Response == "notResponded" ? "not answered" : "declined");
        if (e.Cancelled)
            notes.Add("cancelled");
        if (e.Sensitivity is "private" or "confidential")
            notes.Add(e.Sensitivity);
        var subject = e.Subject.Length == 0 ? "(no subject)" : e.Subject;
        return $"{when}  {ShowAs(e.ShowAs),-9} {subject}" + (notes.Count > 0 ? "  · " + string.Join(" · ", notes) : "");
    }

    /// <summary>HH:mm, or the date too when the time falls on another day (an event that runs past midnight).</summary>
    private static string Clock(DateTime time, DateTime day) =>
        time.Date == day.Date ? time.ToString("HH:mm", En) : time == day.AddDays(1) ? "24:00" : time.ToString("dd/MM HH:mm", En);

    private static string ShowAs(string showAs) => showAs switch
    {
        "oof" => "away",
        "workingElsewhere" => "elsewhere",
        _ => showAs,
    };

    /// <summary>The day's work plan: "office 08:30-17:00, remote 13:00-17:30"; "" if there's none.</summary>
    internal static string Where(IReadOnlyList<WorkSegment> plan, DateTime day) =>
        string.Join(", ", plan.Where(s => s.Start < day.AddDays(1) && s.End > day)
            .Select(s => $"{Place(s.Location)} {Clock(Max(s.Start, day), day)}-{Clock(Min(s.End, day.AddDays(1)), day)}"));

    private static string Place(string location) => location switch
    {
        "timeOff" => "time off",
        _ => location,
    };

    private static bool On(Event e, DateTime day) =>
        e.Start < day.AddDays(1) && (e.End > day || (e.End == e.Start && e.Start.Date == day));

    private static IEnumerable<DateTime> Days(Report report)
    {
        for (var day = report.From.Date; day < report.To; day = day.AddDays(1))
            yield return day;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    // ---- JSON ---------------------------------------------------------------------------------------------------

    public static string Json(Report report)
    {
        var people = new JsonArray();
        foreach (var p in report.People)
        {
            var item = new JsonObject { ["address"] = p.Person.Address, ["name"] = p.Person.Name };
            if (p.Events is not null)
                item["events"] = new JsonArray(p.Events.Select(e => (JsonNode?)new JsonObject
                {
                    ["start"] = Iso(e.Start), ["end"] = Iso(e.End), ["allDay"] = e.AllDay, ["showAs"] = e.ShowAs,
                    ["subject"] = e.Subject, ["location"] = e.Location, ["organizer"] = e.Organizer, ["attendees"] = e.Attendees,
                    ["online"] = e.Online, ["sensitivity"] = e.Sensitivity,
                    ["categories"] = new JsonArray(e.Categories.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                    ["cancelled"] = e.Cancelled, ["response"] = e.Response,
                }).ToArray());
            if (p.WorkPlanUnreadable)
                item["workPlanNote"] = PersonSchedule.Unreadable;
            if (p.WorkPlan is not null)
                item["workPlan"] = new JsonArray(p.WorkPlan.Select(s => (JsonNode?)new JsonObject
                {
                    ["start"] = Iso(s.Start), ["end"] = Iso(s.End), ["location"] = s.Location,
                }).ToArray());
            people.Add((JsonNode)item);
        }
        var root = new JsonObject
        {
            ["from"] = Iso(report.From), ["to"] = Iso(report.To), ["timeZone"] = report.TimeZone, ["people"] = people,
        };
        return PyJson.Dump(root, 2, false) + "\n";
    }

    private static string Iso(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss", En);

    // ---- CSV ----------------------------------------------------------------------------------------------------

    /// <summary>One row per event (with the day's work plan in a column when both were read), or, for a work plan
    /// alone, one row per stretch of a day.</summary>
    public static string Csv(Report report)
    {
        var text = new StringBuilder();
        var events = report.People.Any(p => p.Events is not null);
        var plans = report.People.Any(p => p.WorkPlan is not null);
        if (events)
        {
            text.Append(Row(["address", "name", "date", "start", "end", "all_day", "show_as", "subject", "location", "organizer",
                "attendees", "online", "sensitivity", "categories", "cancelled", "response", .. (plans ? new[] { "work_plan" } : [])]));
            foreach (var p in report.People)
                foreach (var e in p.Events ?? [])
                    text.Append(Row([p.Person.Address, p.Person.Name, e.Start.ToString("yyyy-MM-dd", En), Iso(e.Start), Iso(e.End),
                        Bool(e.AllDay), e.ShowAs, e.Subject, e.Location ?? "", e.Organizer ?? "", e.Attendees.ToString(En), Bool(e.Online),
                        e.Sensitivity, string.Join(";", e.Categories), Bool(e.Cancelled), e.Response ?? "",
                        .. (plans ? new[] { p.WorkPlan is null ? (p.WorkPlanUnreadable ? "(not readable)" : "") : Where(p.WorkPlan, e.Start.Date) } : [])]));
        }
        else
        {
            text.Append(Row(["address", "name", "date", "start", "end", "location"]));
            foreach (var p in report.People)
                foreach (var s in p.WorkPlan ?? [])
                    text.Append(Row([p.Person.Address, p.Person.Name, s.Start.ToString("yyyy-MM-dd", En), Iso(s.Start), Iso(s.End), s.Location]));
        }
        return text.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Row(IEnumerable<string> cells) =>
        string.Join(",", cells.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + c.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : c)) + "\r\n";
}
