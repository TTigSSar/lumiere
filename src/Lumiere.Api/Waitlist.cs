using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Lumiere.Api;

public static class WaitlistStatus
{
    public const string Active = "Active";
    public const string Notified = "Notified";
    public const string Expired = "Expired";
}

/// <summary>One waiting-list request (ADR-004/007). <see cref="Dates"/> are Zurich local dates, ascending.</summary>
public sealed record WaitlistEntry(
    string Id,
    string ServiceKey,
    string PreferredDaysText,
    PartOfDay PartOfDay,
    IReadOnlyList<DateOnly> Dates,
    string FirstName,
    string PhoneE164,
    DateTimeOffset CreatedUtc,
    string Status,
    DateTimeOffset? NotifiedUtc);

/// <summary>
/// The waitlist in one SQLite file (ADR-004 amended, ADR-017): Microsoft.Data.Sqlite, plain SQL, one table.
/// A connection per operation; pooling off so the file is never held open between calls.
/// </summary>
public sealed class WaitlistStore
{
    public const string FileName = "lumiere.db";
    private const string DateFormat = "yyyy-MM-dd";
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ssZ";
    private readonly string _connectionString;

    public WaitlistStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDir, FileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS WaitlistEntry (
                id TEXT PRIMARY KEY,
                service_key TEXT NOT NULL,
                preferred_days_text TEXT NOT NULL,
                part_of_day TEXT NOT NULL,
                dates TEXT NOT NULL,
                first_name TEXT NOT NULL,
                phone_e164 TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                notified_utc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WaitlistEntry_status ON WaitlistEntry(status);
            """);
    }

    /// <summary>
    /// Adds the entry, or — when the same phone already has an Active entry for the same service — replaces that
    /// entry's preferences and returns its id (created_utc, i.e. the queue position, is kept).
    /// </summary>
    public string Upsert(WaitlistEntry entry)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        string? existing;
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM WaitlistEntry WHERE phone_e164 = $p AND service_key = $s AND status = $a LIMIT 1";
            find.Parameters.AddWithValue("$p", entry.PhoneE164);
            find.Parameters.AddWithValue("$s", entry.ServiceKey);
            find.Parameters.AddWithValue("$a", WaitlistStatus.Active);
            existing = find.ExecuteScalar() as string;
        }

        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = existing is null
            ? """
              INSERT INTO WaitlistEntry (id, service_key, preferred_days_text, part_of_day, dates, first_name, phone_e164, created_utc, status, notified_utc)
              VALUES ($id, $s, $text, $part, $dates, $name, $p, $created, $a, NULL)
              """
            : "UPDATE WaitlistEntry SET preferred_days_text = $text, part_of_day = $part, dates = $dates, first_name = $name WHERE id = $id";
        var id = existing ?? entry.Id;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", entry.ServiceKey);
        cmd.Parameters.AddWithValue("$text", entry.PreferredDaysText);
        cmd.Parameters.AddWithValue("$part", entry.PartOfDay.ToString());
        cmd.Parameters.AddWithValue("$dates", string.Join(',', entry.Dates.Order().Select(d => d.ToString(DateFormat, CultureInfo.InvariantCulture))));
        cmd.Parameters.AddWithValue("$name", entry.FirstName);
        cmd.Parameters.AddWithValue("$p", entry.PhoneE164);
        cmd.Parameters.AddWithValue("$created", Instant(entry.CreatedUtc));
        cmd.Parameters.AddWithValue("$a", WaitlistStatus.Active);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return id;
    }

    /// <summary>Active entries whose dates include <paramref name="date"/>, earliest-joined first.</summary>
    public List<WaitlistEntry> ActiveForDate(DateOnly date)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM WaitlistEntry
            WHERE status = $a AND (',' || dates || ',') LIKE $d
            ORDER BY created_utc, id
            """;
        cmd.Parameters.AddWithValue("$a", WaitlistStatus.Active);
        cmd.Parameters.AddWithValue("$d", $"%,{date.ToString(DateFormat, CultureInfo.InvariantCulture)},%");
        return Read(cmd);
    }

    public WaitlistEntry? Get(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM WaitlistEntry WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Read(cmd).SingleOrDefault();
    }

    public List<WaitlistEntry> All()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM WaitlistEntry ORDER BY created_utc, id";
        return Read(cmd);
    }

    /// <summary>Active → Notified; false when the entry is no longer Active.</summary>
    public bool MarkNotified(string id, DateTimeOffset nowUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE WaitlistEntry SET status = $n, notified_utc = $t WHERE id = $id AND status = $a";
        cmd.Parameters.AddWithValue("$n", WaitlistStatus.Notified);
        cmd.Parameters.AddWithValue("$t", Instant(nowUtc));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$a", WaitlistStatus.Active);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Retention (ADR-004): deletes every entry whose last date ended more than 24 h ago. Returns the count.</summary>
    public int PurgeExpired(DateTimeOffset nowUtc)
    {
        using var c = Open();
        var expired = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, dates FROM WaitlistEntry";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var dates = ParseDates(r.GetString(1));
                // No parseable date: nothing to wait for — purge as well.
                if (dates.Count == 0 || Salon.ToUtc(dates[^1].AddDays(1), TimeOnly.MinValue) + TimeSpan.FromHours(24) < nowUtc)
                    expired.Add(r.GetString(0));
            }
        }
        foreach (var id in expired)
        {
            using var del = c.CreateCommand();
            del.CommandText = "DELETE FROM WaitlistEntry WHERE id = $id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
        }
        return expired.Count;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<WaitlistEntry> Read(SqliteCommand cmd)
    {
        var result = new List<WaitlistEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var notified = r.IsDBNull(r.GetOrdinal("notified_utc")) ? (DateTimeOffset?)null : ParseInstant(r.GetString(r.GetOrdinal("notified_utc")));
            result.Add(new WaitlistEntry(
                r.GetString(r.GetOrdinal("id")),
                r.GetString(r.GetOrdinal("service_key")),
                r.GetString(r.GetOrdinal("preferred_days_text")),
                Enum.TryParse<PartOfDay>(r.GetString(r.GetOrdinal("part_of_day")), out var part) ? part : PartOfDay.Any,
                ParseDates(r.GetString(r.GetOrdinal("dates"))),
                r.GetString(r.GetOrdinal("first_name")),
                r.GetString(r.GetOrdinal("phone_e164")),
                ParseInstant(r.GetString(r.GetOrdinal("created_utc"))),
                r.GetString(r.GetOrdinal("status")),
                notified));
        }
        return result;
    }

    private static List<DateOnly> ParseDates(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => DateOnly.TryParseExact(d, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : (DateOnly?)null)
            .OfType<DateOnly>().Order().ToList();

    private static string Instant(DateTimeOffset t) => t.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseInstant(string s) =>
        DateTimeOffset.ParseExact(s, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
}

/// <summary>Concrete open dates for the caller's words, with a description for speech and for staff.</summary>
public sealed record WaitlistWindow(IReadOnlyList<DateOnly> Dates, string Description, string Spoken);

/// <summary>
/// Parses the caller's preferred days (ADR-014: the server parses <c>preferred_days</c>) into open dates within the
/// next <see cref="WindowDays"/> days, today included: weekday lists ("Tuesday or Thursday", "Saturdays"),
/// "weekdays", "weekends", "this week", "next week", "any day"/"anytime", combinations ("Tuesdays next week"),
/// and single or listed dates via <see cref="DateHintParser"/> ("next Friday", "12 November or 14 November").
/// </summary>
public static partial class WaitlistWindowParser
{
    public const int WindowDays = 14;

    private static readonly DayOfWeek[] OpenWeekdays =
        [DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public static WaitlistWindow? Parse(string? text, DateOnly today)
    {
        var t = Spaces().Replace(Punctuation().Replace((text ?? "").ToLowerInvariant(), " "), " ").Trim();
        // Part-of-day words belong to part_of_day, not to the days.
        t = Spaces().Replace(PartWords().Replace(t, " "), " ").Trim();
        if (t.Length == 0) return null;

        var first = today;
        var last = today.AddDays(WindowDays - 1);
        IEnumerable<DateOnly> All(DateOnly from, DateOnly to)
        {
            for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
        }
        WaitlistWindow? Result(IEnumerable<DateOnly> candidates, string description, string spoken)
        {
            var dates = candidates.Where(d => d >= first && d <= last && Salon.IsOpen(d)).Distinct().Order().ToList();
            return dates.Count == 0 ? null : new WaitlistWindow(dates, description, spoken);
        }

        // Explicit dates ("10 November", "Tuesday 10 November or 12 November") win over weekday names.
        if (t.Any(char.IsDigit) && SingleDates(DayName().Replace(t, " "), today, Result) is { } explicitDates)
            return explicitDates;

        if (AnyDay().IsMatch(t))
            return Result(All(first, last), "any day", "on any day");

        // Week restriction.
        var (from, to, weekText) = (first, last, "");
        var mondayThisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (NextWeek().IsMatch(t)) (from, to, weekText) = (mondayThisWeek.AddDays(7), mondayThisWeek.AddDays(13), "next week");
        else if (ThisWeek().IsMatch(t)) (from, to, weekText) = (today, mondayThisWeek.AddDays(6), "this week");

        // Weekday set.
        var days = new List<DayOfWeek>();
        var label = "";
        if (Weekdays().IsMatch(t)) { days.AddRange(OpenWeekdays); label = "weekdays"; }
        else if (Weekends().IsMatch(t)) { days.Add(DayOfWeek.Saturday); label = "Saturdays"; }
        var named = DayName().Matches(t).Select(m => (Mod: m.Groups["mod"].Value, Day: ToDay(m.Groups["day"].Value), Plural: m.Groups["s"].Success)).ToList();

        // A single "next/this/coming Tuesday" (no plural, no list, no week phrase) is one date.
        if (named.Count == 1 && named[0].Mod.Length > 0 && !named[0].Plural && label.Length == 0 && weekText.Length == 0)
            return SingleDates(t, today, Result);

        foreach (var n in named) if (!days.Contains(n.Day)) days.Add(n.Day);
        if (days.Count > 0)
        {
            if (label.Length == 0) label = JoinOr(days.Select(d => $"{d}s").ToList());
            var description = weekText.Length == 0 ? label : $"{label} {weekText}";
            return Result(All(from, to).Where(d => days.Contains(d.DayOfWeek)), description, $"on {description}");
        }
        if (weekText.Length > 0)
            return Result(All(from, to), weekText, weekText);

        return SingleDates(t, today, Result);
    }

    private static WaitlistWindow? SingleDates(string t, DateOnly today, Func<IEnumerable<DateOnly>, string, string, WaitlistWindow?> result)
    {
        var parts = ListSeparator().Split(t).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var dates = new List<DateOnly>();
        foreach (var p in parts)
        {
            if (DateHintParser.Parse(p, today) is not { } d) return null;
            dates.Add(d);
        }
        var valid = dates.Where(d => d >= today && d < today.AddDays(WindowDays) && Salon.IsOpen(d)).Distinct().Order().ToList();
        if (valid.Count == 0) return null;
        var description = JoinOr(valid.Select(Salon.SpokenDay).ToList());
        return result(valid, description, $"on {description}");
    }

    private static DayOfWeek ToDay(string s) =>
        Enum.GetValues<DayOfWeek>().First(d => d.ToString().ToLowerInvariant().StartsWith(s[..3]));

    private static string JoinOr(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];

    [GeneratedRegex(@"[,.;!?/]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(mornings?|afternoons?|evenings?|after work|lunch ?time|please|preferably|ideally)\b")]
    private static partial Regex PartWords();

    [GeneratedRegex(@"^(any ?day|any ?time|any|whenever|whenever you can|anything|every ?day|any day is fine|i'm flexible|flexible)$")]
    private static partial Regex AnyDay();

    [GeneratedRegex(@"\bnext week\b")]
    private static partial Regex NextWeek();

    [GeneratedRegex(@"\b(this|rest of the) week\b")]
    private static partial Regex ThisWeek();

    [GeneratedRegex(@"\b(weekdays?|during the week|work ?days?)\b")]
    private static partial Regex Weekdays();

    [GeneratedRegex(@"\b(weekends?)\b")]
    private static partial Regex Weekends();

    [GeneratedRegex(@"(?:\b(?<mod>this|next|coming)\s+)?\b(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues?|wed|thu(?:rs?)?|fri|sat|sun)(?<s>s)?\b")]
    private static partial Regex DayName();

    [GeneratedRegex(@"\s*(?:\bor\b|\band\b|&)\s*")]
    private static partial Regex ListSeparator();
}

/// <summary>The join-waitlist tool (ADR-007, ADR-014).</summary>
public sealed class WaitlistService(WaitlistStore store, TimeProvider clock)
{
    public const string InvalidWindowSay =
        "I can put you on the waiting list for days within the next two weeks when we're open, Tuesday to Saturday. Which days would suit you, for example Tuesdays or Thursdays?";

    public ToolResult Join(string? serviceText, string? preferredDays, string? partOfDay, string? firstName, string? callerPhone)
    {
        if (ServiceMatcher.Match(serviceText) is not { } service)
            return ToolResult.Fail("UNKNOWN_SERVICE", $"We offer {Salon.ServiceList()}. Which would you like?");
        if (Caller.NormalisePhone(callerPhone) is not { } phone)
            return ToolResult.Fail("PHONE_REQUIRED", "May I have your phone number, please, including the area code?");
        if (Caller.NormaliseName(firstName) is not { } name)
            return ToolResult.Fail("INVALID_NAME", "Could you tell me your first name again, please?");

        var now = clock.GetUtcNow();
        if (WaitlistWindowParser.Parse(preferredDays, Scheduler.Today(now)) is not { } window)
            return ToolResult.Fail("INVALID_WINDOW", InvalidWindowSay);

        var part = PartOfDayParser.Parse(partOfDay);
        if (part == PartOfDay.Any) part = PartOfDayParser.Parse(preferredDays);

        var id = store.Upsert(new WaitlistEntry(Guid.NewGuid().ToString("N"), service.Key, window.Description, part, window.Dates,
            name, phone, now, WaitlistStatus.Active, null));
        var partText = part == PartOfDay.Any ? "" : $" in the {part.ToString().ToLowerInvariant()}";
        return new ToolResult
        {
            Ok = true,
            WaitlistId = id,
            Say = $"You're on the waiting list for {service.WithArticle} {window.Spoken}{partText}. The salon team will call you if a slot opens up.",
        };
    }
}

/// <summary>Waitlist retention (ADR-004): purge at startup and hourly.</summary>
public sealed class WaitlistRetention(WaitlistStore store, TimeProvider clock, ILogger<WaitlistRetention> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                var purged = store.PurgeExpired(clock.GetUtcNow());
                if (purged > 0) log.LogInformation("waitlist retention purged {Count} entries", purged);
            }
            catch (Exception e)
            {
                log.LogWarning("waitlist retention failed: {Error}", e.GetType().Name);
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
