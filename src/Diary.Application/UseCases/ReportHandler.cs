using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Reporting;
using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.UseCases;

public sealed record ReportResult(string Path, int Sections, ReportHeaderStats Stats);

/// <summary>
/// Собирает отчёт: шапка из ядра, секции — от модулей субъекта. Ветвлений по типу модуля
/// здесь нет, порядок задаётся самими секциями.
/// </summary>
public sealed class ReportHandler(
    IMessageRepository messages,
    IEntryRepository entries,
    IEnumerable<IReportSectionProvider> providers,
    IReportRenderer renderer,
    ISubjectContext subjectContext,
    TimeProvider clock,
    ILogger<ReportHandler> logger)
{
    public async Task<ReportResult> RunAsync(
        DateRange period,
        DateRange? compareTo,
        Granularity granularity,
        string outputDirectory,
        CancellationToken ct)
    {
        var subject = subjectContext.Subject;
        var context = new ReportContext(subject, period, compareTo, granularity);

        var enabled = providers
            .Where(p => subject.Modules.Contains(p.ModuleKey, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p.Order)
            .ToArray();

        var sections = new List<ReportSection>(enabled.Length);
        foreach (var provider in enabled)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await provider.BuildAsync(context, ct) is { } section)
                {
                    sections.Add(section);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Упавшая секция не должна ронять весь отчёт: остальные данные всё ещё полезны.
                logger.LogError(ex, "Секция модуля «{Module}» не собралась.", provider.ModuleKey);
            }
        }

        var stats = await BuildStatsAsync(period, ct);
        var html = renderer.Render(subject, period, compareTo, stats, sections, clock.GetUtcNow());

        var directory = Path.Combine(outputDirectory, subject.Key.Value);
        Directory.CreateDirectory(directory);

        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), subject.TimeZone);
        var name = $"{local:yyyy-MM-dd}-{period.Days}d.html";
        var path = Path.Combine(directory, name);

        await File.WriteAllTextAsync(path, html, ct);

        return new ReportResult(Path.GetFullPath(path), sections.Count, stats);
    }

    private async Task<ReportHeaderStats> BuildStatsAsync(DateRange period, CancellationToken ct)
    {
        var captured = await messages.GetByPeriodAsync(period, ct);
        var voice = captured.Where(m => m.Voice is not null).ToArray();
        var speech = voice.Aggregate(TimeSpan.Zero, (acc, m) => acc + m.Voice!.Duration);
        var extracted = await entries.GetAsync(period, ct);

        return new ReportHeaderStats(captured.Count, voice.Length, speech, extracted.Count);
    }
}
