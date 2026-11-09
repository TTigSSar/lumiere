using Lumiere.Api;
using static Lumiere.Tests.T;

namespace Lumiere.Tests;

/// <summary>A WaitlistStore in its own temp directory, deleted on dispose.</summary>
public sealed class TempStore : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "lumiere-tests-" + Guid.NewGuid().ToString("N"));
    public WaitlistStore Store { get; }
    public TempStore() => Store = new WaitlistStore(Dir);
    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class WaitlistTests : IDisposable
{
    private readonly TempStore _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly DateOnly Today = new(2026, 11, 9); // Monday, salon closed

    private static string[] Dates(WaitlistWindow? w) => w!.Dates.Select(d => d.ToString("MM-dd")).ToArray();

    [Theory]
    [InlineData("Tuesday or Thursday", "Tuesdays or Thursdays", "11-10,11-12,11-17,11-19")]
    [InlineData("tuesdays and thursdays", "Tuesdays or Thursdays", "11-10,11-12,11-17,11-19")]
    [InlineData("Saturdays", "Saturdays", "11-14,11-21")]
    [InlineData("weekends", "Saturdays", "11-14,11-21")]
    [InlineData("weekdays", "weekdays", "11-10,11-11,11-12,11-13,11-17,11-18,11-19,11-20")]
    [InlineData("this week", "this week", "11-10,11-11,11-12,11-13,11-14")]
    [InlineData("next week", "next week", "11-17,11-18,11-19,11-20,11-21")]
    [InlineData("Fridays next week", "Fridays next week", "11-20")]
    [InlineData("any day", "any day", "11-10,11-11,11-12,11-13,11-14,11-17,11-18,11-19,11-20,11-21")]
    [InlineData("anytime", "any day", "11-10,11-11,11-12,11-13,11-14,11-17,11-18,11-19,11-20,11-21")]
    [InlineData("Wednesday mornings", "Wednesdays", "11-11,11-18")]
    [InlineData("next Friday", "Friday, 13 November", "11-13")]
    [InlineData("12 November", "Thursday, 12 November", "11-12")]
    [InlineData("12 November or 14 November", "Thursday, 12 November or Saturday, 14 November", "11-12,11-14")]
    [InlineData("Thursday 12 November", "Thursday, 12 November", "11-12")]
    [InlineData("Thursday, 12 November", "Thursday, 12 November", "11-12")]
    [InlineData("Thursday the 12th of November", "Thursday, 12 November", "11-12")]
    [InlineData("Thursday November 12", "Thursday, 12 November", "11-12")]
    [InlineData("Thu 12 Nov", "Thursday, 12 November", "11-12")]
    [InlineData("Thurs, Nov 12th", "Thursday, 12 November", "11-12")]
    [InlineData("12 November afternoon", "Thursday, 12 November", "11-12")]
    [InlineData("Thursday 12 November in the morning", "Thursday, 12 November", "11-12")]
    [InlineData("Friday 12 November", "Thursday, 12 November", "11-12")] // stated weekday wrong: the date decides
    [InlineData("tomorrow", "Tuesday, 10 November", "11-10")]
    public void Preferred_days_parse_to_open_dates_within_two_weeks(string words, string description, string dates)
    {
        var w = WaitlistWindowParser.Parse(words, Today);
        Assert.NotNull(w);
        Assert.Equal(description, w!.Description);
        Assert.Equal(dates.Split(','), Dates(w));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("sometime soon")]
    [InlineData("Mondays")]           // closed
    [InlineData("Sunday or Monday")]  // closed
    [InlineData("1 December")]        // beyond 14 days
    [InlineData("yesterday")]         // past
    [InlineData("25 December")]       // holiday and beyond
    public void Unusable_preferred_days_are_null(string? words) => Assert.Null(WaitlistWindowParser.Parse(words, Today));

    [Fact]
    public void Join_stores_an_Active_entry_and_says_the_window()
    {
        var svc = new WaitlistService(_db.Store, new FakeClock(MondayMorning));
        var r = svc.Join("men's cut", "Tuesday or Thursday", "morning", "Anna", "079 123 45 67");

        Assert.True(r.Ok);
        Assert.Equal("You're on the waiting list for a men's cut on Tuesdays or Thursdays in the morning. The salon team will call you if a slot opens up.", r.Say);
        var e = Assert.Single(_db.Store.All());
        Assert.Equal((r.WaitlistId, "mens_cut", "Tuesdays or Thursdays", PartOfDay.Morning, "Anna", "+41791234567", WaitlistStatus.Active),
            (e.Id, e.ServiceKey, e.PreferredDaysText, e.PartOfDay, e.FirstName, e.PhoneE164, e.Status));
        Assert.Equal([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 19)], e.Dates);
    }

    [Fact]
    public void Join_again_for_same_phone_and_service_updates_the_same_entry()
    {
        var clock = new FakeClock(MondayMorning);
        var svc = new WaitlistService(_db.Store, clock);
        var first = svc.Join("men's cut", "Tuesdays", null, "Anna", "079 123 45 67");
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = svc.Join("mens cut", "Saturdays", "afternoon", "Anna", "+41 79 123 45 67");
        var other = svc.Join("blow-dry", "Saturdays", null, "Anna", "079 123 45 67");

        Assert.Equal(first.WaitlistId, second.WaitlistId);
        Assert.NotEqual(first.WaitlistId, other.WaitlistId);
        var e = _db.Store.Get(first.WaitlistId!)!;
        Assert.Equal(("Saturdays", PartOfDay.Afternoon, MondayMorning), (e.PreferredDaysText, e.PartOfDay, e.CreatedUtc));
        Assert.Equal(2, _db.Store.All().Count);
    }

    [Theory]
    [InlineData("perm", "Tuesdays", "Anna", "079 123 45 67", "UNKNOWN_SERVICE")]
    [InlineData("men's cut", "Mondays", "Anna", "079 123 45 67", "INVALID_WINDOW")]
    [InlineData("men's cut", "", "Anna", "079 123 45 67", "INVALID_WINDOW")]
    [InlineData("men's cut", "Tuesdays", "", "079 123 45 67", "INVALID_NAME")]
    [InlineData("men's cut", "Tuesdays", "Anna", null, "PHONE_REQUIRED")]
    public void Join_business_errors(string service, string days, string name, string? phone, string code)
    {
        var r = new WaitlistService(_db.Store, new FakeClock(MondayMorning)).Join(service, days, null, name, phone);
        Assert.Equal((code, false), (r.ErrorCode, r.Ok));
        Assert.False(string.IsNullOrWhiteSpace(r.Say));
        Assert.Empty(_db.Store.All());
    }

    [Fact]
    public void Retention_deletes_entries_whose_last_date_ended_more_than_24h_ago()
    {
        WaitlistEntry Entry(string id, params DateOnly[] dates) =>
            new(id, "mens_cut", "x", PartOfDay.Any, dates, "Anna", "+4179000000" + id, MondayMorning, WaitlistStatus.Active, null);
        _db.Store.Upsert(Entry("1", new DateOnly(2026, 11, 10)));
        _db.Store.Upsert(Entry("2", new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 12)));
        _db.Store.MarkNotified("2", MondayMorning);

        // 10 Nov ends at 11 Nov 00:00; +24 h = 12 Nov 00:00.
        Assert.Equal(0, _db.Store.PurgeExpired(Zurich(2026, 11, 11, 23, 59)));
        Assert.Equal(1, _db.Store.PurgeExpired(Zurich(2026, 11, 12, 0, 1)));
        Assert.Equal(["2"], _db.Store.All().Select(e => e.Id));
        Assert.Equal(1, _db.Store.PurgeExpired(Zurich(2026, 11, 14, 0, 1)));
        Assert.Empty(_db.Store.All());
    }

    [Fact]
    public void Schema_creation_is_repeatable_and_data_survives_reopen()
    {
        var svc = new WaitlistService(_db.Store, new FakeClock(MondayMorning));
        var id = svc.Join("men's cut", "Tuesdays", null, "Anna", "079 123 45 67").WaitlistId;
        var reopened = new WaitlistStore(_db.Dir);
        Assert.Equal(id, Assert.Single(reopened.All()).Id);
        Assert.True(File.Exists(Path.Combine(_db.Dir, WaitlistStore.FileName)));
    }
}
