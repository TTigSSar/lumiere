using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;

namespace Lumiere.Api;

/// <summary>A staff message: <see cref="Text"/> is sent to the private staff chat (ADR-007);
/// <see cref="LogSafeText"/> has the phone masked and the name shortened (ADR-010) for log output.</summary>
public sealed record StaffMessage(string Text, string LogSafeText);

/// <summary>Internal salon-staff notifications (ADR-007).</summary>
public interface IStaffNotifier
{
    Task NotifyAsync(StaffMessage message, CancellationToken ct);
}

/// <summary>
/// Telegram Bot API <c>sendMessage</c>. The bot token is part of the request URL, so this class owns a plain
/// HttpClient (no IHttpClientFactory logging handlers), never logs the URL, and wraps failures in an exception
/// that carries only the HTTP status or exception type.
/// </summary>
public sealed class TelegramNotifier : IStaffNotifier, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _http;
    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramNotifier(string botToken, string chatId, HttpMessageHandler? handler = null)
    {
        _botToken = botToken;
        _chatId = chatId;
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true) { Timeout = Timeout };
    }

    public async Task NotifyAsync(StaffMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"https://api.telegram.org/bot{_botToken}/sendMessage",
                new TelegramMessage(_chatId, message.Text), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never rethrow the original: its message or inner exception may contain the request URL (token).
            throw new StaffNotificationException($"Telegram sendMessage failed ({e.GetType().Name})");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new StaffNotificationException($"Telegram sendMessage failed (HTTP {(int)response.StatusCode})");
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record TelegramMessage(
        [property: System.Text.Json.Serialization.JsonPropertyName("chat_id")] string ChatId,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text);
}

public sealed class StaffNotificationException(string message) : Exception(message);

/// <summary>Fallback when Telegram is not configured: writes the log-safe text (phone masked).</summary>
public sealed class LogNotifier(ILogger<LogNotifier> log) : IStaffNotifier
{
    public Task NotifyAsync(StaffMessage message, CancellationToken ct)
    {
        log.LogInformation("STAFF NOTIFICATION (Telegram not configured)\n{Message}", message.LogSafeText);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cancellation Recovery (ADR-007): for a freed slot, finds Active waitlist entries for that local date whose
/// part of day matches the slot start and whose service fits at the freed start given the current calendar
/// (<see cref="Scheduler.Fits"/>, buffers), earliest-joined first; sends staff the best match plus the count of
/// other matches and marks that entry Notified. Runs after the cancel response is decided; failures are only logged.
/// </summary>
public sealed class CancellationRecovery(ICalendar calendar, WaitlistStore store, IStaffNotifier notifier, TimeProvider clock,
    ILogger<CancellationRecovery> log)
{
    public static readonly TimeSpan CalendarBudget = TimeSpan.FromSeconds(10);
    // Serializes recoveries so two freed slots never pick (and notify) the same entry.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    /// <summary>Fire-and-forget: never throws, never delays the caller.</summary>
    public void Trigger(FreedSlot slot)
    {
        var task = Task.Run(() => RunSafeAsync(slot));
        _running.TryAdd(task, 0);
        task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Test hook: completes when every recovery started so far has finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_running.Keys.ToArray());

    private async Task RunSafeAsync(FreedSlot slot)
    {
        try { await RecoverAsync(slot, CancellationToken.None); }
        catch (Exception e)
        {
            // Type and our own messages only — a library exception could carry a URL with the bot token.
            log.LogWarning("cancellation recovery failed: {Error} {Message}", e.GetType().Name,
                e is StaffNotificationException ? e.Message : "");
        }
    }

    public async Task RecoverAsync(FreedSlot slot, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var date = Salon.LocalDate(slot.StartUtc);
            var now = clock.GetUtcNow();
            var localStart = TimeOnly.FromDateTime(Salon.ToLocal(slot.StartUtc).DateTime);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CalendarBudget);
            var busy = await calendar.ListAsync(
                Salon.ToUtc(date, TimeOnly.MinValue) - Salon.Buffer,
                Salon.ToUtc(date.AddDays(1), TimeOnly.MinValue) + Salon.Buffer, cts.Token);

            var matches = store.ActiveForDate(date)
                .Where(e => PartOfDayParser.Matches(e.PartOfDay, localStart))
                .Where(e => Salon.FindService(e.ServiceKey) is { } s && Scheduler.Fits(slot.StartUtc, s, now, busy))
                .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id)
                .ToList();

            var best = matches.FirstOrDefault();
            await notifier.NotifyAsync(Message(slot, best, matches.Count - 1), ct);
            if (best is not null) store.MarkNotified(best.Id, clock.GetUtcNow());
            log.LogInformation("cancellation recovery: slot {Slot}, {Matches} waitlist matches", Salon.SpokenLabel(slot.StartUtc), matches.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static StaffMessage Message(FreedSlot slot, WaitlistEntry? best, int others)
    {
        var start = Salon.ToLocal(slot.StartUtc);
        var end = Salon.ToLocal(slot.StartUtc + slot.Service.Duration);
        var head = $"Slot freed: {start.ToString("dddd, d MMMM HH:mm", CultureInfo.InvariantCulture)}–{end.ToString("HH:mm", CultureInfo.InvariantCulture)} ({slot.Service.SpokenName} cancelled).";
        if (best is null)
        {
            var none = $"{head}\nNo waitlist match.";
            return new StaffMessage(none, none);
        }

        var service = Salon.FindService(best.ServiceKey)?.SpokenName ?? best.ServiceKey;
        var part = best.PartOfDay == PartOfDay.Any ? "any time" : best.PartOfDay.ToString().ToLowerInvariant();
        var since = Salon.ToLocal(best.CreatedUtc).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        string Body(string name, string phone) =>
            $"{head}\nBest waitlist match: {name}, {phone} — {service}, {best.PreferredDaysText}, {part} (waiting since {since}).\n" +
            (others switch { 0 => "No other matches.", 1 => "1 other match.", _ => $"{others} other matches." }) +
            "\nPlease call to offer the slot and book it as usual.";
        return new StaffMessage(Body(best.FirstName, Caller.FormatPhone(best.PhoneE164)),
            Body($"{best.FirstName[0]}.", Caller.MaskPhone(best.PhoneE164)));
    }
}
