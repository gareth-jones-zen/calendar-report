using System.Globalization;
using System.Reflection;
using System.Text;
using M365Graph;
using WinBackground;

namespace ScheduleReport;

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
}

/// <summary>schedule-report: read people's calendars and work plans for a range of days. It only reads. Exit codes: 0
/// done; 1 failed; 2 settings, arguments or sign-in.</summary>
public static class Cli
{
    public const string Prog = "schedule-report";

    private const string Usage =
        "usage: schedule-report [-h] [--version] [--config CONFIG] {config,sign-in,events,work-plan,who,groups} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "schedule-report: read people's Outlook calendars and work plans (office, remote, time off) for a range of days.\n" +
        "It only reads.\n\n" +
        "    schedule-report events [PEOPLE...]       every event in their calendars\n" +
        "    schedule-report events [PEOPLE...] --work-plan   ...with each day's work plan\n" +
        "    schedule-report work-plan [PEOPLE...]    where they're working each day\n" +
        "    schedule-report who PEOPLE...            who those words mean (names looked up in the directory)\n" +
        "    schedule-report groups                   the groups in the settings\n" +
        "    schedule-report config                   create the settings file (if missing) and say where it is\n" +
        "    schedule-report sign-in                  sign in to Microsoft (once; opens a browser)\n\n" +
        "  PEOPLE: me (the default), addresses, names, or groups from the settings; commas or spaces between.\n\n" +
        "  events and work-plan:\n" +
        "    --from DATE        the first day (yyyy-mm-dd, today, tomorrow or monday); default today\n" +
        "    --to DATE          the last day, included; or\n" +
        "    --days N           how many days (default 7)\n" +
        "    --format F         table (default), json or csv\n" +
        "    --out FILE         write it to a file instead\n" +
        "    --cancelled        events: include cancelled meetings still on the calendar\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\schedule-report\\config.toml)\n";

    private static readonly string[] Commands = ["config", "sign-in", "events", "work-plan", "who", "groups"];
    private static readonly string[] Formats = ["table", "json", "csv"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config;
        public string Command = "";
        public List<string> People = [];
        public string? From, To, Out;
        public int? Days;
        public string Format = "table";
        public bool WorkPlan, Cancelled;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    public static GraphClient GraphFor(Config config) => new(new GraphClientOptions
    {
        CachePath = config.MsalCache ?? M365Graph.Graph.DefaultCachePath,
        Tenant = config.Tenant,
        LoginHint = config.LoginHint,
        Scopes = [GraphSource.CalendarScope, config.LookupScope],
        SignInHint = "At a console, run: schedule-report sign-in",
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
                host.Out.WriteLine(ScheduleReport.Config.CreateDefault(options.Config ?? host.Env(ScheduleReport.Config.EnvConfig), host.Env));
                return 0;
            }
            var config = ScheduleReport.Config.Load(options.Config, host.Env);
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
            var people = Reports.People(options.People, config, source);
            if (options.Command == "who")
            {
                foreach (var person in people)
                    host.Out.WriteLine(person.Address == "me" ? $"me: {person.Name}" : $"{person.Name} <{person.Address}>");
                return 0;
            }
            var (from, to) = Range(options, host.Today());
            var report = Reports.Read(people, source, from, to, config.TimeZone.Id,
                events: options.Command == "events", workPlan: options.Command == "work-plan" || options.WorkPlan,
                cancelled: options.Cancelled);
            var text = options.Format switch
            {
                "json" => Reports.Json(report),
                "csv" => Reports.Csv(report),
                _ => Reports.Table(report),
            };
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
            host.Err.WriteLine($"Microsoft 365 refused: {error.Message} (is their calendar shared with you?)");
            return 1;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
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

        var reading = options.Command is "events" or "work-plan";
        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            var name = arg.Split('=', 2)[0];
            switch (reading, name)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
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
                    if (!Formats.Contains(options.Format))
                        throw new ArgumentsException($"argument --format: invalid choice: '{options.Format}' (choose from 'table', 'json', 'csv')");
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
        if (options.Command == "who" && options.People.Count == 0)
            throw new ArgumentsException("the following arguments are required: people");
        if (options.Command is "config" or "sign-in" or "groups" && options.People.Count > 0)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.People)}");
        return options;
    }
}
