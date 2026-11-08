using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>ADR-015: deterministic event ids make a repeated booking resolve to the same calendar event.</summary>
public class BookingIdempotencyTests
{
    private const string Phone = "079 000 00 01"; // synthetic
    private const string E164 = "+41790000001";
    private static readonly DateTimeOffset TuesdayNine = Zurich(2026, 11, 10, 9, 0);
    private static string OwnId => EventIds.For(TuesdayNine, "mens_cut", E164, Key);

    private static async Task<string> TuesdayNineSlot(BookingService svc)
    {
        var r = await svc.CheckAvailabilityAsync("men's cut", "tuesday", "morning", default);
        Assert.True(r.Ok, r.Say);
        Assert.Equal("Tuesday, 10 November at 09:00", r.Slots![0].SpokenLabel);
        return r.Slots[0].SlotId;
    }

    /// <summary>A fresh service on the same calendar, as after a restart.</summary>
    private static BookingService Restarted(ICalendar cal, FakeClock clock, SlotIds ids) => new(cal, clock, ids, Key);

    [Fact]
    public void Event_id_is_deterministic_and_google_valid()
    {
        var id = EventIds.For(TuesdayNine, "mens_cut", E164, Key);
        Assert.Equal(id, EventIds.For(TuesdayNine, "mens_cut", E164, Key));
        Assert.Matches("^[0-9a-v]{32}$", id); // base32hex, within Google's 5–1024 chars
    }

    [Fact]
    public void Event_id_differs_per_phone_start_service_and_key()
    {
        var id = EventIds.For(TuesdayNine, "mens_cut", E164, Key);
        Assert.NotEqual(id, EventIds.For(TuesdayNine, "mens_cut", "+41790000002", Key));
        Assert.NotEqual(id, EventIds.For(TuesdayNine.AddMinutes(15), "mens_cut", E164, Key));
        Assert.NotEqual(id, EventIds.For(TuesdayNine, "womens_cut", E164, Key));
        Assert.NotEqual(id, EventIds.For(TuesdayNine, "mens_cut", E164, "other-key"u8.ToArray()));
    }

    [Fact]
    public async Task Repeat_after_success_returns_same_booking_id_and_one_event_even_after_restart()
    {
        var (svc, cal, clock, ids) = Booking();
        var slot = await TuesdayNineSlot(svc);
        var first = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        var again = await Restarted(cal, clock, ids).BookAsync(slot, "Testy", "Example", Phone, default);

        Assert.True(first.Ok, first.Say);
        Assert.True(again.Ok, again.Say);
        Assert.Equal(OwnId, first.BookingId);
        Assert.Equal(first.BookingId, again.BookingId);
        var ev = Assert.Single(cal.AllEvents);
        Assert.Equal(OwnId, ev.Id);
        Assert.Equal(OwnId, ev.PrivateProperties["bookingId"]);
        Assert.Equal(1, cal.InsertCalls);
    }

    [Fact]
    public async Task Repeat_succeeds_even_if_the_slot_is_now_blocked_by_someone_else()
    {
        var (svc, cal, clock, ids) = Booking();
        var slot = await TuesdayNineSlot(svc);
        await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        await cal.InsertAsync(Busy(TuesdayNine.AddMinutes(30), TuesdayNine.AddMinutes(60)), default);

        var again = await Restarted(cal, clock, ids).BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(again.Ok, again.Say);
        Assert.Equal(OwnId, again.BookingId);
    }

    [Fact]
    public async Task Insert_conflict_is_success_with_the_existing_booking()
    {
        var (svc0, inner, clock, ids) = Booking();
        var slot = await TuesdayNineSlot(svc0);
        // The event already exists, but the first lookup misses it (e.g. created by a still-running earlier insert).
        await inner.InsertAsync(Busy(TuesdayNine, TuesdayNine.AddMinutes(30)) with { Id = OwnId }, default);
        var cal = new FirstGetMissesCalendar(inner);

        var r = await Restarted(cal, clock, ids).BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(r.Ok, r.Say);
        Assert.Equal(OwnId, r.BookingId);
        Assert.Single(inner.AllEvents);
        Assert.Equal(2, inner.InsertCalls); // seed + the conflicting insert
    }

    [Fact]
    public async Task Insert_that_times_out_but_created_the_event_is_success()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await TuesdayNineSlot(svc);
        cal.InsertFailsAfterCommit = new TimeoutException("created, but the response never arrived");

        var r = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(r.Ok, r.Say);
        Assert.Equal(OwnId, r.BookingId);
        Assert.Single(cal.AllEvents);
    }

    [Fact]
    public async Task Insert_that_times_out_without_an_event_is_CALENDAR_UNAVAILABLE_and_retry_books_once()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await TuesdayNineSlot(svc);
        cal.InsertFailsBeforeCommit = new TimeoutException("nothing created");

        var r = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.Equal(("CALENDAR_UNAVAILABLE", false), (r.ErrorCode, r.Ok));
        Assert.Null(r.BookingId);
        Assert.Contains("not booked", r.Say);
        Assert.Empty(cal.AllEvents);

        cal.InsertFailsBeforeCommit = null; // failures are not cached: the retry goes through
        var retry = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(retry.Ok, retry.Say);
        Assert.Single(cal.AllEvents);
    }

    [Fact]
    public async Task Failing_lookup_is_CALENDAR_UNAVAILABLE_and_inserts_nothing()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await TuesdayNineSlot(svc);
        cal.GetFails = new HttpRequestException("calendar down");

        var r = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.Equal("CALENDAR_UNAVAILABLE", r.ErrorCode);
        Assert.Equal(0, cal.InsertCalls);
    }

    [Fact]
    public async Task Cancelled_event_with_the_same_id_is_restored_not_recreated()
    {
        var (svc, cal, clock, ids) = Booking();
        var slot = await TuesdayNineSlot(svc);
        await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        cal.Cancel(OwnId); // staff deleted it in Google Calendar

        var r = await Restarted(cal, clock, ids).BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(r.Ok, r.Say);
        Assert.Equal(OwnId, r.BookingId);
        var ev = Assert.Single(cal.AllEvents);
        Assert.False(ev.Cancelled);
        Assert.Equal(TuesdayNine, ev.StartUtc);
        Assert.Equal(1, cal.InsertCalls);
    }

    [Fact]
    public async Task Cancelled_event_is_not_restored_when_the_slot_was_taken_meanwhile()
    {
        var (svc, cal, clock, ids) = Booking();
        var slot = await TuesdayNineSlot(svc);
        await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        cal.Cancel(OwnId);
        await cal.InsertAsync(Busy(TuesdayNine, TuesdayNine.AddMinutes(60)), default);

        var r = await Restarted(cal, clock, ids).BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.Equal("SLOT_TAKEN", r.ErrorCode);
        Assert.True(cal.AllEvents.Single(e => e.Id == OwnId).Cancelled);
    }

    [Fact]
    public async Task Repeat_in_the_same_instance_after_cancellation_restores_the_event()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await TuesdayNineSlot(svc);
        var first = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        cal.Cancel(OwnId); // staff deleted it in Google Calendar; no restart in between

        var again = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.True(again.Ok, again.Say);
        Assert.Equal(first.BookingId, again.BookingId);
        var ev = Assert.Single(cal.AllEvents);
        Assert.Equal(OwnId, ev.Id);
        Assert.False(ev.Cancelled);
        Assert.Equal(1, cal.InsertCalls);
    }

    [Fact]
    public async Task Repeat_in_the_same_instance_after_cancellation_is_SLOT_TAKEN_when_the_slot_was_taken()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await TuesdayNineSlot(svc);
        Assert.True((await svc.BookAsync(slot, "Testy", "Example", Phone, default)).Ok);
        cal.Cancel(OwnId);
        await cal.InsertAsync(Busy(TuesdayNine, TuesdayNine.AddMinutes(60)), default);

        var again = await svc.BookAsync(slot, "Testy", "Example", Phone, default);
        Assert.Equal(("SLOT_TAKEN", false), (again.ErrorCode, again.Ok));
        Assert.Null(again.BookingId);
        Assert.True(cal.AllEvents.Single(e => e.Id == OwnId).Cancelled);
    }

    /// <summary>Delegates to an in-memory calendar, but the first GetAsync reports "not found".</summary>
    private sealed class FirstGetMissesCalendar(InMemoryCalendar inner) : ICalendar
    {
        private int _gets;
        public Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) => inner.ListAsync(fromUtc, toUtc, ct);
        public Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct) => inner.InsertAsync(ev, ct);
        public Task<CalendarEvent?> GetAsync(string id, CancellationToken ct) =>
            Interlocked.Increment(ref _gets) == 1 ? Task.FromResult<CalendarEvent?>(null) : inner.GetAsync(id, ct);
        public Task RestoreAsync(CalendarEvent ev, CancellationToken ct) => inner.RestoreAsync(ev, ct);
    }
}
