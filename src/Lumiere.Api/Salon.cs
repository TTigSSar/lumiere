using System.Globalization;

namespace Lumiere.Api;

/// <summary>A bookable service. Must match docs/assistant/system-prompt.md exactly (ADR-009).</summary>
public sealed record Service(string Key, string SpokenName, string Article, int Minutes, int PriceChf)
{
    public TimeSpan Duration => TimeSpan.FromMinutes(Minutes);
    /// <summary>"a men's cut", "highlights".</summary>
    public string WithArticle => Article.Length == 0 ? SpokenName : $"{Article} {SpokenName}";
}

/// <summary>Salon facts and scheduling rules (ADR-009).</summary>
public static class Salon
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    public static readonly TimeSpan Grid = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Buffer = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LeadTime = TimeSpan.FromHours(2);
    public const int HorizonDays = 30;
    public const int MaxSlots = 3;
    public static readonly TimeSpan PreferredGap = TimeSpan.FromMinutes(30);

    public static readonly IReadOnlyList<Service> Services =
    [
        new("womens_cut", "women's cut and style", "a", 60, 95),
        new("mens_cut", "men's cut", "a", 30, 55),
        new("childrens_cut", "children's cut", "a", 30, 35),
        new("blow_dry", "blow-dry", "a", 30, 45),
        new("full_colour", "full colour", "a", 120, 180),
        new("highlights", "highlights", "", 150, 220),
    ];

    /// <summary>Zürich public holidays the salon is closed, 2026-11-01 .. 2027-12-31.</summary>
    public static readonly IReadOnlySet<DateOnly> Holidays = new HashSet<DateOnly>
    {
        new(2026, 12, 25), new(2026, 12, 26),
        new(2027, 1, 1), new(2027, 1, 2),
        new(2027, 3, 26),  // Good Friday
        new(2027, 3, 29),  // Easter Monday
        new(2027, 5, 1),
        new(2027, 5, 6),   // Ascension
        new(2027, 5, 17),  // Whit Monday
        new(2027, 8, 1),
        new(2027, 12, 25), new(2027, 12, 26),
    };

    public static Service? FindService(string key) => Services.FirstOrDefault(s => s.Key == key);

    /// <summary>Opening hours for a local date, or null when closed.</summary>
    public static (TimeOnly Open, TimeOnly Close)? Hours(DateOnly date)
    {
        if (Holidays.Contains(date)) return null;
        return date.DayOfWeek switch
        {
            DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Friday => (new(9, 0), new(18, 30)),
            DayOfWeek.Saturday => (new(8, 30), new(16, 0)),
            _ => null,
        };
    }

    public static bool IsOpen(DateOnly date) => Hours(date) is not null;

    public static DateOnly NextOpenDay(DateOnly after)
    {
        var d = after.AddDays(1);
        while (!IsOpen(d)) d = d.AddDays(1);
        return d;
    }

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Zone), TimeSpan.Zero);

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    /// <summary>"Tuesday, 10 November at 14:30".</summary>
    public static string SpokenLabel(DateTimeOffset startUtc) =>
        ToLocal(startUtc).ToString("dddd, d MMMM 'at' HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Tuesday, 10 November".</summary>
    public static string SpokenDay(DateOnly date) => date.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);

    /// <summary>"Monday, 9 November 2026".</summary>
    public static string SpokenToday(DateOnly date) => date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    public static string ServiceList() =>
        string.Join(", ", Services.Take(Services.Count - 1).Select(s => s.SpokenName)) + " or " + Services[^1].SpokenName;
}

public enum PartOfDay { Any, Morning, Afternoon, Evening }

public static class PartOfDayParser
{
    public static PartOfDay Parse(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Contains("morning")) return PartOfDay.Morning;
        if (t.Contains("afternoon") || t.Contains("lunch")) return PartOfDay.Afternoon;
        if (t.Contains("evening") || t.Contains("after work")) return PartOfDay.Evening;
        return PartOfDay.Any;
    }

    public static bool Matches(PartOfDay part, TimeOnly start) => part switch
    {
        PartOfDay.Morning => start < new TimeOnly(12, 0),
        PartOfDay.Afternoon => start >= new TimeOnly(12, 0) && start < new TimeOnly(17, 0),
        PartOfDay.Evening => start >= new TimeOnly(17, 0),
        _ => true,
    };
}

/// <summary>Tolerant mapping of the caller's words to a service key.</summary>
public static class ServiceMatcher
{
    public static Service? Match(string? text)
    {
        var raw = (text ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0) return null;
        var exact = Salon.FindService(raw);
        if (exact is not null) return exact;

        var words = raw.Split(raw.Where(c => !char.IsLetter(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();
        bool Any(params string[] ws) => ws.Any(words.Contains);

        string? key =
            Any("highlight", "highlights", "streaks", "foils") ? "highlights" :
            Any("colour", "color", "colouring", "coloring", "dye", "tint") ? "full_colour" :
            Any("blow", "blowdry", "blowout") ? "blow_dry" :
            Any("child", "children", "childrens", "kid", "kids", "boy", "girl") ? "childrens_cut" :
            Any("women", "womens", "woman", "ladies", "lady", "female") ? "womens_cut" :
            Any("men", "mens", "man", "male", "gents", "gentleman", "gentlemen", "barber") ? "mens_cut" :
            null;
        return key is null ? null : Salon.FindService(key);
    }
}
