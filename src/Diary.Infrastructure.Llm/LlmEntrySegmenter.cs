using System.Reflection;
using System.Text;
using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Prompts;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Infrastructure.Llm;

internal sealed record FragmentDto(string? Text, string? Category, double? Confidence);

internal sealed record SegmentationDto(List<FragmentDto>? Fragments);

/// <summary>
/// Делит сообщение на смысловые фрагменты. Список категорий не зашит в промпт —
/// он собирается из зарегистрированных модулей, поэтому новый модуль появляется
/// здесь сам, без правки этого файла и без правки промпта.
/// </summary>
public sealed class LlmEntrySegmenter(IStructuredCompletion llm, ILogger<LlmEntrySegmenter> logger)
    : IEntrySegmenter
{
    internal const string PromptVersion = "segment-v1";

    private static readonly string Template =
        PromptLoader.Load(Assembly.GetExecutingAssembly(), "segment.md");

    public async Task<IReadOnlyList<EntryFragment>> SegmentAsync(
        string text, IReadOnlyList<CategoryDescriptor> categories, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(categories);

        if (categories.Count == 0)
        {
            return [];
        }

        // Единственная категория: делить не на что, вызов модели был бы напрасным.
        if (categories.Count == 1)
        {
            return [new EntryFragment(text, categories[0].Key, Confidence.Certain)];
        }

        var prompt = Template.Replace("{{CATEGORIES}}", BuildCategoryBlock(categories), StringComparison.Ordinal);
        var dto = await llm.CompleteAsync<SegmentationDto>(prompt, text, LlmRole.Segmentation, ct);

        var known = categories.ToDictionary(c => c.Key.Value, StringComparer.OrdinalIgnoreCase);
        var fragments = new List<EntryFragment>();

        foreach (var fragment in dto.Fragments ?? [])
        {
            if (string.IsNullOrWhiteSpace(fragment.Text) || string.IsNullOrWhiteSpace(fragment.Category))
            {
                continue;
            }

            if (!known.TryGetValue(fragment.Category!, out var descriptor))
            {
                logger.LogDebug("Модель вернула неизвестную категорию {Category}, фрагмент отброшен.",
                    fragment.Category);
                continue;
            }

            fragments.Add(new EntryFragment(
                fragment.Text!.Trim(),
                descriptor.Key,
                new Confidence(Math.Clamp(fragment.Confidence ?? 0.8, 0, 1))));
        }

        return fragments;
    }

    internal static string BuildCategoryBlock(IReadOnlyList<CategoryDescriptor> categories)
    {
        var sb = new StringBuilder();
        foreach (var category in categories)
        {
            sb.Append("- `").Append(category.Key.Value).Append("` — ").Append(category.Title)
              .Append(". Когда: ").Append(category.WhenToUse).AppendLine(".");

            if (category.Examples.Count > 0)
            {
                sb.Append("  Примеры: ")
                  .AppendLine(string.Join(" · ", category.Examples.Select(e => $"«{e}»")));
            }
        }

        return sb.ToString().TrimEnd();
    }
}
