using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>my-appointments and cancel (ADR-006 ownership, ADR-005 serialization).</summary>
public class CancellationTests
{
    private const string Mine = "079 000 00 01";   // synthetic
    private const string Theirs = "079 000 00 02"; // synthetic

    private static async Task<string> BookAt(BookingService svc, SlotIds ids, DateTimeOffset start, string serviceKey, string phone, string first = "Testy")
    {
        var r = await svc.BookAsync(ids.Create(start, serviceKey), first, "Example", phone, default);
        Assert.True(r.Ok, r.Say);
        return r.BookingId!;
    }

    [Fact]
    public async Task My_appointments_lists_only_own_future_confirmed_bookings_up_to_three()
    {
        var (svc, cal, clock, ids) = Booking();
        var past = await BookAt(svc, ids, Zurich(2026, 11, 10, 9, 0), "mens_cut", Mine);
        await BookAt(svc, ids, Zurich(2026, 11, 10, 12, 0), "mens_cut", Mine);
        await BookAt(svc, ids, Zurich(2026, 11, 11, 10, 0), "blow_dry", Mine);
        var cancelled = await BookAt(svc, ids, Zurich(2026, 11, 12, 10, 0), "mens_cut", Mine);
        await BookAt(svc, ids, Zurich(2026, 11, 13, 10, 0), "womens_cut", Mine);
        await BookAt(svc, ids, Zurich(2026, 11, 14, 10, 0), "mens_cut", Mine);
        await BookAt(svc, ids, Zurich(2026, 11, 10, 14, 0), "mens_cut", Theirs);
        cal.Cancel(cancelled);
        clock.UtcNow = Zurich(2026, 11, 10, 9, 15); // the 09:00 booking is under way → not "upcoming"

        var r = await svc.MyAppointmentsAsync("+41790000001", default);

        Assert.True(r.Ok);
        Assert.Equal(
            ["men's cut on Tuesday, 10 November at 12:00", "blow-dry on Wednesday, 11 November at 10:00", "women's cut and style on Friday, 13 November at 10:00"],
            r.Appointments!.Select(a => a.SpokenLabel));
        Assert.DoesNotContain(r.Appointments!, a => a.BookingId == past || a.BookingId == cancelled);
        Assert.StartsWith("I can see 3 upcoming appointments: a men's cut on Tuesday, 10 November at 12:00", r.Say);
    }

    [Fact]
    public async Task My_appointments_none_is_ok_with_empty_list()
    {
        var (svc, _, _, ids) = Booking();
        await BookAt(svc, ids, Zurich(2026, 11, 10, 12, 0), "mens_cut", Theirs);
        var r = await svc.MyAppointmentsAsync(Mine, default);
        Assert.True(r.Ok);
        Assert.Empty(r.Appointments!);
        Assert.Equal("I can't find any upcoming appointments for this number.", r.Say);
    }

    [Fact]
    public async Task My_appointments_without_phone_is_PHONE_REQUIRED_and_calendar_failure_is_unavailable()
    {
        var (svc, _, _, _) = Booking();
        Assert.Equal("PHONE_REQUIRED", (await svc.MyAppointmentsAsync(null, default)).ErrorCode);
        var clock = new FakeClock(MondayMorning);
        var down = new BookingService(new ThrowingCalendar(), clock, new SlotIds(Key, clock), Key);
        Assert.Equal("CALENDAR_UNAVAILABLE", (await down.MyAppointmentsAsync(Mine, default)).ErrorCode);
    }

    [Fact]
    public async Task Cancel_own_booking_cancels_it_and_frees_the_slot()
    {
        var (svc, cal, _, ids) = Booking();
        var id = await BookAt(svc, ids, Zurich(2026, 11, 11, 9, 0), "mens_cut", Mine);

        var outcome = await svc.CancelAsync(id, Mine, default);

        Assert.True(outcome.Result.Ok);
        Assert.True(outcome.Result.Cancelled);
        Assert.Equal("Your men's cut on Wednesday, 11 November at 09:00 has been cancelled.", outcome.Result.Say);
        Assert.Equal(new FreedSlot(Zurich(2026, 11, 11, 9, 0), Svc("mens_cut")), outcome.Freed);
        Assert.True(cal.AllEvents.Single(e => e.Id == id).Cancelled);
    }

    [Fact]
    public async Task Cancel_twice_is_the_same_success_but_frees_the_slot_only_once()
    {
        var (svc, _, _, ids) = Booking();
        var id = await BookAt(svc, ids, Zurich(2026, 11, 11, 9, 0), "mens_cut", Mine);

        var first = await svc.CancelAsync(id, Mine, default);
        var second = await svc.CancelAsync(id, Mine, default);

        Assert.Equal(first.Result, second.Result);
        Assert.NotNull(first.Freed);
        Assert.Null(second.Freed);
    }

    [Fact]
    public async Task Cancel_foreign_booking_is_NOT_FOUND_and_leaves_it_untouched()
    {
        var (svc, cal, _, ids) = Booking();
        var id = await BookAt(svc, ids, Zurich(2026, 11, 11, 9, 0), "mens_cut", Theirs);

        var outcome = await svc.CancelAsync(id, Mine, default);
        var unknown = await svc.CancelAsync("nosuchbooking", Mine, default);

        Assert.Equal(("NOT_FOUND", false), (outcome.Result.ErrorCode, outcome.Result.Ok));
        Assert.Null(outcome.Freed);
        Assert.Equal(unknown.Result, outcome.Result); // indistinguishable from a missing booking
        Assert.False(cal.AllEvents.Single(e => e.Id == id).Cancelled);
    }

    [Fact]
    public async Task Cancel_past_booking_or_staff_event_is_NOT_FOUND()
    {
        var (svc, cal, clock, ids) = Booking();
        var id = await BookAt(svc, ids, Zurich(2026, 11, 10, 9, 0), "mens_cut", Mine);
        var staff = await cal.InsertAsync(Busy(Zurich(2026, 11, 12, 9, 0), Zurich(2026, 11, 12, 10, 0)), default);
        clock.UtcNow = Zurich(2026, 11, 10, 9, 0);

        Assert.Equal("NOT_FOUND", (await svc.CancelAsync(id, Mine, default)).Result.ErrorCode);
        Assert.Equal("NOT_FOUND", (await svc.CancelAsync(staff, Mine, default)).Result.ErrorCode);
        Assert.False(cal.AllEvents.Single(e => e.Id == id).Cancelled);
        Assert.False(cal.AllEvents.Single(e => e.Id == staff).Cancelled);
    }

    [Fact]
    public async Task Cancel_failure_is_CALENDAR_UNAVAILABLE_without_freed_slot()
    {
        var (svc, cal, _, ids) = Booking();
        var id = await BookAt(svc, ids, Zurich(2026, 11, 11, 9, 0), "mens_cut", Mine);
        cal.CancelFails = new HttpRequestException("calendar down");

        var outcome = await svc.CancelAsync(id, Mine, default);

        Assert.Equal(("CALENDAR_UNAVAILABLE", false), (outcome.Result.ErrorCode, outcome.Result.Ok));
        Assert.DoesNotContain("has been cancelled", outcome.Result.Say);
        Assert.Null(outcome.Freed);
        Assert.False(cal.AllEvents.Single(e => e.Id == id).Cancelled);
    }

    [Fact]
    public async Task Cancel_without_phone_is_PHONE_REQUIRED()
    {
        var (svc, _, _, _) = Booking();
        Assert.Equal("PHONE_REQUIRED", (await svc.CancelAsync("x", "not a number", default)).Result.ErrorCode);
    }

    [Fact]
    public async Task A_cancelled_slot_can_be_booked_again()
    {
        var (svc, _, _, ids) = Booking();
        var id = await BookAt(svc, ids, Zurich(2026, 11, 11, 9, 0), "mens_cut", Mine);
        await svc.CancelAsync(id, Mine, default);
        await BookAt(svc, ids, Zurich(2026, 11, 11, 9, 0), "mens_cut", Theirs);
    }
}
