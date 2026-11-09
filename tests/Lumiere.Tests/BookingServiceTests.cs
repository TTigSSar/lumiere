using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

public class BookingServiceTests
{
    private const string Phone = "079 000 00 01"; // synthetic

    private static async Task<SlotDto> FirstSlot(BookingService svc, string service = "men's cut", string day = "tuesday")
    {
        var r = await svc.CheckAvailabilityAsync(service, day, null, default);
        Assert.True(r.Ok, r.Say);
        return r.Slots![0];
    }

    [Fact]
    public async Task Check_availability_returns_today_and_spoken_labels()
    {
        var (svc, _, _, _) = Booking();
        var r = await svc.CheckAvailabilityAsync("men's cut", "tuesday", "afternoon", default);
        Assert.True(r.Ok);
        Assert.Equal("Monday, 9 November 2026", r.Today);
        Assert.Equal(3, r.Slots!.Count);
        Assert.Equal("Tuesday, 10 November at 12:00", r.Slots[0].SpokenLabel);
        Assert.Null(r.Note);
    }

    [Fact]
    public async Task Closed_day_names_next_open_day()
    {
        var (svc, _, _, _) = Booking();
        var r = await svc.CheckAvailabilityAsync("men's cut", "today", null, default);
        Assert.Equal(("CLOSED", false), (r.ErrorCode, r.Ok));
        Assert.Contains("Tuesday, 10 November", r.Say);
    }

    [Theory]
    [InlineData("perm", "tuesday", "UNKNOWN_SERVICE")]
    [InlineData("men's cut", "sometime", "DATE_UNCLEAR")]
    [InlineData("men's cut", "2026-11-03", "DATE_IN_PAST")]
    [InlineData("men's cut", "yesterday", "DATE_IN_PAST")]
    [InlineData("men's cut", "last friday", "DATE_IN_PAST")]
    [InlineData("men's cut", "2026-12-18", "BEYOND_HORIZON")]
    public async Task Business_errors(string service, string hint, string code)
    {
        var (svc, _, _, _) = Booking();
        var r = await svc.CheckAvailabilityAsync(service, hint, null, default);
        Assert.False(r.Ok);
        Assert.Equal(code, r.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(r.Say));
    }

    [Theory]
    [InlineData("Thursday 12 November")]
    [InlineData("Thu, Nov 12th")]
    [InlineData("Thursday the 12th of November")]
    public async Task Weekday_with_matching_date_offers_slots_on_that_date(string hint)
    {
        var (svc, _, _, _) = Booking();
        var r = await svc.CheckAvailabilityAsync("men's cut", hint, null, default);
        Assert.True(r.Ok);
        Assert.StartsWith("Thursday, 12 November", r.Slots![0].SpokenLabel);
    }

    [Fact]
    public async Task Weekday_that_does_not_match_the_date_is_DATE_MISMATCH()
    {
        var (svc, _, _, _) = Booking();
        var r = await svc.CheckAvailabilityAsync("men's cut", "Friday 12 November", null, default);
        Assert.Equal(("DATE_MISMATCH", false), (r.ErrorCode, r.Ok));
        Assert.Equal("12 November is a Thursday — did you mean Thursday, 12 November, or a different day?", r.Say);
    }

    [Fact]
    public async Task Full_day_returns_alternatives_from_following_days()
    {
        var (svc, cal, _, _) = Booking();
        await cal.InsertAsync(Busy(Zurich(2026, 11, 10, 9, 0), Zurich(2026, 11, 10, 18, 30)), default);
        var r = await svc.CheckAvailabilityAsync("men's cut", "tuesday", "morning", default);
        Assert.True(r.Ok);
        Assert.Equal("alternatives", r.Note);
        Assert.Equal("Wednesday, 11 November at 09:00", r.Slots![0].SpokenLabel);
        Assert.Contains("fully booked", r.Say);
    }

    [Fact]
    public async Task Labels_are_correct_after_spring_forward()
    {
        var (svc, _, _, _) = Booking(Zurich(2027, 3, 27, 7, 0));
        var r = await svc.CheckAvailabilityAsync("men's cut", "2027-03-30", "morning", default);
        Assert.Equal("Saturday, 27 March 2027", r.Today);
        Assert.Equal("Tuesday, 30 March at 09:00", r.Slots![0].SpokenLabel);
    }

    [Fact]
    public async Task Happy_path_writes_one_event_with_extended_properties()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await FirstSlot(svc);
        var r = await svc.BookAsync(slot.SlotId, " Testy ", "Example", Phone, default);

        Assert.True(r.Ok, r.Say);
        Assert.Equal("You're booked for a men's cut on Tuesday, 10 November at 09:00. See you then!", r.ConfirmationText);
        var ev = Assert.Single(cal.Events);
        Assert.Equal("Men's cut — Testy E.", ev.Summary);
        Assert.Equal(Zurich(2026, 11, 10, 9, 0), ev.StartUtc);
        Assert.Equal(Zurich(2026, 11, 10, 9, 30), ev.EndUtc);
        Assert.Contains("+41790000001", ev.Description);
        Assert.Equal(r.BookingId, ev.PrivateProperties["bookingId"]);
        Assert.Equal(Caller.PhoneHash("+41790000001", Key), ev.PrivateProperties["phoneHash"]);
        Assert.Matches("^[0-9a-f]{64}$", ev.PrivateProperties["phoneHash"]);
        Assert.Equal("mens_cut", ev.PrivateProperties["service"]);
        Assert.Equal("ai-phone", ev.PrivateProperties["source"]);
    }

    [Fact]
    public async Task Taken_slot_returns_SLOT_TAKEN_with_alternatives_and_writes_nothing()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await FirstSlot(svc);
        await cal.InsertAsync(Busy(Zurich(2026, 11, 10, 9, 0), Zurich(2026, 11, 10, 10, 0)), default);

        var r = await svc.BookAsync(slot.SlotId, "Testy", "Example", Phone, default);
        Assert.Equal(("SLOT_TAKEN", false), (r.ErrorCode, r.Ok));
        Assert.InRange(r.Slots!.Count, 1, 2);
        Assert.Single(cal.Events);
    }

    [Fact]
    public async Task Same_booking_twice_is_idempotent()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await FirstSlot(svc);
        var first = await svc.BookAsync(slot.SlotId, "Testy", "Example", Phone, default);
        var again = await svc.BookAsync(slot.SlotId, "Testy", "Example", "+41 79 000 00 01", default);
        Assert.True(again.Ok);
        Assert.Equal(first.BookingId, again.BookingId);
        Assert.Single(cal.Events);
    }

    [Fact]
    public async Task Twenty_parallel_bookings_of_one_slot_yield_exactly_one_success()
    {
        var (svc, cal, _, _) = Booking();
        var slot = await FirstSlot(svc);
        var results = await Task.WhenAll(Enumerable.Range(10, 20).Select(i =>
            Task.Run(() => svc.BookAsync(slot.SlotId, "Testy", "Example", $"079 000 00 {i}", default))));
        Assert.Single(results, r => r.Ok);
        Assert.All(results.Where(r => !r.Ok), r => Assert.Equal("SLOT_TAKEN", r.ErrorCode));
        Assert.Single(cal.Events);
    }

    [Fact]
    public async Task Calendar_failure_is_CALENDAR_UNAVAILABLE_and_never_implies_success()
    {
        var clock = new FakeClock(MondayMorning);
        var ids = new SlotIds(Key, clock);
        var svc = new BookingService(new ThrowingCalendar(), clock, ids, Key);
        var slot = await FirstSlot(svc);
        var r = await svc.BookAsync(slot.SlotId, "Testy", "Example", Phone, default);
        Assert.Equal(("CALENDAR_UNAVAILABLE", false), (r.ErrorCode, r.Ok));
        Assert.Null(r.BookingId);
        Assert.Contains("not booked", r.Say);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]
    [InlineData("079 123")]
    public async Task Invalid_phone_is_PHONE_REQUIRED(string? phone)
    {
        var (svc, cal, _, _) = Booking();
        var slot = await FirstSlot(svc);
        var r = await svc.BookAsync(slot.SlotId, "Testy", "Example", phone, default);
        Assert.Equal("PHONE_REQUIRED", r.ErrorCode);
        Assert.Empty(cal.Events);
    }

    [Fact]
    public async Task Tampered_slot_is_SLOT_INVALID()
    {
        var (svc, _, _, _) = Booking();
        var r = await svc.BookAsync("bm90LWEtc2xvdA.AAAA", "Testy", "Example", Phone, default);
        Assert.Equal("SLOT_INVALID", r.ErrorCode);
    }

    [Theory]
    [InlineData("079 000 00 01", "+41790000001")]
    [InlineData("+41 79 000 00 01", "+41790000001")]
    [InlineData("0041 79 000 00 01", "+41790000001")]
    [InlineData("+41 (0)79 000 00 01", "+41790000001")]
    [InlineData("+49 30 0000000", "+49300000000")]
    public void Phone_normalisation(string input, string e164) => Assert.Equal(e164, Caller.NormalisePhone(input));

    [Fact]
    public void Phone_mask_hides_the_middle() => Assert.Equal("+41 79 *** ** 01", Caller.MaskPhone("+41790000001"));

    [Theory]
    [InlineData("O'Neil", true)]
    [InlineData("Anne-Marie", true)]
    [InlineData("Zoë", true)]
    [InlineData("R2D2", false)]
    [InlineData("", false)]
    public void Name_validation(string name, bool valid) => Assert.Equal(valid, Caller.NormaliseName(name) is not null);
}
