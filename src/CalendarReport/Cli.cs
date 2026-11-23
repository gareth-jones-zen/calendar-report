using System.Globalization;
using System.Reflection;
using System.Text;
using M365Graph;
using WinBackground;

namespace CalendarReport;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> Env { get; init; } = Config.Environment;

    /// <summary>Where schedules come from, for these settings.</summary>
    public Func<Config, ISource> Source { get; init; } = config => new GraphSource(Cli.GraphFor(config), config);

    /// <summary>Sign in, interactively.</summary>
    public Action<Config> SignIn { get; init; } = Cli.SignIn;

    public Func<DateTime> Today { get; init; } = () => DateTime.Today;

    /// <summary>The time now, in the settings' time zone.</summary>
    public Func<TimeZoneInfo, DateTime> Now { get; init; } = zone => TimeZoneInfo.ConvertTime(DateTime.UtcNow, zone);
}

/// <summary>calendar-report: read people's calendars and work plans for a range of days. It only reads. Exit codes: 0
/// done; 1 failed; 2 settings, arguments or sign-in.</summary>
public static class Cli
{
    public const string Prog = "calendar-report";

    private const string Usage =
        "usage: calendar-report [-h] [--version] [--config CONFIG] {config,sign-in,events,work-plan,summary,contacts,slots,rooms,who,groups} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "calendar-report: read people's Outlook calendars and work plans (office, remote, time off) for a range of days.\n" +
        "It only reads.\n\n" +
        "    calendar-report events [PEOPLE...]       every event in their calendars\n" +
        "    calendar-report events [PEOPLE...] --work-plan   ...with each day's work plan\n" +
        "    calendar-report work-plan [PEOPLE...]    where they're working each day\n" +
        "    calendar-report summary [PEOPLE...]      where their time went, by week (rules in the settings)\n" +
        "    calendar-report contacts [PEOPLE...]     when you last met, and next meet, the people in your rings\n" +
        "    calendar-report slots PEOPLE... --minutes N   times for a meeting with them, and what's in the way\n" +
        "    calendar-report rooms                    the meeting rooms `slots` chooses from, best first\n" +
        "    calendar-report who PEOPLE...            who those words mean (names looked up in the directory)\n" +
        "    calendar-report groups                   the groups in the settings\n" +
        "    calendar-report config                   create the settings file (if missing) and say where it is\n" +
        "    calendar-report sign-in                  sign in to Microsoft (once; opens a browser)\n\n" +
        "  PEOPLE: me (the default), addresses, names, or groups from the settings; commas or spaces between.\n\n" +
        "  events and work-plan:\n" +
        "    --from DATE        the first day (yyyy-mm-dd, today, tomorrow or monday); default today\n" +
        "    --to DATE          the last day, included; or\n" +
        "    --days N           how many days (default 7)\n" +
        "    --format F         table (default), json or csv\n" +
        "    --out FILE         write it to a file instead\n" +
        "    --cancelled        events: include cancelled meetings still on the calendar\n\n" +
        "  summary (also --from, --to, --days, --format table|json, --out; default last week):\n" +
        "    --weeks N          how many weeks, from --from (or last week's Monday)\n" +
        "    --view NAME        a view from the settings (default: summary.default_view)\n" +
        "    --depth D          groups, buckets or events (each event, with the minutes it counted)\n\n" +
        "  contacts (also --format table|json, --out; the days and rings are in the settings):\n" +
        "    --ring NAME        one ring\n" +
        "    --flagged          only the people flagged (past the ring's gap, or a recurring meeting lost)\n\n" +
        "  slots (you're always included, as essential; it only prints, so there's no --out):\n" +
        "    --minutes N        how long (required)\n" +
        "    --optional PEOPLE  people who'd be good to have but aren't essential (repeat it, or use commas)\n" +
        "    --from DATE        the first day (default tomorrow)\n" +
        "    --by DATE          when you'd like it by: a preference (default 10 working days on)\n" +
        "    --until DATE       the last day looked at (default [slots] past_by_days working days after --by)\n" +
        "    --place P          in-person, online or either (default either): in-person puts your office days first;\n" +
        "                       in-person or either finds a free room that fits on your office days\n" +
        "    --room-only        only times with a room (so only your office days)\n" +
        "    --max N            how many candidates (default [slots] max_candidates)\n" +
        "    --topic WORDS      also list meetings already in anyone's calendar whose subject shares these words\n" +
        "    --format F         table (default) or json; --json is the same as --format json\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\calendar-report\\config.toml)\n";

    private static readonly string[] Commands = ["config", "sign-in", "events", "work-plan", "summary", "contacts", "slots", "rooms", "who", "groups"];
    private static readonly string[] Formats = ["table", "json", "csv"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config;
        public string Command = "";
        public List<string> People = [];
        public string? From, To, Out, View, Depth, Ring;
        public int? Days, Weeks;
        public List<string> Optional = [];
        public string? By, Until, Place, Topic;
        public int? Minutes, Max;
        public string Format = "table";
        public bool WorkPlan, Cancelled, Flagged, RoomOnly;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    public static GraphClient GraphFor(Config config) => new(new GraphClientOptions
    {
        CachePath = config.MsalCache ?? M365Graph.Graph.DefaultCachePath,
        Tenant = config.Tenant,
        LoginHint = config.LoginHint,
        Scopes = [GraphSource.CalendarScope, config.LookupScope],
        SignInHint = "At a console, run: calendar-report sign-in",
        Log = Logger.Create(config.LogFile),
    });

    public static void SignIn(Config config) =>
        GraphFor(config).TokenAsync([GraphSource.CalendarScope, config.LookupScope], true).GetAwaiter().GetResult();

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }

        try
        {
            if (options.Command == "config")
            {
                host.Out.WriteLine(CalendarReport.Config.CreateDefault(options.Config ?? host.Env(CalendarReport.Config.EnvConfig), host.Env));
                return 0;
            }
            var config = CalendarReport.Config.Load(options.Config, host.Env);
            switch (options.Command)
            {
                case "sign-in":
                    host.SignIn(config);
                    host.Out.WriteLine("Signed in.");
                    return 0;
                case "groups":
                    if (config.Groups.Count == 0)
                        host.Out.WriteLine($"No groups in {config.Path}.");
                    foreach (var (name, members) in config.Groups)
                        host.Out.WriteLine($"{name}: {string.Join(", ", members)}");
                    return 0;
            }
            var source = host.Source(config);
            if (options.Command == "rooms")
            {
                host.Out.Write(RoomsCommand(config, source));
                return 0;
            }
            if (options.Command == "slots")
            {
                host.Out.Write(SlotsCommand(options, config, source, host.Today(), host.Now(config.TimeZone)));
                return 0;
            }
            var people =options.Command is "summary" or "contacts" ? [] : Reports.People(options.People, config, source);
            if (options.Command == "who")
            {
                foreach (var person in people)
                    host.Out.WriteLine(person.Address == "me" ? $"me: {person.Name}" : $"{person.Name} <{person.Address}>");
                return 0;
            }
            string text;
            if (options.Command == "summary")
                text = Summary(options, config, source, host.Today());
            else if (options.Command == "contacts")
            {
                var rings = Contacts.Resolve(config, source, options.People, options.Ring);
                var report = Contacts.Build(config, source, source.Resolve("me"), rings, host.Now(config.TimeZone));
                text = options.Format == "json" ? Contacts.Json(report, options.Flagged) : Contacts.Table(report, options.Flagged);
            }
            else
            {
                var (from, to) = Range(options, host.Today());
                var report = Reports.Read(people, source, from, to, config.TimeZone.Id,
                    events: options.Command == "events", workPlan: options.Command == "work-plan" || options.WorkPlan,
                    cancelled: options.Cancelled);
                text = options.Format switch
                {
                    "json" => Reports.Json(report),
                    "csv" => Reports.Csv(report),
                    _ => Reports.Table(report),
                };
            }
            if (options.Out is { } file)
            {
                var full = Path.GetFullPath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text, new UTF8Encoding(options.Format == "csv"));   // a BOM, so Excel reads CSV as UTF-8
                host.Out.WriteLine($"Wrote {full}");
            }
            else
                host.Out.Write(text);
            return 0;
        }
        catch (Exception error) when (error is UsageException or SignInNeededException)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (GraphRequestException error) when ((int)error.Status is 403 or 404)
        {
            host.Err.WriteLine($"Microsoft 365 refused: {error.Message} (neither their calendar nor their free/busy can be read)");
            return 1;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static string Summary(Options options, Config config, ISource source, DateTime today)
    {
        var settings = config.Summary;
        var name = options.View ?? settings.DefaultView;
        if (!settings.Views.TryGetValue(name, out var view))
            throw new UsageException($"There's no view '{name}' in {config.Path} (views: {string.Join(", ", settings.Views.Keys.Order())}).");
        var people = Reports.People(options.People.Count > 0 ? options.People : view.People, config, source);
        if (options.Weeks is { } weeks)
            options.Days = weeks * 7;
        options.From ??= Summaries.Monday(today).AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (from, to) = Range(options, today);
        var rules = settings.Rules[view.Rules];
        var context = new RuleContext(source, config);
        var summaries = people.Select(p => Summaries.Summarise(p, CalendarReads.Read(source, p, from, to).Events, settings, rules, context, from, to)).ToList();
        var report = new SummaryReport(from, to, config.TimeZone.Id, view, options.Depth ?? view.Depth,
            Summaries.Order(settings, rules, summaries), settings.FreeBands, summaries);
        return options.Format == "json" ? Summaries.Json(report) : Summaries.Table(report);
    }

    /// <summary>`rooms` (0.10.0): the meeting rooms `slots` chooses from, in the order it tries them, and where the
    /// list came from (the directory, or the settings when rooms_scope isn't granted).</summary>
    private static string RoomsCommand(Config config, ISource source)
    {
        var (rooms, from) = Slots.RoomList(config, source);
        var b = new StringBuilder();
        b.Append(from == "directory"
            ? $"{rooms.Count} room{(rooms.Count == 1 ? "" : "s")} from the directory ({config.RoomsScope} granted)"
            : $"{rooms.Count} room{(rooms.Count == 1 ? "" : "s")} from the settings ({config.RoomsScope} not granted)");
        b.Append(rooms.Count == 0 ? ".\n" : ", in the order slots tries them:\n");
        foreach (var r in Slots.RankRooms(rooms, config.Slots))
            b.Append("  ").Append(r.Name).Append(" <").Append(r.Address).Append(">  seats ")
                .Append(r.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "?").Append("  floor ").Append(r.Floor ?? "?")
                .Append(r.Building is { } building ? "  " + building : "").Append('\n');
        return b.ToString();
    }

    /// <summary>`slots` (0.7.0): the request from the options, then the report. You come first among the essential
    /// people; anyone named both ways is essential.</summary>
    private static string SlotsCommand(Options options, Config config, ISource source, DateTime today, DateTime now)
    {
        if (options.Minutes is not { } minutes)
            throw new UsageException("slots needs --minutes: how long the meeting is.");
        var me = source.Resolve("me");
        var essential = new List<Person> { me };
        foreach (var p in Reports.People(options.People, config, source))
            if (!essential.Any(e => string.Equals(e.Address, p.Address, StringComparison.OrdinalIgnoreCase)))
                essential.Add(p);
        var optional = options.Optional.Count == 0 ? [] : Reports.People(options.Optional, config, source)
            .Where(p => !essential.Any(e => string.Equals(e.Address, p.Address, StringComparison.OrdinalIgnoreCase))).ToList();
        var from = options.From is { } f ? Day(f, today, "--from") : today.Date.AddDays(1);
        var by = options.By is { } b ? Day(b, today, "--by") : Slots.AddWorkingDays(from, 10);
        if (by < from)
            throw new UsageException($"--by ({by:yyyy-MM-dd}) is before --from ({from:yyyy-MM-dd}).");
        var until = options.Until is { } u ? Day(u, today, "--until") : Slots.AddWorkingDays(by, config.Slots.PastByDays);
        if (until < by)
            throw new UsageException($"--until ({until:yyyy-MM-dd}) is before --by ({by:yyyy-MM-dd}).");
        if ((until - from).TotalDays > 62)
            throw new UsageException("That's more than two months to look through: ask for a shorter range.");
        if (options.RoomOnly && options.Place == "online")
            throw new UsageException("--room-only is for a meeting in person: not with --place online.");
        var request = new SlotRequest(essential, optional, minutes, from, by, until, options.Place ?? "either",
            options.Max ?? config.Slots.MaxCandidates, options.Topic, options.RoomOnly);
        var report = Slots.Build(config, source, request, now);
        return options.Format == "json" ? Slots.Json(report) : Slots.Table(report);
    }

    /// <summary>[from, to): from midnight on the first day to midnight after the last.</summary>
    internal static (DateTime From, DateTime To) Range(Options options, DateTime today)
    {
        var from = options.From is { } f ? Day(f, today, "--from") : today.Date;
        DateTime last;
        if (options.To is { } t)
        {
            last = Day(t, today, "--to");
            if (last < from)
                throw new UsageException($"--to ({last:yyyy-MM-dd}) is before --from ({from:yyyy-MM-dd}).");
        }
        else
            last = from.AddDays((options.Days ?? 7) - 1);
        if ((last - from).TotalDays > 366)
            throw new UsageException("That's more than a year: ask for a shorter range.");
        return (from, last.AddDays(1));
    }

    internal static DateTime Day(string text, DateTime today, string option)
    {
        switch (text.ToLowerInvariant())
        {
            case "today":
                return today.Date;
            case "tomorrow":
                return today.Date.AddDays(1);
            case "monday":   // the Monday of this week
                return today.Date.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        }
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day;
        throw new UsageException($"{option} '{text}' isn't a date: use yyyy-mm-dd, today, tomorrow or monday.");
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --config: expected one argument");
                options.Config = argv[i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else
                throw new ArgumentsException($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new ArgumentsException("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands.Select(c => $"'{c}'"))})");

        string Value(string name, ref int at, string arg)
        {
            if (arg.StartsWith(name + "=", StringComparison.Ordinal))
                return arg[(name.Length + 1)..];
            if (++at >= argv.Count)
                throw new ArgumentsException($"argument {name}: expected one argument");
            return argv[at];
        }

        var reading = options.Command is "events" or "work-plan" or "summary" or "contacts" or "slots";
        var summary = options.Command == "summary";
        var contacts = options.Command == "contacts";
        var slots = options.Command == "slots";
        int Number(string name, string text, int min, int max) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
                ? n : throw new ArgumentsException($"argument {name}: expected a number from {min} to {max}, not '{text}'");
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            var name = arg.Split('=', 2)[0];
            switch (reading, name)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
                case (true, "--minutes") when slots:
                    options.Minutes = Number(name, Value(name, ref i, arg), 5, 480);
                    break;
                case (true, "--max") when slots:
                    options.Max = Number(name, Value(name, ref i, arg), 1, 100);
                    break;
                case (true, "--optional") when slots:
                    options.Optional.Add(Value(name, ref i, arg));
                    break;
                case (true, "--by") when slots:
                    options.By = Value(name, ref i, arg);
                    break;
                case (true, "--until") when slots:
                    options.Until = Value(name, ref i, arg);
                    break;
                case (true, "--topic") when slots:
                    options.Topic = Value(name, ref i, arg);
                    break;
                case (true, "--place") when slots:
                    options.Place = Value(name, ref i, arg).ToLowerInvariant();
                    if (options.Place is not ("in-person" or "online" or "either"))
                        throw new ArgumentsException($"argument --place: invalid choice: '{options.Place}' (choose from 'in-person', 'online', 'either')");
                    break;
                case (true, "--json") when slots:
                    options.Format = "json";
                    break;
                case (true, "--room-only") when slots:
                    options.RoomOnly = true;
                    break;
                case (true, "--to" or "--days" or "--out") when slots:
                    throw new ArgumentsException($"unrecognized arguments: {arg} (slots takes --from, --by and --until, and only prints)");
                case (true, "--ring") when contacts:
                    options.Ring = Value(name, ref i, arg);
                    break;
                case (true, "--flagged") when contacts:
                    options.Flagged = true;
                    break;
                case (true, "--from" or "--to" or "--days") when contacts:
                    throw new ArgumentsException($"unrecognized arguments: {arg} (contacts reads the days in the settings' [contacts])");
                case (true, "--from"):
                    options.From = Value(name, ref i, arg);
                    break;
                case (true, "--to"):
                    options.To = Value(name, ref i, arg);
                    break;
                case (true, "--days"):
                    var text = Value(name, ref i, arg);
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 366)
                        throw new ArgumentsException($"argument --days: expected a number from 1 to 366, not '{text}'");
                    options.Days = days;
                    break;
                case (true, "--format"):
                    options.Format = Value(name, ref i, arg).ToLowerInvariant();
                    if (!(summary || contacts || slots ? Formats[..2] : Formats).Contains(options.Format))
                        throw new ArgumentsException($"argument --format: invalid choice: '{options.Format}' (choose from 'table', 'json'{(summary || contacts || slots ? "" : ", 'csv'")})");
                    break;
                case (true, "--weeks") when summary:
                    var w = Value(name, ref i, arg);
                    if (!int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var weeks) || weeks is < 1 or > 52)
                        throw new ArgumentsException($"argument --weeks: expected a number from 1 to 52, not '{w}'");
                    options.Weeks = weeks;
                    break;
                case (true, "--view") when summary:
                    options.View = Value(name, ref i, arg);
                    break;
                case (true, "--depth") when summary:
                    options.Depth = Value(name, ref i, arg).ToLowerInvariant();
                    if (!SummarySettings.Depths.Contains(options.Depth))
                        throw new ArgumentsException($"argument --depth: invalid choice: '{options.Depth}' (choose from 'groups', 'buckets', 'events')");
                    break;
                case (true, "--out"):
                    options.Out = Value(name, ref i, arg);
                    break;
                case (true, "--work-plan") when options.Command == "events":
                    options.WorkPlan = true;
                    break;
                case (true, "--cancelled") when options.Command == "events":
                    options.Cancelled = true;
                    break;
                default:
                    if (arg.StartsWith('-') && arg != "-")
                        throw new ArgumentsException($"unrecognized arguments: {arg}");
                    options.People.Add(arg);
                    break;
            }
        }
        if (options.To is not null && options.Days is not null)
            throw new ArgumentsException("argument --days: not allowed with argument --to");
        if (options.Weeks is not null && (options.To is not null || options.Days is not null))
            throw new ArgumentsException("argument --weeks: not allowed with argument --to or --days");
        if (options.Command == "who" && options.People.Count == 0)
            throw new ArgumentsException("the following arguments are required: people");
        if (options.Command == "slots" && options.People.Count == 0)
            throw new ArgumentsException("the following arguments are required: people (who the meeting is with)");
        if (options.Command is "config" or "sign-in" or "groups" or "rooms" && options.People.Count > 0)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.People)}");
        return options;
    }
}
