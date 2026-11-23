using System.Text.Json.Nodes;

namespace CalendarReport.Tests;

/// <summary>0.10.0: a meeting room for each candidate on the owner's office days, for a meeting in person or either:
/// the directory's rooms (/places) when the permission is granted, else [[slots.rooms]]; room_floors first, then the
/// smallest that fits; free through free/busy. The owner is in the office on Mon 23 Nov and remote on Tue 24 Nov.</summary>
public sealed class RoomsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-rooms-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSource source = new();
    private static readonly DateTime Today = new(2026, 11, 22);   // a Sunday
    private static readonly DateTime Now = new(2026, 11, 22, 18, 0, 0);

    private const string Ann = "ann@example.com";
    private const string Bob = "bob@example.com";
    private const string R2 = "room2.a@example.com";
    private const string R3Small = "room3.a@example.com";
    private const string R3Big = "room3.b@example.com";

    /// <summary>Every hour as its own candidate, so each can be checked.</summary>
    private const string Hourly = "[slots]\nper_day = 50\nstep_minutes = 60\nmax_candidates = 100\n";

    private const string ListedRooms =
        "[[slots.rooms]]\nname = \"Room 2.A\"\naddress = \"room2.a@example.com\"\ncapacity = 4\nfloor = \"2\"\n" +
        "[[slots.rooms]]\nname = \"Room 3.A\"\naddress = \"room3.a@example.com\"\ncapacity = 4\nfloor = \"3\"\n" +
        "[[slots.rooms]]\nname = \"Room 3.B\"\naddress = \"room3.b@example.com\"\ncapacity = 10\nfloor = \"3\"\n";

    public RoomsTests()
    {
        Directory.CreateDirectory(dir);
        source.Calendars["me"] = [];
        source.Calendars[Ann] = [];
        source.Calendars[Bob] = [];
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-23T09:00"), DateTime.Parse("2026-11-23T17:00"), "office"));
        source.MyPlan.Add(new WorkSegment(DateTime.Parse("2026-11-24T09:00"), DateTime.Parse("2026-11-24T17:00"), "remote"));
        foreach (var room in new[] { R2, R3Small, R3Big })
            source.Bookings[room] = [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string text)
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n" + text);
        return path;
    }

    private Config Settings(string text) => Config.Load(Write(text), _ => null);

    private SlotsReport Build(Config config, string place = "in-person", bool bob = false, bool roomOnly = false, int minutes = 30)
    {
        var request = new SlotRequest([source.Resolve("me"), source.Resolve(Ann)], bob ? [source.Resolve(Bob)] : [], minutes,
            DateTime.Parse("2026-11-23"), DateTime.Parse("2026-11-24"), DateTime.Parse("2026-11-24"), place, 100, null, roomOnly);
        return Slots.Build(config, source, request, Now);
    }

    private static SlotCandidate At(SlotsReport report, string start) =>
        report.Candidates.SingleOrDefault(c => c.Start == DateTime.Parse(start))
        ?? throw new Xunit.Sdk.XunitException($"no candidate at {start}: {string.Join(", ", report.Candidates.Select(c => c.Start.ToString("ddd HH:mm")))}");

    private static void Busy(FakeSource source, string room, string start, string end) =>
        source.Bookings[room].Add((DateTime.Parse(start), DateTime.Parse(end)));

    [Fact]
    public void When_the_rooms_permission_is_refused_the_settings_list_is_used_and_said_once()
    {
        source.DirectoryRooms = null;
        var report = Build(Settings(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms));
        Assert.Equal("settings", report.RoomsFrom);
        Assert.Single(report.Notes, n => n == "Room sizes from settings (Place.Read.All not granted).");
        Assert.Equal("Room 3.A", At(report, "2026-11-23T09:00").Room?.Name);
    }

    [Fact]
    public void When_the_directory_can_be_read_its_rooms_are_used_with_blanks_filled_from_the_settings()
    {
        source.DirectoryRooms =
        [
            new Room("Directory 3.A", R3Small, null, "3", "HQ"),
            new Room("Directory 2.A", R2, 4, "2", "HQ"),
        ];
        var config = Settings(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms);
        var report = Build(config, bob: true);
        Assert.Equal("directory", report.RoomsFrom);
        Assert.DoesNotContain(report.Notes, n => n.StartsWith("Room sizes from settings", StringComparison.Ordinal));
        var room = At(report, "2026-11-23T09:00").Room!;
        Assert.Equal("Directory 3.A", room.Name);
        Assert.Equal(4, room.Capacity);   // the directory left it blank; the settings know it
        var (rooms, from) = Slots.RoomList(config, source);
        Assert.Equal("directory", from);
        Assert.Contains(rooms, r => r.Address == R3Big);   // only the settings name it: added
    }

    [Fact]
    public void Preferred_floors_come_first_then_the_smallest_that_fits()
    {
        source.DirectoryRooms = null;
        // Floor 2 first: Room 2.A, though the floor 3 rooms are listed after it either way.
        Assert.Equal("Room 2.A", At(Build(Settings(Hourly + "room_floors = [\"2\", \"3\"]\n" + ListedRooms)), "2026-11-23T09:00").Room?.Name);
        // Floor 3 first, and of the two on it the smaller that fits.
        Assert.Equal("Room 3.A", At(Build(Settings(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms)), "2026-11-23T09:00").Room?.Name);
        // No floors given: the smallest that fits, then by name.
        Assert.Equal("Room 2.A", At(Build(Settings(Hourly + ListedRooms)), "2026-11-23T09:00").Room?.Name);
        // room_prefer, after the floors.
        Assert.Equal("Room 3.B", At(Build(Settings(Hourly + "room_prefer = '3\\.B'\n" + ListedRooms)), "2026-11-23T09:00").Room?.Name);
    }

    [Fact]
    public void A_room_too_small_is_passed_over_and_a_blank_capacity_counts_as_fitting()
    {
        source.DirectoryRooms = null;
        const string small =
            "[[slots.rooms]]\nname = \"Pod\"\naddress = \"room3.a@example.com\"\ncapacity = 2\nfloor = \"3\"\n" +
            "[[slots.rooms]]\nname = \"Unknown size\"\naddress = \"room3.b@example.com\"\nfloor = \"3\"\n";
        var settings = Settings(Hourly + "room_floors = [\"3\"]\n" + small);
        Assert.Equal("Pod", At(Build(settings), "2026-11-23T09:00").Room?.Name);   // two people: the pod fits
        Assert.Equal("Unknown size", At(Build(settings, bob: true), "2026-11-23T09:00").Room?.Name);   // three, optional included
    }

    [Fact]
    public void A_booked_room_is_skipped_and_none_free_is_a_catch()
    {
        source.DirectoryRooms = null;
        Busy(source, R3Small, "2026-11-23T09:00", "2026-11-23T10:00");
        var config = Settings(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms);
        var report = Build(config);
        Assert.Equal("Room 3.B", At(report, "2026-11-23T09:00").Room?.Name);
        Assert.Equal("Room 3.A", At(report, "2026-11-23T10:00").Room?.Name);

        Busy(source, R3Big, "2026-11-23T09:00", "2026-11-23T10:00");
        Busy(source, R2, "2026-11-23T09:00", "2026-11-23T09:15");
        var none = At(Build(config), "2026-11-23T09:00");
        Assert.True(none.RoomSought);
        Assert.Null(none.Room);
        Assert.Contains("room: no room free that fits", none.Gives);
    }

    [Fact]
    public void A_room_whose_bookings_cant_be_read_isnt_offered()
    {
        source.DirectoryRooms = null;
        source.Bookings.Remove(R3Small);
        var report = Build(Settings(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms));
        Assert.Equal("Room 3.B", At(report, "2026-11-23T09:00").Room?.Name);
        Assert.Contains(report.Notes, n => n.Contains("can't be read, so not offered: Room 3.A", StringComparison.Ordinal));
    }

    [Fact]
    public void A_remote_day_gets_no_room_and_an_online_meeting_asks_for_none()
    {
        source.DirectoryRooms = null;
        var config = Settings(Hourly + ListedRooms);
        var remote = At(Build(config), "2026-11-24T09:00");
        Assert.False(remote.RoomSought);
        Assert.Null(remote.Room);
        Assert.DoesNotContain(remote.Gives, g => g.StartsWith("room:", StringComparison.Ordinal));

        Assert.NotNull(At(Build(config, place: "either"), "2026-11-23T09:00").Room);   // either: a room too

        source.Asked.Clear();
        var online = Build(config, place: "online");
        Assert.All(online.Candidates, c => Assert.False(c.RoomSought));
        Assert.DoesNotContain("rooms", source.Asked);
        Assert.Null(online.RoomsFrom);
    }

    [Fact]
    public void No_rooms_known_is_said_and_nothing_is_sought()
    {
        source.DirectoryRooms = null;
        var report = Build(Settings(Hourly));
        Assert.Contains(report.Notes, n => n.StartsWith("No meeting rooms known", StringComparison.Ordinal));
        Assert.All(report.Candidates, c => Assert.False(c.RoomSought));
    }

    [Fact]
    public void In_person_an_office_time_with_a_room_outranks_one_without()
    {
        source.DirectoryRooms = null;
        foreach (var room in new[] { R2, R3Small, R3Big })
            Busy(source, room, "2026-11-23T09:00", "2026-11-23T10:00");
        var config = Settings("[slots]\nper_day = 1\nstep_minutes = 60\n" + ListedRooms);
        var monday = Build(config).Candidates.Single(c => c.Section == "by-day" && c.Start.Date == new DateTime(2026, 11, 23));
        Assert.Equal(DateTime.Parse("2026-11-23T10:00"), monday.Start);
        Assert.NotNull(monday.Room);
        // Either: the ranking is as before (the earliest), the room or its lack just shown.
        var either = Build(config, place: "either").Candidates.Single(c => c.Section == "by-day" && c.Start.Date == new DateTime(2026, 11, 23));
        Assert.Equal(DateTime.Parse("2026-11-23T09:00"), either.Start);
        Assert.Null(either.Room);
    }

    [Fact]
    public void Room_only_keeps_just_the_times_with_a_room()
    {
        source.DirectoryRooms = null;
        var report = Build(Settings(Hourly + ListedRooms), roomOnly: true);
        Assert.NotEmpty(report.Candidates);
        Assert.All(report.Candidates, c => Assert.NotNull(c.Room));
        Assert.Contains(report.Notes, n => n.StartsWith("--room-only:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_of_starts_is_cut_back_to_when_the_room_is_free()
    {
        source.DirectoryRooms = null;
        Busy(source, R3Small, "2026-11-23T10:00", "2026-11-23T17:00");
        var report = Build(Settings("[slots]\nper_day = 50\nstep_minutes = 15\nroom_floors = [\"3\"]\n" + ListedRooms));
        var first = At(report, "2026-11-23T09:00");
        Assert.Equal("Room 3.A", first.Room?.Name);
        Assert.Equal(DateTime.Parse("2026-11-23T09:30"), first.LatestStart);
    }

    [Theory]
    [InlineData("[[slots.rooms]]\nname = \"A\"\n", "address should be a non-empty string")]
    [InlineData("[[slots.rooms]]\nname = \"A\"\naddress = \"not-an-address\"\n", "the room's email address")]
    [InlineData("[[slots.rooms]]\nname = \"A\"\naddress = \"a@example.com\"\nseats = 4\n", "unknown setting 'seats'")]
    [InlineData("[[slots.rooms]]\nname = \"A\"\naddress = \"a@example.com\"\ncapacity = 0\n", "capacity should be a whole number")]
    [InlineData("[slots]\nroom_floors = \"3\"\n", "room_floors should be a list")]
    [InlineData("[slots]\nroom_prefer = '('\n", "room_prefer isn't a valid regular expression")]
    public void Bad_room_settings_are_named(string text, string message)
    {
        var error = Assert.Throws<UsageException>(() => Settings(text));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_floor_may_be_a_number()
    {
        var settings = Settings("[slots]\nroom_floors = [3, \"2\"]\n[[slots.rooms]]\nname = \"A\"\naddress = \"a@example.com\"\nfloor = 3\n").Slots;
        Assert.Equal(["3", "2"], settings.RoomFloors);
        Assert.Equal("3", settings.Rooms[0].Floor);
    }

    private (int Code, string Out, string Err) Run(string config, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Write(config), .. args], new CliHost
        {
            Out = output, Err = errors, Source = _ => source, Today = () => Today, Now = _ => Now, Env = _ => null,
        });
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public void The_json_has_a_room_for_each_candidate()
    {
        source.DirectoryRooms = null;
        var (code, output, error) = Run(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms,
            "slots", Ann, "--minutes", "30", "--from", "2026-11-23", "--by", "2026-11-24", "--until", "2026-11-24", "--place", "in-person", "--json");
        Assert.True(code == 0, error);
        var root = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("settings", root["roomsFrom"]!.GetValue<string>());
        var candidates = root["candidates"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var office = candidates.First(c => c["ownerLocation"]!.GetValue<string>() == "office");
        Assert.True(office["roomSought"]!.GetValue<bool>());
        var room = office["room"]!.AsObject();
        Assert.Equal("Room 3.A", room["name"]!.GetValue<string>());
        Assert.Equal(R3Small, room["address"]!.GetValue<string>());
        Assert.Equal(4, room["capacity"]!.GetValue<int>());
        Assert.Equal("3", room["floor"]!.GetValue<string>());
        var remote = candidates.First(c => c["ownerLocation"]!.GetValue<string>() == "remote");
        Assert.False(remote["roomSought"]!.GetValue<bool>());
        Assert.True(remote.ContainsKey("room"));
        Assert.Null(remote["room"]);
    }

    [Fact]
    public void The_table_shows_the_room_and_its_size()
    {
        source.DirectoryRooms = null;
        var (code, output, error) = Run(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms,
            "slots", Ann, "--minutes", "30", "--from", "2026-11-23", "--by", "2026-11-24", "--until", "2026-11-24", "--place", "in-person");
        Assert.True(code == 0, error);
        Assert.Contains("you: office  room: Room 3.A (4)", output, StringComparison.Ordinal);
        Assert.Contains("Room sizes from settings (Place.Read.All not granted).", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Room_only_isnt_for_an_online_meeting()
    {
        var (code, _, error) = Run(Hourly, "slots", Ann, "--minutes", "30", "--place", "online", "--room-only");
        Assert.Equal(2, code);
        Assert.Contains("--room-only is for a meeting in person", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rooms_command_lists_them_in_the_order_slots_tries_them()
    {
        source.DirectoryRooms = null;
        var (code, output, error) = Run(Hourly + "room_floors = [\"3\", \"2\"]\n" + ListedRooms, "rooms");
        Assert.True(code == 0, error);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("3 rooms from the settings (Place.Read.All not granted), in the order slots tries them:", lines[0]);
        Assert.Equal("  Room 3.A <room3.a@example.com>  seats 4  floor 3", lines[1]);
        Assert.StartsWith("  Room 3.B", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("  Room 2.A", lines[3], StringComparison.Ordinal);
    }
}
