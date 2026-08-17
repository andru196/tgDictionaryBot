using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;
using Diary.Modules.Gi.Analysis;

namespace Diary.Modules.Gi;

public sealed record GiAnalysis(GiStatistics Statistics, IReadOnlyList<CalibratedWindow> Calibration);

/// <summary>
/// Готовит наблюдения из хранилища и запускает счёт. Здесь же живёт правило о запасе:
/// приёмы пищи грузятся раньше начала периода, иначе первые часы любого интервала
/// систематически выглядели бы «чистыми».
/// </summary>
public sealed class GiAnalysisService(
    IEntryRepository entries,
    IMessageRepository messages,
    ISubjectContext subjectContext,
    GiStatisticsCalculator calculator,
    MealSymptomLinker linker,
    ExposureWindowCalibrator calibrator)
{
    public async Task<GiAnalysis> AnalyzeAsync(
        DateRange period,
        DateRange? compareTo,
        Granularity granularity,
        CancellationToken ct)
    {
        var subject = subjectContext.Subject;
        var settings = subject.Analysis;

        var baseline = ExposureWindowPolicy.Default;
        var extended = period.ExtendStartBack(baseline.MaxLookback);

        var (meals, symptoms) = await LoadAsync(extended, ct);

        // Калибровка идёт по всей доступной истории: подтверждённых связок мало,
        // и сужать их выборку до отчётного периода — терять то немногое, что есть.
        var calibration = Array.Empty<CalibratedWindow>() as IReadOnlyList<CalibratedWindow>;
        var policy = baseline;

        if (settings.UseCalibratedWindows)
        {
            var kinds = symptoms.ToDictionary(s => s.Id, s => s.Kind);
            var links = linker.Link(meals, symptoms, baseline);
            calibration = calibrator.Calibrate(links, kinds, settings.CalibrationMinSamples);
            policy = ExposureWindowCalibrator.Apply(baseline, calibration);
        }

        (IReadOnlyList<MealObservation>, IReadOnlyList<SymptomObservation>, DateRange)? comparison = null;
        if (compareTo is { } other)
        {
            var (otherMeals, otherSymptoms) = await LoadAsync(other.ExtendStartBack(policy.MaxLookback), ct);
            comparison = (otherMeals, otherSymptoms, other);
        }

        var statistics = calculator.Calculate(
            period, meals, symptoms, policy, settings, subject.TimeZone, granularity, comparison);

        return new GiAnalysis(statistics, calibration);
    }

    private async Task<(IReadOnlyList<MealObservation> Meals, IReadOnlyList<SymptomObservation> Symptoms)>
        LoadAsync(DateRange range, CancellationToken ct)
    {
        var mealEntries = await entries.GetByCategoryAsync(
            GiCategories.ModuleKey, GiCategories.Meal, range, ct);
        var symptomEntries = await entries.GetByCategoryAsync(
            GiCategories.ModuleKey, GiCategories.Symptom, range, ct);

        // Reply живёт на сообщении, а не на записи: чтобы связать симптом с едой по ответу,
        // нужен исходник.
        var captured = await messages.GetByPeriodAsync(range, ct);
        var byMessage = captured.ToDictionary(m => m.Id, m => (m.TelegramMessageId, m.ReplyToTelegramMessageId));

        var meals = mealEntries
            .Select(e =>
            {
                var payload = e.Payload<MealPayload>();
                byMessage.TryGetValue(e.SourceMessageId, out var source);
                return new MealObservation(
                    e.Id, e.OccurredAtUtc, payload.Items, payload.Type,
                    source.TelegramMessageId == 0 ? null : source.TelegramMessageId);
            })
            .OrderBy(m => m.At)
            .ToArray();

        var symptoms = symptomEntries
            .Select(e =>
            {
                var payload = e.Payload<SymptomPayload>();
                byMessage.TryGetValue(e.SourceMessageId, out var source);
                return new SymptomObservation(
                    e.Id, e.OccurredAtUtc, payload.Kind, payload.Severity,
                    payload.SuspectedFoodMention, source.ReplyToTelegramMessageId);
            })
            .OrderBy(s => s.At)
            .ToArray();

        return (meals, symptoms);
    }
}
