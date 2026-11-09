using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumiere.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumiere.Tests;

/// <summary>ADR-018 C-1 (fail fast on calendar config) and m-5 (no input yields HTTP 500).</summary>
public sealed class HardeningHttpTests : IDisposable
{
    private const string Secret = "test-tool-secret";
    private readonly TempStore _db = new();

    public void Dispose() => _db.Dispose();

    private WebApplicationFactory<Program> Factory(string environment, ICalendar? calendar, CapturingLoggerProvider? logs = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment)
                .UseSetting("TOOL_SHARED_SECRET", Secret)
                .UseSetting("SLOT_SIGNING_KEY", "test-signing-key")
                .UseSetting("DATA_DIR", _db.Dir)
                .UseSetting("GOOGLE_CALENDAR_ID", "")
                .UseSetting("GOOGLE_SERVICE_ACCOUNT_KEY_PATH", "")
                .ConfigureTestServices(s =>
                {
                    s.AddSingleton<TimeProvider>(new FakeClock(T.MondayMorning));
                    if (calendar is not null) s.AddSingleton(calendar);
                });
            if (logs is not null)
                b.ConfigureLogging(l => l.AddProvider(logs).AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace));
        });

    private static async Task<(HttpStatusCode Status, JsonElement Json, string Raw)> Post(HttpClient client, string path, JsonObject args, string phoneNumber = "unknown")
    {
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tool-invocation.synthetic.json")))!.AsObject();
        body["phoneNumber"] = phoneNumber;
        body["tool"]!["arguments"] = args;
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Tool-Secret", Secret);
        var res = await client.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(raw).RootElement, raw);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_Development_startup_fails_without_the_calendar_config(string environment)
    {
        using var factory = Factory(environment, calendar: null);
        var ex = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(ex);
        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) messages.Add(e.Message);
        Assert.Contains(messages, m => m.Contains("GOOGLE_CALENDAR_ID and GOOGLE_SERVICE_ACCOUNT_KEY_PATH are required"));
    }

    [Fact]
    public async Task Development_without_the_calendar_config_starts_with_the_in_memory_calendar()
    {
        using var factory = Factory("Development", calendar: null);
        using var client = factory.CreateClient();
        var json = JsonDocument.Parse(await client.GetStringAsync("/health")).RootElement;
        Assert.Equal("in-memory", json.GetProperty("calendar").GetString());
    }

    [Theory]
    [InlineData("12 November 0000")]
    [InlineData("0 November")]
    [InlineData("November 99")]
    [InlineData("12 November 9999")]
    [InlineData("0000-01-01")]
    [InlineData("99999999999999999999 November")]
    public async Task Out_of_range_dates_are_200_not_500(string hint)
    {
        using var factory = Factory("Testing", new InMemoryCalendar());
        using var client = factory.CreateClient();

        var (status, json, _) = await Post(client, "/tools/check-availability",
            new JsonObject { ["service"] = "men's cut", ["date_hint"] = hint });
        var (waitStatus, waitJson, _) = await Post(client, "/tools/join-waitlist",
            new JsonObject { ["service"] = "men's cut", ["preferred_days"] = hint, ["first_name"] = "Anna", ["caller_phone"] = "079 000 00 01" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains(json.GetProperty("error_code").GetString(), new[] { "DATE_UNCLEAR", "BEYOND_HORIZON", "DATE_IN_PAST" });
        Assert.Equal(HttpStatusCode.OK, waitStatus);
        Assert.Equal("INVALID_WINDOW", waitJson.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Unexpected_exception_is_200_INTERNAL_and_logs_the_type_only()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = Factory("Testing", new BrokenEventCalendar(), logs);
        using var client = factory.CreateClient();

        var (status, json, raw) = await Post(client, "/tools/cancel", new JsonObject { ["booking_id"] = "abc" }, "+41790000001");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("INTERNAL", false), (json.GetProperty("error_code").GetString(), json.GetProperty("ok").GetBoolean()));
        Assert.Equal("Sorry, something went wrong on my side. Could you say that again?", json.GetProperty("say").GetString());
        Assert.DoesNotContain("Exception", raw);
        var all = string.Join("\n", logs.Lines);
        Assert.Contains("tool cancel -> INTERNAL (NullReferenceException)", all);
        Assert.DoesNotContain("   at ", all); // no stack trace
        Assert.DoesNotContain("+41790000001", all);
        Assert.DoesNotContain("SYNTHETIC-TRANSCRIPT-MARKER", all);
    }

    /// <summary>Returns a malformed event (no private properties) — an input the service does not expect.</summary>
    private sealed class BrokenEventCalendar : ICalendar
    {
        public Task<CalendarEvent?> GetAsync(string id, CancellationToken ct) => Task.FromResult<CalendarEvent?>(
            new CalendarEvent(id, T.MondayMorning.AddDays(2), T.MondayMorning.AddDays(2).AddMinutes(30), "x", "", null!));
        public Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);
        public Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct) => Task.FromResult(ev.Id!);
        public Task RestoreAsync(CalendarEvent ev, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<CalendarEvent>> ListByPhoneHashAsync(string phoneHash, DateTimeOffset fromUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);
        public Task CancelAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }
}
