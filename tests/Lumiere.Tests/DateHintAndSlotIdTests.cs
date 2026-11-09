using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

public class DateHintTests
{
    private static readonly DateOnly Monday = new(2026, 11, 9);

    [Theory]
    [InlineData("today", "2026-11-09")]
    [InlineData("Tomorrow", "2026-11-10")]
    [InlineData("day after tomorrow", "2026-11-11")]
    [InlineData("the day after tomorrow", "2026-11-11")]
    [InlineData("thursday", "2026-11-12")]
    [InlineData("this Thursday", "2026-11-12")]
    [InlineData("next thursday", "2026-11-12")]
    [InlineData("next Thursday afternoon", "2026-11-12")]
    [InlineData("on Saturday", "2026-11-14")]
    [InlineData("monday", "2026-11-09")]      // plain = including today
    [InlineData("next monday", "2026-11-16")] // next = strictly after today
    [InlineData("2026-11-12", "2026-11-12")]
    [InlineData("12 November", "2026-11-12")]
    [InlineData("November 12", "2026-11-12")]
    [InlineData("12th of November", "2026-11-12")]
    [InlineData("the 12th of November", "2026-11-12")]
    [InlineData("November 12th, 2026", "2026-11-12")]
    [InlineData("2 January", "2027-01-02")]   // past day this year → next year
    [InlineData("yesterday", "2026-11-08")]   // past relative days → past date (DATE_IN_PAST upstream)
    [InlineData("day before yesterday", "2026-11-07")]
    [InlineData("last thursday", "2026-11-05")]
    [InlineData("last Monday", "2026-11-02")]
    [InlineData("next week", "2026-11-17")]   // Monday 16th closed → Tuesday
    public void Parses(string hint, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), DateHintParser.Parse(hint, Monday));

    [Theory]
    [InlineData("")]
    [InlineData("sometime soon")]
    [InlineData("31 February")]
    [InlineData("whenever you have time")]
    public void Unclear(string hint) => Assert.Null(DateHintParser.Parse(hint, Monday));

    [Theory]
    [InlineData("Thursday 12 November")]
    [InlineData("Thursday, 12 November")]
    [InlineData("Thursday the 12th of November")]
    [InlineData("Thursday November 12")]
    [InlineData("Thu 12 Nov")]
    [InlineData("Thu. 12 Nov.")]
    [InlineData("Thurs, Nov 12th")]
    [InlineData("this Thursday 12 November")]
    [InlineData("Thursday 12 November 2026")]
    [InlineData("12 November afternoon")]
    [InlineData("Thursday 12 November in the morning")]
    [InlineData("12 Nov")]
    [InlineData("Nov 12")]
    public void Weekday_with_date_and_abbreviations_parse_to_the_date(string hint)
    {
        var r = DateHintParser.Interpret(hint, Monday);
        Assert.Equal(new DateOnly(2026, 11, 12), r.Date);
        Assert.Null(r.MismatchedWeekday);
    }

    [Theory]
    [InlineData("Tue 10 Nov", "2026-11-10")]
    [InlineData("Wed 11 Nov", "2026-11-11")]
    [InlineData("Fri 13 Nov", "2026-11-13")]
    [InlineData("Sat 14 Nov", "2026-11-14")]
    [InlineData("Mon 16 Nov", "2026-11-16")]
    [InlineData("Sun 15 Nov", "2026-11-15")]
    [InlineData("Tue 1 Dec", "2026-12-01")]
    [InlineData("Sat 2 Jan", "2027-01-02")]
    [InlineData("Sept 15", "2027-09-15")]
    public void Weekday_and_month_abbreviations(string hint, string expected)
    {
        var r = DateHintParser.Interpret(hint, Monday);
        Assert.Equal(DateOnly.Parse(expected), r.Date);
        Assert.Null(r.MismatchedWeekday);
    }

    [Theory]
    [InlineData("Friday 12 November", DayOfWeek.Friday)]
    [InlineData("Fri, Nov 12", DayOfWeek.Friday)]
    [InlineData("Tuesday the 12th of November", DayOfWeek.Tuesday)]
    public void Weekday_that_does_not_match_the_date_is_reported_and_the_date_decides(string hint, DayOfWeek stated)
    {
        var r = DateHintParser.Interpret(hint, Monday);
        Assert.Equal(new DateOnly(2026, 11, 12), r.Date);
        Assert.Equal(stated, r.MismatchedWeekday);
        Assert.Equal(new DateOnly(2026, 11, 12), DateHintParser.Parse(hint, Monday));
    }

    [Theory]
    [InlineData("Someday 12 November")]
    [InlineData("Thursday sometime")]
    [InlineData("Thursday 31 February")]
    public void Weekday_with_unclear_date_is_unclear(string hint) => Assert.Null(DateHintParser.Interpret(hint, Monday).Date);
}

public class SlotIdTests
{
    private static readonly DateTimeOffset Start = Zurich(2026, 11, 10, 14, 30);

    [Fact]
    public void Roundtrip()
    {
        var ids = new SlotIds(Key, new FakeClock(MondayMorning));
        var slot = ids.Verify(ids.Create(Start, "mens_cut"));
        Assert.NotNull(slot);
        Assert.Equal(Start, slot.Value.StartUtc);
        Assert.Equal("mens_cut", slot.Value.Service.Key);
    }

    [Fact]
    public void Tampered_payload_or_mac_is_rejected()
    {
        var ids = new SlotIds(Key, new FakeClock(MondayMorning));
        var id = ids.Create(Start, "mens_cut");
        var forged = new SlotIds(Key, new FakeClock(MondayMorning)).Create(Start.AddHours(1), "highlights").Split('.')[0];
        Assert.Null(ids.Verify(forged + "." + id.Split('.')[1]));
        Assert.Null(ids.Verify(id[..^2] + (id[^2] == 'A' ? "BB" : "AA")));
        Assert.Null(new SlotIds("other-key"u8.ToArray(), new FakeClock(MondayMorning)).Verify(id));
        Assert.Null(ids.Verify("garbage"));
        Assert.Null(ids.Verify(null));
    }

    [Fact]
    public void Expired_after_30_minutes()
    {
        var clock = new FakeClock(MondayMorning);
        var ids = new SlotIds(Key, clock);
        var id = ids.Create(Start, "mens_cut");
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.NotNull(ids.Verify(id));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(ids.Verify(id));
    }
}
