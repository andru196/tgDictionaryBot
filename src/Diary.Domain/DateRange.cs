namespace Diary.Domain;

/// <summary>Полуинтервал времени [Start, End) в UTC.</summary>
public readonly record struct DateRange
{
    public DateRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start)
        {
            throw new ArgumentException($"Конец периода ({end:O}) раньше начала ({start:O}).", nameof(end));
        }

        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration => End - Start;

    public int Days => (int)Math.Ceiling(Duration.TotalDays);

    public bool Contains(DateTimeOffset moment) => moment >= Start && moment < End;

    /// <summary>
    /// Расширить начало назад. Нужно при выборке приёмов пищи: симптом в 02:00 первого дня
    /// периода вызван ужином предыдущего, и без запаса начало периода систематически
    /// выглядело бы «чистым».
    /// </summary>
    public DateRange ExtendStartBack(TimeSpan margin) => new(Start - margin, End);

    /// <summary>Период той же длительности, непосредственно предшествующий текущему.</summary>
    public DateRange Previous() => new(Start - Duration, Start);

    public static DateRange FromDays(DateTimeOffset endExclusive, int days) =>
        new(endExclusive - TimeSpan.FromDays(days), endExclusive);

    /// <summary>Календарные сутки в указанной таймзоне, переведённые в UTC.</summary>
    public static DateRange CalendarDays(DateOnly fromInclusive, DateOnly toInclusive, TimeZoneInfo zone)
    {
        var start = new DateTimeOffset(fromInclusive.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(
            fromInclusive.ToDateTime(TimeOnly.MinValue)));
        var endLocal = toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var end = new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));
        return new DateRange(start, end);
    }

    public override string ToString() => $"{Start:yyyy-MM-dd HH:mm}Z .. {End:yyyy-MM-dd HH:mm}Z";
}

/// <summary>Шаг разбиения периода для трендов.</summary>
public enum Granularity
{
    Day = 0,
    Week = 1,
    Month = 2,
}
