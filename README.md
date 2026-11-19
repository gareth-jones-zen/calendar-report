# calendar-report

Reads people's Outlook calendars, and your own work plan (office, remote, time off), for a range of days, and shows
them as a table, JSON or CSV: for capacity checks, reviews, or anything that wants to know where people's time goes.
**It only reads.**

```
calendar-report events ann@example.com "Bob Smith" --from monday --days 5
calendar-report events team --work-plan --format csv --out week.csv
calendar-report work-plan --days 14
```

```
Ann Smith <ann@example.com>
  Mon 16 Nov
    09:00-09:30  busy      Stand-up  · 8 people · online
    10:00-11:00  busy      Design review  · 4 people · online · tentative
  Tue 17 Nov
    all day      free      Release freeze
    13:00-17:00  busy      Focus time
```

## Install

Windows (x64) only; nothing else is needed.

1. Download `calendar-report-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/calendar-report/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\calendar-report`.
3. Run the commands below from that folder, or add it to your `PATH`.

**It needs Calendars.Read.Shared** (to read calendars shared with you, as Outlook does) and **User.ReadBasic.All** (to
look people up by name) on Microsoft's "Graph Command Line Tools" app. The first `sign-in` asks for them; some
organisations need an admin to approve.

## Commands

```
calendar-report events [PEOPLE...]              every event in their calendars
calendar-report events [PEOPLE...] --work-plan  ...with each day's work plan
calendar-report work-plan [PEOPLE...]           where they're working each day
calendar-report who PEOPLE...                   who those words mean
calendar-report groups                          the groups in the settings
calendar-report config                          create the settings file and say where it is
calendar-report sign-in                         sign in to Microsoft (once; opens a browser)
```

**PEOPLE** are `me` (the default), addresses, names (looked up in your organisation's directory: a full name, or
enough of one to be unique) or groups from the settings, separated by spaces or commas.

**For `events` and `work-plan`:**

| Option | |
|---|---|
| `--from DATE` | The first day: `yyyy-mm-dd`, `today`, `tomorrow` or `monday` (this week's). Default today |
| `--to DATE` | The last day, included; or |
| `--days N` | How many days (default 7) |
| `--format F` | `table` (default), `json` or `csv` |
| `--out FILE` | Write to a file instead (CSV gets a byte-order mark, so Excel reads it as UTF-8) |
| `--cancelled` | `events`: include cancelled meetings still on a calendar (left out by default) |

**Exit codes:** 0 done; 1 failed (Microsoft 365 refused, say); 2 settings, arguments or sign-in.

## What it reads

- **Events:** every event from `calendarView` between midnight on the first day and midnight after the last, with
  recurring meetings expanded: start, end, all-day, show-as (busy, free, tentative, away, working elsewhere),
  subject, location, organiser (name, and address in JSON and CSV), number of attendees, online or not, sensitivity,
  categories, cancelled, and your response. **Since 0.3.0, who the attendees are too**: in JSON, `invitees`, each with
  `name`, `address`, `type` (required, optional, or `resource` for a room) and `response`; in CSV, their addresses.
  The organiser isn't one of them. You see what Outlook shows you: someone's private appointments carry only what
  their sharing allows.
- **Work plan:** Outlook and Teams' "work hours and location", as stretches of each day: `office`, `remote` or
  `timeOff`. **Only your own can be read.** Asked for anyone else's, Microsoft Graph answers with *yours*, without an
  error, so calendar-report never asks: another person's work plan is reported as "only your own work plan can be
  read".
- **Times** are shown in the settings' `time_zone`, or this PC's.

In the table, a day with nothing on it is left out of `events` (weekends, usually); `work-plan` shows every day.
JSON has the range, the time zone and, per person, `events` and `workPlan` (with `workPlanNote` where it can't be
read). CSV has a row per event, with a `work_plan` column when `--work-plan` is given, or for `work-plan` alone, a
row per stretch of a day.

## Sharing settings with other tools: `base`

Several tools built on the shared library can keep their common settings in one file, named at the top of each
tool's settings: `base = "base-config.toml"` (relative to the settings file). Then:

- **the base's `[paths]` are variables** for this tool's paths, looked up before the environment: with
  `EA_LOCAL = "%LOCALAPPDATA%/claude-ea"` there, `log_file = "%EA_LOCAL%/calendar-report.log"` works here;
- **its other settings that calendar-report knows and this file doesn't set are used**, typically `login_hint`,
  `tenant`, `msal_cache` and `time_zone`;
- **a table such as `[groups]` applies only if this file has that section too**, its own entries winning.

The loader is background-tools-lib's `PyCompat.BaseSettings`.

## Settings

`%APPDATA%\calendar-report\config.toml` by default (`--config` or `CALENDAR_REPORT_CONFIG` for another). None of it
is needed to read your own calendar.

```toml
base = "base-config.toml"   # optional: settings shared with other tools
login_hint = "you@example.com"
tenant = "organizations"             # or your organisation's tenant id
msal_cache = "..."                   # optional: another tool's token cache, to share its sign-in
log_file = "..."                     # optional; default %LOCALAPPDATA%\calendar-report\calendar-report.log
time_zone = "GMT Standard Time"      # optional (a Windows name); default this PC's
lookup_scope = "User.ReadBasic.All"  # optional: the permission for name lookups (User.Read.All works too)

[groups]                             # names for lists of people
team = ["ann@example.com", "Bob Smith"]
```

The file is read strictly: an unknown setting, or one of the wrong type, is an error that names it.

## Building

.NET 11 (see `global.json`), with the shared libraries from
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib) checked out beside this repo:

```
dotnet test -c Release
dotnet publish src/CalendarReport.Console -c Release -r win-x64 -o out    # Native AOT
```

A tag `vX.Y.Z` builds a release (`.github/workflows/release.yml`), against the library commit pinned there.
