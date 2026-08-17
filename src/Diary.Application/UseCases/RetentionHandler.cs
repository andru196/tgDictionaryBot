using System.Security.Cryptography;
using System.Text;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.UseCases;

public sealed record RetentionReport(RetentionMode Mode, int Affected, bool DryRun, IReadOnlyList<string> Preview);

/// <summary>
/// Единственное место в системе, которое что-то удаляет. Поэтому предохранителей больше,
/// чем логики: стирается только доведённое до конца, старше порога, с локальной копией
/// текста и скачанным аудио, и никогда — провалившееся.
/// </summary>
public sealed class RetentionHandler(
    IMessageSource source,
    IMessageRepository messages,
    IVoiceStorage storage,
    IDeletionLog deletionLog,
    ISubjectContext subjectContext,
    IUnitOfWork uow,
    TimeProvider clock,
    ILogger<RetentionHandler> logger)
{
    public async Task<RetentionReport> RunAsync(RetentionSettings? overrideSettings, CancellationToken ct)
    {
        var subject = subjectContext.Subject;
        var settings = overrideSettings ?? subject.Retention;

        if (settings.Mode == RetentionMode.Keep)
        {
            return new RetentionReport(RetentionMode.Keep, 0, settings.DryRun, []);
        }

        var cutoff = clock.GetUtcNow() - settings.MinAge;
        var candidates = await messages.GetForRetentionAsync(settings.RequiresState, cutoff, ct);

        var eligible = new List<CapturedMessage>(candidates.Count);
        foreach (var message in candidates)
        {
            if (settings.KeepFailed && message.State == ProcessingState.Failed)
            {
                continue;
            }

            // Нельзя удалять то, чего нет локально: аудио должно быть скачано,
            // иначе переразбор новой моделью станет невозможен.
            if (message.Voice is { } voice && !storage.Exists(voice.RelativePath))
            {
                logger.LogWarning(
                    "Сообщение {Id} пропущено: файл {Path} отсутствует локально.",
                    message.TelegramMessageId, voice.RelativePath);
                continue;
            }

            if (string.IsNullOrWhiteSpace(message.EffectiveText))
            {
                continue;
            }

            eligible.Add(message);
        }

        var preview = eligible
            .Take(10)
            .Select(m => $"#{m.TelegramMessageId} {m.SentAtUtc:yyyy-MM-dd HH:mm} — {Shorten(m.EffectiveText)}")
            .ToArray();

        if (settings.DryRun)
        {
            logger.LogInformation(
                "Пробный прогон: под {Mode} попадает {Count} сообщений субъекта «{Subject}».",
                settings.Mode, eligible.Count, subject.Key);
            return new RetentionReport(settings.Mode, eligible.Count, true, preview);
        }

        if (eligible.Count == 0)
        {
            return new RetentionReport(settings.Mode, 0, false, []);
        }

        if (settings.Mode == RetentionMode.React)
        {
            foreach (var message in eligible)
            {
                await source.ReactAsync(message.PeerId, message.TelegramMessageId, "✅", ct);
            }

            return new RetentionReport(RetentionMode.React, eligible.Count, false, preview);
        }

        var now = clock.GetUtcNow();
        var records = eligible
            .Select(m => new DeletionRecord(
                m.PeerId, m.TelegramMessageId, now,
                m.EffectiveText?.Length ?? 0,
                Hash(m.EffectiveText ?? string.Empty)))
            .ToArray();

        // Журнал пишется до удаления: если Telegram ответит ошибкой на половине пачки,
        // лучше иметь лишнюю запись в журнале, чем потерять след удалённого.
        await deletionLog.AddRangeAsync(records, ct);
        await uow.SaveChangesAsync(ct);

        foreach (var group in eligible.GroupBy(m => m.PeerId))
        {
            await source.DeleteAsync(group.Key, [.. group.Select(m => m.TelegramMessageId)], ct);
        }

        logger.LogInformation(
            "Удалено {Count} сообщений субъекта «{Subject}» из Telegram. Локальные копии сохранены.",
            eligible.Count, subject.Key);

        return new RetentionReport(RetentionMode.Delete, eligible.Count, false, preview);
    }

    private static string Shorten(string? text) =>
        text is null ? string.Empty : text.Length <= 60 ? text : text[..60] + "…";

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
