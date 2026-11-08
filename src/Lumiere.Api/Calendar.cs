namespace Lumiere.Api;

/// <summary>A calendar entry. <see cref="Id"/> is null before insertion.</summary>
public sealed record CalendarEvent(
    string? Id,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Summary,
    string Description,
    IReadOnlyDictionary<string, string> PrivateProperties);

/// <summary>The appointment calendar (ADR-003). Implementations never delete events.</summary>
public interface ICalendar
{
    /// <summary>Busy events overlapping [fromUtc, toUtc], recurring events expanded, ordered by start.</summary>
    Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Inserts the event and returns its id.</summary>
    Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct);
}

/// <summary>Process-local calendar for development and tests (not persistent).</summary>
public sealed class InMemoryCalendar : ICalendar
{
    private readonly List<CalendarEvent> _events = [];
    private readonly object _gate = new();

    public IReadOnlyList<CalendarEvent> Events { get { lock (_gate) return _events.ToList(); } }

    public Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CalendarEvent>>(
                _events.Where(e => e.StartUtc <= toUtc && e.EndUtc >= fromUtc).OrderBy(e => e.StartUtc).ToList());
    }

    public Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate) _events.Add(ev with { Id = id });
        return Task.FromResult(id);
    }
}
