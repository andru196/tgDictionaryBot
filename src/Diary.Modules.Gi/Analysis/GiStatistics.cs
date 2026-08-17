using Diary.Domain;

namespace Diary.Modules.Gi.Analysis;

public enum SignalStrength
{
    /// <summary>Наблюдений меньше порога — вывод не делается, но строка показывается.</summary>
    LowData = 0,

    /// <summary>Связи нет.</summary>
    None = 1,

    /// <summary>Отношение выше порога, но статистически неубедительно.</summary>
    Weak = 2,

    /// <summary>Отношение выше порога и p-value мало.</summary>
    Strong = 3,
}

/// <param name="Support">Сколько раз продукт (или свойство) встречался в периоде.</param>
/// <param name="WithSymptom">Из них — после скольких был симптом.</param>
/// <param name="Lift">Во сколько раз чаще симптом случается после этого, чем в среднем.</param>
/// <param name="Confirmed">Сколько связок опираются на reply или упоминание, а не на окно.</param>
public sealed record SuspectRow(
    string Name,
    int Support,
    int WithSymptom,
    double Probability,
    double Lift,
    double PValue,
    TimeSpan? MedianLag,
    int Confirmed,
    SignalStrength Strength);

public sealed record SymptomTotal(SymptomKind Kind, int Episodes, double AverageSeverity);

public sealed record TrendPoint(DateTimeOffset BucketStartUtc, int Episodes, double AverageSeverity, int Meals);

/// <summary>Сводка за период — то, что сравнивается с другим периодом.</summary>
public sealed record PeriodSummary(
    int Meals,
    int Episodes,
    double EpisodesPer10Meals,
    double AverageSeverity,
    int MaxCleanDayStreak,
    int NightEpisodes,
    double BaseRate);

public sealed record GiStatistics(
    DateRange Period,
    PeriodSummary Summary,
    PeriodSummary? Comparison,
    IReadOnlyList<SuspectRow> Suspects,
    IReadOnlyList<SuspectRow> TagSuspects,
    IReadOnlyList<SuspectRow> Tolerated,
    IReadOnlyList<SymptomTotal> SymptomTotals,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<DailySeverity> Daily,
    int ConfirmedLinks,
    int HypothesesTested,
    ExposureWindowPolicy Windows);

/// <summary>Тяжесть по календарным дням — для тепловой карты.</summary>
public sealed record DailySeverity(DateOnly Day, int Episodes, int MaxSeverity);
