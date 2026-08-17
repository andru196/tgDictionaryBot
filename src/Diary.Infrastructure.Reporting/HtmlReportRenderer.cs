using System.Reflection;
using System.Text;
using Diary.Application.Prompts;
using Diary.Application.Reporting;
using Diary.Application.Subjects;
using Diary.Domain;

namespace Diary.Infrastructure.Reporting;

/// <summary>
/// Собирает документ: шапка от ядра, секции от модулей. Ветвлений по типу модуля здесь нет.
/// </summary>
/// <remarks>
/// Результат — один самодостаточный файл: стили инлайном, графика в SVG, ноль внешних
/// запросов. Он должен открываться офлайн и через десять лет, поэтому ни CDN, ни шрифтов
/// из сети здесь нет и не будет.
/// </remarks>
public sealed class HtmlReportRenderer : IReportRenderer
{
    private static readonly string Css =
        PromptLoader.Load(Assembly.GetExecutingAssembly(), "report.css");

    public string Render(
        SubjectDefinition subject,
        DateRange period,
        DateRange? compareTo,
        ReportHeaderStats stats,
        IReadOnlyList<ReportSection> sections,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(sections);

        var zone = subject.TimeZone;
        var from = TimeZoneInfo.ConvertTime(period.Start, zone);
        var to = TimeZoneInfo.ConvertTime(period.End.AddSeconds(-1), zone);
        var generated = TimeZoneInfo.ConvertTime(generatedAtUtc, zone);

        var title = $"Дневник · {subject.DisplayName} · {from:d MMMM} — {to:d MMMM yyyy}";

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("<!doctype html>")
          .AppendLine("<html lang=\"ru\">")
          .AppendLine("<head>")
          .AppendLine("<meta charset=\"utf-8\">")
          .AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(Html.Enc(title)).AppendLine("</title>")
          .AppendLine("<style>")
          .AppendLine(Css)
          .AppendLine("</style>")
          .AppendLine("</head>")
          .AppendLine("<body>")
          .AppendLine("<div class=\"wrap\">");

        AppendHeader(sb, subject, period, compareTo, stats, from, to, generated);

        foreach (var section in sections)
        {
            sb.Append("<section><div class=\"sec-head\"><h2>")
              .Append(Html.Enc(section.Title))
              .Append("</h2>");

            if (!string.IsNullOrWhiteSpace(section.Count))
            {
                sb.Append("<span class=\"count\">").Append(Html.Enc(section.Count)).Append("</span>");
            }

            sb.AppendLine("<div class=\"rule\"></div></div>")
              .AppendLine(section.Html)
              .AppendLine("</section>");
        }

        sb.Append("<footer class=\"colophon\"><span>")
          .Append(Html.Enc($"reports/{subject.Key}/ · модули: {string.Join(", ", subject.Modules)}"))
          .AppendLine("</span><span>Ни один байт не покидал эту машину</span></footer>")
          .AppendLine("</div></body></html>");

        return sb.ToString();
    }

    private static void AppendHeader(
        StringBuilder sb,
        SubjectDefinition subject,
        DateRange period,
        DateRange? compareTo,
        ReportHeaderStats stats,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset generated)
    {
        var culture = Html.Culture;
        var heading = period.Days switch
        {
            <= 1 => from.ToString("d MMMM yyyy", culture),
            <= 8 => $"Неделя {culture.Calendar.GetWeekOfYear(from.DateTime, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday)}",
            <= 31 => from.ToString("MMMM yyyy", culture),
            _ => $"{from:MMMM} — {to:MMMM yyyy}",
        };

        var periodText = new StringBuilder()
            .Append(from.ToString("d MMMM", culture))
            .Append(" — ")
            .Append(to.ToString("d MMMM yyyy", culture))
            .Append(" · ")
            .Append(period.Days)
            .Append(period.Days is 1 ? " день" : period.Days is >= 2 and <= 4 ? " дня" : " дней");

        if (compareTo is { } other)
        {
            var otherFrom = TimeZoneInfo.ConvertTime(other.Start, subject.TimeZone);
            var otherTo = TimeZoneInfo.ConvertTime(other.End.AddSeconds(-1), subject.TimeZone);
            periodText.Append(" · сравнение с ")
                      .Append(otherFrom.ToString("d MMMM", culture))
                      .Append(" — ")
                      .Append(otherTo.ToString("d MMMM", culture));
        }

        periodText.Append(" · собрано ").Append(generated.ToString("d MMMM 'в' HH:mm", culture));

        sb.AppendLine("<header class=\"masthead\">")
          .Append("<div class=\"eyebrow\">Дневник · ")
          .Append(Html.Enc(subject.DisplayName))
          .AppendLine(" · отчёт за период</div>")
          .Append("<h1>").Append(Html.Enc(heading)).AppendLine("</h1>")
          .Append("<div class=\"period\">").Append(Html.Enc(periodText.ToString())).AppendLine("</div>")
          .AppendLine("<div class=\"stats\">");

        AppendStat(sb, stats.Messages.ToString(culture), "сообщений");
        AppendStat(sb, stats.VoiceMessages.ToString(culture), "голосовых");
        AppendStat(sb, Html.Duration(stats.SpeechDuration), "речи расшифровано");
        AppendStat(sb, stats.Entries.ToString(culture), "записей извлечено");

        sb.AppendLine("</div></header>");
    }

    private static void AppendStat(StringBuilder sb, string value, string label) =>
        sb.Append("<div class=\"stat\"><b>").Append(Html.Enc(value))
          .Append("</b><span>").Append(Html.Enc(label)).AppendLine("</span></div>");
}
