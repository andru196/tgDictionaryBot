namespace Diary.Modules.Notes;

/// <param name="Themes">Темы для группировки в отчёте: «разработка», «продуктивность».</param>
/// <param name="IsActionable">Мысль требует действия, а не просто зафиксирована.</param>
public sealed record IdeaPayload(
    string Title,
    string? Body,
    IReadOnlyList<string> Themes,
    bool IsActionable);

public sealed record QuestionPayload(
    string Question,
    string? Topic,
    string? Answer);

public static class NotesCategories
{
    public const string Idea = "idea";
    public const string Question = "question";
    public const string ModuleKey = "notes";
}
