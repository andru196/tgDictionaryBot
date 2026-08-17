using Diary.Application.Subjects;
using Diary.Domain;

namespace Diary.Application.Reporting;

/// <summary>Что известно секции о том, за какой период и по кому её строят.</summary>
public sealed record ReportContext(
    SubjectDefinition Subject,
    DateRange Period,
    DateRange? CompareTo,
    Granularity Granularity);

/// <summary>Готовая секция отчёта: заголовок, подпись под ним и разметка.</summary>
public sealed record ReportSection(string Title, string? Count, string Html);

/// <summary>
/// Модуль поставляет свою секцию, ядро собирает документ и сортирует по <see cref="Order"/>.
/// Ветвлений по типу модуля в сборщике нет.
/// </summary>
public interface IReportSectionProvider
{
    string ModuleKey { get; }

    int Order { get; }

    /// <summary>null — модулю нечего показать за этот период, секция не рисуется.</summary>
    Task<ReportSection?> BuildAsync(ReportContext context, CancellationToken ct);
}

/// <summary>Сводка для шапки отчёта.</summary>
public sealed record ReportHeaderStats(
    int Messages,
    int VoiceMessages,
    TimeSpan SpeechDuration,
    int Entries);

public interface IReportRenderer
{
    string Render(
        SubjectDefinition subject,
        DateRange period,
        DateRange? compareTo,
        ReportHeaderStats stats,
        IReadOnlyList<ReportSection> sections,
        DateTimeOffset generatedAtUtc);
}
