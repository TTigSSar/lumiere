using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumiere.Api;

public sealed record SlotDto(string SlotId, string SpokenLabel);

public sealed record AppointmentDto(string BookingId, string SpokenLabel);

/// <summary>A slot freed by a cancellation: its start and the cancelled service (input to Cancellation Recovery, ADR-007).</summary>
public sealed record FreedSlot(DateTimeOffset StartUtc, Service Service);

/// <summary>The cancel response plus the freed slot — set only when this call actually cancelled the event.</summary>
public sealed record CancelOutcome(ToolResult Result, FreedSlot? Freed);

/// <summary>Tool response. Business failures are ok:false with error_code and say (ADR-014).</summary>
public sealed record ToolResult
{
    public bool Ok { get; init; }
    public string? ErrorCode { get; init; }
    public string? Say { get; init; }
    public string? Today { get; init; }
    public IReadOnlyList<SlotDto>? Slots { get; init; }
    public string? Note { get; init; }
    public string? BookingId { get; init; }
    public string? ConfirmationText { get; init; }
    public IReadOnlyList<AppointmentDto>? Appointments { get; init; }
    public bool? Cancelled { get; init; }
    public string? WaitlistId { get; init; }

    public static ToolResult Fail(string code, string say) => new() { Ok = false, ErrorCode = code, Say = say };
}

public sealed class BookingService(ICalendar calendar, TimeProvider clock, SlotIds slotIds, byte[] phoneHashKey)
{
    public static readonly TimeSpan CalendarTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Budget for the single lookup after a failed or timed-out insert (ADR-015).</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);
    private const int AlternativeDays = 7;

    // Bookings are serialized process-wide; valid because the service runs as a single instance (ADR-005).
    // No in-process success cache: the deterministic event id + get-by-id is the idempotency mechanism (ADR-015),
    // so a repeat after a cancellation in Google is restored instead of answered from memory.
    private static readonly SemaphoreSlim BookingLock = new(1, 1);

    private static ToolResult CalendarUnavailable(bool booking) => ToolResult.Fail("CALENDAR_UNAVAILABLE", booking
        ? "Sorry, I can't reach our calendar right now, so the appointment is not booked. Shall I try again in a moment?"
        : "Sorry, I can't reach our calendar right now. Shall I try again in a moment?");

    public async Task<ToolResult> CheckAvailabilityAsync(string? serviceText, string? dateHint, string? partOfDay, CancellationToken ct)
    {
        var service = ServiceMatcher.Match(serviceText);
        if (service is null)
            return ToolResult.Fail("UNKNOWN_SERVICE", $"We offer {Salon.ServiceList()}. Which would you like?");

        var now = clock.GetUtcNow();
        var today = Scheduler.Today(now);
        var part = PartOfDayParser.Parse(partOfDay);
        if (part == PartOfDay.Any) part = PartOfDayParser.Parse(dateHint);

        var hint = DateHintParser.Interpret(dateHint, today);
        if (hint.Date is not { } date)
            return ToolResult.Fail("DATE_UNCLEAR", "Which day would suit you?");
        if (hint.MismatchedWeekday is not null)
        {
            var dayMonth = date.ToString("d MMMM", CultureInfo.InvariantCulture);
            return ToolResult.Fail("DATE_MISMATCH",
                $"{dayMonth} is a {date.DayOfWeek} — did you mean {Salon.SpokenDay(date)}, or a different day?");
        }
        if (date < today)
            return ToolResult.Fail("DATE_IN_PAST", $"That day has already passed. Today is {Salon.SpokenToday(today)}. Which day would suit you?");
        var lastDay = today.AddDays(Salon.HorizonDays);
        if (date > lastDay)
            return ToolResult.Fail("BEYOND_HORIZON", $"I can book up to {Salon.SpokenDay(lastDay)}. Which day before then would suit you?");
        if (!Salon.IsOpen(date))
            return ToolResult.Fail("CLOSED", $"We're closed on {Salon.SpokenDay(date)}. The next open day is {Salon.SpokenDay(Salon.NextOpenDay(date))}. Would that suit you?");

        IReadOnlyList<CalendarEvent> busy;
        try { busy = await ListBusyAsync(date, date.AddDays(AlternativeDays), ct); }
        catch (Exception) { return CalendarUnavailable(booking: false); }

        var todayLabel = Salon.SpokenToday(today);
        var free = Scheduler.FreeStarts(date, service, part, now, busy);
        if (free.Count > 0)
        {
            var slots = ToSlots(Scheduler.Spread(free), service);
            return new ToolResult { Ok = true, Today = todayLabel, Slots = slots, Say = $"Available times: {JoinLabels(slots)}." };
        }

        var later = Enumerable.Range(1, AlternativeDays)
            .SelectMany(i => Scheduler.FreeStarts(date.AddDays(i), service, part, now, busy)).ToList();
        var partText = part == PartOfDay.Any ? "" : $" in the {part.ToString().ToLowerInvariant()}";
        if (later.Count == 0)
            return ToolResult.Fail("NO_AVAILABILITY",
                $"Sorry, there is nothing free{partText} on {Salon.SpokenDay(date)} or in the following week. Would another day or time of day suit you?");

        var alternatives = ToSlots(Scheduler.Earliest(later, Salon.MaxSlots), service);
        return new ToolResult
        {
            Ok = true, Today = todayLabel, Slots = alternatives, Note = "alternatives",
            Say = $"Sorry, {Salon.SpokenDay(date)} is fully booked{partText}. The nearest free times are: {JoinLabels(alternatives)}.",
        };
    }

    public async Task<ToolResult> BookAsync(string? slotId, string? firstName, string? lastName, string? callerPhone, CancellationToken ct)
    {
        if (slotIds.Verify(slotId) is not { } slot)
            return ToolResult.Fail("SLOT_INVALID", "Sorry, that time is no longer valid. Shall I check the available times again?");
        if (Caller.NormalisePhone(callerPhone) is not { } phone)
            return ToolResult.Fail("PHONE_REQUIRED", "May I have your phone number, please, including the area code?");
        if (Caller.NormaliseName(firstName) is not { } first || Caller.NormaliseName(lastName) is not { } last)
            return ToolResult.Fail("INVALID_NAME", "Could you tell me your first and last name again, please?");

        var (startUtc, service) = slot;
        var endUtc = startUtc + service.Duration;
        // Deterministic event id (ADR-015): a retry of the same booking can never create a second event.
        // It doubles as the booking id, so a retry returns the same booking_id.
        var eventId = EventIds.For(startUtc, service.Key, phone, phoneHashKey);

        await BookingLock.WaitAsync(ct);
        try
        {
            ToolResult Booked()
            {
                var text = $"You're booked for {service.WithArticle} on {Salon.SpokenLabel(startUtc)}. See you then!";
                return new ToolResult { Ok = true, BookingId = eventId, ConfirmationText = text, Say = text };
            }

            CalendarEvent? existing;
            try { existing = await WithTimeout(t => calendar.GetAsync(eventId, t), ct); }
            catch (Exception) { return CalendarUnavailable(booking: true); }
            if (existing is { Cancelled: false }) return Booked();

            var now = clock.GetUtcNow();
            IReadOnlyList<CalendarEvent> busy;
            try { busy = await WithTimeout(t => calendar.ListAsync(startUtc - Salon.Buffer, endUtc + Salon.Buffer, t), ct); }
            catch (Exception) { return CalendarUnavailable(booking: true); }

            // The booking's own event (same id) never blocks its own slot.
            if (!Scheduler.Fits(startUtc, service, now, busy.Where(e => e.Id != eventId).ToList()))
                return await SlotTakenAsync(startUtc, service, now, ct);

            var ev = new CalendarEvent(eventId, startUtc, endUtc,
                $"{char.ToUpperInvariant(service.SpokenName[0])}{service.SpokenName[1..]} — {first} {char.ToUpperInvariant(last[0])}.",
                $"Customer: {first} {last}\nPhone: {phone}\nService: {service.SpokenName} ({service.Minutes} min, CHF {service.PriceChf})\nBooked by the AI phone receptionist.",
                new Dictionary<string, string>
                {
                    ["bookingId"] = eventId,
                    ["phoneHash"] = Caller.PhoneHash(phone, phoneHashKey),
                    ["service"] = service.Key,
                    ["source"] = "ai-phone",
                });

            try
            {
                if (existing is { Cancelled: true })
                    await WithTimeout(async t => { await calendar.RestoreAsync(ev, t); return true; }, ct);
                else
                    await WithTimeout(t => calendar.InsertAsync(ev, t), ct);
                return Booked();
            }
            catch (Exception)
            {
                // Conflict (already exists), timeout or error: the event may exist anyway — look once before answering.
                return await ExistsConfirmedAsync(eventId, ct) ? Booked() : CalendarUnavailable(booking: true);
            }
        }
        finally
        {
            BookingLock.Release();
        }
    }

    public const int MaxAppointments = 3;

    private static readonly ToolResult PhoneRequired =
        ToolResult.Fail("PHONE_REQUIRED", "May I have your phone number, please, including the area code?");

    private static readonly ToolResult BookingNotFound = ToolResult.Fail("NOT_FOUND",
        "I can't find that appointment for this phone number. Shall I check your upcoming appointments?");

    /// <summary>Up to 3 future confirmed bookings of the caller (ADR-006: matched by phone hash only).</summary>
    public async Task<ToolResult> MyAppointmentsAsync(string? callerPhone, CancellationToken ct)
    {
        if (Caller.NormalisePhone(callerPhone) is not { } phone) return PhoneRequired;
        var now = clock.GetUtcNow();
        IReadOnlyList<CalendarEvent> events;
        try { events = await WithTimeout(t => calendar.ListByPhoneHashAsync(Caller.PhoneHash(phone, phoneHashKey), now, t), ct); }
        catch (Exception) { return CalendarUnavailable(booking: false); }

        var own = events
            .Where(e => !e.Cancelled && e.Id is not null && e.StartUtc > now && ServiceOf(e) is not null)
            .OrderBy(e => e.StartUtc).Take(MaxAppointments)
            .Select(e => (Event: e, Service: ServiceOf(e)!)).ToList();
        if (own.Count == 0)
            return new ToolResult { Ok = true, Appointments = [], Say = "I can't find any upcoming appointments for this number." };

        var list = own.Select(o => new AppointmentDto(o.Event.Id!, AppointmentLabel(o.Service, o.Event.StartUtc))).ToList();
        var spoken = own.Select(o => $"{o.Service.WithArticle} on {Salon.SpokenLabel(o.Event.StartUtc)}").ToList();
        var say = spoken.Count == 1
            ? $"I can see {spoken[0]}."
            : $"I can see {spoken.Count} upcoming appointments: {string.Join(", ", spoken.Take(spoken.Count - 1))} and {spoken[^1]}.";
        return new ToolResult { Ok = true, Appointments = list, Say = say };
    }

    /// <summary>
    /// Cancels the caller's own future booking, serialized with bookings (ADR-005). A missing, foreign, past or
    /// non-booking event is NOT_FOUND (ADR-006 — never reveal that a foreign booking exists). Repeat-safe: an own
    /// booking that is already cancelled answers the same success, but only the call that cancelled it frees a slot.
    /// </summary>
    public async Task<CancelOutcome> CancelAsync(string? bookingId, string? callerPhone, CancellationToken ct)
    {
        if (Caller.NormalisePhone(callerPhone) is not { } phone) return new(PhoneRequired, null);
        var id = (bookingId ?? "").Trim();
        if (id.Length == 0 || id.Length > 1024) return new(BookingNotFound, null);

        await BookingLock.WaitAsync(ct);
        try
        {
            CalendarEvent? ev;
            try { ev = await WithTimeout(t => calendar.GetAsync(id, t), ct); }
            catch (Exception) { return new(CalendarUnavailable(booking: false), null); }

            var now = clock.GetUtcNow();
            if (ev is null || ServiceOf(ev) is not { } service || !Owns(ev, id, service, phone) || ev.StartUtc <= now)
                return new(BookingNotFound, null);

            var text = $"Your {service.SpokenName} on {Salon.SpokenLabel(ev.StartUtc)} has been cancelled.";
            var success = new ToolResult { Ok = true, BookingId = id, Cancelled = true, Say = text };
            if (ev.Cancelled) return new(success, null);

            try
            {
                await WithTimeout(async t => { await calendar.CancelAsync(id, t); return true; }, ct);
            }
            catch (Exception)
            {
                // Timeout or error: Google may have cancelled it anyway — look once before answering.
                CalendarEvent? after;
                try { after = await WithTimeout(t => calendar.GetAsync(id, t), ct, LookupTimeout); }
                catch (Exception) { after = null; }
                if (after is not { Cancelled: true })
                    return new(ToolResult.Fail("CALENDAR_UNAVAILABLE",
                        "Sorry, I can't reach our calendar right now, so the appointment is not cancelled yet. Shall I try again in a moment?"), null);
            }
            return new(success, new FreedSlot(ev.StartUtc, service));
        }
        finally
        {
            BookingLock.Release();
        }
    }

    /// <summary>"men's cut on Tuesday, 10 November at 12:00".</summary>
    public static string AppointmentLabel(Service service, DateTimeOffset startUtc) => $"{service.SpokenName} on {Salon.SpokenLabel(startUtc)}";

    private static Service? ServiceOf(CalendarEvent e) =>
        e.PrivateProperties.TryGetValue("service", out var key) ? Salon.FindService(key) : null;

    /// <summary>The phone hash matches; or, when Google returns a cancelled event without it, the deterministic
    /// event id recomputed with the caller's phone matches (ADR-015).</summary>
    private bool Owns(CalendarEvent e, string id, Service service, string phone)
    {
        if (e.PrivateProperties.TryGetValue("phoneHash", out var hash))
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Caller.PhoneHash(phone, phoneHashKey)));
        return e.StartUtc != default && EventIds.For(e.StartUtc, service.Key, phone, phoneHashKey) == id;
    }

    /// <summary>One get-by-id with its own short budget; false when missing, cancelled or unreachable.</summary>
    private async Task<bool> ExistsConfirmedAsync(string eventId, CancellationToken ct)
    {
        try { return await WithTimeout(t => calendar.GetAsync(eventId, t), ct, LookupTimeout) is { Cancelled: false }; }
        catch (Exception) { return false; }
    }

    private async Task<ToolResult> SlotTakenAsync(DateTimeOffset startUtc, Service service, DateTimeOffset now, CancellationToken ct)
    {
        const string taken = "Sorry, that time has just been taken.";
        var date = Salon.LocalDate(startUtc);
        try
        {
            var busy = await ListBusyAsync(date, date.AddDays(AlternativeDays), ct);
            var free = Enumerable.Range(0, AlternativeDays + 1)
                .SelectMany(i => Scheduler.FreeStarts(date.AddDays(i), service, PartOfDay.Any, now, busy)).ToList();
            var alternatives = ToSlots(Scheduler.Nearest(free, startUtc, 2), service);
            return alternatives.Count == 0
                ? ToolResult.Fail("SLOT_TAKEN", $"{taken} Would another day suit you?")
                : ToolResult.Fail("SLOT_TAKEN", $"{taken} I can offer {JoinLabels(alternatives)}. Would one of those suit you?") with { Slots = alternatives };
        }
        catch (Exception)
        {
            return ToolResult.Fail("SLOT_TAKEN", $"{taken} Shall I check other times?");
        }
    }

    private Task<IReadOnlyList<CalendarEvent>> ListBusyAsync(DateOnly from, DateOnly toInclusive, CancellationToken ct) =>
        WithTimeout(t => calendar.ListAsync(
            Salon.ToUtc(from, TimeOnly.MinValue) - Salon.Buffer,
            Salon.ToUtc(toInclusive.AddDays(1), TimeOnly.MinValue) + Salon.Buffer, t), ct);

    /// <summary>Runs a calendar call with the 3 s budget (or <paramref name="budget"/>); throws on timeout or failure.</summary>
    private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct, TimeSpan? budget = null)
    {
        var limit = budget ?? CalendarTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        return await call(cts.Token).WaitAsync(limit, ct);
    }

    private List<SlotDto> ToSlots(IEnumerable<DateTimeOffset> starts, Service service) =>
        starts.Select(s => new SlotDto(slotIds.Create(s, service.Key), Salon.SpokenLabel(s))).ToList();

    private static string JoinLabels(IReadOnlyList<SlotDto> slots) =>
        slots.Count == 1 ? slots[0].SpokenLabel
            : string.Join(", ", slots.Take(slots.Count - 1).Select(s => s.SpokenLabel)) + " or " + slots[^1].SpokenLabel;
}
