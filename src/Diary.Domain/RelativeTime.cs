namespace Diary.Domain;

public enum PartOfDay
{
    Unspecified = 0,
    Morning = 1,
    Noon = 2,
    Afternoon = 3,
    Evening = 4,
    Night = 5,
}

/// <summary>
/// Указание времени в том виде, в каком его отдаёт модель. Модель не считает даты —
/// она лишь сообщает, что было сказано; в абсолютный момент это переводит
/// <see cref="RelativeTimeResolver"/>.
/// </summary>
/// <param name="DayOffset">Сдвиг в днях: 0 — сегодня, −1 — вчера.</param>
/// <param name="PartOfDay">Часть суток, если названа словом.</param>
/// <param name="LocalTime">Точное время, если названо («в семь утра»).</param>
/// <param name="HoursAgo">«Часа два назад» — сдвиг от момента отправки.</param>
public sealed record RelativeTimeSpec(
    int? DayOffset = null,
    PartOfDay PartOfDay = PartOfDay.Unspecified,
    TimeOnly? LocalTime = null,
    double? HoursAgo = null)
{
    public static RelativeTimeSpec Now { get; } = new();

    public bool IsEmpty =>
        DayOffset is null && PartOfDay == PartOfDay.Unspecified && LocalTime is null && HoursAgo is null;
}

/// <summary>
/// Переводит сказанное вслух время в абсолютный момент. Детерминированно и без LLM:
/// модель ошибается в арифметике дат, а результат обязан быть одинаковым при переразборе.
/// </summary>
public sealed class RelativeTimeResolver(TimeZoneInfo zone)
{
    /// <summary>Часы, которыми считаются части суток при отсутствии точного времени.</summary>
    private static readonly Dictionary<PartOfDay, TimeOnly> PartHours = new()
    {
        [PartOfDay.Morning] = new TimeOnly(8, 0),
        [PartOfDay.Noon] = new TimeOnly(13, 0),
        [PartOfDay.Afternoon] = new TimeOnly(16, 0),
        [PartOfDay.Evening] = new TimeOnly(20, 0),
        [PartOfDay.Night] = new TimeOnly(23, 0),
    };

    public (DateTimeOffset OccurredAtUtc, TimeCertainty Certainty) Resolve(
        RelativeTimeSpec spec,
        DateTimeOffset sentAtUtc)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.IsEmpty)
        {
            return (sentAtUtc.ToUniversalTime(), TimeCertainty.Exact);
        }

        // «Часа два назад» — самое точное из относительных указаний, обрабатываем первым.
        if (spec.HoursAgo is { } hours)
        {
            return (sentAtUtc.AddHours(-hours).ToUniversalTime(), TimeCertainty.Resolved);
        }

        var local = TimeZoneInfo.ConvertTime(sentAtUtc, zone);
        var date = DateOnly.FromDateTime(local.DateTime).AddDays(spec.DayOffset ?? 0);

        var (time, certainty) = spec switch
        {
            { LocalTime: { } explicitTime } => (explicitTime, TimeCertainty.Resolved),
            { PartOfDay: not PartOfDay.Unspecified } => (PartHours[spec.PartOfDay], TimeCertainty.Approximate),
            // Назван только день — берём время отправки, чтобы не выдумывать час.
            _ => (TimeOnly.FromDateTime(local.DateTime), TimeCertainty.Resolved),
        };

        var naive = date.ToDateTime(time);
        var resolved = new DateTimeOffset(naive, zone.GetUtcOffset(naive));

        // «Вечером» сказанное в час ночи почти всегда означает вчерашний вечер, а не сегодняшний,
        // который ещё не наступил. Смещаем на сутки назад, если день явно не назван.
        if (spec.DayOffset is null && resolved > sentAtUtc)
        {
            naive = date.AddDays(-1).ToDateTime(time);
            resolved = new DateTimeOffset(naive, zone.GetUtcOffset(naive));
            certainty = TimeCertainty.Approximate;
        }

        return (resolved.ToUniversalTime(), certainty);
    }
}
