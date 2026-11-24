using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CalendarDays;
using Tomlyn.Model;

namespace CalendarReport;

/// <summary>[free] (0.12.0): how `free` reads someone's day for a quick call.</summary>
public sealed class FreeSettings
{
    private static readonly string[] Keys = ["min_minutes", "rules", "presence_scope", "auto_reply_scope", "show_phone", "focus"];

    /// <summary>The shortest gap worth showing (design decision 3: 10; 15 hides the gaps between meetings).</summary>
    public int MinMinutes { get; init; } = 10;

    /// <summary>The rule list that says what's in the way (default "slots", so the owner's slot labels apply; the
    /// built-in slot list when the settings have none).</summary>
    public string Rules { get; init; } = "slots";

    /// <summary>The permission another person's Teams status is read with, asked for silently only: not granted, the
    /// rest of the answer still comes and one line says the status is waiting on IT.</summary>
    public string PresenceScope { get; init; } = "Presence.Read.All";

    /// <summary>The permission their automatic reply is read with (MailTips), when Teams status can't say.</summary>
    public string AutoReplyScope { get; init; } = "Mail.Read";

    /// <summary>Show the work numbers the directory lists for them (decision 7).</summary>
    public bool ShowPhone { get; init; } = true;

    /// <summary>Subjects that mark a block as focus time, which could be interrupted for something quick (their own
    /// blocks are always so, where their calendar shows who's invited).</summary>
    public Regex Focus { get; init; } = new(@"\bfocus\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static FreeSettings Parse(TomlTable raw, string file)
    {
        if (!raw.TryGetValue("free", out var f))
            return new FreeSettings();
        UsageException Bad(string message) => new($"{file}: {message}");
        if (f is not TomlTable table)
            throw Bad("[free] should be a table.");
        foreach (var key in table.Keys)
            if (!Keys.Contains(key))
                throw Bad($"unknown setting '{key}' in [free] (known: {string.Join(", ", Keys)}).");
        string? Text(string key) => table.TryGetValue(key, out var v)
            ? v as string is { Length: > 0 } t ? t : throw Bad($"free.{key} should be a non-empty string.")
            : null;
        var min = 10;
        if (table.TryGetValue("min_minutes", out var m))
            min = m is long l && l is >= 1 and <= 240 ? (int)l : throw Bad("free.min_minutes should be a whole number from 1 to 240.");
        var showPhone = true;
        if (table.TryGetValue("show_phone", out var p))
            showPhone = p is bool b ? b : throw Bad("free.show_phone should be true or false.");
        var focus = new FreeSettings().Focus;
        if (Text("focus") is { } pattern)
        {
            try
            {
                focus = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException error)
            {
                throw Bad($"free.focus isn't a valid regular expression: {error.Message}");
            }
        }
        return new FreeSettings
        {
            MinMinutes = min,
            Rules = Text("rules") ?? "slots",
            PresenceScope = Text("presence_scope") ?? "Presence.Read.All",
            AutoReplyScope = Text("auto_reply_scope") ?? "Mail.Read",
            ShowPhone = showPhone,
            Focus = focus,
        };
    }
}

/// <summary>Someone's working hours as their Outlook has them (getSchedule's workingHours), in the report's time zone.</summary>
public sealed record WorkingHours(IReadOnlyList<DayOfWeek> Days, TimeSpan Start, TimeSpan End);

/// <summary>Teams status as Graph gives it for another person.</summary>
public sealed record PresenceInfo(string Availability, string Activity, string? StatusMessage, bool OutOfOffice,
    string? OutOfOfficeMessage, string? WorkLocation);

/// <summary>Reading someone's Teams status: State is "shown" (Presence set), "not-granted" (the permission isn't held:
/// waiting on IT), "not-available" (refused for them: external, guest, unlicensed), "failed" (Why says) or "skipped".</summary>
public sealed record PresenceRead(string State, PresenceInfo? Presence = null, string? Why = null);

/// <summary>What the directory lists for someone: their object id (for presence) and work numbers.</summary>
public sealed record DirectoryContact(string? Id, IReadOnlyList<string> BusinessPhones, string? MobilePhone);

/// <summary>Something in someone's day for `free`: Kind is "busy" (a meeting, or anything that closes a gap), "away"
/// (out of office, leave, travel, lunch entries), "lunch" (an entry for lunch, kept apart so --lunch can look past it)
/// or "own-block" (their own blocked time or focus time: could be interrupted, so it doesn't close a gap).</summary>
public sealed record Busy(DateTime Start, DateTime End, string Kind);

/// <summary>A gap of at least the minimum: when GJ (the owner) is free within it (null when it's the owner being
/// asked about), any of their own blocks inside it (could be interrupted), and whether it runs over a lunch entry
/// (only with --lunch).</summary>
public sealed record Gap(DateTime Start, DateTime End, IReadOnlyList<(DateTime Start, DateTime End)>? OwnerFree,
    IReadOnlyList<Busy> Soft, bool OverLunch)
{
    public int Minutes => (int)Math.Round((End - Start).TotalMinutes);
}

/// <summary>Where they are now: State is free, busy, away, lunch, own-block, before-hours, after-hours, not-working
/// or leave; Until when it changes (null if not known).</summary>
public sealed record NowState(string State, DateTime? Until, string Text);

public sealed record FreePerson(
    Person Person, bool Readable, bool TitlesOnly, string? DayNote, DateTime WorkStart, DateTime WorkEnd, bool HoursFromOutlook,
    NowState? Now, IReadOnlyList<Gap> Gaps, PresenceRead? Presence, string? PresenceNote, string? AutoReply,
    IReadOnlyList<(string Number, string Type)> Phones, string? CallLink, string? ChatLink, IReadOnlyList<string> Notes);

public sealed record FreeRequest(IReadOnlyList<Person> People, DateTime Day, bool NowOnly, bool Lunch, int MinMinutes,
    bool Presence, string? ChatTopic);

public sealed record FreeReport(FreeRequest Request, DateTime Now, bool Today, string TimeZone, IReadOnlyList<FreePerson> People);

/// <summary>`free` (0.12.0; assistant/designs/free-to-call): when someone's free for a quick call. Their gaps of
/// min_minutes or more left in the day (from now, today), within their Outlook working hours, from their calendar (or
/// free/busy); whether the owner is free in each; their Teams status and status message (Presence.Read.All, asked
/// for silently only), their automatic reply's first line, a Teams call link and their directory work numbers. It
/// only reads and only prints: no meeting titles are printed, and nothing about anyone is written anywhere.</summary>
public static class Free
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>The rule list for `free`: [free] rules, or the built-in slot labels when that's "slots" and the
    /// settings have none.</summary>
    public static IReadOnlyList<Rule> RulesFor(Config config)
    {
        var name = config.Free.Rules;
        if (config.Summary.Rules.TryGetValue(name, out var rules))
            return rules;
        if (name == "slots")
            return SlotSettings.BuiltInRules;
        throw new UsageException($"{config.Path}: free.rules: there's no rule list '{name}' ([[rules.{name}]]).");
    }

    public static FreeReport Build(Config config, ISource source, FreeRequest request, DateTime now)
    {
        now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        var today = request.Day.Date == now.Date;
        if (request.Day.Date < now.Date)
            throw new UsageException($"{request.Day:ddd d MMM} has gone: `free` looks at today or a day ahead.");
        var rules = RulesFor(config);
        var context = new RuleContext(source, config);
        var me = source.Resolve("me");
        var day = request.Day.Date;
        IReadOnlyList<(DateTime, DateTime)>? ownerBusy = null;
        try
        {
            ownerBusy = OwnerBusy(CalendarReads.Read(source, me, day, day.AddDays(1)).Events, rules, context, me);
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 403 or 404)
        {
            // your own calendar refused: shown without your side
        }
        var people = request.People.Select(p => ForPerson(config, source, request, p, me, ownerBusy, rules, context, now, today)).ToList();
        return new FreeReport(request, now, today, config.TimeZone.Id, people);
    }

    private static FreePerson ForPerson(Config config, ISource source, FreeRequest request, Person person, Person me,
        IReadOnlyList<(DateTime, DateTime)>? ownerBusy, IReadOnlyList<Rule> rules, RuleContext context, DateTime now, bool today)
    {
        var settings = config.Free;
        var day = request.Day.Date;
        var isMe = person.Address == "me";
        var notes = new List<string>();
        var address = isMe ? source.MyAddress() : person.Address;

        var contact = isMe || !settings.ShowPhone && !request.Presence ? null : source.Contact(person);
        var phones = new List<(string, string)>();
        if (settings.ShowPhone && contact is not null)
        {
            phones.AddRange(contact.BusinessPhones.Where(n => n.Trim().Length > 0).Select(n => (n.Trim(), "work")));
            if (contact.MobilePhone is { Length: > 0 } mobile)
                phones.Add((mobile.Trim(), "mobile"));
        }
        var user = Uri.EscapeDataString(address).Replace("%40", "@", StringComparison.Ordinal);   // as Microsoft's own examples
        var callLink = isMe ? null : "https://teams.microsoft.com/l/call/0/0?users=" + user;
        var chatLink = isMe || request.ChatTopic is null ? null
            : "https://teams.microsoft.com/l/chat/0/0?users=" + user + "&message=" + Uri.EscapeDataString(ChatMessage(request.ChatTopic));

        // Teams status is only ever "now", so only for today.
        PresenceRead? presence = null;
        if (today && request.Presence && !isMe)
            presence = source.Presence(person, contact?.Id);
        string? autoReply = null;
        if (!isMe)
        {
            if (presence?.Presence is { } p)
                autoReply = p.OutOfOffice ? FirstLine(p.OutOfOfficeMessage) ?? "" : null;
            else
                autoReply = source.AutoReply(person);
        }

        CalendarRead read;
        try
        {
            read = CalendarReads.Read(source, person, day, day.AddDays(1));
        }
        catch (M365Graph.GraphRequestException error) when ((int)error.Status is 403 or 404)
        {
            notes.Add("Can't see their calendar (neither in full nor as free/busy).");
            return new FreePerson(person, false, false, null, day, day, false, null, [], presence, null, autoReply, phones,
                callLink, chatLink, notes);
        }
        var events = read.Events.Where(e => !e.Cancelled).ToList();

        var hours = source.WorkingHours(person, day);
        var fromOutlook = hours is not null;
        var workStart = day + (hours?.Start ?? config.Slots.HoursFrom);
        var workEnd = day + (hours?.End ?? config.Slots.HoursTo);
        var workingDays = hours?.Days is { Count: > 0 } d ? d
            : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

        string? dayNote = null;
        NowState? nowState = null;
        var gaps = new List<Gap>();
        var dayOff = DayOffFor(config, context, person, events, day);
        if (!workingDays.Contains(day.DayOfWeek))
        {
            dayNote = fromOutlook ? "Not one of their working days (their Outlook working hours)." : "Not a working day.";
            nowState = today ? new NowState("not-working", null, "not a working day for them") : null;
        }
        else if (dayOff.Kind is DayOffKind.Leave or DayOffKind.BankHoliday)
        {
            var back = BackOn(config, source, context, person, day, workingDays);
            var what = dayOff.Kind == DayOffKind.BankHoliday ? "A bank holiday" : $"On leave {(today ? "today" : "that day")}";
            dayNote = back is { } b ? $"{what} (back {b:ddd d MMM})." : $"{what}.";
            nowState = today ? new NowState("leave", null, dayOff.Kind == DayOffKind.BankHoliday ? "a bank holiday" : "on leave") : null;
        }
        else
        {
            var midday = day + DayOffRules.Midday.ToTimeSpan();
            if (dayOff.Kind == DayOffKind.HalfDay && dayOff.Half == HalfOfDay.Am && workStart < midday)
            {
                workStart = midday;
                notes.Add("On leave in the morning.");
            }
            else if (dayOff.Kind == DayOffKind.HalfDay && dayOff.Half == HalfOfDay.Pm && workEnd > midday)
            {
                workEnd = midday;
                notes.Add("On leave in the afternoon.");
            }
            else if (dayOff.Kind == DayOffKind.HalfDay)
                notes.Add("Half a day's leave today; which half isn't known.");

            var theirs = Classify(events, rules, context, person, settings.Focus, config.Slots);
            var from = today && now > workStart ? now : workStart;
            gaps = Gaps(from, workEnd, theirs, isMe ? null : ownerBusy, request.MinMinutes, request.Lunch);
            if (today)
                nowState = Now(now, workStart, workEnd, theirs, request.Lunch);
        }
        var presenceNote = presence?.Presence is { } shown && nowState is not null ? PresenceNote(shown, nowState) : null;
        return new FreePerson(person, true, read.FreeBusyOnly, dayNote, workStart, workEnd, fromOutlook, nowState, gaps,
            presence, presenceNote, autoReply, phones, callLink, chatLink, notes);
    }

    /// <summary>"Have you got 5 minutes for a quick call about X?" (decision 7's extra): sent by the owner, never the tool.</summary>
    public static string ChatMessage(string topic)
    {
        var t = topic.Trim().TrimEnd('?', '.', '!').Trim();
        if (t.StartsWith("about ", StringComparison.OrdinalIgnoreCase))
            t = t[6..].Trim();
        return t.Length == 0 ? "Have you got 5 minutes for a quick call?" : $"Have you got 5 minutes for a quick call about {t}?";
    }

    private static DayOff DayOffFor(Config config, RuleContext context, Person person, IReadOnlyList<Event> events, DateTime day)
    {
        var self = context.AddressOf(person);
        var onDay = events.Where(e => e.Start < day.AddDays(1) && e.End > day).Select(e =>
        {
            var organizer = e.OrganizerAddress?.ToLowerInvariant();
            var mine = (organizer is null || organizer == self) && (e.Invitees ?? []).All(a => a.Type == "resource");
            return new DayEvent(e.Subject, e.Start, e.End, e.AllDay, e.ShowAs, e.Categories, mine, e.Attendees);
        }).ToList();
        return DayOffRules.Classify(config.Leave, onDay, DateOnly.FromDateTime(day));
    }

    /// <summary>The first working day after their leave, within three weeks; null if none is found.</summary>
    private static DateTime? BackOn(Config config, ISource source, RuleContext context, Person person, DateTime day,
        IReadOnlyList<DayOfWeek> workingDays)
    {
        try
        {
            var from = day.AddDays(1);
            var events = CalendarReads.Read(source, person, from, from.AddDays(21)).Events.Where(e => !e.Cancelled).ToList();
            for (var d = from; d < from.AddDays(21); d = d.AddDays(1))
            {
                if (!workingDays.Contains(d.DayOfWeek))
                    continue;
                if (DayOffFor(config, context, person, events, d).Kind is not (DayOffKind.Leave or DayOffKind.BankHoliday))
                    return d;
            }
        }
        catch (M365Graph.GraphRequestException)
        {
        }
        return null;
    }

    private static readonly Regex LunchSubject = new(@"\blunch\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What's in their day, by kind (see <see cref="Busy"/>). Declined meetings, and anything shown as free or
    /// working elsewhere, aren't in the way; nor is anything a rule counts as "skip". A rule's "not-working" (lunch,
    /// travel, the school run) and "leave", and show-as out of office, are away; an entry for lunch (by subject, or a
    /// not-working entry starting in [slots] lunch) is "lunch". Their own blocks (no one else invited, where the
    /// calendar shows that) and focus time are "own-block". Everything else is busy.</summary>
    internal static List<Busy> Classify(IEnumerable<Event> events, IReadOnlyList<Rule> rules, RuleContext context, Person person,
        Regex focus, SlotSettings slots)
    {
        var self = context.AddressOf(person);
        var list = new List<Busy>();
        foreach (var e in events)
        {
            if (e.Cancelled || RuleContext.ResponseOf(e) == "declined" || e.End <= e.Start)
                continue;
            if (e.ShowAs.Equals("free", StringComparison.OrdinalIgnoreCase) || e.ShowAs.Equals("workingElsewhere", StringComparison.OrdinalIgnoreCase))
                continue;
            var oof = e.ShowAs.Equals("oof", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(e.Subject))
            {
                list.Add(new Busy(e.Start, e.End, oof ? "away" : "busy"));
                continue;
            }
            var (_, rule) = context.Classify(e, rules, person);
            if (rule?.Count == "skip")
                continue;
            if (rule?.Count == "not-working")
            {
                var lunch = LunchSubject.IsMatch(e.Subject) || (slots.LunchFrom is { } lf && slots.LunchTo is { } lt
                    && e.Start.TimeOfDay >= lf && e.Start.TimeOfDay < lt && (e.End - e.Start).TotalMinutes <= 90);
                list.Add(new Busy(e.Start, e.End, lunch ? "lunch" : "away"));
                continue;
            }
            if (rule?.Count == "leave" || oof)
            {
                list.Add(new Busy(e.Start, e.End, "away"));
                continue;
            }
            var organizer = e.OrganizerAddress?.ToLowerInvariant() ?? "";
            var others = (e.Invitees ?? []).Where(a => a.Type != "resource" && !string.IsNullOrEmpty(a.Address)
                && !string.Equals(a.Address, self, StringComparison.OrdinalIgnoreCase)).ToList();
            var ownBlock = !e.FreeBusy && !e.AllDay && (organizer.Length == 0 || organizer == self) && others.Count == 0;
            list.Add(new Busy(e.Start, e.End, ownBlock || focus.IsMatch(e.Subject) ? "own-block" : "busy"));
        }
        return list;
    }

    /// <summary>When the owner is busy: anything but free time, working elsewhere, what they declined and their own
    /// blocks (they can step out of their own block for a call; travel and lunch they can't).</summary>
    internal static List<(DateTime, DateTime)> OwnerBusy(IEnumerable<Event> events, IReadOnlyList<Rule> rules, RuleContext context, Person me)
    {
        var focus = new Regex("(?!)");   // for the owner, only a real own block counts as free
        return [.. Classify(events, rules, context, me, focus, new SlotSettings())
            .Where(b => b.Kind != "own-block").Select(b => (b.Start, b.End))];
    }

    /// <summary>Intervals merged where they touch or overlap, in order.</summary>
    internal static List<(DateTime Start, DateTime End)> Merge(IEnumerable<(DateTime Start, DateTime End)> intervals)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var (s, e) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && s <= merged[^1].End)
                merged[^1] = (merged[^1].Start, e > merged[^1].End ? e : merged[^1].End);
            else
                merged.Add((s, e));
        }
        return merged;
    }

    /// <summary>[from, to) less the intervals.</summary>
    internal static List<(DateTime Start, DateTime End)> Subtract(DateTime from, DateTime to, IEnumerable<(DateTime Start, DateTime End)> intervals)
    {
        var free = new List<(DateTime, DateTime)>();
        var at = from;
        foreach (var (s, e) in Merge(intervals))
        {
            if (e <= at)
                continue;
            if (s >= to)
                break;
            if (s > at)
                free.Add((at, s));
            at = e > at ? e : at;
        }
        if (at < to)
            free.Add((at, to));
        return free;
    }

    /// <summary>The gaps of minMinutes or more in [from, to): their busy, away and (unless lunch) lunch entries close a
    /// gap; their own blocks don't, and are listed in the gap. For each, where the owner is free too (stretches of five
    /// minutes or more; null when ownerBusy is).</summary>
    internal static List<Gap> Gaps(DateTime from, DateTime to, IReadOnlyList<Busy> theirs,
        IReadOnlyList<(DateTime, DateTime)>? ownerBusy, int minMinutes, bool lunch)
    {
        if (to <= from)
            return [];
        var closing = theirs.Where(b => b.Kind is "busy" or "away" || (b.Kind == "lunch" && !lunch)).Select(b => (b.Start, b.End));
        var gaps = new List<Gap>();
        foreach (var (s, e) in Subtract(from, to, closing))
        {
            if ((e - s).TotalMinutes < minMinutes)
                continue;
            var soft = theirs.Where(b => b.Kind == "own-block" && b.Start < e && b.End > s)
                .Select(b => b with { Start = b.Start < s ? s : b.Start, End = b.End > e ? e : b.End })
                .OrderBy(b => b.Start).ToList();
            var overLunch = lunch && theirs.Any(b => b.Kind == "lunch" && b.Start < e && b.End > s);
            var ownerFree = ownerBusy is null ? null
                : Subtract(s, e, ownerBusy).Where(f => (f.End - f.Start).TotalMinutes >= 5).ToList();
            gaps.Add(new Gap(s, e, ownerFree, soft, overLunch));
        }
        return gaps;
    }

    /// <summary>Where they are now, by the calendar.</summary>
    internal static NowState Now(DateTime now, DateTime workStart, DateTime workEnd, IReadOnlyList<Busy> theirs, bool lunch)
    {
        if (now < workStart)
            return new NowState("before-hours", workStart, $"not started yet (their working hours start at {workStart:HH:mm})");
        if (now >= workEnd)
            return new NowState("after-hours", null, $"finished for the day (their working hours end at {workEnd:HH:mm})");
        var closing = theirs.Where(b => b.Kind is "busy" or "away" || (b.Kind == "lunch" && !lunch)).ToList();
        var blocks = Merge(closing.Select(b => (b.Start, b.End)));
        var current = blocks.FindIndex(b => b.Start <= now && b.End > now);
        if (current >= 0)
        {
            var until = blocks[current].End;
            var kinds = closing.Where(b => b.Start <= now && b.End > now).Select(b => b.Kind).ToList();
            var state = kinds.Contains("busy") ? "busy" : kinds.Contains("away") ? "away" : "lunch";
            var text = state switch { "busy" => "busy (in a meeting or busy)", "away" => "away", _ => "at lunch" };
            return new NowState(state, until, $"{text} until {until:HH:mm}");
        }
        var next = blocks.Where(b => b.Start > now).Select(b => b.Start).DefaultIfEmpty(workEnd).Min();
        if (next > workEnd)
            next = workEnd;
        if (theirs.FirstOrDefault(b => b.Kind == "own-block" && b.Start <= now && b.End > now) is { } own)
        {
            var ownUntil = own.End < next ? own.End : next;
            return new NowState("own-block", ownUntil, $"in their own block until {ownUntil:HH:mm} (could be interrupted for something quick)");
        }
        return new NowState("free", next, next >= workEnd ? $"free until the end of their day ({next:HH:mm})" : $"free until {next:HH:mm}");
    }

    /// <summary>A label for Teams' availability or activity (Graph's docs list the two fields' values the other way
    /// round from what the API returns, so either field's values are known here).</summary>
    internal static string? Label(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "presenceunknown" => null,
        "available" => "Available",
        "availableidle" => "Available, idle",
        "away" => "Away",
        "berightback" => "Be right back",
        "busy" => "Busy",
        "busyidle" => "Busy, idle",
        "donotdisturb" => "Do not disturb",
        "offline" => "Offline",
        "inacall" => "in a call",
        "inaconferencecall" => "in a conference call",
        "inameeting" => "in a meeting",
        "presenting" => "presenting",
        "focusing" => "focusing",
        "inactive" => "inactive",
        "offwork" => "off work",
        "outofoffice" => "out of office",
        "urgentinterruptionsonly" => "urgent interruptions only",
        _ => value,
    };

    /// <summary>"Busy, in a meeting": the availability, then the activity where it adds something.</summary>
    public static string StatusText(PresenceInfo p)
    {
        var availability = Label(p.Availability);
        var activity = Label(p.Activity);
        if (availability is null)
            return activity is null ? "Unknown" : char.ToUpperInvariant(activity[0]) + activity[1..];
        if (activity is null || string.Equals(activity, availability, StringComparison.OrdinalIgnoreCase))
            return availability;
        return $"{availability}, {activity}";
    }

    private static bool Is(string? value, params string[] values) =>
        value is not null && values.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Teams and the calendar together (design section 3): status wins for "now", the calendar for later.</summary>
    internal static string? PresenceNote(PresenceInfo p, NowState now)
    {
        var free = now.State is "free" or "own-block";
        bool Any(params string[] values) => Is(p.Availability, values) || Is(p.Activity, values);
        if (p.OutOfOffice || Is(p.Activity, "OutOfOffice"))
            return "Teams shows them out of office.";
        if (Any("DoNotDisturb", "Presenting", "UrgentInterruptionsOnly"))
            return "Do not disturb: a chat rather than a call.";
        if (free && Any("InACall", "InAConferenceCall", "InAMeeting"))
            return "On a call now (not in the calendar).";
        if (now.State == "busy" && Is(p.Availability, "Available", "AvailableIdle"))
            return $"The calendar says busy until {now.Until:HH:mm}, but Teams shows Available: the meeting may have ended.";
        if (free && Any("Away", "BeRightBack"))
            return "Away from their desk (Teams doesn't say for how long).";
        if (free && Is(p.Availability, "Offline"))
            return "Offline in Teams (may be on mobile, or not at a desk).";
        return null;
    }

    private static readonly Regex Breaks = new(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex Hidden = new(@"<\s*(style|script|head|title)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>An automatic reply's first line of text (it's often HTML), at most 200 characters; null when empty.</summary>
    public static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var text = Hidden.Replace(message, " ");
        text = Breaks.Replace(text, "\n");
        text = WebUtility.HtmlDecode(Tags.Replace(text, " "));
        var line = text.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length > 200 ? line[..197] + "..." : line;
    }

    private static string Time(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm", En);

    /// <summary>Where the owner is free in a gap, in words.</summary>
    internal static string? OwnerText(Gap g)
    {
        if (g.OwnerFree is not { } free)
            return null;
        if (free.Count == 0)
            return "you're busy then";
        if (free.Count == 1 && free[0].Start == g.Start && free[0].End == g.End)
            return "you're free too";
        if (free.Count == 1 && free[0].Start == g.Start)
            return $"you're free until {free[0].End:HH:mm}";
        if (free.Count == 1 && free[0].End == g.End)
            return $"you're in something until {free[0].Start:HH:mm}";
        return "you're free " + string.Join(", ", free.Select(f => $"{f.Start:HH:mm}-{f.End:HH:mm}"));
    }

    private static string GapLine(Gap g, DateTime now, bool today)
    {
        var b = new StringBuilder();
        b.Append(En, $"{g.Start:HH:mm}-{g.End:HH:mm}  {g.Minutes,3} min");
        if (today && g.Start == now)
            b.Append("  (free now)");
        if (OwnerText(g) is { } owner)
            b.Append("   ").Append(owner);
        if (g.OverLunch)
            b.Append("   (over a lunch entry)");
        return b.ToString();
    }

    public static string Table(FreeReport report)
    {
        var b = new StringBuilder();
        var r = report.Request;
        foreach (var p in report.People)
        {
            if (b.Length > 0)
                b.Append('\n');
            b.Append(p.Person.Name).Append(En, $" · {r.Day:ddd d MMM}").Append(report.Today ? $", {report.Now:HH:mm}" : "").Append('\n');
            if (p.Presence is { } presence)
            {
                b.Append("  Teams:    ");
                b.Append(presence.State switch
                {
                    "shown" => PresenceLine(presence.Presence!),
                    "not-granted" => $"status waiting on IT ({presence.Why ?? "permission not granted yet"})",
                    "not-available" => "status not available for them",
                    _ => $"status couldn't be read ({presence.Why ?? "unknown error"})",
                }).Append('\n');
                if (p.PresenceNote is { } note)
                    b.Append("            ").Append(note).Append('\n');
            }
            if (p.AutoReply is { } reply)
                b.Append("  Auto-reply: on").Append(reply.Length > 0 ? $": \"{reply}\"" : "").Append('\n');
            foreach (var note in p.Notes)
                b.Append("  ").Append(note).Append('\n');
            if (!p.Readable)
            {
                Contact(b, p);
                continue;
            }
            if (p.Now is { } now)
                b.Append("  Now:      ").Append(now.Text).Append('\n');
            if (p.DayNote is { } dayNote)
                b.Append("  ").Append(dayNote).Append('\n');
            else if (r.NowOnly)
            {
                var next = p.Gaps.FirstOrDefault(g => g.Start > report.Now);
                b.Append("  Next gap: ").Append(next is null ? $"none of {r.MinMinutes} min or more left today" : GapLine(next, report.Now, report.Today)).Append('\n');
                if (next is not null)
                    Soft(b, next, "            ");
            }
            else
            {
                var hours = $"{p.WorkStart:HH:mm}-{p.WorkEnd:HH:mm}{(p.HoursFromOutlook ? "" : ", default hours")}";
                b.Append(En, $"  {(report.Today ? "Free today" : "Free")} ({r.MinMinutes} min or more, within {hours}):");
                if (p.Gaps.Count == 0)
                    b.Append(report.Today && report.Now >= p.WorkEnd ? " none: their day has ended\n" : " none\n");
                else
                {
                    b.Append('\n');
                    foreach (var g in p.Gaps)
                    {
                        b.Append("    ").Append(GapLine(g, report.Now, report.Today)).Append('\n');
                        Soft(b, g, "                        ");
                    }
                }
            }
            Contact(b, p);
        }
        return b.ToString();
    }

    private static void Soft(StringBuilder b, Gap g, string indent)
    {
        foreach (var s in g.Soft)
            b.Append(indent).Append(En, $"their own block {s.Start:HH:mm}-{s.End:HH:mm}: could be interrupted for something quick\n");
    }

    private static void Contact(StringBuilder b, FreePerson p)
    {
        if (p.Phones.Count > 0)
            b.Append("  Phone:    ").Append(string.Join(", ", p.Phones.Select(x => $"{x.Number} ({x.Type})"))).Append('\n');
        if (p.CallLink is { } call)
            b.Append("  Call:     ").Append(call).Append('\n');
        if (p.ChatLink is { } chat)
            b.Append("  Chat:     ").Append(chat).Append('\n');
    }

    private static string PresenceLine(PresenceInfo p)
    {
        var line = "shows " + StatusText(p);
        if (p.StatusMessage is { Length: > 0 } message)
            line += $" · \"{message}\"";
        if (p.WorkLocation is { Length: > 0 } where)
            line += " · " + where switch
            {
                "office" => "in the office",
                "remote" => "working remotely",
                "timeOff" => "time off",
                _ => where,
            };
        return line;
    }

    public static string Json(FreeReport report)
    {
        var r = report.Request;
        var root = new JsonObject
        {
            ["day"] = r.Day.ToString("yyyy-MM-dd", En),
            ["now"] = Time(report.Now),
            ["today"] = report.Today,
            ["timeZone"] = report.TimeZone,
            ["minMinutes"] = r.MinMinutes,
            ["lunch"] = r.Lunch,
            ["people"] = new JsonArray([.. report.People.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Person.Name,
                ["address"] = p.Person.Address,
                ["readable"] = p.Readable,
                ["titlesOnly"] = p.TitlesOnly,
                ["dayNote"] = p.DayNote,
                ["notes"] = new JsonArray([.. p.Notes.Select(n => (JsonNode)JsonValue.Create(n))]),
                ["workingHours"] = p.Readable ? new JsonObject
                {
                    ["start"] = p.WorkStart.ToString("HH:mm", En),
                    ["end"] = p.WorkEnd.ToString("HH:mm", En),
                    ["from"] = p.HoursFromOutlook ? "outlook" : "settings",
                } : null,
                ["now"] = p.Now is { } now ? new JsonObject
                {
                    ["state"] = now.State,
                    ["until"] = now.Until is { } until ? Time(until) : null,
                    ["text"] = now.Text,
                } : null,
                ["presence"] = p.Presence is { } presence ? new JsonObject
                {
                    ["state"] = presence.State,
                    ["why"] = presence.Why,
                    ["availability"] = presence.Presence?.Availability,
                    ["activity"] = presence.Presence?.Activity,
                    ["text"] = presence.Presence is { } shown ? StatusText(shown) : null,
                    ["statusMessage"] = presence.Presence?.StatusMessage,
                    ["outOfOffice"] = presence.Presence?.OutOfOffice,
                    ["workLocation"] = presence.Presence?.WorkLocation,
                    ["note"] = p.PresenceNote,
                } : null,
                ["autoReply"] = p.AutoReply is null ? null : new JsonObject { ["on"] = true, ["firstLine"] = p.AutoReply.Length > 0 ? p.AutoReply : null },
                ["gaps"] = new JsonArray([.. p.Gaps.Select(g => (JsonNode)new JsonObject
                {
                    ["start"] = Time(g.Start),
                    ["end"] = Time(g.End),
                    ["minutes"] = g.Minutes,
                    ["ownerFree"] = g.OwnerFree is { } free
                        ? new JsonArray([.. free.Select(f => (JsonNode)new JsonObject { ["start"] = Time(f.Start), ["end"] = Time(f.End) })])
                        : null,
                    ["owner"] = OwnerText(g),
                    ["soft"] = new JsonArray([.. g.Soft.Select(s => (JsonNode)new JsonObject
                    {
                        ["start"] = Time(s.Start),
                        ["end"] = Time(s.End),
                        ["kind"] = "own-block",
                    })]),
                    ["overLunch"] = g.OverLunch,
                })]),
                ["phones"] = new JsonArray([.. p.Phones.Select(x => (JsonNode)new JsonObject { ["number"] = x.Number, ["type"] = x.Type })]),
                ["callLink"] = p.CallLink,
                ["chatLink"] = p.ChatLink,
            })]),
        };
        return PyCompat.PyJson.Dump(root, 2, false) + "\n";
    }
}
