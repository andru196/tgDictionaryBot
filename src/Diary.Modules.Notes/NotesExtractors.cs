using System.Reflection;
using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Prompts;
using Diary.Domain;

namespace Diary.Modules.Notes;

internal sealed record IdeaDto(string? Title, string? Body, List<string>? Themes, bool? IsActionable);

internal sealed record QuestionDto(string? Question, string? Topic);

public sealed class IdeaExtractor(IStructuredCompletion llm) : IEntryExtractor
{
    internal const string PromptVersion = "idea-v1";

    private static readonly string Prompt =
        PromptLoader.Load(Assembly.GetExecutingAssembly(), "extract-idea.md");

    public CategoryKey Category => NotesCategories.Idea;

    public async Task<DiaryEntry> ExtractAsync(
        EntryFragment fragment, ExtractionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(context);

        var dto = await llm.CompleteAsync<IdeaDto>(Prompt, fragment.Text, LlmRole.Extraction, ct);

        // Заголовок — единственное обязательное поле: без него карточка в отчёте бессмысленна.
        var title = string.IsNullOrWhiteSpace(dto.Title)
            ? Shorten(fragment.Text)
            : dto.Title!.Trim();

        var payload = new IdeaPayload(
            title,
            string.IsNullOrWhiteSpace(dto.Body) ? null : dto.Body!.Trim(),
            [.. (dto.Themes ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant())],
            dto.IsActionable ?? false);

        return DiaryEntry.Create(
            context.SourceMessageId, NotesCategories.ModuleKey, Category,
            context.SentAtUtc, TimeCertainty.Exact,
            fragment.Text, fragment.Confidence, payload, $"{context.ExtractorVersion}/{PromptVersion}");
    }

    private static string Shorten(string text) =>
        text.Length <= 60 ? text : text[..60].TrimEnd() + "…";
}

public sealed class QuestionExtractor(IStructuredCompletion llm) : IEntryExtractor
{
    internal const string PromptVersion = "question-v1";

    private static readonly string Prompt =
        PromptLoader.Load(Assembly.GetExecutingAssembly(), "extract-question.md");

    public CategoryKey Category => NotesCategories.Question;

    public async Task<DiaryEntry> ExtractAsync(
        EntryFragment fragment, ExtractionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(context);

        var dto = await llm.CompleteAsync<QuestionDto>(Prompt, fragment.Text, LlmRole.Extraction, ct);

        var payload = new QuestionPayload(
            string.IsNullOrWhiteSpace(dto.Question) ? fragment.Text.Trim() : dto.Question!.Trim(),
            string.IsNullOrWhiteSpace(dto.Topic) ? null : dto.Topic!.Trim(),
            Answer: null);

        return DiaryEntry.Create(
            context.SourceMessageId, NotesCategories.ModuleKey, Category,
            context.SentAtUtc, TimeCertainty.Exact,
            fragment.Text, fragment.Confidence, payload, $"{context.ExtractorVersion}/{PromptVersion}");
    }
}
