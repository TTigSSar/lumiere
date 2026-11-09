using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumiere.Api;

/// <summary>
/// A calendar entry. <see cref="Id"/> is null when the calendar should assign one; bookings supply a
/// deterministic id (ADR-015). <see cref="Cancelled"/> can only be true for events returned by GetAsync.
/// </summary>
public sealed record CalendarEvent(
    string? Id,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Summary,
    string Description,
    IReadOnlyDictionary<string, string> PrivateProperties,
    bool Cancelled = false);

/// <summary>Thrown by <see cref="ICalendar.InsertAsync"/> when an event with the supplied id already exists (Google: HTTP 409).</summary>
public sealed class CalendarConflictException(string eventId) : Exception($"Event id already exists: {eventId}");

/// <summary>
/// The appointment calendar (ADR-003). Implementations never delete competing events (ADR-005); the only removal is
/// <see cref="CancelAsync"/> of a booking whose ownership the caller has already verified (ADR-006).
/// </summary>
public interface ICalendar
{
    /// <summary>Busy (non-cancelled) events overlapping [fromUtc, toUtc], recurring events expanded, ordered by start.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Inserts the event (with its supplied id, if any) and returns its id.
    /// Throws <see cref="CalendarConflictException"/> when the id already exists, cancelled or not.</summary>
    Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct);

    /// <summary>The event with this id, including a cancelled one; null when it does not exist.</summary>
    Task<CalendarEvent?> GetAsync(string id, CancellationToken ct);

    /// <summary>Overwrites the existing event <c>ev.Id</c> with <paramref name="ev"/> and marks it confirmed (undelete).</summary>
    Task RestoreAsync(CalendarEvent ev, CancellationToken ct);

    /// <summary>Confirmed (non-cancelled) events whose private property <c>phoneHash</c> equals <paramref name="phoneHash"/>
    /// and that end after <paramref name="fromUtc"/>, ordered by start.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListByPhoneHashAsync(string phoneHash, DateTimeOffset fromUtc, CancellationToken ct);

    /// <summary>Cancels the event with this id (Google keeps it with status "cancelled"). Cancelling an already
    /// cancelled event is a no-op.</summary>
    Task CancelAsync(string id, CancellationToken ct);
}

/// <summary>Deterministic per-booking Google event id (ADR-015).</summary>
public static class EventIds
{
    public const int Length = 32; // 160 bits of the HMAC
    private const string Base32Hex = "0123456789abcdefghijklmnopqrstuv"; // Google: lowercase a–v and digits 0–9

    /// <summary>base32hex(HMAC-SHA256(key, "startUtc|serviceKey|E.164 phone")) truncated to <see cref="Length"/> chars.</summary>
    public static string For(DateTimeOffset startUtc, string serviceKey, string e164Phone, byte[] key)
    {
        var message = $"{startUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}|{serviceKey}|{e164Phone}";
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
        var sb = new StringBuilder(Length);
        int buffer = 0, bits = 0;
        foreach (var b in mac)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && sb.Length < Length)
            {
                sb.Append(Base32Hex[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
            if (sb.Length == Length) break;
        }
        return sb.ToString();
    }
}

/// <summary>Process-local calendar for development and tests (not persistent). Mirrors Google's id semantics.</summary>
public sealed class InMemoryCalendar : ICalendar
{
    private readonly List<CalendarEvent> _events = [];
    private readonly object _gate = new();
    private int _insertCalls;

    // Failure-injection hooks for tests; each applies to every call while set.
    /// <summary>Insert waits this long before committing, ignoring cancellation — like Google creating the event after the client gave up.</summary>
    public TimeSpan InsertDelay { get; set; }
    /// <summary>Insert throws this before committing anything.</summary>
    public Exception? InsertFailsBeforeCommit { get; set; }
    /// <summary>Insert commits the event and then throws this (e.g. TimeoutException: created, but the client never heard back).</summary>
    public Exception? InsertFailsAfterCommit { get; set; }
    /// <summary>GetAsync throws this.</summary>
    public Exception? GetFails { get; set; }
    /// <summary>CancelAsync throws this before cancelling anything.</summary>
    public Exception? CancelFails { get; set; }

    public int InsertCalls => Volatile.Read(ref _insertCalls);

    /// <summary>All events, including cancelled ones.</summary>
    public IReadOnlyList<CalendarEvent> AllEvents { get { lock (_gate) return _events.ToList(); } }

    /// <summary>Non-cancelled events.</summary>
    public IReadOnlyList<CalendarEvent> Events { get { lock (_gate) return _events.Where(e => !e.Cancelled).ToList(); } }

    public Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CalendarEvent>>(
                _events.Where(e => !e.Cancelled && e.StartUtc <= toUtc && e.EndUtc >= fromUtc).OrderBy(e => e.StartUtc).ToList());
    }

    public async Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct)
    {
        Interlocked.Increment(ref _insertCalls);
        if (InsertDelay > TimeSpan.Zero) await Task.Delay(InsertDelay, CancellationToken.None);
        if (InsertFailsBeforeCommit is { } before) throw before;
        var id = ev.Id ?? Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            if (_events.Any(e => e.Id == id)) throw new CalendarConflictException(id);
            _events.Add(ev with { Id = id, Cancelled = false });
        }
        if (InsertFailsAfterCommit is { } after) throw after;
        return id;
    }

    public Task<CalendarEvent?> GetAsync(string id, CancellationToken ct)
    {
        if (GetFails is { } failure) throw failure;
        lock (_gate) return Task.FromResult(_events.FirstOrDefault(e => e.Id == id));
    }

    public Task RestoreAsync(CalendarEvent ev, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _events.FindIndex(e => e.Id == ev.Id);
            if (i < 0) throw new InvalidOperationException($"No event {ev.Id} to restore");
            _events[i] = ev with { Cancelled = false };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CalendarEvent>> ListByPhoneHashAsync(string phoneHash, DateTimeOffset fromUtc, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CalendarEvent>>(_events
                .Where(e => !e.Cancelled && e.EndUtc > fromUtc
                    && e.PrivateProperties.TryGetValue("phoneHash", out var h) && h == phoneHash)
                .OrderBy(e => e.StartUtc).ToList());
    }

    public Task CancelAsync(string id, CancellationToken ct)
    {
        if (CancelFails is { } failure) throw failure;
        lock (_gate)
        {
            var i = _events.FindIndex(e => e.Id == id);
            if (i < 0) throw new InvalidOperationException($"No event {id} to cancel");
            _events[i] = _events[i] with { Cancelled = true };
        }
        return Task.CompletedTask;
    }

    /// <summary>Test helper: marks an event cancelled, as Google does when staff delete it.</summary>
    public void Cancel(string id)
    {
        lock (_gate)
        {
            var i = _events.FindIndex(e => e.Id == id);
            if (i >= 0) _events[i] = _events[i] with { Cancelled = true };
        }
    }
}
