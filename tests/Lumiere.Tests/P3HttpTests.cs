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
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>my-appointments, cancel (+ Cancellation Recovery) and join-waitlist over HTTP, with fakes only.</summary>
public sealed class P3HttpTests : IDisposable
{
    private const string Secret = "test-tool-secret";
    private const string Mine = "+41790000001";   // synthetic
    private const string Theirs = "+41790000002"; // synthetic
    private readonly CapturingLoggerProvider _logs = new();
    private readonly InMemoryCalendar _calendar = new();
    private readonly TempStore _db = new();
    private readonly FakeTelegramHandler _telegram = new(HttpStatusCode.OK);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public P3HttpTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("TOOL_SHARED_SECRET", Secret)
            .UseSetting("SLOT_SIGNING_KEY", "test-signing-key")
            .UseSetting("DATA_DIR", _db.Dir)
            .UseSetting("TELEGRAM_BOT_TOKEN", RecoveryTests.BotToken)
            .UseSetting("TELEGRAM_STAFF_CHAT_ID", "-100123")
            .ConfigureLogging(l => l.AddProvider(_logs).AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace))
            .ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(new FakeClock(MondayMorning));
                s.AddSingleton<ICalendar>(_calendar);
                // The real TelegramNotifier, with the network replaced by a fake handler.
                s.AddSingleton<IStaffNotifier>(sp => new TelegramNotifier(
                    sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["TELEGRAM_BOT_TOKEN"]!, "-100123", _telegram));
            }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _db.Dispose();
    }

    private CancellationRecovery Recovery => _factory.Services.GetRequiredService<CancellationRecovery>();
    private WaitlistStore Store => _factory.Services.GetRequiredService<WaitlistStore>();

    private async Task<JsonElement> Call(string path, JsonObject args, string? metadataPhone)
    {
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tool-invocation.synthetic.json")))!.AsObject();
        body["phoneNumber"] = metadataPhone ?? "unknown";
        body["tool"]!["arguments"] = args;
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Tool-Secret", Secret);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<string> Book(DateTimeOffset start, string phone)
    {
        var ids = _factory.Services.GetRequiredService<SlotIds>();
        var r = await Call("/tools/book", new JsonObject { ["slot_id"] = ids.Create(start, "mens_cut"), ["first_name"] = "Testy", ["last_name"] = "Example" }, phone);
        Assert.True(r.GetProperty("ok").GetBoolean());
        return r.GetProperty("booking_id").GetString()!;
    }

    private async Task JoinWaitlist(string phone, string name = "Anna") =>
        Assert.True((await Call("/tools/join-waitlist", new JsonObject
        {
            ["service"] = "men's cut", ["preferred_days"] = "Wednesday", ["part_of_day"] = "morning", ["first_name"] = name,
        }, phone)).GetProperty("ok").GetBoolean());

    [Theory]
    [InlineData("/tools/my-appointments")]
    [InlineData("/tools/cancel")]
    [InlineData("/tools/join-waitlist")]
    public async Task New_endpoints_require_the_secret(string path)
    {
        var res = await _client.PostAsync(path, new StringContent("{\"tool\":{\"arguments\":{}}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task My_appointments_returns_only_the_callers_bookings()
    {
        var mine = await Book(Zurich(2026, 11, 10, 12, 0), Mine);
        await Book(Zurich(2026, 11, 10, 14, 0), Theirs);

        var r = await Call("/tools/my-appointments", new JsonObject(), Mine);

        var a = Assert.Single(r.GetProperty("appointments").EnumerateArray());
        Assert.Equal(mine, a.GetProperty("booking_id").GetString());
        Assert.Equal("men's cut on Tuesday, 10 November at 12:00", a.GetProperty("spoken_label").GetString());

        var none = await Call("/tools/my-appointments", new JsonObject(), "+41790000009");
        Assert.True(none.GetProperty("ok").GetBoolean());
        Assert.Equal(0, none.GetProperty("appointments").GetArrayLength());
        Assert.Equal("I can't find any upcoming appointments for this number.", none.GetProperty("say").GetString());

        // No call-metadata number at all: not PHONE_REQUIRED — a spoken number would not be accepted either (ADR-018 M-1).
        var noPhone = await Call("/tools/my-appointments", new JsonObject(), null);
        Assert.Equal("IDENTITY_REQUIRED", noPhone.GetProperty("error_code").GetString());
    }

    private const string IdentityRequiredSay =
        "For your security I can only look up or cancel appointments when you call from the phone number used for the booking.";

    [Theory]
    [InlineData("unknown")]
    [InlineData("Browser Test")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Spoken_victim_number_without_call_metadata_cannot_list_or_cancel(string? metadataPhone)
    {
        var victimBooking = await Book(Zurich(2026, 11, 11, 9, 0), Theirs);
        await JoinWaitlist(Mine);

        var post = (string path, JsonObject args) =>
        {
            var b = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tool-invocation.synthetic.json")))!.AsObject();
            if (metadataPhone is null) b.Remove("phoneNumber"); else b["phoneNumber"] = metadataPhone;
            b["tool"]!["arguments"] = args;
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(b.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Tool-Secret", Secret);
            return _client.SendAsync(req);
        };

        var listRes = await post("/tools/my-appointments", new JsonObject { ["caller_phone"] = "079 000 00 02" });
        var cancelRes = await post("/tools/cancel", new JsonObject { ["booking_id"] = victimBooking, ["caller_phone"] = "079 000 00 02" });
        await Recovery.WhenIdleAsync();

        foreach (var res in new[] { listRes, cancelRes })
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var raw = await res.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(raw).RootElement;
            Assert.Equal(("IDENTITY_REQUIRED", false), (json.GetProperty("error_code").GetString(), json.GetProperty("ok").GetBoolean()));
            Assert.Equal(IdentityRequiredSay, json.GetProperty("say").GetString());
            Assert.False(json.TryGetProperty("appointments", out _));
            Assert.False(json.TryGetProperty("cancelled", out _));
            Assert.DoesNotContain(victimBooking, raw);
            Assert.DoesNotContain("November", raw);
        }
        Assert.False(_calendar.AllEvents.Single(e => e.Id == victimBooking).Cancelled);
        Assert.Empty(_telegram.Requests);
        Assert.Contains("tool cancel -> IDENTITY_REQUIRED", string.Join("\n", _logs.Lines));
    }

    [Fact]
    public async Task Spoken_number_still_books_and_joins_the_waitlist()
    {
        var ids = _factory.Services.GetRequiredService<SlotIds>();
        var book = await Call("/tools/book", new JsonObject
        {
            ["slot_id"] = ids.Create(Zurich(2026, 11, 10, 12, 0), "mens_cut"), ["first_name"] = "Testy", ["last_name"] = "Example",
            ["caller_phone"] = "079 000 00 02",
        }, "unknown");
        var join = await Call("/tools/join-waitlist", new JsonObject
        {
            ["service"] = "men's cut", ["preferred_days"] = "Wednesday", ["first_name"] = "Anna", ["caller_phone"] = "079 000 00 02",
        }, "Browser Test");

        Assert.True(book.GetProperty("ok").GetBoolean());
        Assert.True(join.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Health_reports_the_telegram_notifier()
    {
        var json = JsonDocument.Parse(await _client.GetStringAsync("/health")).RootElement;
        Assert.Equal("telegram", json.GetProperty("notifier").GetString());
        Assert.Equal("in-memory", json.GetProperty("calendar").GetString());
    }

    [Fact]
    public async Task Cancel_own_booking_triggers_one_recovery_notification_and_never_logs_the_token()
    {
        var id = await Book(Zurich(2026, 11, 11, 9, 0), Mine);
        await JoinWaitlist(Theirs);

        var first = await Call("/tools/cancel", new JsonObject { ["booking_id"] = id }, Mine);
        await Recovery.WhenIdleAsync();
        var second = await Call("/tools/cancel", new JsonObject { ["booking_id"] = id }, Mine);
        await Recovery.WhenIdleAsync();

        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.True(first.GetProperty("cancelled").GetBoolean());
        Assert.Equal("Your men's cut on Wednesday, 11 November at 09:00 has been cancelled.", first.GetProperty("say").GetString());
        Assert.Equal(first.ToString(), second.ToString());
        Assert.True(_calendar.AllEvents.Single(e => e.Id == id).Cancelled);

        var (_, body) = Assert.Single(_telegram.Requests);
        var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString();
        Assert.StartsWith("Slot freed: Wednesday, 11 November 09:00–09:30 (men's cut cancelled).\nBest waitlist match: Anna, +41 79 000 00 02", text);
        Assert.Equal(WaitlistStatus.Notified, Assert.Single(Store.All()).Status);

        var logs = string.Join("\n", _logs.Lines);
        Assert.Contains("tool cancel -> OK", logs);
        Assert.DoesNotContain(RecoveryTests.BotToken, logs);
        Assert.DoesNotContain("SYNTHETIC-BOT-TOKEN", logs);
        Assert.DoesNotContain("api.telegram.org", logs);
        Assert.DoesNotContain(Theirs, logs);
        Assert.DoesNotContain("Anna", logs);
    }

    [Fact]
    public async Task Cancel_foreign_booking_is_NOT_FOUND_and_triggers_nothing()
    {
        var id = await Book(Zurich(2026, 11, 11, 9, 0), Theirs);
        await JoinWaitlist(Mine);

        var r = await Call("/tools/cancel", new JsonObject { ["booking_id"] = id }, Mine);
        await Recovery.WhenIdleAsync();

        Assert.Equal(("NOT_FOUND", false), (r.GetProperty("error_code").GetString(), r.GetProperty("ok").GetBoolean()));
        Assert.False(_calendar.AllEvents.Single(e => e.Id == id).Cancelled);
        Assert.Empty(_telegram.Requests);
    }

    [Fact]
    public async Task Notifier_failure_does_not_change_the_cancel_response()
    {
        var id = await Book(Zurich(2026, 11, 11, 9, 0), Mine);
        await JoinWaitlist(Theirs);
        var failing = new FakeTelegramHandler(HttpStatusCode.InternalServerError);
        await using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IStaffNotifier>(new TelegramNotifier(RecoveryTests.BotToken, "-100123", failing))));
        using var client = factory.CreateClient();
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "tool-invocation.synthetic.json")))!.AsObject();
        body["phoneNumber"] = Mine;
        body["tool"]!["arguments"] = new JsonObject { ["booking_id"] = id };
        var req = new HttpRequestMessage(HttpMethod.Post, "/tools/cancel") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Tool-Secret", Secret);

        var res = await client.SendAsync(req);
        await factory.Services.GetRequiredService<CancellationRecovery>().WhenIdleAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Single(failing.Requests);
        var logs = string.Join("\n", _logs.Lines);
        Assert.Contains("cancellation recovery failed: StaffNotificationException Telegram sendMessage failed (HTTP 500)", logs);
        Assert.DoesNotContain(RecoveryTests.BotToken, logs);
        Assert.Equal(WaitlistStatus.Active, Assert.Single(factory.Services.GetRequiredService<WaitlistStore>().All()).Status);
    }

    [Fact]
    public async Task Join_waitlist_parses_days_and_updates_the_same_entry()
    {
        var first = await Call("/tools/join-waitlist", new JsonObject
        {
            ["service"] = "men's cut", ["preferred_days"] = "Tuesday or Thursday", ["part_of_day"] = "morning", ["first_name"] = "Anna",
        }, Mine);
        var second = await Call("/tools/join-waitlist", new JsonObject
        {
            ["service"] = "men's cut", ["preferred_days"] = "next week", ["first_name"] = "Anna",
        }, Mine);
        var invalid = await Call("/tools/join-waitlist", new JsonObject
        {
            ["service"] = "men's cut", ["preferred_days"] = "Mondays", ["first_name"] = "Anna",
        }, Mine);

        Assert.Equal("You're on the waiting list for a men's cut on Tuesdays or Thursdays in the morning. The salon team will call you if a slot opens up.",
            first.GetProperty("say").GetString());
        Assert.Equal(first.GetProperty("waitlist_id").GetString(), second.GetProperty("waitlist_id").GetString());
        Assert.Equal("You're on the waiting list for a men's cut next week. The salon team will call you if a slot opens up.",
            second.GetProperty("say").GetString());
        Assert.Equal(("INVALID_WINDOW", false), (invalid.GetProperty("error_code").GetString(), invalid.GetProperty("ok").GetBoolean()));
        Assert.Equal("next week", Assert.Single(Store.All()).PreferredDaysText);
        Assert.Contains("tool join-waitlist -> INVALID_WINDOW", string.Join("\n", _logs.Lines));
    }
}
