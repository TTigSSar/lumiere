using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumiere.Api;

/// <summary>
/// Parses the caller's English words for a day into a Zurich local date.
/// Plain or "this &lt;weekday&gt;" = next occurrence including today; "next &lt;weekday&gt;" = strictly after today;
/// "last &lt;weekday&gt;", "yesterday" etc. = past dates (rejected later as DATE_IN_PAST).
/// Part-of-day words ("next thursday afternoon") are ignored here.
/// A weekday stated with a date ("Thursday 12 November", "Thu, Nov 12") is accepted; the date decides, and
/// <see cref="Interpret"/> reports a stated weekday that does not match the date.
/// </summary>
public static partial class DateHintParser
{
    private static readonly string[] Months =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    /// <summary>A yearless date at most this many days ago is read as the past, not next year (ADR-018 m-5).</summary>
    public const int RecentPastDays = 30;

    /// <summary>The parsed date (null = unclear) and, when the caller stated a weekday that the date does not fall on, that weekday.</summary>
    public readonly record struct Result(DateOnly? Date, DayOfWeek? MismatchedWeekday);

    /// <summary>The date the hint names; with a weekday + date, the date decides.</summary>
    public static DateOnly? Parse(string? hint, DateOnly today) => Interpret(hint, today).Date;

    public static Result Interpret(string? hint, DateOnly today)
    {
        var t = Noise().Replace((hint ?? "").ToLowerInvariant().Replace(',', ' ').Replace('.', ' '), " ");
        t = Spaces().Replace(t, " ").Trim();
        if (ParseNormalized(t, today) is { } date) return new(date, null);

        // "Thursday 12 November", "next Thu Nov 12": an optional leading weekday with an explicit date.
        var lead = LeadingWeekday().Match(t);
        if (lead.Success && TryDay(lead.Groups["day"].Value, out var stated)
            && ExplicitDate(lead.Groups["rest"].Value, today) is { } explicitDate)
            return new(explicitDate, explicitDate.DayOfWeek == stated ? null : stated);
        return new(null, null);
    }

    private static DateOnly? ParseNormalized(string t, DateOnly today)
    {
        if (t.Length == 0) return null;

        switch (t)
        {
            case "today": return today;
            case "tomorrow": return today.AddDays(1);
            // Past relative days parse to a past date so the caller hears DATE_IN_PAST, not DATE_UNCLEAR.
            case "yesterday": return today.AddDays(-1);
            case "day before yesterday": return today.AddDays(-2);
            case "last week": return today.AddDays(-7);
            case "day after tomorrow" or "the day after tomorrow": return today.AddDays(2);
            case "next week":
                var toMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
                var monday = today.AddDays(toMonday == 0 ? 7 : toMonday);
                return Salon.IsOpen(monday) ? monday : Salon.NextOpenDay(monday);
        }

        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;

        var wd = Weekday().Match(t);
        if (wd.Success && TryDay(wd.Groups["day"].Value, out var dow))
        {
            var ahead = ((int)dow - (int)today.DayOfWeek + 7) % 7;
            if (wd.Groups["mod"].Value == "next" && ahead == 0) ahead = 7;
            if (wd.Groups["mod"].Value == "last") ahead = ahead == 0 ? -7 : ahead - 7;
            return today.AddDays(ahead);
        }

        return ExplicitDate(t, today);
    }

    /// <summary>"12 November", "Nov 12th 2026", "12th of Nov".</summary>
    private static DateOnly? ExplicitDate(string t, DateOnly today)
    {
        var dm = DayMonth().Match(t);
        var md = dm.Success ? dm : MonthDay().Match(t);
        if (md.Success && TryMonth(md.Groups["month"].Value, out var month) && int.TryParse(md.Groups["day"].Value, out var day))
        {
            var hasYear = int.TryParse(md.Groups["year"].Value, out var year);
            if (!hasYear) year = today.Year;
            // Out-of-range numbers ("0 November", "12 November 0000") are unclear, never an exception (ADR-018 m-5).
            if (year is < 1 or > 9998 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
            var date = new DateOnly(year, month, day);
            // A yearless day within the last RecentPastDays is the past ("8 November" on 9 November → DATE_IN_PAST,
            // not BEYOND_HORIZON); an older one means next year ("2 January" in November).
            if (!hasYear && date < today && today.DayNumber - date.DayNumber > RecentPastDays) date = date.AddYears(1);
            return date;
        }
        return null;
    }

    private static bool TryDay(string s, out DayOfWeek day)
    {
        foreach (var d in Enum.GetValues<DayOfWeek>())
            if (d.ToString().ToLowerInvariant() is var name && s.Length >= 3 && name.StartsWith(s)) { day = d; return true; }
        day = default;
        return false;
    }

    private static bool TryMonth(string s, out int month)
    {
        month = Array.FindIndex(Months, m => m == s || (s.Length >= 3 && m.StartsWith(s))) + 1;
        return month > 0;
    }

    [GeneratedRegex(@"\b(on|in|the|at|for|morning|afternoon|evening|please)\b")]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?:(?<mod>this|next|coming|last)\s+)?(?<day>[a-z]+)$")]
    private static partial Regex Weekday();

    [GeneratedRegex(@"^(?:(?:this|next|coming)\s+)?(?<day>[a-z]+)\s+(?<rest>.+)$")]
    private static partial Regex LeadingWeekday();

    [GeneratedRegex(@"^(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<month>[a-z]+)(?:\s+(?<year>\d{4}))?$")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"^(?<month>[a-z]+)\s+(?<day>\d{1,2})(?:st|nd|rd|th)?(?:\s+(?<year>\d{4}))?$")]
    private static partial Regex MonthDay();
}
