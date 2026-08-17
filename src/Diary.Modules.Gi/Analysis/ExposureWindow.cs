namespace Diary.Modules.Gi.Analysis;

/// <summary>
/// Окно, в котором приём пищи считается возможной причиной симптома.
/// Полуинтервал [From, To) от момента еды.
/// </summary>
public readonly record struct ExposureWindow(TimeSpan From, TimeSpan To)
{
    public bool Contains(TimeSpan lag) => lag >= From && lag < To;

    public override string ToString() => $"{Format(From)}–{Format(To)}";

    private static string Format(TimeSpan span) => $"{(int)span.TotalHours}:{span.Minutes:00}";
}

/// <summary>
/// Окна по видам симптомов. Одно окно на всех даёт мусор: изжога приходит за часы,
/// запор — за сутки с лишним.
/// </summary>
public sealed class ExposureWindowPolicy
{
    private readonly Dictionary<SymptomKind, ExposureWindow> _windows;

    public ExposureWindowPolicy(IReadOnlyDictionary<SymptomKind, ExposureWindow> windows, bool calibrated = false)
    {
        _windows = new Dictionary<SymptomKind, ExposureWindow>(windows);
        IsCalibrated = calibrated;
    }

    /// <summary>Окна рассчитаны по подтверждённым связкам субъекта, а не взяты из таблицы.</summary>
    public bool IsCalibrated { get; }

    /// <summary>Стартовое приближение из общей физиологии. Личные окна даёт калибровка.</summary>
    public static ExposureWindowPolicy Default { get; } = new(new Dictionary<SymptomKind, ExposureWindow>
    {
        [SymptomKind.Reflux] = new(TimeSpan.Zero, TimeSpan.FromHours(4)),
        [SymptomKind.Heartburn] = new(TimeSpan.Zero, TimeSpan.FromHours(4)),
        [SymptomKind.Belching] = new(TimeSpan.Zero, TimeSpan.FromHours(4)),
        [SymptomKind.Nausea] = new(TimeSpan.Zero, TimeSpan.FromHours(6)),
        [SymptomKind.Bloating] = new(TimeSpan.FromHours(1), TimeSpan.FromHours(8)),
        [SymptomKind.Gas] = new(TimeSpan.FromHours(1), TimeSpan.FromHours(8)),
        [SymptomKind.Diarrhea] = new(TimeSpan.FromHours(2), TimeSpan.FromHours(24)),
        [SymptomKind.AbdominalPain] = new(TimeSpan.FromHours(2), TimeSpan.FromHours(24)),
        [SymptomKind.Constipation] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(48)),
        [SymptomKind.Other] = new(TimeSpan.Zero, TimeSpan.FromHours(12)),
    });

    public ExposureWindow For(SymptomKind kind) =>
        _windows.TryGetValue(kind, out var window) ? window : Default._windows[SymptomKind.Other];

    /// <summary>
    /// Самое длинное окно. На него расширяется выборка назад от начала периода: симптом
    /// в 02:00 первого дня вызван вчерашним ужином, и без запаса начало периода
    /// систематически выглядело бы «чистым».
    /// </summary>
    public TimeSpan MaxLookback => _windows.Count == 0
        ? TimeSpan.FromHours(48)
        : _windows.Values.Max(w => w.To);

    public IReadOnlyDictionary<SymptomKind, ExposureWindow> All => _windows;

    public ExposureWindowPolicy With(SymptomKind kind, ExposureWindow window)
    {
        var copy = new Dictionary<SymptomKind, ExposureWindow>(_windows) { [kind] = window };
        return new ExposureWindowPolicy(copy, calibrated: true);
    }
}
