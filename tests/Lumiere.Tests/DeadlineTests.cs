using System.Diagnostics;
using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>
/// Per-request deadline (ADR-018 M-2): one budget across all calendar calls of a tool request, including the wait
/// for the booking lock. Each test uses its own lock so holding it never blocks other test classes.
/// </summary>
public class DeadlineTests
{
    private const string Phone = "079 000 00 01"; // synthetic
    private static readonly DateTimeOffset TuesdayNine = Zurich(2026, 11, 10, 9, 0);

    private static (BookingService Service, InMemoryCalendar Inner, SlowCalendar Slow, SlotIds Ids) Setup(TimeSpan? budget, SemaphoreSlim? bookingLock = null)
    {
        var (_, inner, clock, ids) = Booking();
        var slow = new SlowCalendar(inner);
        return (new BookingService(slow, clock, ids, Key, budget, bookingLock ?? new SemaphoreSlim(1, 1)), inner, slow, ids);
    }

    [Fact]
    public async Task Lock_held_beyond_the_budget_gives_CALENDAR_UNAVAILABLE_within_the_budget()
    {
        using var held = new SemaphoreSlim(1, 1);
        await held.WaitAsync(); // another request holds the booking lock for longer than the whole budget
        var (svc, inner, _, ids) = Setup(budget: null, held);

        var watch = Stopwatch.StartNew();
        var book = svc.BookAsync(ids.Create(TuesdayNine, "mens_cut"), "Testy", "Example", Phone, default);
        var cancel = svc.CancelAsync("some-booking-id", Phone, default);
        var (booked, cancelled) = (await book, (await cancel).Result);
        watch.Stop();
        held.Release();

        Assert.Equal(("CALENDAR_UNAVAILABLE", false), (booked.ErrorCode, booked.Ok));
        Assert.Equal("Sorry, I can't reach our calendar right now, so the appointment is not booked. Shall I try again in a moment?", booked.Say);
        Assert.Equal(("CALENDAR_UNAVAILABLE", false), (cancelled.ErrorCode, cancelled.Ok));
        Assert.InRange(watch.Elapsed, BookingService.RequestBudget - TimeSpan.FromSeconds(0.5), BookingService.RequestBudget + TimeSpan.FromSeconds(1));
        Assert.Equal(0, inner.InsertCalls);
    }

    [Fact]
    public async Task Slow_chain_stops_at_the_budget_instead_of_adding_up_the_per_call_caps()
    {
        // get 2.5 s + list 2.5 s + insert (never answers in time) — per-call caps alone would allow 3 + 3 + 3 + 1.5 s.
        var (svc, inner, slow, ids) = Setup(budget: null);
        slow.GetDelay = TimeSpan.FromSeconds(2.5);
        slow.ListDelay = TimeSpan.FromSeconds(2.5);
        inner.InsertDelay = TimeSpan.FromSeconds(10);

        var watch = Stopwatch.StartNew();
        var r = await svc.BookAsync(ids.Create(TuesdayNine, "mens_cut"), "Testy", "Example", Phone, default);
        watch.Stop();

        Assert.Equal("CALENDAR_UNAVAILABLE", r.ErrorCode);
        Assert.Equal(1, inner.InsertCalls); // the insert started with the remaining ~2 s only
        Assert.InRange(watch.Elapsed, BookingService.RequestBudget - TimeSpan.FromSeconds(0.5), BookingService.RequestBudget + TimeSpan.FromSeconds(0.5));
        Assert.Equal(1, slow.GetCalls); // no post-insert lookup once the budget is spent
    }

    [Fact]
    public async Task No_calendar_write_starts_after_the_budget_is_spent()
    {
        var (svc, inner, slow, ids) = Setup(budget: TimeSpan.FromSeconds(1));
        slow.GetDelay = TimeSpan.FromSeconds(0.6);
        slow.ListDelay = TimeSpan.FromSeconds(0.6); // cut to the remaining 0.4 s

        var watch = Stopwatch.StartNew();
        var r = await svc.BookAsync(ids.Create(TuesdayNine, "mens_cut"), "Testy", "Example", Phone, default);
        watch.Stop();

        Assert.Equal("CALENDAR_UNAVAILABLE", r.ErrorCode);
        Assert.Equal(0, inner.InsertCalls);
        Assert.Empty(inner.AllEvents);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Fast_requests_are_unaffected()
    {
        var (svc, _, _, ids) = Setup(budget: null);
        var r = await svc.BookAsync(ids.Create(TuesdayNine, "mens_cut"), "Testy", "Example", Phone, default);
        Assert.True(r.Ok, r.Say);
    }

    /// <summary>Delays get/list (honouring cancellation, like an HTTP call) before delegating.</summary>
    private sealed class SlowCalendar(InMemoryCalendar inner) : ICalendar
    {
        private int _getCalls;
        public TimeSpan GetDelay { get; set; }
        public TimeSpan ListDelay { get; set; }
        public int GetCalls => Volatile.Read(ref _getCalls);

        public async Task<CalendarEvent?> GetAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref _getCalls);
            if (GetDelay > TimeSpan.Zero) await Task.Delay(GetDelay, ct);
            return await inner.GetAsync(id, ct);
        }

        public async Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            if (ListDelay > TimeSpan.Zero) await Task.Delay(ListDelay, ct);
            return await inner.ListAsync(fromUtc, toUtc, ct);
        }

        public Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct) => inner.InsertAsync(ev, ct);
        public Task RestoreAsync(CalendarEvent ev, CancellationToken ct) => inner.RestoreAsync(ev, ct);
        public Task<IReadOnlyList<CalendarEvent>> ListByPhoneHashAsync(string phoneHash, DateTimeOffset fromUtc, CancellationToken ct) =>
            inner.ListByPhoneHashAsync(phoneHash, fromUtc, ct);
        public Task CancelAsync(string id, CancellationToken ct) => inner.CancelAsync(id, ct);
    }
}
