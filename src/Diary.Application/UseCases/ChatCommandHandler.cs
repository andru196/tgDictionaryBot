using System.Globalization;
using System.Text;
using Diary.Application.Commands;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.UseCases;

/// <summary>
/// Исполняет команды, присланные в сам чат, и отвечает туда же. Работает в скоупе
/// субъекта: чей чат — того и отчёт.
/// </summary>
public sealed class ChatCommandHandler(
    ReportHandler reports,
    IMessageRepository messages,
    IEntryRepository entries,
    IChatResponder responder,
    ISubjectContext subjectContext,
    ILogger<ChatCommandHandler> logger)
{
    public async Task ExecuteAsync(PendingCommand pending, string outputDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var subject = subjectContext.Subject;

        try
        {
            switch (pending.Command)
            {
                case ChatCommand.Report report:
                    await SendReportAsync(pending, report, outputDirectory, ct);
                    break;

                case ChatCommand.Status:
                    await responder.SendTextAsync(
                        pending.PeerId, await BuildStatusAsync(ct), pending.MessageId, ct);
                    break;

                case ChatCommand.Help:
                    await responder.SendTextAsync(
                        pending.PeerId, ChatCommandParser.HelpText, pending.MessageId, ct);
                    break;

                case ChatCommand.Unknown unknown:
                    await responder.SendTextAsync(
                        pending.PeerId,
                        $"{unknown.Hint}\n\nПодсказка: /help",
                        pending.MessageId,
                        ct);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Молча проглоченная команда выглядит как зависший сервис — сообщаем в чат.
            logger.LogError(ex, "Команда от субъекта «{Subject}» не выполнилась.", subject.Key);

            await responder.SendTextAsync(
                pending.PeerId, $"Не получилось: {ex.Message}", pending.MessageId, ct);
        }
    }

    private async Task SendReportAsync(
        PendingCommand pending, ChatCommand.Report report, string outputDirectory, CancellationToken ct)
    {
        var compare = report.Compare ? report.Period.Previous() : (DateRange?)null;

        var result = await reports.RunAsync(
            report.Period, compare, report.Granularity, outputDirectory, ct);

        if (result.Stats.Entries == 0)
        {
            await responder.SendTextAsync(
                pending.PeerId,
                $"За {Describe(report.Period)} записей нет. Возможно, ещё не всё разобрано — /status.",
                pending.MessageId,
                ct);
            return;
        }

        var caption = new StringBuilder()
            .Append("Отчёт за ").Append(Describe(report.Period)).Append('.').AppendLine()
            .Append(result.Stats.Messages).Append(" сообщений, ")
            .Append(result.Stats.Entries).Append(" записей")
            .Append(report.Compare ? ", со сравнением с предыдущим периодом." : ".")
            .ToString();

        await responder.SendDocumentAsync(pending.PeerId, result.Path, caption, pending.MessageId, ct);
    }

    private async Task<string> BuildStatusAsync(CancellationToken ct)
    {
        var counts = await messages.CountByStateAsync(ct);
        var total = await entries.CountAsync(ct);

        var text = new StringBuilder();
        text.Append("Записей в дневнике: ").Append(total).AppendLine().AppendLine();

        if (counts.Count == 0)
        {
            text.AppendLine("Сообщений пока нет.");
            return text.ToString();
        }

        foreach (var (state, count) in counts.OrderBy(c => c.Key))
        {
            text.Append("• ").Append(Describe(state)).Append(": ").Append(count).AppendLine();
        }

        var pending = counts.GetValueOrDefault(ProcessingState.Captured)
                    + counts.GetValueOrDefault(ProcessingState.Transcribed);

        if (pending > 0)
        {
            text.AppendLine().Append("Ждут обработки: ").Append(pending).Append('.');
        }

        return text.ToString();

        static string Describe(ProcessingState state) => state switch
        {
            ProcessingState.Captured => "ждут расшифровки",
            ProcessingState.Transcribed => "ждут разбора",
            ProcessingState.Extracted => "разобрано",
            ProcessingState.Failed => "ошибок",
            ProcessingState.Skipped => "пропущено",
            ProcessingState.Superseded => "устарело",
            _ => state.ToString(),
        };
    }

    private string Describe(DateRange period)
    {
        var zone = subjectContext.Subject.TimeZone;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var from = TimeZoneInfo.ConvertTime(period.Start, zone);
        var to = TimeZoneInfo.ConvertTime(period.End.AddSeconds(-1), zone);

        return from.Date == to.Date
            ? from.ToString("d MMMM", culture)
            : $"{from.ToString("d MMMM", culture)} — {to.ToString("d MMMM yyyy", culture)}";
    }
}
