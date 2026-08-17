using System.Diagnostics;
using System.Text.Json;
using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.Evaluation;

/// <summary>
/// Размеченный вручную пример. Публичные бенчмарки меряют что угодно, только не
/// разговорный русский про изжогу, поэтому единственный релевантный тест — свои сообщения.
/// </summary>
/// <param name="Categories">Ожидаемые категории фрагментов; повторы значимы.</param>
/// <param name="Foods">Канонические названия еды, если фрагмент про еду.</param>
public sealed record EvalCase(
    string Text,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string>? Foods = null,
    IReadOnlyList<string>? Tags = null,
    string? SymptomKind = null,
    int? Severity = null,
    string? Comment = null);

public sealed record EvalMetrics(
    int Cases,
    double FragmentCountAccuracy,
    double CategoryF1,
    double FoodF1,
    double FoodPrecision,
    double FoodRecall,
    double TagF1,
    double SymptomKindAccuracy,
    double SeverityMae,
    double FailureRate,
    double SecondsPerCase,
    string Model);

public sealed record EvalCaseResult(
    EvalCase Case,
    IReadOnlyList<string> ActualCategories,
    IReadOnlyList<string> ActualFoods,
    IReadOnlyList<string> ActualTags,
    string? ActualSymptomKind,
    int? ActualSeverity,
    string? Error);

public sealed record EvalReport(EvalMetrics Metrics, IReadOnlyList<EvalCaseResult> Results);

/// <summary>
/// Прогоняет сегментацию и извлечение на золотом наборе и считает, насколько
/// результат совпал с разметкой. Превращает «какая модель лучше» из спора в измерение.
/// </summary>
/// <remarks>
/// Сравнение идёт по полям payload, найденным по именам, а не по типам модулей:
/// иначе харнесс пришлось бы менять при каждом новом модуле, а он должен работать
/// со всеми сразу.
/// </remarks>
public sealed class EvaluationRunner(
    IEntrySegmenter segmenter,
    IEnumerable<IEntryExtractor> extractors,
    IModuleRegistry modules,
    ISubjectContext subjectContext,
    IStructuredCompletion llm,
    TimeProvider clock,
    ILogger<EvaluationRunner> logger)
{
    public async Task<EvalReport> RunAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var subject = subjectContext.Subject;
        var categories = modules.CategoriesFor(subject.Modules);
        var byCategory = extractors.ToDictionary(e => e.Category.Value, StringComparer.OrdinalIgnoreCase);

        var results = new List<EvalCaseResult>(cases.Count);
        var stopwatch = Stopwatch.StartNew();

        foreach (var probe in cases)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(probe, categories, byCategory, ct));
        }

        stopwatch.Stop();

        return new EvalReport(
            Aggregate(results, stopwatch.Elapsed, llm.ModelFor(LlmRole.Extraction)),
            results);
    }

    private async Task<EvalCaseResult> RunCaseAsync(
        EvalCase probe,
        IReadOnlyList<CategoryDescriptor> categories,
        Dictionary<string, IEntryExtractor> byCategory,
        CancellationToken ct)
    {
        try
        {
            var fragments = await segmenter.SegmentAsync(probe.Text, categories, ct);

            var context = new ExtractionContext(
                MessageId.New(), clock.GetUtcNow(),
                subjectContext.TimeResolver, "eval", null);

            var foods = new List<string>();
            var tags = new List<string>();
            string? symptomKind = null;
            int? severity = null;

            foreach (var fragment in fragments)
            {
                if (!byCategory.TryGetValue(fragment.Category.Value, out var extractor))
                {
                    continue;
                }

                var entry = await extractor.ExtractAsync(fragment, context, ct);
                using var payload = JsonDocument.Parse(entry.PayloadJson);
                var root = payload.RootElement;

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("canonicalName", out var name))
                        {
                            foods.Add(Normalize(name.GetString()));
                        }

                        if (item.TryGetProperty("tags", out var itemTags) &&
                            itemTags.ValueKind == JsonValueKind.Array)
                        {
                            tags.AddRange(itemTags.EnumerateArray()
                                .Select(t => Normalize(t.GetString()))
                                .Where(t => t.Length > 0));
                        }
                    }
                }

                if (root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
                {
                    symptomKind = Normalize(kind.GetString());
                }

                if (root.TryGetProperty("severity", out var severityValue) &&
                    severityValue.ValueKind == JsonValueKind.Number)
                {
                    severity = severityValue.GetInt32();
                }
            }

            return new EvalCaseResult(
                probe,
                [.. fragments.Select(f => f.Category.Value)],
                foods, tags.Distinct().ToArray(), symptomKind, severity, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Кейс не отработал: {Text}", probe.Text);
            return new EvalCaseResult(probe, [], [], [], null, null, ex.Message);
        }
    }

    private static EvalMetrics Aggregate(
        IReadOnlyList<EvalCaseResult> results, TimeSpan elapsed, string model)
    {
        var usable = results.Where(r => r.Error is null).ToArray();

        var fragmentHits = usable.Count(r => r.ActualCategories.Count == r.Case.Categories.Count);

        var categoryF1 = MeanF1(usable, r => r.Case.Categories, r => r.ActualCategories);
        var foodCases = usable.Where(r => r.Case.Foods is { Count: > 0 }).ToArray();
        var (foodPrecision, foodRecall, foodF1) = MeanPrf(
            foodCases, r => r.Case.Foods!.Select(Normalize).ToArray(), r => r.ActualFoods);
        var tagF1 = MeanF1(
            usable.Where(r => r.Case.Tags is { Count: > 0 }).ToArray(),
            r => r.Case.Tags!.Select(Normalize).ToArray(),
            r => r.ActualTags);

        var symptomCases = usable.Where(r => r.Case.SymptomKind is { Length: > 0 }).ToArray();
        var symptomHits = symptomCases.Count(r =>
            string.Equals(r.ActualSymptomKind, Normalize(r.Case.SymptomKind), StringComparison.Ordinal));

        var severityCases = usable
            .Where(r => r.Case.Severity is not null && r.ActualSeverity is not null)
            .ToArray();
        var severityMae = severityCases.Length == 0
            ? 0
            : severityCases.Average(r => Math.Abs(r.Case.Severity!.Value - r.ActualSeverity!.Value));

        return new EvalMetrics(
            results.Count,
            Ratio(fragmentHits, usable.Length),
            categoryF1,
            foodF1,
            foodPrecision,
            foodRecall,
            tagF1,
            Ratio(symptomHits, symptomCases.Length),
            severityMae,
            Ratio(results.Count - usable.Length, results.Count),
            results.Count == 0 ? 0 : elapsed.TotalSeconds / results.Count,
            model);
    }

    private static double MeanF1(
        IReadOnlyList<EvalCaseResult> cases,
        Func<EvalCaseResult, IReadOnlyList<string>> expected,
        Func<EvalCaseResult, IReadOnlyList<string>> actual) =>
        MeanPrf(cases, expected, actual).F1;

    private static (double Precision, double Recall, double F1) MeanPrf(
        IReadOnlyList<EvalCaseResult> cases,
        Func<EvalCaseResult, IReadOnlyList<string>> expected,
        Func<EvalCaseResult, IReadOnlyList<string>> actual)
    {
        if (cases.Count == 0)
        {
            return (0, 0, 0);
        }

        double precision = 0, recall = 0, f1 = 0;

        foreach (var probe in cases)
        {
            var want = expected(probe).ToHashSet(StringComparer.Ordinal);
            var got = actual(probe).ToHashSet(StringComparer.Ordinal);
            var hits = want.Intersect(got, StringComparer.Ordinal).Count();

            var p = got.Count == 0 ? 0 : (double)hits / got.Count;
            var r = want.Count == 0 ? 0 : (double)hits / want.Count;

            precision += p;
            recall += r;
            f1 += p + r == 0 ? 0 : 2 * p * r / (p + r);
        }

        return (precision / cases.Count, recall / cases.Count, f1 / cases.Count);
    }

    private static double Ratio(int part, int total) => total == 0 ? 0 : (double)part / total;

    /// <summary>Сравнение не должно спотыкаться о регистр и «ё».</summary>
    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant().Replace('ё', 'е') ?? string.Empty;

    public static async Task<IReadOnlyList<EvalCase>> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Золотой набор не найден: {Path.GetFullPath(path)}", path);
        }

        var cases = new List<EvalCase>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (JsonSerializer.Deserialize<EvalCase>(line, DiaryJson.Options) is { } probe)
            {
                cases.Add(probe);
            }
        }

        return cases;
    }
}
