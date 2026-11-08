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
    [InlineData("next week", "2026-11-17")]   // Monday 16th closed → Tuesday
    public void Parses(string hint, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), DateHintParser.Parse(hint, Monday));

    [Theory]
    [InlineData("")]
    [InlineData("sometime soon")]
    [InlineData("31 February")]
    [InlineData("whenever you have time")]
    public void Unclear(string hint) => Assert.Null(DateHintParser.Parse(hint, Monday));
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
