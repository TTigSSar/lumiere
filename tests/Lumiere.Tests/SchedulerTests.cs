using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

public class SchedulerTests
{
    private static readonly DateOnly Tue = new(2026, 11, 10), Sat = new(2026, 11, 14);

    private static List<DateTimeOffset> Free(DateOnly date, string service, PartOfDay part = PartOfDay.Any,
        DateTimeOffset? now = null, params CalendarEvent[] busy) =>
        Scheduler.FreeStarts(date, Svc(service), part, now ?? MondayMorning, busy);

    [Fact]
    public void Tuesday_runs_0900_to_last_start_fitting_1830()
    {
        var free = Free(Tue, "mens_cut");
        Assert.Equal(Zurich(2026, 11, 10, 9, 0), free[0]);
        Assert.Equal(Zurich(2026, 11, 10, 18, 0), free[^1]);
        Assert.Equal(37, free.Count); // 09:00..18:00 every 15 min
    }

    [Fact]
    public void Saturday_runs_0830_to_1600()
    {
        Assert.Equal(Zurich(2026, 11, 14, 8, 30), Free(Sat, "mens_cut")[0]);
        Assert.Equal(Zurich(2026, 11, 14, 15, 30), Free(Sat, "mens_cut")[^1]);
        Assert.Equal(Zurich(2026, 11, 14, 15, 0), Free(Sat, "womens_cut")[^1]);
    }

    [Fact]
    public void Appointment_must_end_by_closing()
    {
        Assert.Equal(Zurich(2026, 11, 10, 16, 0), Free(Tue, "highlights")[^1]); // 150 min → 18:30
        Assert.Equal(Zurich(2026, 11, 10, 16, 30), Free(Tue, "full_colour")[^1]); // 120 min → 18:30
    }

    [Fact]
    public void Ten_minute_buffer_before_and_after_existing_events()
    {
        var busy = Busy(Zurich(2026, 11, 10, 10, 0), Zurich(2026, 11, 10, 11, 0));
        var free = Free(Tue, "mens_cut", busy: busy);
        Assert.Contains(Zurich(2026, 11, 10, 9, 15), free);      // ends 09:45, 15 min before
        Assert.DoesNotContain(Zurich(2026, 11, 10, 9, 30), free); // ends 10:00, no buffer
        Assert.DoesNotContain(Zurich(2026, 11, 10, 11, 0), free); // within 10 min after end
        Assert.Contains(Zurich(2026, 11, 10, 11, 15), free);
    }

    [Fact]
    public void Two_hour_lead_time()
    {
        var free = Free(Tue, "mens_cut", now: Zurich(2026, 11, 10, 10, 5));
        Assert.Equal(Zurich(2026, 11, 10, 12, 15), free[0]);
    }

    [Theory]
    [InlineData(2026, 11, 15)] // Sunday
    [InlineData(2026, 11, 16)] // Monday
    [InlineData(2026, 12, 25)] // Christmas, a Friday
    [InlineData(2027, 3, 26)]  // Good Friday
    [InlineData(2027, 5, 6)]   // Ascension, a Thursday
    public void Closed_days_have_no_slots(int y, int m, int d)
    {
        Assert.False(Salon.IsOpen(new DateOnly(y, m, d)));
        Assert.Empty(Free(new DateOnly(y, m, d), "mens_cut", now: Zurich(2026, 11, 20, 8, 0)));
    }

    [Fact]
    public void Horizon_is_30_days()
    {
        Assert.NotEmpty(Free(new DateOnly(2026, 12, 9), "mens_cut"));  // today + 30, Wednesday
        Assert.Empty(Free(new DateOnly(2026, 12, 10), "mens_cut"));    // today + 31, Thursday
    }

    [Theory]
    [InlineData(PartOfDay.Morning, 9, 0, 11, 45)]
    [InlineData(PartOfDay.Afternoon, 12, 0, 16, 45)]
    [InlineData(PartOfDay.Evening, 17, 0, 18, 0)]
    public void Part_of_day_filter(PartOfDay part, int fh, int fm, int lh, int lm)
    {
        var free = Free(Tue, "mens_cut", part);
        Assert.Equal(Zurich(2026, 11, 10, fh, fm), free[0]);
        Assert.Equal(Zurich(2026, 11, 10, lh, lm), free[^1]);
    }

    [Fact]
    public void Three_slots_are_spread_across_the_day()
    {
        var picked = Scheduler.Spread(Free(Tue, "mens_cut"));
        Assert.Equal(3, picked.Count);
        Assert.Equal(Zurich(2026, 11, 10, 9, 0), picked[0]);
        Assert.Equal(Zurich(2026, 11, 10, 18, 0), picked[2]);
        Assert.True(picked[1] - picked[0] >= TimeSpan.FromMinutes(30) && picked[2] - picked[1] >= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Spread_avoids_adjacent_starts_when_possible()
    {
        // Free only 09:00–10:30 for a 30-min service: starts 09:00..10:00 (5 candidates).
        var busy = Busy(Zurich(2026, 11, 10, 10, 40), Zurich(2026, 11, 10, 18, 30));
        var picked = Scheduler.Spread(Free(Tue, "mens_cut", busy: busy));
        Assert.Equal([Zurich(2026, 11, 10, 9, 0), Zurich(2026, 11, 10, 9, 30), Zurich(2026, 11, 10, 10, 0)], picked);
    }

    [Fact]
    public void Dst_spring_forward_week_gives_cest_instants()
    {
        // Clock on Saturday 27 March 2027 (CET); DST starts Sunday 28 March; Tuesday 30 March is CEST (+02:00).
        var free = Free(new DateOnly(2027, 3, 30), "mens_cut", now: Zurich(2027, 3, 27, 7, 0));
        Assert.Equal(new DateTimeOffset(2027, 3, 30, 7, 0, 0, TimeSpan.Zero), free[0]);
        Assert.Equal("Tuesday, 30 March at 09:00", Salon.SpokenLabel(free[0]));
    }

    [Fact]
    public void Dst_fall_back_week_gives_cet_instants()
    {
        // Clock on Friday 29 October 2027 (CEST); DST ends Sunday 31 October; Tuesday 2 November is CET (+01:00).
        var free = Free(new DateOnly(2027, 11, 2), "mens_cut", now: Zurich(2027, 10, 29, 7, 0));
        Assert.Equal(new DateTimeOffset(2027, 11, 2, 8, 0, 0, TimeSpan.Zero), free[0]);
        Assert.Equal("Tuesday, 2 November at 09:00", Salon.SpokenLabel(free[0]));
    }

    [Theory]
    [InlineData("men's cut", "mens_cut")]
    [InlineData("haircut for men", "mens_cut")]
    [InlineData("Women's cut & style", "womens_cut")]
    [InlineData("kids haircut", "childrens_cut")]
    [InlineData("Blow-dry", "blow_dry")]
    [InlineData("colour", "full_colour")]
    [InlineData("hair color", "full_colour")]
    [InlineData("HIGHLIGHTS", "highlights")]
    [InlineData("mens_cut", "mens_cut")]
    public void Service_matching_is_tolerant(string input, string key) => Assert.Equal(key, ServiceMatcher.Match(input)?.Key);

    [Theory]
    [InlineData("perm")]
    [InlineData("haircut")]
    [InlineData("")]
    public void Unknown_service(string input) => Assert.Null(ServiceMatcher.Match(input));
}
