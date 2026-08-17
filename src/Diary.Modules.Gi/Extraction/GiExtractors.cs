using System.Globalization;
using System.Reflection;
using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Prompts;
using Diary.Domain;

namespace Diary.Modules.Gi.Extraction;

/// <summary>
/// Контракт с моделью. Поля намеренно плоские и строковые: перечисление, пришедшее строкой,
/// можно сопоставить с запасным вариантом, а невалидное значение enum обрушило бы разбор.
/// </summary>
internal sealed record TimeDto(int? DayOffset, string? PartOfDay, string? LocalTime, double? HoursAgo)
{
    public RelativeTimeSpec ToSpec()
    {
        var part = Enum.TryParse<PartOfDay>(PartOfDay, ignoreCase: true, out var parsed)
            ? parsed
            : Domain.PartOfDay.Unspecified;

        TimeOnly? local = TimeOnly.TryParse(LocalTime, CultureInfo.InvariantCulture, out var time) ? time : null;

        return new RelativeTimeSpec(DayOffset, part, local, HoursAgo);
    }
}

internal sealed record FoodItemDto(string? Canonical, string? Raw, string? Quantity, List<string>? Tags);

internal sealed record MealDto(List<FoodItemDto>? Items, string? MealType, TimeDto? Time);

internal sealed record SymptomDto(
    string? Kind,
    int? Severity,
    int? DurationMinutes,
    string? SuspectedFood,
    string? Notes,
    TimeDto? Time);

public sealed class MealExtractor(IStructuredCompletion llm) : IEntryExtractor
{
    internal const string PromptVersion = "meal-v1";

    private static readonly string Prompt =
        PromptLoader.Load(Assembly.GetExecutingAssembly(), "extract-meal.md");

    public CategoryKey Category => GiCategories.Meal;

    public async Task<DiaryEntry> ExtractAsync(
        EntryFragment fragment, ExtractionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(context);

        var dto = await llm.CompleteAsync<MealDto>(Prompt, fragment.Text, LlmRole.Extraction, ct);

        var items = (dto.Items ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Canonical))
            .Select(i => new FoodItem(
                i.Canonical!.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(i.Raw) ? i.Canonical!.Trim() : i.Raw!.Trim(),
                string.IsNullOrWhiteSpace(i.Quantity) ? null : i.Quantity!.Trim(),
                ParseTags(i.Tags)))
            .ToArray();

        var payload = new MealPayload(
            items,
            Enum.TryParse<MealType>(dto.MealType, ignoreCase: true, out var type) ? type : MealType.Unspecified);

        var (at, certainty) = context.TimeResolver.Resolve(
            dto.Time?.ToSpec() ?? RelativeTimeSpec.Now, context.SentAtUtc);

        return DiaryEntry.Create(
            context.SourceMessageId, GiCategories.ModuleKey, Category, at, certainty,
            fragment.Text, fragment.Confidence, payload, $"{context.ExtractorVersion}/{PromptVersion}");
    }

    private static IReadOnlyList<FoodTag> ParseTags(List<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        var parsed = new List<FoodTag>(tags.Count);
        foreach (var tag in tags)
        {
            // Незнакомый тег — не повод ронять разбор: остальные данные всё ещё полезны.
            if (Enum.TryParse<FoodTag>(tag, ignoreCase: true, out var value) && !parsed.Contains(value))
            {
                parsed.Add(value);
            }
        }

        return parsed;
    }
}

public sealed class SymptomExtractor(IStructuredCompletion llm) : IEntryExtractor
{
    internal const string PromptVersion = "symptom-v1";

    private static readonly string Prompt =
        PromptLoader.Load(Assembly.GetExecutingAssembly(), "extract-symptom.md");

    public CategoryKey Category => GiCategories.Symptom;

    public async Task<DiaryEntry> ExtractAsync(
        EntryFragment fragment, ExtractionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(context);

        var dto = await llm.CompleteAsync<SymptomDto>(Prompt, fragment.Text, LlmRole.Extraction, ct);

        var payload = new SymptomPayload(
            Enum.TryParse<SymptomKind>(dto.Kind, ignoreCase: true, out var kind) ? kind : SymptomKind.Other,
            new Severity(Math.Clamp(dto.Severity ?? 4, 0, 10)),
            dto.DurationMinutes is > 0 ? TimeSpan.FromMinutes(dto.DurationMinutes.Value) : null,
            string.IsNullOrWhiteSpace(dto.SuspectedFood) ? null : dto.SuspectedFood!.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes!.Trim());

        var (at, certainty) = context.TimeResolver.Resolve(
            dto.Time?.ToSpec() ?? RelativeTimeSpec.Now, context.SentAtUtc);

        return DiaryEntry.Create(
            context.SourceMessageId, GiCategories.ModuleKey, Category, at, certainty,
            fragment.Text, fragment.Confidence, payload, $"{context.ExtractorVersion}/{PromptVersion}");
    }
}
