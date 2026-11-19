# calendar-report

Reads people's Outlook calendars, and your own work plan (office, remote, time off), for a range of days, and shows
them as a table, JSON or CSV: for capacity checks, reviews, or anything that wants to know where people's time goes.
**It only reads.**

```
calendar-report events ann@example.com "Bob Smith" --from monday --days 5
calendar-report events team --work-plan --format csv --out week.csv
calendar-report work-plan --days 14
calendar-report summary --view team --weeks 4
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
calendar-report summary [PEOPLE...]             where their time went, by week (see Summaries)
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

**For `summary`:** `--from`, `--to`, `--days`, `--format table|json` and `--out` as above (the default period is
last week, Monday to Sunday), and:

| Option | |
|---|---|
| `--weeks N` | How many weeks from `--from` (or last week's Monday) |
| `--view NAME` | A view from the settings (default `summary.default_view`) |
| `--depth D` | `groups`, `buckets`, or `events`: every event under its bucket, with the minutes it counted |

PEOPLE given on the command line replace the view's.

**Exit codes:** 0 done; 1 failed (Microsoft 365 refused, say); 2 settings, arguments or sign-in.

## Summaries

`summary` adds up where people's time went, week by week: hours and minutes per **group** (such as "Meetings with
my team") and **bucket** within it, the time left available, and how many free blocks there were between meetings.
Rules in the settings decide where each event goes, so the groups are yours.

```
Ann Smith  (37.5 h week; leave: Mon 16 Nov)
                                              w/c 09 Nov  w/c 16 Nov       total     %
Team sessions                                     4h 30m      2h 00m      6h 30m   10%
Ceremonies                                        5h 30m      4h 30m     10h 00m   15%
Task time                                         2h 45m      2h 45m      5h 30m    8%
Available                                        14h 30m     16h 30m     31h 00m   46%
Week total (the % base)                          37h 30m     30h 00m     67h 30m  100%
Free blocks between meetings (how many)
  30m-1h                                               7           4          11
  1h-2h                                                6           9          15
```

**How time is counted:**

- **Each event goes to the first rule it meets.** Where events overlap, the overlapping minutes count once, for the
  event whose rule comes first. Cancelled events never count.
- **Lengths are rounded up to the quarter hour** (`round_up_to`, default 15): Outlook's "end meetings early" makes a
  half-hour meeting 25 minutes and an hour 50 or 55, and the gap left isn't free time.
- **A week is `work_week_hours` long** (default 37.5; per person under `[summary.work_week]`), less a fifth for each
  day of leave. **Available** is the week less the time counted; a week with more counted than that is its own total
  (and nothing is available). Percentages are of the total. Saturdays and Sundays aren't counted.
- **Free blocks** are the gaps between one entry and the next on a working day, counted by size
  (`free_bands`). The time before a day's first entry and after its last isn't one, as working hours aren't known.
- A rule's `count` can instead **skip** an event (shown as free, say), make its days **leave** (taken out of the
  week), or mark it **not-working** (travel, lunch: neither busy nor free, but it bounds free blocks).

With no rules in the settings, a built-in list is used: free time skipped, your own blocks as "Task time", everything
else as "Meetings".

```toml
[summary]
default_view = "me"
order = ["Me", "Team", "Task time", "Other"]   # groups shown first, in this order; the rest as the rules name them
free_bands = [30, 60, 120, 150]                # minutes: 30m-1h, 1h-2h, 2h-2h 30m, 2h 30m+
tentative = "busy"                             # or "free": events shown as tentative don't count
round_up_to = 15
work_week_hours = 37.5
[summary.work_week]
"ann@example.com" = 30                         # an address, or "me"

[view.me]
rules = "mine"            # a rule list, below
people = ["me"]           # people or groups (default me)
depth = "buckets"         # groups (default), buckets or events
split = { "Projects" = "subject", "Team / 1:1s" = "person" }   # buckets listed by subject or attendees
[view.team]
rules = "mine"
people = ["team"]
team_total = true         # everyone added together, after each person

[[rules.common]]          # a list other lists can use
show_as = "free"
count = "skip"
[[rules.common]]
private = true
subject = '^private appointment$'
starts_between = "11:30-14:00"
max_minutes = 60
count = "not-working"     # lunch

[[rules.mine]]
use = "common"            # the common rules, here
[[rules.mine]]
group = "Me"
bucket = "1:1 with my manager"
one_to_one_with = "manager"
[[rules.mine]]
group = "Team"
bucket = "1:1s"
one_to_one_with = "team"  # a group from [groups]
[[rules.mine]]
group = "Team"
bucket = "Team meetings"
mostly = "team"
[[rules.mine]]
group = "Task time"
bucket = "Blocked time"
own_block = true
```

Anything no rule matches goes to Other / Everything else.

**A rule's tests** (all must hold; patterns are regular expressions, matched ignoring case):

| Test | Holds when |
|---|---|
| `subject`, `not_subject` | The subject matches (doesn't match) the pattern |
| `category` | One of its Outlook categories matches |
| `organizer` | The organiser's name matches |
| `organizer_title` | The organiser's job title in the directory matches |
| `one_to_one_with` | Exactly one other person is invited (rooms don't count) and they're in the group, or are the person's `"manager"` |
| `mostly` | At least two others, and at least half of everyone (the person included) is in the group |
| `own_block` | `true`: the person's own, with no one else invited; `false`: anything else |
| `private` | `true`: marked private; `false`: not |
| `show_as` | Outlook's show-as: `free`, `tentative`, `busy`, `oof`, `workingElsewhere` |
| `min_attendees` | At least this many invited, rooms included |
| `min_hours`, `max_minutes` | At least (at most) this long, as booked |
| `starts_between` | It starts between these times, `"hh:mm-hh:mm"`, inclusive |

**Where it goes:** `group` and `bucket` (the bucket defaults to the group), or `count` = `"skip"`, `"leave"` or
`"not-working"`. `use = "name"` puts another list's rules in at that point (on its own in the rule).

**Permissions:** `"manager"` and `organizer_title` read the directory, which needs `lookup_scope = "User.Read.All"`.
Other people's calendars need sharing as for `events`; their private appointments show only as "Private Appointment".

**JSON** has, per person, the leave days, each week (`days`, `base`, `busy`, `total` and `available` in minutes, the
groups and buckets, and `freeBlocks` by band) and every event with its group, bucket or count and the minutes it
counted; with `team_total`, a `team` the same shape without events.

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

# [summary], [view.NAME] and [[rules.NAME]]: see Summaries
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
