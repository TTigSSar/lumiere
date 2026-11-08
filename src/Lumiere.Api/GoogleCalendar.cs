using System.Net;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Lumiere.Api;

/// <summary>
/// Google Calendar adapter (ADR-003): service account, scope calendar.events only; events.list, events.insert with a
/// client-supplied id (ADR-015), events.get and events.update (restore). Never deletes.
/// </summary>
public sealed class GoogleCalendar : ICalendar
{
    private readonly CalendarService _service;
    private readonly string _calendarId;

    public GoogleCalendar(string calendarId, string serviceAccountKeyPath)
    {
        var credential = CredentialFactory.FromFile<ServiceAccountCredential>(serviceAccountKeyPath)
            .ToGoogleCredential()
            .CreateScoped(CalendarService.Scope.CalendarEvents);
        _service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "lumiere-receptionist",
        });
        _calendarId = calendarId;
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var result = new List<CalendarEvent>();
        string? pageToken = null;
        do
        {
            var request = _service.Events.List(_calendarId);
            request.TimeMinDateTimeOffset = fromUtc;
            request.TimeMaxDateTimeOffset = toUtc;
            request.SingleEvents = true;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
            request.MaxResults = 250;
            request.PageToken = pageToken;
            var page = await request.ExecuteAsync(ct);
            foreach (var e in page.Items ?? [])
            {
                // "Free" (transparent) entries, e.g. all-day reminders, do not block the chair.
                if (e.Transparency == "transparent") continue;
                var (start, end) = (ToUtc(e.Start), ToUtc(e.End));
                if (start is null || end is null) continue;
                result.Add(new CalendarEvent(e.Id, start.Value, end.Value, e.Summary ?? "", e.Description ?? "",
                    e.ExtendedProperties?.Private__?.AsReadOnly() ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>()));
            }
            pageToken = page.NextPageToken;
        } while (pageToken is not null);
        return result;
    }

    public async Task<string> InsertAsync(CalendarEvent ev, CancellationToken ct)
    {
        try
        {
            var created = await _service.Events.Insert(ToGoogle(ev), _calendarId).ExecuteAsync(ct);
            return created.Id;
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Conflict && ev.Id is not null)
        {
            throw new CalendarConflictException(ev.Id);
        }
    }

    public async Task<CalendarEvent?> GetAsync(string id, CancellationToken ct)
    {
        Event e;
        try { e = await _service.Events.Get(_calendarId, id).ExecuteAsync(ct); }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { return null; }

        // Deleted events are only guaranteed to carry the id, so times may be missing.
        var (start, end) = (ToUtc(e.Start), ToUtc(e.End));
        return new CalendarEvent(e.Id, start ?? default, end ?? default, e.Summary ?? "", e.Description ?? "",
            e.ExtendedProperties?.Private__?.AsReadOnly() ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(),
            Cancelled: e.Status == "cancelled");
    }

    public async Task RestoreAsync(CalendarEvent ev, CancellationToken ct)
    {
        var body = ToGoogle(ev);
        body.Status = "confirmed";
        await _service.Events.Update(body, _calendarId, ev.Id).ExecuteAsync(ct);
    }

    private static Event ToGoogle(CalendarEvent ev) => new()
    {
        Id = ev.Id,
        Summary = ev.Summary,
        Description = ev.Description,
        Start = new EventDateTime { DateTimeDateTimeOffset = Salon.ToLocal(ev.StartUtc), TimeZone = Salon.Zone.Id },
        End = new EventDateTime { DateTimeDateTimeOffset = Salon.ToLocal(ev.EndUtc), TimeZone = Salon.Zone.Id },
        ExtendedProperties = new Event.ExtendedPropertiesData { Private__ = new Dictionary<string, string>(ev.PrivateProperties) },
    };

    /// <summary>Timed events → instant; all-day events → whole local days.</summary>
    private static DateTimeOffset? ToUtc(EventDateTime? t) =>
        t?.DateTimeDateTimeOffset?.ToUniversalTime()
        ?? (DateOnly.TryParse(t?.Date, out var d) ? Salon.ToUtc(d, TimeOnly.MinValue) : null);
}
