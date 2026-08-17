using Diary.Application.Ports;
using Diary.Application.Reporting;
using Diary.Domain;

namespace Diary.Modules.Notes;

/// <summary>Карточки идей, сгруппированные по темам.</summary>
public sealed class IdeasSectionProvider(IEntryRepository entries) : IReportSectionProvider
{
    public string ModuleKey => NotesCategories.ModuleKey;

    public int Order => 20;

    public async Task<ReportSection?> BuildAsync(ReportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var found = await entries.GetByCategoryAsync(
            NotesCategories.ModuleKey, NotesCategories.Idea, context.Period, ct);

        if (found.Count == 0)
        {
            return null;
        }

        var zone = context.Subject.TimeZone;
        var html = new Html();
        html.Raw("<div class=\"cards\">");

        foreach (var entry in found.OrderByDescending(e => e.OccurredAtUtc))
        {
            var idea = entry.Payload<IdeaPayload>();
            var local = TimeZoneInfo.ConvertTime(entry.OccurredAtUtc, zone);

            html.Raw("<article class=\"idea\"><h3>").Text(idea.Title).Raw("</h3>");

            if (!string.IsNullOrWhiteSpace(idea.Body))
            {
                html.Raw("<p>").Text(idea.Body).Raw("</p>");
            }

            html.Raw("<footer>");
            foreach (var theme in idea.Themes.Take(2))
            {
                html.Raw("<span class=\"theme\">").Text(theme).Raw("</span>");
            }

            if (idea.IsActionable)
            {
                html.Raw("<span class=\"badge-do\">сделать</span>");
            }

            html.Raw("<time>").Text(local.ToString("ddd, HH:mm", Html.Culture)).Raw("</time></footer></article>");
        }

        html.Raw("</div>");

        return new ReportSection("Идеи", $"{found.Count} за период", html.ToString());
    }
}

/// <summary>Чеклист вопросов, сгруппированный по темам.</summary>
public sealed class QuestionsSectionProvider(IEntryRepository entries) : IReportSectionProvider
{
    public string ModuleKey => NotesCategories.ModuleKey;

    public int Order => 30;

    public async Task<ReportSection?> BuildAsync(ReportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var found = await entries.GetByCategoryAsync(
            NotesCategories.ModuleKey, NotesCategories.Question, context.Period, ct);

        if (found.Count == 0)
        {
            return null;
        }

        var zone = context.Subject.TimeZone;
        var html = new Html();

        var groups = found
            .Select(e => (Entry: e, Payload: e.Payload<QuestionPayload>()))
            .GroupBy(x => x.Payload.Topic ?? "прочее")
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            html.Raw("<div class=\"qgroup\"><h3>").Text(group.Key).Raw("</h3><ul class=\"qlist\">");

            foreach (var (entry, question) in group.OrderBy(x => x.Entry.OccurredAtUtc))
            {
                var local = TimeZoneInfo.ConvertTime(entry.OccurredAtUtc, zone);

                html.Raw("<li><i class=\"box\"></i><div><span class=\"q\">")
                    .Text(question.Question).Raw("</span>");

                if (!string.IsNullOrWhiteSpace(question.Answer))
                {
                    html.Raw("<span class=\"a\">").Text(question.Answer).Raw("</span>");
                }

                html.Raw("</div><time>").Text(local.ToString("dd.MM", Html.Culture)).Raw("</time></li>");
            }

            html.Raw("</ul></div>");
        }

        var unanswered = found.Count(e => string.IsNullOrWhiteSpace(e.Payload<QuestionPayload>().Answer));
        return new ReportSection("Вопросы", $"{unanswered} без ответа", html.ToString());
    }
}
