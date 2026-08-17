namespace Diary.Modules.Gi.Analysis;

public sealed record CalibratedWindow(
    SymptomKind Kind,
    ExposureWindow Suggested,
    ExposureWindow Default,
    int Samples,
    TimeSpan MedianLag);

/// <summary>
/// Подстраивает окна экспозиции под конкретного человека по подтверждённым связкам.
/// Здесь редкие reply отрабатывают непропорционально своей частоте: суженное окно режет
/// ложные совпадения по всей остальной выборке, включая записи, где ничего не подтверждали.
/// </summary>
public sealed class ExposureWindowCalibrator
{
    public IReadOnlyList<CalibratedWindow> Calibrate(
        IReadOnlyList<MealSymptomLink> links,
        IReadOnlyDictionary<Domain.EntryId, SymptomKind> symptomKinds,
        int minSamples)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(symptomKinds);

        var result = new List<CalibratedWindow>();

        // Только подтверждённое: связки по окну обучали бы окна на самих себе.
        var byKind = links
            .Where(l => l.IsConfirmed)
            .Where(l => symptomKinds.ContainsKey(l.SymptomId))
            .GroupBy(l => symptomKinds[l.SymptomId]);

        foreach (var group in byKind)
        {
            var lags = group.Select(l => l.Lag).OrderBy(l => l).ToArray();
            if (lags.Length < minSamples)
            {
                continue;
            }

            var low = Percentile(lags, 0.10);
            var high = Percentile(lags, 0.90);

            // Слишком узкое окно на малой выборке — самообман, поэтому есть нижняя граница ширины.
            if (high - low < TimeSpan.FromMinutes(45))
            {
                var centre = Percentile(lags, 0.5);
                low = centre - TimeSpan.FromMinutes(30);
                high = centre + TimeSpan.FromMinutes(30);
            }

            low = Floor(low < TimeSpan.Zero ? TimeSpan.Zero : low);
            high = Ceil(high);

            result.Add(new CalibratedWindow(
                group.Key,
                new ExposureWindow(low, high),
                ExposureWindowPolicy.Default.For(group.Key),
                lags.Length,
                Percentile(lags, 0.5)));
        }

        return result;
    }

    public static ExposureWindowPolicy Apply(
        ExposureWindowPolicy baseline,
        IReadOnlyList<CalibratedWindow> calibrated)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(calibrated);

        var policy = baseline;
        foreach (var window in calibrated)
        {
            policy = policy.With(window.Kind, window.Suggested);
        }

        return policy;
    }

    private static TimeSpan Percentile(TimeSpan[] sorted, double q)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var weight = position - lower;

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
    }

    private static TimeSpan Floor(TimeSpan value) =>
        TimeSpan.FromMinutes(Math.Floor(value.TotalMinutes / 15) * 15);

    private static TimeSpan Ceil(TimeSpan value) =>
        TimeSpan.FromMinutes(Math.Ceiling(value.TotalMinutes / 15) * 15);
}
