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

/// <summary>One calendar event, in the report's time zone. Type (singleInstance, occurrence or exception),
/// SeriesMasterId and OriginalStart (when an occurrence was first due, before any move) place an occurrence in its
/// recurring meeting (0.5.0, for `contacts`). FreeBusy (0.8.0) marks an event read through free/busy (getSchedule)
/// because the person's calendar itself was refused: only its times, show-as, subject and location (where their
/// sharing shows them) and whether it's private are known; organiser, attendees, response and recurrence aren't.</summary>
public sealed record Event(
    DateTime Start, DateTime End, bool AllDay, string ShowAs, string Subject, string? Location, string? Organizer,
    int Attendees, bool Online, string Sensitivity, IReadOnlyList<string> Categories, bool Cancelled, string? Response,
    IReadOnlyList<Attendee>? Invitees = null, string? OrganizerAddress = null,
    string? Type = null, string? SeriesMasterId = null, DateTime? OriginalStart = null, bool FreeBusy = false);

/// <summary>A recurring meeting's pattern, as Graph's patternedRecurrence gives it: the pattern type (daily, weekly,
/// absoluteMonthly, relativeMonthly, absoluteYearly, relativeYearly), its interval and days, and the range (from
/// StartDate; to EndDate, or for Count occurrences, or with no end). StartTime is the series' time of day.</summary>
public sealed record Recurrence(
    string Subject, string Type, int Interval, IReadOnlyList<DayOfWeek> DaysOfWeek, int DayOfMonth, int Month,
    string Index, DayOfWeek FirstDayOfWeek, DateTime StartDate, DateTime? EndDate, int? Count, TimeSpan StartTime);

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

    /// <summary>Someone's schedule through free/busy (Graph's getSchedule), as Outlook shows a colleague's calendar
    /// when it isn't shared in full: times, show-as, and subject and location where their sharing shows them. Each
    /// event has FreeBusy set. Throws GraphRequestException (403) when even that is refused.</summary>
    IReadOnlyList<Event> FreeBusy(Person person, DateTime from, DateTime to);

    /// <summary>The work plan, or null where it can't be read: Graph serves only your own.</summary>
    IReadOnlyList<WorkSegment>? WorkPlan(Person person, DateTime from, DateTime to);

    /// <summary>The signed-in user's address (what "me" means).</summary>
    string MyAddress();

    /// <summary>Their manager's address, from the directory; null if they have none.</summary>
    string? Manager(Person person);

    /// <summary>The job title the directory gives an address; null for someone outside the organisation.</summary>
    string? JobTitle(string address);

    /// <summary>A recurring meeting's pattern, from its series master in their calendar; null if it can't be read.</summary>
    Recurrence? Series(Person person, string seriesMasterId);

    /// <summary>The organisation's meeting rooms, from the directory's places (Graph's /places, 0.10.0); null when
    /// that's refused (the rooms permission isn't granted), so the settings' list is used instead.</summary>
    IReadOnlyList<Room>? Rooms();

    /// <summary>When each of these rooms is booked, through free/busy (getSchedule), by address; null for a room whose
    /// schedule can't be read.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<(DateTime Start, DateTime End)>?> RoomBookings(
        IReadOnlyList<string> addresses, DateTime from, DateTime to);

    /// <summary>Their working hours as their Outlook has them (getSchedule's workingHours, 0.12.0), in the report's
    /// time zone; null when they can't be read.</summary>
    WorkingHours? WorkingHours(Person person, DateTime day);

    /// <summary>Their Teams status (0.12.0), with the presence permission asked for silently only: never a browser,
    /// and never a failure of the run. <paramref name="id"/> is their directory object id when known.</summary>
    PresenceRead Presence(Person person, string? id);

    /// <summary>Their automatic reply (MailTips, 0.12.0): null when it's off or can't be read; "" when it's on with no
    /// text; else its first line.</summary>
    string? AutoReply(Person person);

    /// <summary>What the directory lists for them (0.12.0): object id and work numbers; null when it can't be read.</summary>
    DirectoryContact? Contact(Person person);
}

/// <summary>A meeting room (0.10.0): its name and address, how many it seats (null: not known, which counts as
/// fitting), its floor (as the directory or the settings give it) and building.</summary>
public sealed record Room(string Name, string Address, int? Capacity, string? Floor, string? Building = null);

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
        "subject,start,end,isAllDay,showAs,location,organizer,attendees,isOnlineMeeting,sensitivity,categories,isCancelled,responseStatus,"
        + "type,seriesMasterId,originalStart";

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
                // /users/{x} takes the sign-in name, which isn't always the email address (andy.wilson@ can sign in
                // as andrew.wilson@), so look for the address too.
                var quoted = who.Replace("'", "''", StringComparison.Ordinal);
                var match = graph.PagesAsync($"/users?$filter=mail eq '{Uri.EscapeDataString(quoted)}'&$select=displayName,mail,userPrincipalName",
                    Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>().FirstOrDefault();
                if (match is null)
                    throw new UsageException($"No one in the directory has the address {who}.");
                var address = Text(match, "mail") ?? who;
                return IsMe(address) ? Resolve("me") : new Person(address, Text(match, "displayName") ?? who);
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
        return graph.PagesAsync(url, Scopes, Headers).ToBlockingEnumerable().OfType<JsonObject>()
            .Select(o => ReadEvent(o, config.TimeZone)).ToList();
    }

    /// <summary>getSchedule takes at most 62 days at a time.</summary>
    private const int FreeBusyChunkDays = 60;

    public IReadOnlyList<Event> FreeBusy(Person person, DateTime from, DateTime to)
    {
        var address = person.Address == "me" ? MyAddress() : person.Address;
        var events = new List<Event>();
        for (var start = from; start < to; start = start.AddDays(FreeBusyChunkDays))
        {
            var end = start.AddDays(FreeBusyChunkDays) < to ? start.AddDays(FreeBusyChunkDays) : to;
            var body = new JsonObject
            {
                ["schedules"] = new JsonArray(address),
                ["startTime"] = When(start),
                ["endTime"] = When(end),
                ["availabilityViewInterval"] = 15,
            };
            var found = graph.RequestAsync("/me/calendar/getSchedule", HttpMethod.Post, body, [config.FreeBusyScope], Headers, policy: GraphRetryPolicy.Read)
                .GetAwaiter().GetResult();
            var schedule = (found["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            if (schedule is null || schedule["error"] is JsonObject || schedule["scheduleItems"] is not JsonArray items)
            {
                var why = (schedule?["error"] as JsonObject) is { } error ? Text(error, "message") ?? "refused" : "no schedule returned";
                throw new GraphRequestException(System.Net.HttpStatusCode.Forbidden, "POST", "/me/calendar/getSchedule", why);
            }
            events.AddRange(items.OfType<JsonObject>().Select(ReadScheduleItem));
        }
        return events.DistinctBy(e => (e.Start, e.End, e.Subject)).OrderBy(e => e.Start).ToList();
    }

    public IReadOnlyList<Room>? Rooms()
    {
        try
        {
            // Never a browser: a permission not yet granted is a fallback to the settings, not a sign-in.
            graph.TokenAsync([config.RoomsScope], interactive: false).GetAwaiter().GetResult();
            return graph.PagesAsync("/places/microsoft.graph.room?$top=100", [config.RoomsScope], Headers).ToBlockingEnumerable()
                .OfType<JsonObject>().Select(ReadRoom).Where(r => r.Address.Length > 0)
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (SignInNeededException)
        {
            return null;
        }
        catch (GraphRequestException error) when ((int)error.Status is 401 or 403)
        {
            return null;
        }
    }

    /// <summary>A Graph room place.</summary>
    internal static Room ReadRoom(JsonObject o)
    {
        int? capacity = o["capacity"] is JsonValue c && c.TryGetValue<int>(out var n) && n > 0 ? n : null;
        string? floor = o["floorNumber"] is JsonValue f && f.TryGetValue<int>(out var number) ? number.ToString(CultureInfo.InvariantCulture)
            : Text(o, "floorLabel") is { Length: > 0 } label ? label : null;
        return new Room(Text(o, "displayName") ?? Text(o, "emailAddress") ?? "", Text(o, "emailAddress") ?? "", capacity, floor,
            Text(o, "building") is { Length: > 0 } building ? building : null);
    }

    /// <summary>getSchedule takes at most 20 schedules a call here (to be safe).</summary>
    private const int SchedulesPerCall = 20;

    public IReadOnlyDictionary<string, IReadOnlyList<(DateTime Start, DateTime End)>?> RoomBookings(
        IReadOnlyList<string> addresses, DateTime from, DateTime to)
    {
        var found = new Dictionary<string, List<(DateTime, DateTime)>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in addresses.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(SchedulesPerCall))
        {
            for (var start = from; start < to; start = start.AddDays(FreeBusyChunkDays))
            {
                var end = start.AddDays(FreeBusyChunkDays) < to ? start.AddDays(FreeBusyChunkDays) : to;
                var body = new JsonObject
                {
                    ["schedules"] = new JsonArray([.. batch.Select(a => (JsonNode)JsonValue.Create(a))]),
                    ["startTime"] = When(start),
                    ["endTime"] = When(end),
                    ["availabilityViewInterval"] = 15,
                };
                var reply = graph.RequestAsync("/me/calendar/getSchedule", HttpMethod.Post, body, [config.FreeBusyScope], Headers, policy: GraphRetryPolicy.Read)
                    .GetAwaiter().GetResult();
                foreach (var schedule in (reply["value"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    if (Text(schedule, "scheduleId") is not { } id)
                        continue;
                    if (schedule["error"] is JsonObject || schedule["scheduleItems"] is not JsonArray items)
                    {
                        found[id] = null;
                        continue;
                    }
                    if (found.TryGetValue(id, out var known) && known is null)
                        continue;
                    if (known is null)
                        found[id] = known = [];
                    known.AddRange(items.OfType<JsonObject>()
                        .Where(i => !string.Equals(Text(i, "status"), "free", StringComparison.OrdinalIgnoreCase))
                        .Select(i => (Time(i["start"]), Time(i["end"]))));
                }
            }
        }
        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(a => a,
            a => found.TryGetValue(a, out var b) && b is not null ? (IReadOnlyList<(DateTime, DateTime)>?)b.Distinct().OrderBy(x => x.Item1).ToList() : null,
            StringComparer.OrdinalIgnoreCase);
    }

    public WorkingHours? WorkingHours(Person person, DateTime day)
    {
        var address = person.Address == "me" ? MyAddress() : person.Address;
        var body = new JsonObject
        {
            ["schedules"] = new JsonArray(address),
            ["startTime"] = When(day.Date),
            ["endTime"] = When(day.Date.AddDays(1)),
            ["availabilityViewInterval"] = 60,
        };
        try
        {
            var reply = graph.RequestAsync("/me/calendar/getSchedule", HttpMethod.Post, body, [config.FreeBusyScope], Headers, policy: GraphRetryPolicy.Read)
                .GetAwaiter().GetResult();
            var schedule = (reply["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            return schedule?["workingHours"] is JsonObject hours && schedule["error"] is not JsonObject
                ? ReadWorkingHours(hours, day, config.TimeZone) : null;
        }
        catch (GraphRequestException error) when ((int)error.Status is 400 or 403 or 404)
        {
            return null;
        }
    }

    /// <summary>getSchedule's workingHours (days, start and end in their own time zone) as the report's zone sees them
    /// on this day. A time zone this PC can't find (a "Customized Time Zone") is taken as the report's.</summary>
    internal static WorkingHours? ReadWorkingHours(JsonObject hours, DateTime day, TimeZoneInfo zone)
    {
        static TimeSpan? Clock(string? text) =>
            text is { Length: >= 5 } t && TimeSpan.TryParse(t.Length > 8 ? t[..8] : t, CultureInfo.InvariantCulture, out var span) ? span : null;
        if (Clock(Text(hours, "startTime")) is not { } start || Clock(Text(hours, "endTime")) is not { } end)
            return null;
        var days = (hours["daysOfWeek"] as JsonArray)?.Select(d => d is JsonValue v && v.TryGetValue<string>(out var s)
                && Enum.TryParse<DayOfWeek>(s, true, out var dow) ? (DayOfWeek?)dow : null).OfType<DayOfWeek>().ToList() ?? [];
        var theirs = zone;
        if ((hours["timeZone"] as JsonObject) is { } tz && Text(tz, "name") is { Length: > 0 } name)
        {
            try
            {
                theirs = TimeZoneInfo.FindSystemTimeZoneById(name);
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        TimeSpan Local(TimeSpan at)
        {
            var converted = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(day.Date + at, DateTimeKind.Unspecified), theirs, zone);
            return converted.Date < day.Date ? TimeSpan.Zero : converted.Date > day.Date ? TimeSpan.FromHours(24) : converted.TimeOfDay;
        }
        var from = Local(start);
        var to = end == TimeSpan.Zero ? TimeSpan.FromHours(24) : Local(end);
        return to > from ? new WorkingHours(days, from, to) : null;
    }

    private readonly Dictionary<string, DirectoryContact?> contacts = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryContact? Contact(Person person)
    {
        if (contacts.TryGetValue(person.Address, out var known))
            return known;
        JsonObject? user = null;
        foreach (var select in new[] { "id,businessPhones,mobilePhone", "id" })
        {
            try
            {
                user = Get($"{person.GraphPath}?$select={select}");
                break;
            }
            catch (GraphRequestException error) when ((int)error.Status is 400 or 403 or 404)
            {
            }
        }
        var found = user is null ? null : new DirectoryContact(Text(user, "id"),
            (user["businessPhones"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .OfType<string>().ToList() ?? [],
            Text(user, "mobilePhone"));
        contacts[person.Address] = found;
        return found;
    }

    public PresenceRead Presence(Person person, string? id)
    {
        var scope = config.Free.PresenceScope;
        try
        {
            // Silently only: a permission not yet granted is a line saying so, never a browser or a failed run, and
            // the shared sign-in is left as it was.
            graph.TokenAsync([scope], interactive: false).GetAwaiter().GetResult();
            var path = person.Address == "me" ? "/me/presence"
                : "/users/" + Uri.EscapeDataString(id ?? Contact(person)?.Id ?? person.Address) + "/presence";
            var reply = graph.RequestAsync(path, HttpMethod.Get, null, [scope], null, interactive: false).GetAwaiter().GetResult();
            return new PresenceRead("shown", ReadPresence(reply));
        }
        catch (SignInNeededException)
        {
            return new PresenceRead("not-granted", null, $"{scope} not granted yet");
        }
        catch (GraphRequestException error) when ((int)error.Status is 403 or 404)
        {
            return new PresenceRead("not-available", null, $"{(int)error.Status}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var why = $"{error.GetType().Name}: {error.Message}";
            return new PresenceRead("failed", null, why.Length > 160 ? why[..160] : why);
        }
    }

    /// <summary>A presence resource.</summary>
    internal static PresenceInfo ReadPresence(JsonObject o)
    {
        var message = (o["statusMessage"]?["message"] as JsonObject) is { } m ? Free.FirstLine(Text(m, "content")) : null;
        var ooo = o["outOfOfficeSettings"] as JsonObject;
        var where = o["workLocation"] is JsonObject wl ? Text(wl, "workLocationType") ?? Text(wl, "location")
            : Text(o, "workLocation");
        return new PresenceInfo(Text(o, "availability") ?? "", Text(o, "activity") ?? "", message,
            ooo is not null && Flag(ooo, "isOutOfOffice"), ooo is not null ? Text(ooo, "message") : null,
            where is { Length: > 0 } && !where.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? where : null);
    }

    public string? AutoReply(Person person)
    {
        var scope = config.Free.AutoReplyScope;
        var address = person.Address == "me" ? MyAddress() : person.Address;
        try
        {
            graph.TokenAsync([scope], interactive: false).GetAwaiter().GetResult();
            var body = new JsonObject
            {
                ["EmailAddresses"] = new JsonArray(address),
                ["MailTipsOptions"] = "automaticReplies",
            };
            var reply = graph.RequestAsync("/me/getMailTips", HttpMethod.Post, body, [scope], null, interactive: false, policy: GraphRetryPolicy.Read)
                .GetAwaiter().GetResult();
            var tips = (reply["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            return ReadAutoReply(tips);
        }
        catch (Exception error) when (error is SignInNeededException or GraphRequestException)
        {
            return null;
        }
    }

    /// <summary>A MailTips entry's automatic reply: null when off; "" when on with no text; else its first line.</summary>
    internal static string? ReadAutoReply(JsonObject? tips)
    {
        if (tips?["automaticReplies"] is not JsonObject replies || Text(replies, "message") is not { } message || message.Trim().Length == 0)
            return null;
        return Free.FirstLine(message) ?? "";
    }

    private JsonObject When(DateTime t) => new()
    {
        ["dateTime"] = t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        ["timeZone"] = config.TimeZone.Id,
    };

    /// <summary>A getSchedule scheduleItem as an event (FreeBusy set). An item from midnight to midnight is all-day.</summary>
    internal static Event ReadScheduleItem(JsonObject item)
    {
        var start = Time(item["start"]);
        var end = Time(item["end"]);
        return new Event(start, end, start.TimeOfDay == TimeSpan.Zero && end.TimeOfDay == TimeSpan.Zero && end > start,
            Text(item, "status") ?? "busy", Text(item, "subject") ?? "",
            Text(item, "location") is { Length: > 0 } location ? location : null, null, 0, false,
            Flag(item, "isPrivate") ? "private" : "normal", [], false, null, null, null, null, null, null, FreeBusy: true);
    }

    public Recurrence? Series(Person person, string seriesMasterId)
    {
        try
        {
            var master = Get($"{person.GraphPath}/events/{Uri.EscapeDataString(seriesMasterId)}?$select=subject,start,recurrence");
            return ReadRecurrence(master);
        }
        catch (GraphRequestException error) when ((int)error.Status is 400 or 403 or 404)
        {
            return null;
        }
    }

    /// <summary>A series master's patternedRecurrence; null if it has none, or one this can't follow.</summary>
    internal static Recurrence? ReadRecurrence(JsonObject master)
    {
        if (master["recurrence"] is not JsonObject recurrence || recurrence["pattern"] is not JsonObject pattern
            || recurrence["range"] is not JsonObject range || Text(pattern, "type") is not { } type
            || !DateTime.TryParseExact(Text(range, "startDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
            return null;
        static int Number(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        static DayOfWeek? Day(string? name) => Enum.TryParse<DayOfWeek>(name, true, out var d) ? d : null;
        var days = (pattern["daysOfWeek"] as JsonArray)?.Select(d => Day(d?.GetValue<string>())).OfType<DayOfWeek>().ToList() ?? [];
        var rangeType = Text(range, "type") ?? "noEnd";
        DateTime? endDate = rangeType == "endDate"
            && DateTime.TryParseExact(Text(range, "endDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            ? end : null;
        int? count = rangeType == "numbered" && Number(range, "numberOfOccurrences") > 0 ? Number(range, "numberOfOccurrences") : null;
        return new Recurrence(Text(master, "subject") ?? "", type, Math.Max(1, Number(pattern, "interval")), days,
            Number(pattern, "dayOfMonth"), Number(pattern, "month"), Text(pattern, "index") ?? "first",
            Day(Text(pattern, "firstDayOfWeek")) ?? DayOfWeek.Sunday, startDate, endDate, count, Time(master["start"]).TimeOfDay);
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

    public string MyAddress()
    {
        if (myAddress is null)
            Resolve("me");
        return myAddress ?? "me";
    }

    private const string NeedsUserRead =
        "Summary rules naming a manager or an organiser's title need lookup_scope = \"User.Read.All\" in the settings.";

    public string? Manager(Person person)
    {
        try
        {
            var manager = Get($"{person.GraphPath}/manager?$select=mail,userPrincipalName");
            return Text(manager, "mail") ?? Text(manager, "userPrincipalName");
        }
        catch (GraphRequestException error) when ((int)error.Status == 404)
        {
            return null;
        }
        catch (GraphRequestException error) when ((int)error.Status == 403)
        {
            throw new UsageException(NeedsUserRead);
        }
    }

    public string? JobTitle(string address)
    {
        try
        {
            return Text(Get($"/users/{Uri.EscapeDataString(address)}?$select=jobTitle"), "jobTitle");
        }
        catch (GraphRequestException error) when ((int)error.Status is 400 or 404)
        {
            return null;   // someone outside the organisation
        }
        catch (GraphRequestException error) when ((int)error.Status == 403)
        {
            throw new UsageException(NeedsUserRead);
        }
    }

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

    internal static Event ReadEvent(JsonObject o) => ReadEvent(o, null);

    /// <summary>An event; originalStart (UTC, unlike start and end) is put in the zone given.</summary>
    internal static Event ReadEvent(JsonObject o, TimeZoneInfo? zone)
    {
        DateTime? originalStart = Text(o, "originalStart") is { } os
            && DateTimeOffset.TryParse(os, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? TimeZoneInfo.ConvertTime(parsed, zone ?? TimeZoneInfo.Local).DateTime
            : null;
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
            (o["organizer"]?["emailAddress"] as JsonObject) is { } orgAddress ? Text(orgAddress, "address") : null,
            Text(o, "type"), Text(o, "seriesMasterId"), originalStart);
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

/// <summary>Someone's events, and whether they came through free/busy (0.8.0) because their calendar itself was
/// refused: Outlook shows a colleague's calendar with titles at Zen's default sharing, which apps can only read through
/// getSchedule, while the full calendar (attendees, responses) needs it shared in full.</summary>
public sealed record CalendarRead(IReadOnlyList<Event> Events, bool FreeBusyOnly);

public static class CalendarReads
{
    /// <summary>The calendar, or free/busy where the calendar is refused (403/404); the calendar's refusal is
    /// rethrown if free/busy is refused too.</summary>
    public static CalendarRead Read(ISource source, Person person, DateTime from, DateTime to)
    {
        try
        {
            return new CalendarRead(source.Events(person, from, to), false);
        }
        catch (GraphRequestException refused) when ((int)refused.Status is 403 or 404)
        {
            try
            {
                return new CalendarRead(source.FreeBusy(person, from, to), true);
            }
            catch (GraphRequestException also) when ((int)also.Status is 400 or 403 or 404)
            {
                throw refused;
            }
        }
    }
}
