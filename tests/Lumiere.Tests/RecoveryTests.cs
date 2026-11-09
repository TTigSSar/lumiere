using System.Net;
using Lumiere.Api;
using Microsoft.Extensions.Logging;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>Records staff messages; optionally fails.</summary>
public sealed class FakeNotifier : IStaffNotifier
{
    public List<StaffMessage> Sent { get; } = [];
    public Exception? Fails { get; set; }
    public Task NotifyAsync(StaffMessage message, CancellationToken ct)
    {
        if (Fails is { } e) throw e;
        lock (Sent) Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>Answers every request with a fixed status (or throws), recording the URL and body.</summary>
public sealed class FakeTelegramHandler(HttpStatusCode status, Exception? throws = null) : HttpMessageHandler
{
    public List<(Uri Url, string Body)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
        if (throws is not null) throw throws;
        return new HttpResponseMessage(status) { Content = new StringContent("{\"ok\":true}") };
    }
}

public class RecoveryTests : IDisposable
{
    public const string BotToken = "123456:SYNTHETIC-BOT-TOKEN-MARKER";
    private readonly TempStore _db = new();
    private readonly InMemoryCalendar _calendar = new();
    private readonly FakeClock _clock = new(MondayMorning);
    private readonly FakeNotifier _notifier = new();
    private readonly CapturingLoggerProvider _logs = new();

    public void Dispose() => _db.Dispose();

    private CancellationRecovery Recovery(IStaffNotifier? notifier = null) =>
        new(_calendar, _db.Store, notifier ?? _notifier, _clock, LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace)).CreateLogger<CancellationRecovery>());

    /// <summary>Adds an Active entry joined at <paramref name="joined"/> (Zurich, 8 Nov + minutes).</summary>
    private string Join(string name, string phone, string service, string days, PartOfDay part, int joinedMinutes)
    {
        var window = WaitlistWindowParser.Parse(days, new DateOnly(2026, 11, 9))!;
        var id = Guid.NewGuid().ToString("N");
        _db.Store.Upsert(new WaitlistEntry(id, service, window.Description, part, window.Dates, name, phone,
            Zurich(2026, 11, 8, 22, 40) + TimeSpan.FromMinutes(joinedMinutes), WaitlistStatus.Active, null));
        return id;
    }

    // Freed slot: Wednesday 11 November 09:00, men's cut (30 min).
    private static readonly FreedSlot Freed = new(Zurich(2026, 11, 11, 9, 0), Svc("mens_cut"));

    [Fact]
    public async Task Best_match_is_earliest_joined_and_is_marked_Notified()
    {
        var anna = Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Morning, 0);
        var ben = Join("Ben", "+41791234568", "mens_cut", "weekdays", PartOfDay.Any, 10);
        var cleo = Join("Cleo", "+41791234569", "blow_dry", "any day", PartOfDay.Any, 20);

        await Recovery().RecoverAsync(Freed, default);

        var msg = Assert.Single(_notifier.Sent);
        Assert.Equal(
            "Slot freed: Wednesday, 11 November 09:00–09:30 (men's cut cancelled).\n" +
            "Best waitlist match: Anna, +41 79 123 45 67 — men's cut, Wednesdays, morning (waiting since Sun 8 Nov 22:40).\n" +
            "2 other matches.\n" +
            "Please call to offer the slot and book it as usual.", msg.Text);
        Assert.Equal(WaitlistStatus.Notified, _db.Store.Get(anna)!.Status);
        Assert.NotNull(_db.Store.Get(anna)!.NotifiedUtc);
        Assert.Equal(WaitlistStatus.Active, _db.Store.Get(ben)!.Status);
        Assert.Equal(WaitlistStatus.Active, _db.Store.Get(cleo)!.Status);
    }

    [Fact]
    public async Task Notified_entry_is_not_notified_twice()
    {
        Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Any, 0);
        Join("Ben", "+41791234568", "mens_cut", "Wednesdays", PartOfDay.Any, 10);
        var recovery = Recovery();

        await recovery.RecoverAsync(Freed, default);
        await recovery.RecoverAsync(Freed with { StartUtc = Zurich(2026, 11, 11, 11, 0) }, default);
        await recovery.RecoverAsync(Freed with { StartUtc = Zurich(2026, 11, 11, 14, 0) }, default);

        Assert.Contains("Best waitlist match: Anna", _notifier.Sent[0].Text);
        Assert.Contains("1 other match.", _notifier.Sent[0].Text);
        Assert.Contains("Best waitlist match: Ben", _notifier.Sent[1].Text);
        Assert.Contains("No other matches.", _notifier.Sent[1].Text);
        Assert.EndsWith("No waitlist match.", _notifier.Sent[2].Text);
    }

    [Fact]
    public async Task Part_of_day_must_match_the_freed_start()
    {
        Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Afternoon, 0);
        Join("Ben", "+41791234568", "mens_cut", "Wednesdays", PartOfDay.Morning, 10);

        await Recovery().RecoverAsync(Freed, default);

        Assert.Contains("Best waitlist match: Ben", Assert.Single(_notifier.Sent).Text);
    }

    [Fact]
    public async Task Date_must_be_in_the_entry_window()
    {
        Join("Anna", "+41791234567", "mens_cut", "Tuesdays or Thursdays", PartOfDay.Any, 0);
        await Recovery().RecoverAsync(Freed, default);
        Assert.Equal(
            "Slot freed: Wednesday, 11 November 09:00–09:30 (men's cut cancelled).\nNo waitlist match.",
            Assert.Single(_notifier.Sent).Text);
    }

    [Fact]
    public async Task Service_must_fit_at_the_freed_start_with_buffers()
    {
        // Next appointment at 10:00: 09:00 + 60 min + 10 min buffer overlaps it; 30 min + buffer fits.
        await _calendar.InsertAsync(Busy(Zurich(2026, 11, 11, 10, 0), Zurich(2026, 11, 11, 11, 0)), default);
        var womens = Join("Anna", "+41791234567", "womens_cut", "Wednesdays", PartOfDay.Any, 0);
        Join("Ben", "+41791234568", "blow_dry", "Wednesdays", PartOfDay.Any, 10);

        await Recovery().RecoverAsync(Freed, default);

        var text = Assert.Single(_notifier.Sent).Text;
        Assert.Contains("Best waitlist match: Ben", text);
        Assert.Contains("No other matches.", text);
        Assert.Equal(WaitlistStatus.Active, _db.Store.Get(womens)!.Status);
    }

    [Fact]
    public async Task Longer_service_fits_when_the_following_time_is_free()
    {
        Join("Anna", "+41791234567", "womens_cut", "Wednesdays", PartOfDay.Any, 0);
        await Recovery().RecoverAsync(Freed, default);
        Assert.Contains("Best waitlist match: Anna", Assert.Single(_notifier.Sent).Text);
    }

    [Fact]
    public async Task Notifier_failure_leaves_the_entry_Active_and_is_logged_without_details()
    {
        var anna = Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Any, 0);
        _notifier.Fails = new HttpRequestException($"https://api.telegram.org/bot{BotToken}/sendMessage failed");
        var recovery = Recovery();

        recovery.Trigger(Freed);
        await recovery.WhenIdleAsync();

        Assert.Equal(WaitlistStatus.Active, _db.Store.Get(anna)!.Status);
        var logs = string.Join("\n", _logs.Lines);
        Assert.Contains("cancellation recovery failed: HttpRequestException", logs);
        Assert.DoesNotContain(BotToken, logs);
    }

    [Fact]
    public async Task Log_safe_text_masks_phone_and_name()
    {
        Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Any, 0);
        await Recovery().RecoverAsync(Freed, default);
        var safe = Assert.Single(_notifier.Sent).LogSafeText;
        Assert.Contains("Best waitlist match: A., +41 79 *** ** 67", safe);
        Assert.DoesNotContain("123 45", safe);
        Assert.DoesNotContain("Anna", safe);
    }

    [Fact]
    public async Task LogNotifier_writes_the_masked_text()
    {
        Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Any, 0);
        var logNotifier = new LogNotifier(LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace)).CreateLogger<LogNotifier>());

        await Recovery(logNotifier).RecoverAsync(Freed, default);

        var logs = string.Join("\n", _logs.Lines);
        Assert.Contains("STAFF NOTIFICATION (Telegram not configured)", logs);
        Assert.Contains("+41 79 *** ** 67", logs);
        Assert.DoesNotContain("+41791234567", logs);
        Assert.DoesNotContain("+41 79 123 45 67", logs);
        Assert.DoesNotContain("Anna", logs);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task Telegram_send_posts_chat_and_text_and_never_logs_the_token(HttpStatusCode status, bool networkError)
    {
        var anna = Join("Anna", "+41791234567", "mens_cut", "Wednesdays", PartOfDay.Any, 0);
        var handler = new FakeTelegramHandler(status,
            networkError ? new HttpRequestException($"Connection refused (api.telegram.org/bot{BotToken}/sendMessage)") : null);
        using var telegram = new TelegramNotifier(BotToken, "-100123", handler);
        var recovery = Recovery(telegram);

        recovery.Trigger(Freed);
        await recovery.WhenIdleAsync();

        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal($"https://api.telegram.org/bot{BotToken}/sendMessage", url.ToString());
        Assert.Contains("\"chat_id\":\"-100123\"", body);
        Assert.Contains("Best waitlist match: Anna, +41 79 123 45 67", System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("text").GetString());
        var ok = status == HttpStatusCode.OK && !networkError;
        Assert.Equal(ok ? WaitlistStatus.Notified : WaitlistStatus.Active, _db.Store.Get(anna)!.Status);
        var logs = string.Join("\n", _logs.Lines);
        Assert.DoesNotContain(BotToken, logs);
        Assert.DoesNotContain("SYNTHETIC-BOT-TOKEN", logs);
        if (!ok) Assert.Contains("cancellation recovery failed", logs);
    }
}
