# calendar-report

Reads people's Outlook calendars, and your own work plan (office, remote, time off), for a range of days, and shows
them as a table, JSON or CSV: for capacity checks, reviews, or anything that wants to know where people's time goes.
**It only reads.**

```
calendar-report events ann@example.com "Bob Smith" --from monday --days 5
calendar-report events team --work-plan --format csv --out week.csv
calendar-report work-plan --days 14
calendar-report summary --view team --weeks 4
calendar-report contacts --flagged
calendar-report free "Ann Smith"
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
organisations need an admin to approve. **Free/busy** (below) needs **Calendars.Read** (`free_busy_scope`), which a
sign-in shared with another tool usually already holds. **Meeting rooms** (`slots`, 0.10.0) are read from the
directory with **Place.Read.All** (`rooms_scope`), which needs an admin's consent; it's only ever asked for silently,
and without it the rooms come from the settings (`[[slots.rooms]]`). **Teams status** (`free`, 0.12.0) needs
**Presence.Read.All** (`[free] presence_scope`), asked for silently only; organisations that restrict user consent need
an admin to approve it (`sign-in --presence` raises the request). Without it `free` still answers, and says the status
is waiting on IT. **Automatic replies** (`free`) are read through MailTips with **Mail.Read** (`[free]
auto_reply_scope`), also silently.

**It only reads**: nothing is ever written to a calendar. Every call, the free/busy `getSchedule` POSTs included, is
retried on a 429, a 5xx or a dropped connection.

**A colleague's calendar that isn't shared in full** (0.8.0): many organisations' default sharing lets Outlook show
a colleague's meetings with their titles, which an app can only read through free/busy (Graph's `getSchedule`), not
the calendar itself (that answers 403). So when a calendar is refused, `events`, `summary` and `slots` read that
person's free/busy instead, and say **"titles only"**: each meeting's times, show-as, subject and location (where
their sharing shows them) and whether it's private, but not who's invited, the organiser, their response or whether
it recurs. Only when free/busy is refused too is the person shown as not visible.

## Commands

```
calendar-report events [PEOPLE...]              every event in their calendars
calendar-report events [PEOPLE...] --work-plan  ...with each day's work plan
calendar-report work-plan [PEOPLE...]           where they're working each day
calendar-report summary [PEOPLE...]             where their time went, by week (see Summaries)
calendar-report contacts [PEOPLE...]            when you last met, and next meet, the people in your rings (see Contacts)
calendar-report slots PEOPLE... --minutes N     times for a meeting with them, and what's in the way (see Slots)
calendar-report free PEOPLE...                  when they're free today for a quick call, and their Teams status (see Free to call)
calendar-report rooms                           the meeting rooms slots chooses from, best first (see Slots)
calendar-report who PEOPLE...                   who those words mean
calendar-report groups                          the groups in the settings
calendar-report config                          create the settings file and say where it is
calendar-report sign-in                         sign in to Microsoft (once; opens a browser)
calendar-report sign-in --presence              ask for Teams status (Presence.Read.All) on its own (see Free to call)
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

**For `contacts`:** `--format table|json` and `--out` as above, `--ring NAME` (one ring) and `--flagged` (only the
people flagged). Its days come from the settings, so it takes no `--from`.

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

## Contacts

`contacts` answers "who haven't I met for a while?" from **your own calendar**: for each person in the rings in your
settings, the last meeting you both attended, the next one booked, and, for a recurring meeting, how many of its
recent occurrences were lost. Someone past their ring's gap is flagged with `!`.

```
calendar-report contacts                  everyone in the rings
calendar-report contacts --flagged        only the flagged
calendar-report contacts --ring wider     one ring
calendar-report contacts "Ann Smith"      one person (anyone not in a ring is shown, never flagged)
```

```
Contacts as of Fri 20 Nov 2026 13:51: meetings of 6 people or fewer, 23 Jul to 19 Jan. ! = flagged.

core  (flagged after 10 days, or 2 of a recurring meeting's last 4 lost)
  Ann Smith  last Tue 17 Nov (3 days)      next Tue 24 Nov
! Bob Jones  last Mon 02 Nov (18 days)     nothing booked
             "Bob / Pat 1:1" (weekly): 2 of the last 4 lost (09 Nov, 16 Nov)
             why: Engineering lead for billing

wider  (flagged after 42 days)
! Cara Lee   last Wed 30 Sep (7 weeks)     nothing booked
             why: Head of Delivery: plans and architecture in step
```

**What counts:**

- **A meeting is contact** when it isn't all-day or cancelled, they're its organiser or invited and haven't declined,
  it has `max_attendees` people or fewer (you included; rooms aren't people), and you accepted or organised it.
  Someone is matched by address, or by display name.
- **The next one booked** is the first ahead that you haven't declined (tentative and unanswered count).
- **Lost occurrences:** for each recurring meeting with them of `cadence_max_attendees` people or fewer (default 2,
  a 1:1), the dates it was due on are worked out from its pattern, and each of the last `recent_occurrences` (before
  now) with no occurrence held on that date is lost: cancelled, removed from your calendar, or declined by either of
  you. An occurrence moved to another day counts for the day it was first due. A series with nothing due from today
  on has ended, and isn't checked.
- **Flagged:** no meeting in the lookback; or more days since the last one than the ring's `gap_days`; or, for a ring
  with `lost_occurrences`, that many of a recurring meeting's last occurrences lost.

```toml
[contacts]
max_attendees = 6            # an all-hands isn't contact
cadence_max_attendees = 2    # recurring meetings checked for lost occurrences: 1:1s
lookback_days = 120          # how far back to look (at least the longest gap)
lookahead_days = 60          # how far ahead for the next one booked (default: calendar_months_ahead, else 60)
recent_occurrences = 4       # how many of a recurring meeting's latest occurrences are checked

[[contacts.ring]]
name = "core"
gap_days = 10
lost_occurrences = 2         # optional
people = ["team"]            # addresses, names or groups from [groups]
[[contacts.ring]]
name = "wider"
gap_days = 42
people = ["Cara Lee", "dev@example.com"]

[contacts.why]               # optional: one line each, by address, name, or the group they came from
"Cara Lee" = "Head of Delivery: plans and architecture in step"
"team" = "Direct report"
```

Someone named in two rings is in the first. **JSON** has `asOf`, the range, and per ring its `gapDays`,
`lostOccurrences` and `people`, each with `name`, `address`, `why`, `last` and `next` (`start`, `subject`, `people`),
`daysSince`, `recurring` (`subject`, `pattern`, `checked`, `lost` dates), `flagged` and `reasons`.

**Notes about people are personal data.** A `why` line is about the work, never a judgement of the person: write it
as if they might read it.

## Slots

`slots` (0.7.0) finds times for a meeting with several people, and doesn't stop at "everyone's free". For each
candidate time it shows, per person, **what's in the way, labelled `fixed`, `soft` or `movable`**, with why: your own
blocked time and a 1:1 with you can be moved, a sprint ceremony or a meeting they're tentative on might be gone
over, an outside meeting can't. It's the data for deciding (a person, or a tool such as an assistant, weighs the
options); it only reads, and **it only prints**: nothing about anyone's calendar is written to disk.

```
calendar-report slots ann@example.com "Bob Jones" --minutes 45 --by 2026-12-04
calendar-report slots ann@example.com --optional bob@example.com --minutes 30 --place in-person --json
```

| Option | |
|---|---|
| `PEOPLE` | Who the meeting is with (names, addresses, groups). **You're always included**, as essential |
| `--minutes N` | How long (required) |
| `--optional PEOPLE` | People who'd be good to have but aren't essential (repeat it, or use commas) |
| `--from DATE` | The first day (default tomorrow) |
| `--by DATE` | When you'd like it by: a **preference**. Default 10 working days after `--from` |
| `--until DATE` | The last day looked at; candidates after `--by` are marked. Default `past_by_days` working days after `--by` |
| `--place P` | `in-person`, `online` or `either` (default): in-person puts your office days (your work plan) first; in-person or either finds a **room** on your office days (below) |
| `--room-only` | Only times with a room (so only your office days); not with `--place online` |
| `--max N` | How many candidates (default `max_candidates`) |
| `--topic WORDS` | Also list **related meetings**: any meeting already in anyone's calendar (in the range) whose subject shares at least half of these words' significant ones (common words dropped; a word in capitals, like `ITS`, kept; `FibreHub` matches "Fibre Hub"). Listed, never a candidate |
| `--format table\|json`, `--json` | The table, or JSON |

**How the candidates are found:**

- Every start time on a grid (`step_minutes`) within `hours` on weekdays from `--from` to `--until`, the meeting
  ending within `hours` too; none in the past.
- **Your leave and bank holidays** (the shared `[leave]` rules, below) have no candidates; on a half day's leave,
  only the other half. Anyone else's leave or bank holiday is a fixed item for them.
- For each person, every event overlapping the time is **labelled by the first rule it meets** in the rule list
  `[slots] rules` (default `slots`; the built-in list below when the settings have none). A rule with `count =
  "skip"` (free time) isn't in the way, `"not-working"` (lunch, travel) is soft and `"leave"` fixed; an event no rule
  meets is **fixed** ("someone else's meeting"). A meeting they declined isn't in the way.
- **Titles only** (their calendar read through free/busy, 0.8.0): a rule that tests what free/busy can't show
  (`organizer`, `organizer_title`, `category`, `own_block`, `one_to_one_with`, `mostly`, `owner_organizes`,
  `owner_attends`, `external`, `recurring`, `response`, `min_attendees`) never holds for their meetings, so rules on
  the subject, show-as, private and length still apply; one no rule meets is **fixed** if busy and **soft** if
  tentative. **No details visible** (no subject at all): busy is fixed, tentative is soft.
- **Adjacent starts collapse** (0.8.0): starts a step apart on a day with exactly the same things in the way become
  one candidate, shown as "10:00-10:45 (or start up to 10:30)" (`latestStart` in JSON).
- **The sift:** a time where anyone essential has something fixed is dropped (the count is shown). The rest are
  sorted: those where what's in the way is movable before soft; fewer people affected; inside `--by` before after it;
  fewer optional people unable to come; fewer soft items; your office days first for in-person; then earliest.
  `lunch` makes a time over lunch count as soft.
- **What's shown** (0.9.0): **the best of each day, sooner days first** (`per_day`, default 1): every day inside
  `--by` in date order, then the days past it. Then, under **"Clean alternatives"**, up to `clean_extra` (default 3)
  times with nothing in anyone's way that the days list doesn't already show, sooner first. `max_candidates` caps the
  whole list and leaves room for the clean ones. **Why:** ranking every clean time first let a run of clean days next
  week fill the list and push out this week's fair trade-offs (a 1:1 of yours that could move, a stand-up that could be
  gone over), and sooner often matters more than clean. JSON gives each candidate a `section`: `by-day` or `clean`.

**Rooms** (0.10.0): **only on your office days** (your work plan says office at the start), and only for `--place
in-person` or `either`; a remote day gets no room, and an online meeting looks for none.

- **The rooms** come from the directory (Graph's `/places`, with `rooms_scope`, default `Place.Read.All`, asked for
  silently, never with a browser). `[[slots.rooms]]` fills in a capacity or floor the directory leaves blank, and adds
  any room only it names. **When the permission isn't granted, `[[slots.rooms]]` alone is used**, and the run says
  "Room sizes from settings (Place.Read.All not granted)". `calendar-report rooms` lists them, in the order tried,
  and where they came from.
- **Which room:** one that seats everyone asked (you, the essential and the optional; **a room with no capacity
  counts as fitting**) and is **free for the whole meeting** (its free/busy, through `getSchedule`; a room whose
  bookings can't be read isn't offered, and the run says so). Of those, **the floors in `room_floors` first**, in
  their order; then rooms matching `room_prefer`; then **the smallest that fits** (unknown sizes last); then by name.
- **None free that fits** is a catch: `room: no room free that fits` in the candidate's `gives`.
- **Ranking is unchanged, except** that for `--place in-person` an office-day time with a room comes before one
  without, all else equal. Rooms are chosen before adjacent starts collapse, so a run of starts shares its room.
- **It only reads**: booking the room is for whatever makes the meeting (an assistant's hold, say).

```toml
[slots]
room_floors = ["3", "2"]          # rooms on these floors first, in this order (default: none)
room_prefer = '^Board'            # then rooms whose name or address matches (default: none)

[[slots.rooms]]                   # the rooms when the directory's can't be read; else filling in its blanks
name = "Room 3.A"
address = "room3.a@example.com"
capacity = 6                      # optional: none counts as fitting
floor = "3"                       # optional
```

**The built-in labels** (used when the settings have no `[[rules.slots]]`), first match wins:

| Label | When | Why |
|---|---|---|
| (not in the way) | shown as free | |
| fixed | shown as out of office | away |
| fixed | anyone outside the organisation (`external`) | an outside organisation's diary |
| movable | a 1:1 with you (`one_to_one_with = "owner"`) | |
| movable | you organised it (`owner_organizes`) | your team meetings, your own blocks |
| soft | a sprint ceremony: stand-up, daily scrum, (backlog) refinement, sprint planning, sprint review, retro | might be gone over |
| soft | they're tentative (`response = "tentative"`) | |
| soft | their own blocked time (`own_block`) | |
| soft | 12 or more invited (`min_attendees`) | one of many |
| fixed | anything else | someone else's meeting |

**Your own rules** replace the built-in list: a `[[rules.slots]]` list with the same tests as a summary's rules (and
`use` to borrow another list), where each rule has a **`label`** (`fixed`, `soft` or `movable`) and a **`why`**
instead of a group or count. Senior meetings that never move go near the top, for example:

```toml
[[rules.slots]]
show_as = "free"
count = "skip"
[[rules.slots]]
organizer = '^(Jane Director|Board)$'     # or organizer_title = '\b(Director|Chief)\b'
label = "fixed"
why = "a senior meeting"
[[rules.slots]]
one_to_one_with = "owner"
label = "movable"
why = "a 1:1 with you"
```

**The rule tests added for `slots`** (usable in any rule list):

| Test | Holds when |
|---|---|
| `one_to_one_with = "owner"` | It's a 1:1 between the person and you (as well as a group, or `"manager"`) |
| `owner_organizes` | `true`: you organised it (your own blocks included); `false`: not |
| `owner_attends` | `true`: you're in it (organiser or invited); `false`: not |
| `external` | `true`: someone invited (rooms aside), or the organiser, is outside the organisation (`org_domain`, by default your own address's domain) |
| `recurring` | `true`: an occurrence of a recurring meeting; `false`: a one-off |
| `response` | The person's own response is one of these: `accepted`, `tentative`, `none`, `organiser`, `declined` (one, or a list) |

A label rule can't be in a list a summary view uses (it has no group), and a summary rule's `why` is refused.

```toml
[slots]
hours = "09:00-17:00"     # candidates within these (default)
lunch = "12:00-13:30"     # a time over lunch is soft (default: none)
step_minutes = 15         # the grid (default 15 since 0.8.0; 30 before)
max_candidates = 15       # default
per_day = 1               # each day's best, this many (default 1 since 0.9.0; 3 before)
clean_extra = 3           # clean alternatives added after the days (default)
past_by_days = 5          # working days past --by that --until defaults to (default)
rules = "slots"           # the rule list (default)
org_domain = "example.com"   # for external (default: your own address's domain)
```

**`[leave]`** is the shared leave settings (background-tools-lib's CalendarDays: `subjects`, `categories`,
`min_hours`, `half_day`, `bank_holiday_subjects`, `bank_holiday_categories`); a base file's `[leave]` fills it in
when this file has a `[leave]` section. Without one, the library's defaults are used.

**JSON:** `minutes`, `from`, `by`, `until`, `place`, `timeZone`, `people` (`name`, `address`, `essential`),
`considered`, `ruledOut`, `notes`, `titlesOnly` (the names read through free/busy), `roomsFrom` (`directory`,
`settings`, or null when no room was looked for), `roomOnly`, `topic` and `related` (with
`--topic`: each `subject`, `start`, `end`, `organizer` (null for titles only) and `people`, who of those asked has
it), and `candidates`: each with `start`, `end`, `latestStart` (null unless collapsed), `pastBy`, `ownerLocation` (your work
plan: `office`, `remote`, `timeOff`, or null), `roomSought` (a room was looked for: your office day, in person or
either), `room` (`name`, `address`, `capacity`, `floor`; null when none was free that fits, or none was sought),
`lunch`, `people` (`name`, `address`, `essential`, `readable`,
`titlesOnly`, `free`, `items`: `subject` (null when not visible), `start`, `end`, `label`, `why`, `rule`, `detailsVisible`) and
`gives` (a line for each thing in the way).

## Free to call

`free` (0.12.0) answers "can I call them?" in a few lines: their Teams status, where they are now by their calendar,
and the gaps left today of `min_minutes` or more (default 10), with whether you're free in each. **It only reads and
only prints: no meeting titles are printed, nothing is sent, and nothing about anyone is written anywhere** (the log
gets one line per run: how many people were named and how it ended).

```
calendar-report free "Ann Smith"                       today's gaps from now
calendar-report free "Ann Smith" --now                 status, now and the next gap only
calendar-report free ann@example.com --day tomorrow    that day's gaps (no Teams status: it's only ever "now")
calendar-report free team --json                       a group: one block each
calendar-report free "Ann Smith" --chat "the IPv4 plan"   also a pre-filled Teams chat link
```

```
Ann Smith · Tue 24 Nov, 13:05
  Teams:    shows Busy, in a meeting · "Back at 2"
  Now:      busy (in a meeting or busy) until 13:30
  Free today (10 min or more, within 08:30-17:30):
    13:30-14:00   30 min   you're in something until 13:55
    16:00-17:30   90 min   you're free 16:00-16:30, 17:00-17:30
                        their own block 16:00-17:00: could be interrupted for something quick
  Phone:    0161 000 0000 (work)
  Call:     https://teams.microsoft.com/l/call/0/0?users=ann@example.com
```

| Option | |
|---|---|
| `PEOPLE` | Names, addresses or groups, as elsewhere. A name matching two people lists both and stops |
| `--now` | Status, now and the next gap only |
| `--day D` | `today` (the default), `tomorrow` or `yyyy-mm-dd`: that whole working day. Not with `--now` |
| `--min N` | The shortest gap, in minutes (default `[free] min_minutes`) |
| `--lunch` | Look past their lunch entries: a gap over one is shown and marked "over a lunch entry" |
| `--no-presence` | Don't read Teams status |
| `--chat TOPIC` | Also a Teams chat link with "Have you got 5 minutes for a quick call about TOPIC?" filled in. **You send it**: the link opens Teams with the message typed, and nothing is sent until you do |
| `--format table\|json`, `--json` | The table, or JSON |

**How the gaps are found:**

- **Their calendar** in full where it's shared, otherwise their free/busy (titles only), as elsewhere.
- **Their working hours** come from their Outlook settings (free/busy's `workingHours`: days, start and end, converted to
  your time zone); when those can't be read, `[slots] hours` on weekdays. A day outside their working days says so.
- **What closes a gap:** busy, tentative and out of office. Show-as free and working elsewhere don't, nor do meetings
  they declined (full calendars only), nor anything a rule in `[free] rules` (default `slots`) counts as `skip`.
- **Lunch, travel and the like are away:** a rule's `count = "not-working"` (and `"leave"`) closes a gap. A not-working
  entry whose subject says lunch, or that starts in `[slots] lunch`, is a **lunch entry**, which `--lunch` looks past.
- **Their own blocks and focus time don't close a gap**: they're listed in it as "could be interrupted for something
  quick". Their own is an entry with no one else invited (only visible on a calendar shared in full); focus time is a
  subject matching `[free] focus`.
- **Leave and bank holidays** (the shared `[leave]` rules): the day says "On leave today (back Thu 26 Nov)" and stops;
  half a day's leave takes that half out.
- **Today, nothing before now is listed**: a gap already under way starts now.
- **Your side:** each gap says where you're free too (stretches of five minutes or more). Your own blocks count as free
  (you can step out of one); your travel, lunch and out-of-office entries don't.

**Teams status** (today only): availability and activity ("Busy, in a meeting"), their status message, and where
they're working when Teams knows. It's read with `presence_scope` **asked for silently only**: never a browser, never a
failed run, and the shared sign-in is left as it was. Then, with the calendar, one line where they disagree or it
matters: "On a call now (not in the calendar)", "The calendar says busy until 14:30, but Teams shows Available: the
meeting may have ended", "Do not disturb: a chat rather than a call", "Away from their desk", "Offline in Teams (may be
on mobile, or not at a desk)". Teams' own status lags by a few minutes, and "Available" isn't "happy to take a call":
the answer says "shows", not "is".

**Their automatic reply:** when it's on, its first line (as anyone emailing them sees it): from Teams' out-of-office
settings when status can be read, otherwise from MailTips (`auto_reply_scope`, silently).

**Contact:** a Teams call link (Teams asks before it dials) and the work numbers the directory lists for them
(`businessPhones`, `mobilePhone`; needs `lookup_scope = "User.Read.All"`). Nothing else: no other source of numbers is
read.

**Getting Teams status:** run `calendar-report sign-in --presence` at a console. It asks for `presence_scope` on its
own, so the usual sign-in (and every other tool sharing the token cache) is unaffected whatever the answer. Where the
organisation restricts consent, the browser shows "Approval required" with a request to send to IT; once they approve,
`free` shows status by itself, with nothing to install or change.

**When it fails:**

| Failure | What you see |
|---|---|
| Presence.Read.All not granted | `Teams:    status waiting on IT (Presence.Read.All not granted yet)`; the rest as usual |
| Presence refused for them (external, guest, unlicensed) | `Teams:    status not available for them` |
| Status read failed otherwise | `Teams:    status couldn't be read (the reason)` |
| Their calendar can't be read at all | "Can't see their calendar (neither in full nor as free/busy)"; status, auto-reply and contact still shown |
| Their working hours can't be read | `[slots] hours` on weekdays, marked "default hours" |
| Automatic reply can't be read | No auto-reply line |
| Sign-in expired | Exit 2, with what to run |
| Graph slow or throttled | The usual retries; then exit 1 with the reason |

```toml
[free]
min_minutes = 10                      # the shortest gap shown (default)
rules = "slots"                       # the rule list for what's in the way (default; the built-in slot labels without one)
presence_scope = "Presence.Read.All"  # Teams status, asked for silently only (default)
auto_reply_scope = "Mail.Read"        # automatic replies through MailTips (default)
show_phone = true                     # the work numbers the directory lists (default)
focus = '\bfocus\b'                   # subjects of blocks that could be interrupted (default)
```

**JSON:** `day`, `now`, `today`, `timeZone`, `minMinutes`, `lunch`, and `people`, each with `name`, `address`,
`readable`, `titlesOnly`, `dayNote`, `notes`, `workingHours` (`start`, `end`, `from`: `outlook` or `settings`), `now`
(`state`: free, busy, away, lunch, own-block, before-hours, after-hours, not-working or leave; `until`; `text`),
`presence` (`state`: shown, not-granted, not-available, failed; `why`, `availability`, `activity`, `text`,
`statusMessage`, `outOfOffice`, `workLocation`, `note`; null when not asked), `autoReply` (`on`, `firstLine`; null when
off), `gaps` (`start`, `end`, `minutes`, `ownerFree` stretches, `owner` in words, `soft` own blocks, `overLunch`),
`phones` (`number`, `type`), `callLink` and `chatLink`.

## What it reads

- **Events:** every event from `calendarView` between midnight on the first day and midnight after the last, with
  recurring meetings expanded: start, end, all-day, show-as (busy, free, tentative, away, working elsewhere),
  subject, location, organiser (name, and address in JSON and CSV), number of attendees, online or not, sensitivity,
  categories, cancelled, and your response. **Since 0.3.0, who the attendees are too**: in JSON, `invitees`, each with
  `name`, `address`, `type` (required, optional, or `resource` for a room) and `response`; in CSV, their addresses.
  The organiser isn't one of them. **Since 0.5.0, where each sits in a recurring meeting**: in JSON, `type`
  (`singleInstance`, `occurrence` or `exception`), `seriesMasterId` and `originalStart` (when it was first due);
  `contacts` also reads each series' pattern. You see what Outlook shows you: someone's private appointments carry
  only what their sharing allows.
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
  `tenant`, `msal_cache`, `time_zone` and `calendar_months_ahead`;
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
free_busy_scope = "Calendars.Read"   # optional: the permission free/busy is read with, for a calendar refused in full
rooms_scope = "Place.Read.All"       # optional: the permission the directory's meeting rooms are read with (slots)
                                     # (free's own permissions are in [free]: see Free to call)
calendar_months_ahead = 3            # optional (1-24): how far ahead `contacts` looks for the next one booked, in
                                     # calendar months, usually from the base; [contacts] lookahead_days wins over it

[groups]                             # names for lists of people
team = ["ann@example.com", "Bob Smith"]

# [summary], [view.NAME] and [[rules.NAME]]: see Summaries
# [contacts], [[contacts.ring]] and [contacts.why]: see Contacts
# [slots], [[slots.rooms]] and [leave]: see Slots; [free]: see Free to call
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
