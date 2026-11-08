using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Lumiere.Api;

/// <summary>
/// Google Calendar adapter (ADR-003): service account, scope calendar.events only, events.list + events.insert.
/// NOT YET EXERCISED against a real calendar (no credentials in P1 stage 1).
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
        var body = new Event
        {
            Summary = ev.Summary,
            Description = ev.Description,
            Start = new EventDateTime { DateTimeDateTimeOffset = Salon.ToLocal(ev.StartUtc), TimeZone = Salon.Zone.Id },
            End = new EventDateTime { DateTimeDateTimeOffset = Salon.ToLocal(ev.EndUtc), TimeZone = Salon.Zone.Id },
            ExtendedProperties = new Event.ExtendedPropertiesData { Private__ = new Dictionary<string, string>(ev.PrivateProperties) },
        };
        var created = await _service.Events.Insert(body, _calendarId).ExecuteAsync(ct);
        return created.Id;
    }

    /// <summary>Timed events → instant; all-day events → whole local days.</summary>
    private static DateTimeOffset? ToUtc(EventDateTime? t) =>
        t?.DateTimeDateTimeOffset?.ToUniversalTime()
        ?? (DateOnly.TryParse(t?.Date, out var d) ? Salon.ToUtc(d, TimeOnly.MinValue) : null);
}
