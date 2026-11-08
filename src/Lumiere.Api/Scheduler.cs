namespace Lumiere.Api;

/// <summary>Pure slot rules (ADR-009): hours, 15-min grid, buffers, lead time, horizon, part of day, slot choice.</summary>
public static class Scheduler
{
    public static DateOnly Today(DateTimeOffset nowUtc) => Salon.LocalDate(nowUtc);

    public static bool WithinHorizon(DateOnly date, DateTimeOffset nowUtc) =>
        date >= Today(nowUtc) && date <= Today(nowUtc).AddDays(Salon.HorizonDays);

    /// <summary>All free grid starts for the service on a local date, in order.</summary>
    public static List<DateTimeOffset> FreeStarts(DateOnly date, Service service, PartOfDay part, DateTimeOffset nowUtc,
        IReadOnlyList<CalendarEvent> busy)
    {
        var result = new List<DateTimeOffset>();
        if (Salon.Hours(date) is not { } hours || !WithinHorizon(date, nowUtc)) return result;
        var (open, close) = hours;
        for (var t = open; t.Add(service.Duration) <= close && t >= open; t = t.Add(Salon.Grid))
        {
            if (!PartOfDayParser.Matches(part, t)) continue;
            var start = Salon.ToUtc(date, t);
            if (Fits(start, service, nowUtc, busy)) result.Add(start);
        }
        return result;
    }

    /// <summary>True when a slot is still bookable now: open hours, lead time, horizon, buffers around busy events.</summary>
    public static bool Fits(DateTimeOffset startUtc, Service service, DateTimeOffset nowUtc, IReadOnlyList<CalendarEvent> busy)
    {
        var date = Salon.LocalDate(startUtc);
        if (Salon.Hours(date) is not { } hours || !WithinHorizon(date, nowUtc)) return false;
        var (open, close) = hours;
        var localStart = TimeOnly.FromDateTime(Salon.ToLocal(startUtc).DateTime);
        if (localStart < open || Salon.ToUtc(date, close) < startUtc + service.Duration) return false;
        if (startUtc < nowUtc + Salon.LeadTime) return false;
        var endUtc = startUtc + service.Duration;
        return !busy.Any(e => startUtc < e.EndUtc + Salon.Buffer && endUtc + Salon.Buffer > e.StartUtc);
    }

    /// <summary>Up to 3 starts spread across the candidates (first, middle, last), preferably ≥30 min apart.</summary>
    public static List<DateTimeOffset> Spread(IReadOnlyList<DateTimeOffset> candidates)
    {
        var n = candidates.Count;
        if (n == 0) return [];
        var preferred = new List<int>();
        foreach (var target in new[] { 0, n - 1, (n - 1) / 2 })
        {
            // Nearest candidate to each target that keeps the preferred gap to those already chosen.
            var pick = Enumerable.Range(0, n).OrderBy(i => Math.Abs(i - target))
                .FirstOrDefault(i => preferred.All(p => (candidates[p] - candidates[i]).Duration() >= Salon.PreferredGap), -1);
            if (pick >= 0) preferred.Add(pick);
        }
        return Pick(candidates, preferred.Concat(Enumerable.Range(0, n)).ToList(), Salon.MaxSlots);
    }

    /// <summary>Up to <paramref name="count"/> earliest starts, preferably ≥30 min apart.</summary>
    public static List<DateTimeOffset> Earliest(IReadOnlyList<DateTimeOffset> candidates, int count) =>
        Pick(candidates, Enumerable.Range(0, candidates.Count).ToList(), count);

    /// <summary>Up to <paramref name="count"/> starts closest to <paramref name="target"/>, preferably ≥30 min apart.</summary>
    public static List<DateTimeOffset> Nearest(IReadOnlyList<DateTimeOffset> candidates, DateTimeOffset target, int count) =>
        Pick(candidates, Enumerable.Range(0, candidates.Count).OrderBy(i => (candidates[i] - target).Duration()).ToList(), count);

    private static List<DateTimeOffset> Pick(IReadOnlyList<DateTimeOffset> candidates, List<int> order, int count)
    {
        var chosen = new List<DateTimeOffset>();
        foreach (var i in order)
            if (chosen.Count < count && chosen.All(c => (c - candidates[i]).Duration() >= Salon.PreferredGap))
                chosen.Add(candidates[i]);
        foreach (var i in order) // not enough well-spaced times: fill with the rest
            if (chosen.Count < count && !chosen.Contains(candidates[i]))
                chosen.Add(candidates[i]);
        chosen.Sort();
        return chosen;
    }
}
