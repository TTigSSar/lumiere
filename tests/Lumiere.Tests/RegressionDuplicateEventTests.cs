using System.Collections.Concurrent;
using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>
/// Regression for the confirmed duplicate-booking bug (ADR-015, fixed in 778ddce): a cold Google
/// events.insert took longer than the 3 s budget, the caller heard CALENDAR_UNAVAILABLE although the
/// event was created, and the retry created a second event for the same slot.
/// These tests use the real <see cref="BookingService.CalendarTimeout"/> (3 s) and
/// <see cref="BookingService.LookupTimeout"/> (1.5 s); the service does not take its timeouts from the
/// injected clock, so each slow-insert test costs a few seconds of wall time.
/// </summary>
public class RegressionDuplicateEventTests
{
    private const string Phone = "079 000 00 01"; // synthetic
    private const string E164 = "+41790000001";
    private static readonly DateTimeOffset TuesdayNine = Zurich(2026, 11, 10, 9, 0);
    private static string OwnId => EventIds.For(TuesdayNine, "mens_cut", E164, Key);

    /// <summary>Longer than the whole insert + lookup budget: the first call can never see the event.</summary>
    private static readonly TimeSpan BeyondBudget = BookingService.CalendarTimeout + BookingService.LookupTimeout + TimeSpan.FromSeconds(0.5);

    /// <summary>Upper bound for waiting on an abandoned insert; only reached if something hangs.</summary>
    private static readonly TimeSpan SettleLimit = BeyondBudget + TimeSpan.FromSeconds(5);

    private static async Task<string> TuesdayNineSlot(BookingService svc)
    {
        var r = await svc.CheckAvailabilityAsync("men's cut", "tuesday", "morning", default);
        Assert.True(r.Ok, r.Say);
        Assert.Equal("Tuesday, 10 November at 09:00", r.Slots![0].SpokenLabel);
        return r.Slots[0].SlotId;
    }

    private static (BookingService Service, ObservedCalendar Calendar, InMemoryCalendar Inner) Setup()
    {
        var (_, inner, clock, ids) = Booking();
        var cal = new ObservedCalendar(inner);
        return (new BookingService(cal, clock, ids, Key), cal, inner);
    }

    [Fact]
    public async Task Slow_insert_beyond_the_lookup_then_retry_books_exactly_one_event()
    {
        var (svc, cal, inner) = Setup();
        var slot = await TuesdayNineSlot(svc);
        inner.InsertDelay = BeyondBudget;

        var first = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.Equal(("CALENDAR_UNAVAILABLE", false), (first.ErrorCode, first.Ok));
        Assert.Null(first.BookingId);

        await cal.InsertsSettled(SettleLimit); // Google created the event after the caller gave up
        var created = Assert.Single(inner.AllEvents);

        var retry = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(retry.Ok, retry.Say);
        Assert.Equal(OwnId, retry.BookingId);
        Assert.Equal(created.Id, retry.BookingId);
        await cal.InsertsSettled(SettleLimit);
        Assert.Single(inner.AllEvents);
        Assert.Equal(1, inner.InsertCalls); // the retry recognised the event instead of inserting again
    }

    [Fact]
    public async Task Retry_before_the_slow_insert_commits_still_leaves_exactly_one_event()
    {
        // The literal bug: the caller retries while Google is still creating the first event.
        var (svc, cal, inner) = Setup();
        var slot = await TuesdayNineSlot(svc);
        inner.InsertDelay = BeyondBudget;

        var first = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.Equal("CALENDAR_UNAVAILABLE", first.ErrorCode);
        Assert.Empty(inner.AllEvents); // the first insert has not landed yet

        inner.InsertDelay = TimeSpan.Zero; // the retry's insert is fast
        var retry = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(retry.Ok, retry.Say);
        Assert.Equal(OwnId, retry.BookingId);

        await cal.InsertsSettled(SettleLimit); // now the first, late insert lands (or is rejected)
        var ev = Assert.Single(inner.AllEvents);
        Assert.Equal(OwnId, ev.Id);
        Assert.Equal(2, inner.InsertCalls);
    }

    [Fact]
    public async Task Slow_insert_that_lands_during_the_lookup_is_booked_on_the_first_call()
    {
        var (svc, cal, inner) = Setup();
        var slot = await TuesdayNineSlot(svc);
        // Insert commits 0.5 s after the 3 s budget; the lookup starts then and answers 1 s later
        // (inside its 1.5 s budget), so it sees the event. 0.5 s margin on both sides.
        inner.InsertDelay = BookingService.CalendarTimeout + TimeSpan.FromSeconds(0.5);
        cal.GetDelayAfterInsert = TimeSpan.FromSeconds(1);

        var first = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(first.Ok, first.Say);
        Assert.Equal(OwnId, first.BookingId);

        await cal.InsertsSettled(SettleLimit);
        var ev = Assert.Single(inner.AllEvents);
        Assert.Equal(OwnId, ev.Id);
        Assert.Equal(1, inner.InsertCalls);
    }

    [Fact]
    public async Task Insert_that_fails_after_commit_is_booked_and_retries_return_the_same_booking()
    {
        var (svc, cal, inner) = Setup();
        var slot = await TuesdayNineSlot(svc);
        inner.InsertFailsAfterCommit = new HttpRequestException("connection reset after the event was created");

        var first = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(first.Ok, first.Say);
        Assert.Equal(OwnId, first.BookingId);

        var sameProcess = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        var (_, _, clock, ids) = Booking();
        var restarted = await new BookingService(cal, clock, ids, Key).BookAsync(slot, "Testy", "Example", Phone, default);

        Assert.True(sameProcess.Ok, sameProcess.Say);
        Assert.True(restarted.Ok, restarted.Say);
        Assert.Equal(first.BookingId, sameProcess.BookingId);
        Assert.Equal(first.BookingId, restarted.BookingId);
        await cal.InsertsSettled(SettleLimit);
        var ev = Assert.Single(inner.AllEvents);
        Assert.Equal(OwnId, ev.Id);
        Assert.Equal(1, inner.InsertCalls);
    }

    /// <summary>
    /// Delegates to <see cref="InMemoryCalendar"/> and keeps every insert task, so a test can await the
    /// insert the service abandoned on timeout instead of sleeping. Optionally slows GetAsync once an
    /// insert has started (a lookup still in flight while Google commits).
    /// </summary>
    private sealed class ObservedCalendar(InMemoryCalendar inner) : ICalendar
    {
        private readonly ConcurrentQueue<Task> _inserts = new();
        private int _insertStarted;

        public TimeSpan GetDelayAfterInsert { get; set; }

        public Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            inner.ListAsync(fromUtc, toUtc, ct);

        public Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct)
        {
            Volatile.Write(ref _insertStarted, 1);
            var task = inner.InsertAsync(ev, ct);
            _inserts.Enqueue(task);
            return task;
        }

        public async Task<CalendarEvent?> GetAsync(string id, CancellationToken ct)
        {
            if (GetDelayAfterInsert > TimeSpan.Zero && Volatile.Read(ref _insertStarted) == 1)
                await Task.Delay(GetDelayAfterInsert, ct);
            return await inner.GetAsync(id, ct);
        }

        public Task RestoreAsync(CalendarEvent ev, CancellationToken ct) => inner.RestoreAsync(ev, ct);
        public Task<IReadOnlyList<CalendarEvent>> ListByPhoneHashAsync(string phoneHash, DateTimeOffset fromUtc, CancellationToken ct) =>
            inner.ListByPhoneHashAsync(phoneHash, fromUtc, ct);
        public Task CancelAsync(string id, CancellationToken ct) => inner.CancelAsync(id, ct);

        /// <summary>Waits until every insert started so far has finished (committed, conflicted or failed).</summary>
        public async Task InsertsSettled(TimeSpan limit)
        {
            var all = Task.WhenAll(_inserts.ToArray());
            try { await all.WaitAsync(limit); }
            catch (Exception) when (all.IsCompleted) { } // a failed/conflicting insert has still settled
        }
    }
}
