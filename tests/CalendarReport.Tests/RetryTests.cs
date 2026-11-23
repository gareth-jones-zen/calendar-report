using System.Net;
using System.Text;
using M365Graph;

namespace CalendarReport.Tests;

/// <summary>Graph stood in for, under the real GraphSource: each request gets the next queued answer, and is
/// recorded.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> answers = new();
    public List<string> Seen { get; } = [];

    public void Reply(HttpStatusCode status, string body = "{}") =>
        answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        return Task.FromResult(answers.Dequeue()());
    }

    public GraphSource Source(Config config) => new(new GraphClient(new GraphClientOptions { Log = _ => { } }, this,
        token: (_, _) => Task.FromResult("tok"), delay: (_, _) => Task.CompletedTask), config);
}

/// <summary>calendar-report only reads. Its getSchedule calls are POSTs, which background-tools-lib sends once by
/// default; they only read, so they opt in to retries, as GETs have them.</summary>
public sealed class RetryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "calendar-report-retry-" + Guid.NewGuid().ToString("N"));
    private const string Ann = "ann@example.com";
    private static readonly DateTime From = new(2026, 11, 26, 0, 0, 0);

    public RetryTests() => Directory.CreateDirectory(dir);

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

    private Config Settings()
    {
        var path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "time_zone = \"GMT Standard Time\"\n");
        return Config.Load(path, _ => null);
    }

    [Fact]
    public void Free_busy_retries_a_5xx()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.ServiceUnavailable);
        http.Reply(HttpStatusCode.OK, $"{{\"value\": [{{\"scheduleId\": \"{Ann}\", \"scheduleItems\": []}}]}}");
        Assert.Empty(http.Source(Settings()).FreeBusy(new Person(Ann, "Ann"), From, From.AddDays(1)));
        Assert.Equal(["POST /v1.0/me/calendar/getSchedule", "POST /v1.0/me/calendar/getSchedule"], http.Seen);
    }

    [Fact]
    public void Room_bookings_retry_a_5xx()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.BadGateway);
        http.Reply(HttpStatusCode.OK, "{\"value\": [{\"scheduleId\": \"room@example.com\", \"scheduleItems\": []}]}");
        var found = http.Source(Settings()).RoomBookings(["room@example.com"], From, From.AddDays(1));
        Assert.Empty(found["room@example.com"]!);
        Assert.Equal(2, http.Seen.Count);
    }

    [Fact]
    public void A_read_retries_a_5xx()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.GatewayTimeout);
        http.Reply(HttpStatusCode.OK, "{\"value\": []}");
        Assert.Empty(http.Source(Settings()).Rooms()!);
        Assert.Equal(["GET /v1.0/places/microsoft.graph.room", "GET /v1.0/places/microsoft.graph.room"], http.Seen);
    }
}
