using System.Globalization;
using System.Text.Json.Nodes;
using M365Graph;

namespace CalendarReport;

/// <summary>Someone whose schedule is read: their address ("me" for the signed-in user) and display name.</summary>
public sealed record Person(string Address, string Name)
{
    /// <summary>The Graph path for them: /me or /users/{address}.</summary>
    public string GraphPath => Address == "me" ? "/me" : "/users/" + Uri.EscapeDataString(Address);
}

/// <summary>One calendar event, in the report's time zone.</summary>
public sealed record Event(
    DateTime Start, DateTime End, bool AllDay, string ShowAs, string Subject, string? Location, string? Organizer,
    int Attendees, bool Online, string Sensitivity, IReadOnlyList<string> Categories, bool Cancelled, string? Response,
    IReadOnlyList<Attendee>? Invitees = null, string? OrganizerAddress = null);

/// <summary>Someone invited (0.3.0; until then only how many): their name and address as Graph gives them, the type
/// (required, optional, or resource for a room) and their response. The organiser isn't one of them.</summary>
public sealed record Attendee(string? Name, string? Address, string Type, string? Response);

/// <summary>One stretch of a day in someone's work plan: office, remote or timeOff (Graph's workLocationType), in the
/// report's time zone.</summary>
public sealed record WorkSegment(DateTime Start, DateTime End, string Location);

/// <summary>Where schedules come from. GraphSource is the real one; tests give a fake.</summary>
public interface ISource
{
    /// <summary>Who a word means: "me", an address, or a name to look up. Throws UsageException if it's no one, or
    /// more than one person.</summary>
    Person Resolve(string who);

    IReadOnlyList<Event> Events(Person person, DateTime from, DateTime to);

    /// <summary>The work plan, or null where it can't be read: Graph serves only your own.</summary>
    IReadOnlyList<WorkSegment>? WorkPlan(Person person, DateTime from, DateTime to);
}

/// <summary>Microsoft Graph. Reading someone else's calendar needs Calendars.Read.Shared and their calendar shared
/// with you (as in Outlook). Times come back in the report's time zone (Prefer: outlook.timezone).
///
/// <para><b>Only your own work plan can be read.</b> Asked for anyone else's
/// (/users/{id}/settings/workHoursAndLocations/occurrencesView), Graph answers 200 with YOUR work plan, not theirs
/// (found 17 Nov 2026: four colleagues' plans came back identical to the signed-in user's, travel blocks and all).
/// So the work plan is read only for you, and anyone else's is reported as unreadable rather than shown wrongly.</para></summary>
public sealed class GraphSource(GraphClient graph, Config config) : ISource
{
    public const string CalendarScope = "Calendars.Read.Shared";

    private const string EventFields =
        "subject,start,end,isAllDay,showAs,location,organizer,attendees,isOnlineMeeting,sensitivity,categories,isCancelled,responseStatus";

    private IReadOnlyList<string> Scopes => [CalendarScope, config.LookupScope];

    private Dictionary<string, string> Headers => new()
    {
        ["Prefer"] = $"outlook.timezone=\"{config.TimeZone.Id}\"",
        ["ConsistencyLevel"] = "eventual",
    };

    public Person Resolve(string who)
    {
        if (who.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            var me = Get("/me?$select=displayName,mail,userPrincipalName");
            myAddress ??= Text(me, "mail") ?? Text(me, "userPrincipalName");
            return new Person("me", Text(me, "displayName") ?? "me");
        }
        if (who.Contains('@'))
        {
            try
            {
                var user = Get($"/users/{Uri.EscapeDataString(who)}?$select=displayName,mail,userPrincipalName");
                var address = Text(user, "mail") ?? Text(user, "userPrincipalName") ?? who;
                return IsMe(address) ? Resolve("me") : new Person(address, Text(user, "displayName") ?? who);
            }
            catch (GraphRequestException error) when ((int)error.Status == 404)
            {
                throw new UsageException($"No one in the directory has the address {who}.");
            }
        }
        var escaped = who.Replace("'", "''", StringComparison.Ordinal);
        var found = graph.PagesAsync($"/users?$filter=startswith(displayName,'{Uri.EscapeDataString(escaped)}')&$select=displayName,mail,userPrincipalName&$top=10",
                Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>()
            .Where(u => Text(u, "mail") is not null).ToList();
        var exact = found.Where(u => string.Equals(Text(u, "displayName"), who, StringComparison.OrdinalIgnoreCase)).ToList();
        var pick = exact.Count == 1 ? exact[0] : found.Count == 1 ? found[0] : null;
        if (pick is not null)
            return IsMe(Text(pick, "mail")!) ? Resolve("me") : new Person(Text(pick, "mail")!, Text(pick, "displayName") ?? who);
        if (found.Count == 0)
            throw new UsageException($"No one in the directory is called '{who}'. Use their address, or more of their name.");
        throw new UsageException($"'{who}' could be {string.Join(", ", found.Take(6).Select(u => $"{Text(u, "displayName")} ({Text(u, "mail")})"))}"
                                 + (found.Count > 6 ? ", ..." : "") + ". Use their address, or their full name.");
    }

    public IReadOnlyList<Event> Events(Person person, DateTime from, DateTime to)
    {
        var url = $"{person.GraphPath}/calendarView?startDateTime={Utc(from)}&endDateTime={Utc(to)}&$select={EventFields}"
                  + "&$orderby=start/dateTime&$top=250";
        return graph.PagesAsync(url, Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>().Select(ReadEvent).ToList();
    }

    public IReadOnlyList<WorkSegment>? WorkPlan(Person person, DateTime from, DateTime to)
    {
        if (person.Address != "me")
            return null;   // Graph would answer with yours: see the class summary
        var url = $"{person.GraphPath}/settings/workHoursAndLocations/occurrencesView(startDateTime='{Utc(from)}',endDateTime='{Utc(to)}')";
        return graph.PagesAsync(url, Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>()
            .Select(o => new WorkSegment(Time(o["start"]), Time(o["end"]), Text(o, "workLocationType") ?? "unknown"))
            .OrderBy(s => s.Start).ToList();
    }

    private string? myAddress;

    /// <summary>Whether an address is the signed-in user's, so their own work plan can be read for it.</summary>
    private bool IsMe(string address)
    {
        if (myAddress is null)
            Resolve("me");
        return string.Equals(address, myAddress, StringComparison.OrdinalIgnoreCase);
    }

    private JsonObject Get(string url) => graph.GetAsync(url, Scopes, Headers).GetAwaiter().GetResult();

    /// <summary>A time in the report's zone, as the UTC Graph wants in a range.</summary>
    private string Utc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), config.TimeZone)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    internal static Event ReadEvent(JsonObject o)
    {
        var attendees = o["attendees"] as JsonArray;
        return new Event(
            Time(o["start"]), Time(o["end"]),
            Flag(o, "isAllDay"), Text(o, "showAs") ?? "unknown", Text(o, "subject") ?? "",
            (o["location"] as JsonObject) is { } loc && Text(loc, "displayName") is { Length: > 0 } place ? place : null,
            (o["organizer"]?["emailAddress"] as JsonObject) is { } org ? Text(org, "name") ?? Text(org, "address") : null,
            attendees?.Count ?? 0, Flag(o, "isOnlineMeeting"), Text(o, "sensitivity") ?? "normal",
            (o["categories"] as JsonArray)?.Select(c => c?.GetValue<string>() ?? "").Where(c => c.Length > 0).ToList() ?? [],
            Flag(o, "isCancelled"),
            (o["responseStatus"] as JsonObject) is { } r ? Text(r, "response") : null,
            attendees?.OfType<JsonObject>().Select(a => new Attendee(
                (a["emailAddress"] as JsonObject) is { } ea ? Text(ea, "name") : null,
                (a["emailAddress"] as JsonObject) is { } eb ? Text(eb, "address") : null,
                Text(a, "type") ?? "required",
                (a["status"] as JsonObject) is { } st ? Text(st, "response") : null)).ToList() ?? [],
            (o["organizer"]?["emailAddress"] as JsonObject) is { } orgAddress ? Text(orgAddress, "address") : null);
    }

    /// <summary>A Graph dateTimeTimeZone, already in the report's zone (the Prefer header).</summary>
    internal static DateTime Time(JsonNode? node)
    {
        var text = (node as JsonObject) is { } o ? Text(o, "dateTime") : null;
        return text is null ? default : DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    internal static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
