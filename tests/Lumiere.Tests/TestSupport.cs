using System.Collections.Concurrent;
using System.Text;
using Lumiere.Api;
using Microsoft.Extensions.Logging;

namespace Lumiere.Tests;

/// <summary>Settable clock (Microsoft.Extensions.TimeProvider.Testing is intentionally not used).</summary>
public sealed class FakeClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public void Advance(TimeSpan by) => UtcNow += by;
}

public static class T
{
    public static readonly byte[] Key = Encoding.UTF8.GetBytes("test-signing-key");

    /// <summary>Zurich wall time → UTC instant.</summary>
    public static DateTimeOffset Zurich(int y, int m, int d, int h, int min) => Salon.ToUtc(new DateOnly(y, m, d), new TimeOnly(h, min));

    /// <summary>Monday 9 November 2026, 08:00 Zurich (salon closed today).</summary>
    public static DateTimeOffset MondayMorning => Zurich(2026, 11, 9, 8, 0);

    public static Service Svc(string key) => Salon.FindService(key)!;

    public static CalendarEvent Busy(DateTimeOffset start, DateTimeOffset end) =>
        new(null, start, end, "Existing", "", new Dictionary<string, string>());

    public static (BookingService Service, InMemoryCalendar Calendar, FakeClock Clock, SlotIds SlotIds) Booking(DateTimeOffset? now = null)
    {
        var clock = new FakeClock(now ?? MondayMorning);
        var calendar = new InMemoryCalendar();
        var ids = new SlotIds(Key, clock);
        return (new BookingService(calendar, clock, ids, Key), calendar, clock, ids);
    }
}

public sealed class ThrowingCalendar : ICalendar
{
    public Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CalendarEvent>>([]);
    public Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct) => throw new HttpRequestException("calendar down");
    public Task<CalendarEvent?> GetAsync(string id, CancellationToken ct) => Task.FromResult<CalendarEvent?>(null);
    public Task RestoreAsync(CalendarEvent ev, CancellationToken ct) => throw new HttpRequestException("calendar down");
}

/// <summary>Captures every log line (message, state values, exception) at all levels.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(";", kv.Select(p => $"{p.Key}={p.Value}")) : "";
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} [{values}] {exception}");
        }
    }
}
