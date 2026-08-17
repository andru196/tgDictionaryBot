using Diary.Application.Modules;
using Diary.Application.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Modules.Notes;

/// <summary>Идеи и вопросы — то, что приходит в голову и что лень гуглить в моменте.</summary>
public sealed class NotesModule : IDiaryModule
{
    public string Key => NotesCategories.ModuleKey;

    public string Title => "Заметки";

    public IReadOnlyList<CategoryDescriptor> Categories { get; } =
    [
        new CategoryDescriptor(
            NotesCategories.Idea,
            "Идея или мысль",
            "человек делится замыслом, наблюдением или соображением, которое хочет не потерять",
            [
                "прикольная мысль — сделать линтер для промптов",
                "надо бы написать статью про Result вместо исключений",
                "а что если таймер помидоров сделать голосовым",
            ],
            ["#идея", "#мысль"]),

        new CategoryDescriptor(
            NotesCategories.Question,
            "Вопрос",
            "человек задаёт вопрос, ответ на который собирается найти позже",
            [
                "надо загуглить чем span отличается от memory",
                "интересно, а как в SQLite работает WAL",
                "почему split query иногда быстрее",
            ],
            ["#вопрос", "#загуглить"]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IEntryExtractor, IdeaExtractor>();
        services.AddScoped<IEntryExtractor, QuestionExtractor>();

        services.AddScoped<IReportSectionProvider, IdeasSectionProvider>();
        services.AddScoped<IReportSectionProvider, QuestionsSectionProvider>();
    }
}
