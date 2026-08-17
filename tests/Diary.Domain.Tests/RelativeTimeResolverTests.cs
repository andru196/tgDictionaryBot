using Diary.Domain;
using Shouldly;

namespace Diary.Domain.Tests;

public sealed class RelativeTimeResolverTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone(
        "test-msk", TimeSpan.FromHours(3), "Тест +3", "Тест +3");

    private readonly RelativeTimeResolver _resolver = new(Moscow);

    /// <summary>21:40 по Москве 4 августа.</summary>
    private static DateTimeOffset SentAt => new(2026, 8, 4, 18, 40, 0, TimeSpan.Zero);

    [Fact]
    public void ПустоеУказание_ДаётВремяОтправки()
    {
        var (at, certainty) = _resolver.Resolve(RelativeTimeSpec.Now, SentAt);

        at.ShouldBe(SentAt);
        certainty.ShouldBe(TimeCertainty.Exact);
    }

    [Fact]
    public void ВчераВечером_ДаётПредыдущийДень()
    {
        var spec = new RelativeTimeSpec(DayOffset: -1, PartOfDay: PartOfDay.Evening);

        var (at, certainty) = _resolver.Resolve(spec, SentAt);

        var local = TimeZoneInfo.ConvertTime(at, Moscow);
        local.Date.ShouldBe(new DateTime(2026, 8, 3));
        local.Hour.ShouldBe(20);
        certainty.ShouldBe(TimeCertainty.Approximate);
    }

    [Fact]
    public void ЧасаДваНазад_ВычитаетсяОтОтправки()
    {
        var (at, certainty) = _resolver.Resolve(new RelativeTimeSpec(HoursAgo: 2), SentAt);

        at.ShouldBe(SentAt.AddHours(-2));
        certainty.ShouldBe(TimeCertainty.Resolved);
    }

    [Fact]
    public void ТочноеВремя_БерётсяКакЕсть()
    {
        var spec = new RelativeTimeSpec(LocalTime: new TimeOnly(7, 0));

        var (at, certainty) = _resolver.Resolve(spec, SentAt);

        TimeZoneInfo.ConvertTime(at, Moscow).Hour.ShouldBe(7);
        certainty.ShouldBe(TimeCertainty.Resolved);
    }

    [Fact]
    public void ВечеромСказанноеНочью_ОтноситсяКоВчерашнемуВечеру()
    {
        // 01:30 по Москве 5 августа: «вечером» — это вчера, а не сегодняшний вечер,
        // который ещё не наступил.
        var sentAtNight = new DateTimeOffset(2026, 8, 4, 22, 30, 0, TimeSpan.Zero);

        var (at, certainty) = _resolver.Resolve(
            new RelativeTimeSpec(PartOfDay: PartOfDay.Evening), sentAtNight);

        at.ShouldBeLessThan(sentAtNight);
        TimeZoneInfo.ConvertTime(at, Moscow).Date.ShouldBe(new DateTime(2026, 8, 4));
        certainty.ShouldBe(TimeCertainty.Approximate);
    }

    [Fact]
    public void РазрешённоеВремя_НикогдаНеВБудущем()
    {
        foreach (var part in Enum.GetValues<PartOfDay>())
        {
            var (at, _) = _resolver.Resolve(new RelativeTimeSpec(PartOfDay: part), SentAt);
            at.ShouldBeLessThanOrEqualTo(SentAt, $"часть суток {part}");
        }
    }
}

public sealed class DateRangeTests
{
    [Fact]
    public void ЗапасРасширяетТолькоНачало()
    {
        var range = new DateRange(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero));

        var extended = range.ExtendStartBack(TimeSpan.FromHours(48));

        extended.Start.ShouldBe(range.Start.AddHours(-48));
        extended.End.ShouldBe(range.End);
    }

    [Fact]
    public void ПредыдущийПериод_ТойЖеДлины_ИПримыкает()
    {
        var range = DateRange.FromDays(new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero), 30);

        var previous = range.Previous();

        previous.Duration.ShouldBe(range.Duration);
        previous.End.ShouldBe(range.Start);
    }

    [Fact]
    public void КонецРаньшеНачала_Отвергается() =>
        Should.Throw<ArgumentException>(() => new DateRange(
            new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
}
