using System.Text.RegularExpressions;
using PyCompat;
using Tomlyn;
using Tomlyn.Model;

namespace CalendarReport;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>The settings file.
///
///     login_hint = "you@example.com"
///     tenant = "organizations"            # or a tenant id
///     msal_cache = "..."                  # optional: share another tool's sign-in cache
///     log_file = "..."                    # optional; default %LOCALAPPDATA%\calendar-report\calendar-report.log
///     time_zone = "GMT Standard Time"     # optional: times are shown in this (Windows) time zone; default the PC's
///     lookup_scope = "User.ReadBasic.All" # optional: the permission used to look people up by name
///
///     [groups]                            # optional: names for lists of people
///     team = ["ann@example.com", "Bob Smith"]
///
///     [summary], [view.NAME], [[rules.NAME]]    # optional: how `summary` adds time up (SummarySettings)
///
/// The file is read strictly: an unknown key, a key given twice or a value of the wrong type is an error that names
/// the setting. Paths may use ~ and %VARIABLES%; a relative path is relative to the settings file.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "CALENDAR_REPORT_CONFIG";

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# calendar-report settings. See the README.\n" +
        "# base = \"base-config.toml\"        # settings shared with other tools\n" +
        "# login_hint = \"you@example.com\"\n" +
        "# tenant = \"organizations\"          # or your organisation's tenant id\n" +
        "# msal_cache = \"...\"                # another tool's token cache, to share its sign-in\n" +
        "# log_file = \"...\"                  # default %LOCALAPPDATA%\\calendar-report\\calendar-report.log\n" +
        "# time_zone = \"GMT Standard Time\"   # default: this PC's time zone\n" +
        "# lookup_scope = \"User.ReadBasic.All\"\n" +
        "\n" +
        "# Names for lists of people, to use in place of addresses: calendar-report events team\n" +
        "# [groups]\n" +
        "# team = [\"ann@example.com\", \"bob@example.com\"]\n";

    /// <summary>Every top-level setting: the strict check's list, and what a base file may supply.</summary>
    public static readonly string[] KnownKeys =
        ["login_hint", "tenant", "msal_cache", "log_file", "time_zone", "lookup_scope", "groups", "summary", "view", "rules"];

    private static readonly string[] TopKeys = KnownKeys;

    public required string Path { get; init; }
    public string? LoginHint { get; init; }
    public string Tenant { get; init; } = "organizations";
    public string? MsalCache { get; init; }
    public required string LogFile { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public string LookupScope { get; init; } = "User.ReadBasic.All";
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>[summary], [view.NAME] and [rules.NAME]: how `summary` adds time up (0.4.0).</summary>
    public SummarySettings Summary { get; init; } = SummarySettings.Parse([], "", new Dictionary<string, IReadOnlyList<string>>());

    /// <summary>The real environment; an empty name is no variable.</summary>
    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    /// <summary>%LOCALAPPDATA%\calendar-report: the default log.</summary>
    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "calendar-report"));
    }

    /// <summary>%APPDATA%\calendar-report\config.toml.</summary>
    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "calendar-report", "config.toml"));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex GroupName();

    /// <summary>The settings. A missing file at the default place gives the defaults (so `events me` works with no
    /// settings); a missing file that was named is an error.</summary>
    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var named = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : null;
        var full = PyPath.Resolve(named ?? DefaultPath(env));
        TomlTable raw = [];
        if (File.Exists(full))
        {
            try
            {
                var text = PyStr.ReadText(full);
                if (text.StartsWith('﻿'))
                    text = text[1..];
                var read = BaseSettings.Read(text, full, env, KnownKeys);
                raw = read.Raw;
                env = read.Env;   // the base's [paths], then the environment
            }
            catch (TomlException error)
            {
                throw new UsageException($"{full}: {error.Message.TrimEnd()}");
            }
            catch (FormatException error)   // the base file: the message names it
            {
                throw new UsageException(error.Message.TrimEnd());
            }
            catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
            {
                throw new UsageException($"{full}: {error.Message.TrimEnd()}");
            }
        }
        else if (named is not null)
            throw new UsageException($"No settings file at {full}. Run `calendar-report config` to create one.");

        foreach (var key in raw.Keys)
            if (!TopKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", TopKeys)}).");

        var folder = System.IO.Path.GetDirectoryName(full)!;
        string? Text(string key) => raw.TryGetValue(key, out var value)
            ? value as string ?? throw new UsageException($"{full}: {key} should be a string.")
            : null;
        string PathSetting(string value)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, env), env);
            return PyPath.IsAbsolute(expanded) ? PyPath.Pure(expanded) : PyPath.ResolveAgainst(folder, expanded);
        }

        TimeZoneInfo zone;
        var zoneName = Text("time_zone");
        try
        {
            zone = string.IsNullOrWhiteSpace(zoneName) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneName);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new UsageException($"{full}: time_zone '{zoneName}' isn't a time zone this PC knows (a Windows name, such as \"GMT Standard Time\").");
        }

        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (raw.TryGetValue("groups", out var g))
        {
            if (g is not TomlTable table)
                throw new UsageException($"{full}: [groups] should be a table of lists, such as team = [\"ann@example.com\"].");
            foreach (var (name, value) in table)
            {
                if (!GroupName().IsMatch(name))
                    throw new UsageException($"{full}: group name '{name}' should be letters, digits, - and _ only.");
                if (value is not TomlArray members || members.Count == 0 || members.Any(m => m is not string s || s.Trim().Length == 0))
                    throw new UsageException($"{full}: groups.{name} should be a list of addresses or names.");
                groups[name] = members.Select(m => ((string)m!).Trim()).ToList();
            }
        }

        return new Config
        {
            Path = full,
            LoginHint = Text("login_hint"),
            Tenant = Text("tenant") is { Length: > 0 } t ? t : "organizations",
            MsalCache = Text("msal_cache") is { Length: > 0 } cache ? PathSetting(cache) : null,
            LogFile = Text("log_file") is { Length: > 0 } log ? PathSetting(log) : PyPath.Pure(System.IO.Path.Combine(Local(env), "calendar-report.log")),
            TimeZone = zone,
            LookupScope = Text("lookup_scope") is { Length: > 0 } scope ? scope : "User.ReadBasic.All",
            Groups = groups,
            Summary = SummarySettings.Parse(raw, full, groups),
        };
    }

    /// <summary>Create the settings file (if missing) and return where it is.</summary>
    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
