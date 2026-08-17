using Diary.Application.Subjects;
using Diary.Domain;

namespace Diary.Modules.Gi.Analysis;

/// <summary>
/// Считает всё, что попадает в отчёт по ЖКТ. Детерминированно и без модели: цифры обязаны
/// быть одинаковыми при повторном прогоне и объяснимыми построчно.
/// </summary>
public sealed class GiStatisticsCalculator(MealSymptomLinker linker)
{
    /// <param name="meals">
    /// Приёмы пищи, включая запас до начала периода: симптом в 02:00 первого дня вызван
    /// вчерашним ужином. В знаменатель попадают только те, что внутри периода.
    /// </param>
    public GiStatistics Calculate(
        DateRange period,
        IReadOnlyList<MealObservation> meals,
        IReadOnlyList<SymptomObservation> symptoms,
        ExposureWindowPolicy policy,
        AnalysisSettings settings,
        TimeZoneInfo zone,
        Granularity granularity = Granularity.Week,
        (IReadOnlyList<MealObservation> Meals, IReadOnlyList<SymptomObservation> Symptoms, DateRange Period)? compare = null)
    {
        ArgumentNullException.ThrowIfNull(meals);
        ArgumentNullException.ThrowIfNull(symptoms);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(settings);

        var links = linker.Link(meals, symptoms, policy);
        var inPeriod = meals.Where(m => period.Contains(m.At)).ToArray();
        var symptomsInPeriod = symptoms.Where(s => period.Contains(s.At)).ToArray();

        var linkedMealIds = links.Select(l => l.MealId).ToHashSet();
        var lagByMeal = links
            .GroupBy(l => l.MealId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Lag).ElementAt(g.Count() / 2).Lag);
        var confirmedByMeal = links
            .Where(l => l.IsConfirmed)
            .Select(l => l.MealId)
            .ToHashSet();

        var totalMeals = inPeriod.Length;
        var mealsWithSymptom = inPeriod.Count(m => linkedMealIds.Contains(m.Id));

        // Базовая частота считается по этому же периоду: иначе lift сравнивал бы
        // неделю обострения с годовым фоном.
        var baseRate = totalMeals == 0 ? 0 : (double)mealsWithSymptom / totalMeals;

        var suspects = BuildRows(
            inPeriod,
            m => m.Items.Select(i => i.CanonicalName).Distinct(StringComparer.OrdinalIgnoreCase),
            linkedMealIds, confirmedByMeal, lagByMeal, baseRate, mealsWithSymptom, totalMeals, settings);

        var tagSuspects = BuildRows(
            inPeriod,
            m => m.Items.SelectMany(i => i.Tags).Distinct().Select(TagName),
            linkedMealIds, confirmedByMeal, lagByMeal, baseRate, mealsWithSymptom, totalMeals, settings);

        var tolerated = suspects
            .Where(r => r.Support >= settings.ToleratedMinSupport && r.Lift <= settings.ToleratedMaxLift)
            .OrderBy(r => r.Lift)
            .ToArray();

        var totals = symptomsInPeriod
            .GroupBy(s => s.Kind)
            .Select(g => new SymptomTotal(g.Key, g.Count(), g.Average(s => (double)s.Severity.Value)))
            .OrderByDescending(t => t.Episodes)
            .ToArray();

        var summary = Summarize(period, inPeriod, symptomsInPeriod, baseRate, zone);
        PeriodSummary? comparison = null;
        if (compare is { } other)
        {
            var otherInPeriod = other.Meals.Where(m => other.Period.Contains(m.At)).ToArray();
            var otherSymptoms = other.Symptoms.Where(s => other.Period.Contains(s.At)).ToArray();
            var otherLinks = linker.Link(other.Meals, other.Symptoms, policy);
            var otherLinked = otherLinks.Select(l => l.MealId).ToHashSet();
            var otherBase = otherInPeriod.Length == 0
                ? 0
                : (double)otherInPeriod.Count(m => otherLinked.Contains(m.Id)) / otherInPeriod.Length;

            comparison = Summarize(other.Period, otherInPeriod, otherSymptoms, otherBase, zone);
        }

        return new GiStatistics(
            period,
            summary,
            comparison,
            [.. suspects.Where(r => r.Strength is not SignalStrength.None).OrderByDescending(r => r.Lift)],
            [.. tagSuspects.OrderByDescending(r => r.Lift)],
            tolerated,
            totals,
            BuildTrend(period, inPeriod, symptomsInPeriod, granularity, zone),
            BuildDaily(symptomsInPeriod, zone),
            links.Count(l => l.IsConfirmed),
            suspects.Count + tagSuspects.Count,
            policy);
    }

    private static IReadOnlyList<SuspectRow> BuildRows(
        IReadOnlyList<MealObservation> meals,
        Func<MealObservation, IEnumerable<string>> keySelector,
        HashSet<EntryId> linkedMealIds,
        HashSet<EntryId> confirmedMealIds,
        Dictionary<EntryId, TimeSpan> lagByMeal,
        double baseRate,
        int mealsWithSymptom,
        int totalMeals,
        AnalysisSettings settings)
    {
        var groups = new Dictionary<string, List<MealObservation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var meal in meals)
        {
            foreach (var key in keySelector(meal))
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = [];
                }

                list.Add(meal);
            }
        }

        var rows = new List<SuspectRow>(groups.Count);
        foreach (var (name, group) in groups)
        {
            var a = group.Count(m => linkedMealIds.Contains(m.Id));
            var b = group.Count - a;
            var c = mealsWithSymptom - a;
            var d = totalMeals - group.Count - c;

            var probability = group.Count == 0 ? 0 : (double)a / group.Count;
            var lift = baseRate <= 0 ? 0 : probability / baseRate;
            var pValue = FisherExactTest.RightTailPValue(a, b, Math.Max(c, 0), Math.Max(d, 0));

            var lags = group
                .Where(m => lagByMeal.ContainsKey(m.Id))
                .Select(m => lagByMeal[m.Id])
                .OrderBy(l => l)
                .ToArray();
            TimeSpan? medianLag = lags.Length == 0 ? null : lags[lags.Length / 2];

            var strength = Classify(group.Count, lift, pValue, settings);

            rows.Add(new SuspectRow(
                name, group.Count, a, probability, lift, pValue, medianLag,
                group.Count(m => confirmedMealIds.Contains(m.Id)), strength));
        }

        return rows;
    }

    private static SignalStrength Classify(int support, double lift, double pValue, AnalysisSettings settings)
    {
        if (support < settings.MinSupport)
        {
            return SignalStrength.LowData;
        }

        if (lift < settings.MinLift)
        {
            return SignalStrength.None;
        }

        return pValue <= 0.05 ? SignalStrength.Strong : SignalStrength.Weak;
    }

    private static PeriodSummary Summarize(
        DateRange period,
        IReadOnlyList<MealObservation> meals,
        IReadOnlyList<SymptomObservation> symptoms,
        double baseRate,
        TimeZoneInfo zone)
    {
        var episodes = symptoms.Count;
        var perTen = meals.Count == 0 ? 0 : episodes * 10.0 / meals.Count;
        var severity = episodes == 0 ? 0 : symptoms.Average(s => (double)s.Severity.Value);

        var nightly = symptoms.Count(s =>
        {
            var hour = TimeZoneInfo.ConvertTime(s.At, zone).Hour;
            return hour >= 23 || hour < 6;
        });

        return new PeriodSummary(
            meals.Count, episodes, perTen, severity,
            MaxCleanStreak(period, symptoms, zone), nightly, baseRate);
    }

    private static int MaxCleanStreak(
        DateRange period,
        IReadOnlyList<SymptomObservation> symptoms,
        TimeZoneInfo zone)
    {
        var bad = symptoms
            .Select(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.At, zone).DateTime))
            .ToHashSet();

        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.Start, zone).DateTime);
        var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(period.End, zone).DateTime);

        int best = 0, current = 0;
        for (var day = start; day < end; day = day.AddDays(1))
        {
            current = bad.Contains(day) ? 0 : current + 1;
            best = Math.Max(best, current);
        }

        return best;
    }

    private static IReadOnlyList<TrendPoint> BuildTrend(
        DateRange period,
        IReadOnlyList<MealObservation> meals,
        IReadOnlyList<SymptomObservation> symptoms,
        Granularity granularity,
        TimeZoneInfo zone)
    {
        var points = new List<TrendPoint>();
        var cursor = period.Start;

        while (cursor < period.End)
        {
            var next = granularity switch
            {
                Granularity.Day => cursor.AddDays(1),
                Granularity.Month => cursor.AddMonths(1),
                _ => cursor.AddDays(7),
            };

            if (next > period.End)
            {
                next = period.End;
            }

            var bucketSymptoms = symptoms.Where(s => s.At >= cursor && s.At < next).ToArray();
            points.Add(new TrendPoint(
                cursor,
                bucketSymptoms.Length,
                bucketSymptoms.Length == 0 ? 0 : bucketSymptoms.Average(s => (double)s.Severity.Value),
                meals.Count(m => m.At >= cursor && m.At < next)));

            cursor = next;
        }

        _ = zone;
        return points;
    }

    private static IReadOnlyList<DailySeverity> BuildDaily(
        IReadOnlyList<SymptomObservation> symptoms,
        TimeZoneInfo zone) =>
        [.. symptoms
            .GroupBy(s => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.At, zone).DateTime))
            .Select(g => new DailySeverity(g.Key, g.Count(), g.Max(s => s.Severity.Value)))
            .OrderBy(d => d.Day)];

    internal static string TagName(FoodTag tag) => tag switch
    {
        FoodTag.Fatty => "жирное",
        FoodTag.Fried => "жареное",
        FoodTag.Spicy => "острое",
        FoodTag.Acidic => "кислое",
        FoodTag.Dairy => "молочное",
        FoodTag.Gluten => "глютен",
        FoodTag.Caffeine => "кофеин",
        FoodTag.Alcohol => "алкоголь",
        FoodTag.Carbonated => "газированное",
        FoodTag.Legumes => "бобовые",
        FoodTag.RawVegetables => "сырые овощи",
        FoodTag.Sweet => "сладкое",
        FoodTag.Processed => "переработанное",
        _ => tag.ToString(),
    };

    public static string SymptomName(SymptomKind kind) => kind switch
    {
        SymptomKind.Reflux => "рефлюкс",
        SymptomKind.Heartburn => "изжога",
        SymptomKind.Bloating => "вздутие",
        SymptomKind.Gas => "газы",
        SymptomKind.Diarrhea => "диарея",
        SymptomKind.Constipation => "запор",
        SymptomKind.Nausea => "тошнота",
        SymptomKind.AbdominalPain => "боль в животе",
        SymptomKind.Belching => "отрыжка",
        _ => "другое",
    };
}
