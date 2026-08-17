using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.UseCases;

public sealed record SyncReport(
    int Fetched,
    int Stored,
    int Quarantined,
    int Skipped,
    int Superseded,
    IReadOnlyDictionary<string, int> PerSubject);

/// <summary>
/// Забирает новое из Telegram и раскладывает по субъектам. Курсор двигается только после
/// того, как сообщение и его медиа сохранены локально: падение посреди синхронизации
/// означает повтор нескольких сообщений, а не потерю.
/// </summary>
public sealed class SyncHandler(
    IMessageSource source,
    ISubjectScopeFactory scopeFactory,
    ISyncCursorStore cursors,
    IQuarantineStore quarantine,
    ForwardPolicy forwardPolicy,
    bool markAsRead,
    TimeProvider clock,
    ILogger<SyncHandler> logger)
{
    public async Task<SyncReport> RunAsync(IReadOnlyList<SubjectDefinition> subjects, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        var peerNames = subjects.SelectMany(s => s.Sources).Select(s => s.Peer).Distinct().ToArray();
        if (peerNames.Length == 0)
        {
            logger.LogWarning("Ни у одного субъекта не задан источник сообщений.");
            return new SyncReport(0, 0, 0, 0, 0, new Dictionary<string, int>());
        }

        var resolved = await source.ResolvePeersAsync(peerNames, ct);
        foreach (var name in peerNames.Where(n => !resolved.ContainsKey(n)))
        {
            logger.LogError("Чат «{Peer}» не найден или недоступен этому аккаунту.", name);
        }

        var router = new SubjectRouter(subjects, resolved, forwardPolicy);
        var scopes = new Dictionary<SubjectKey, ISubjectScope>();

        int fetched = 0, stored = 0, quarantined = 0, skipped = 0, superseded = 0;
        var perSubject = new Dictionary<string, int>();

        try
        {
            foreach (var peerId in resolved.Values.Distinct())
            {
                var cursor = await cursors.GetAsync(peerId, ct);
                var lastId = cursor?.LastProcessedMessageId ?? 0;
                var highWater = lastId;

                await foreach (var incoming in source.FetchAsync(peerId, lastId, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    fetched++;

                    var routing = router.Route(incoming);
                    if (routing is SubjectRouting.Unassigned unassigned)
                    {
                        if (unassigned.Reason == UnassignedReason.Forwarded)
                        {
                            skipped++;
                        }
                        else
                        {
                            await quarantine.AddAsync(
                                new QuarantinedMessage(
                                    incoming.PeerId, incoming.TelegramMessageId, unassigned.SenderId,
                                    incoming.SentAtUtc, unassigned.Description, Preview(incoming)),
                                ct);
                            quarantined++;
                        }

                        highWater = Math.Max(highWater, incoming.TelegramMessageId);
                        continue;
                    }

                    var subjectKey = ((SubjectRouting.Assigned)routing).Subject;
                    if (!scopes.TryGetValue(subjectKey, out var scope))
                    {
                        scopes[subjectKey] = scope = scopeFactory.Create(subjectKey);
                    }

                    var outcome = await StoreAsync(scope, incoming, ct);
                    switch (outcome)
                    {
                        case StoreOutcome.Stored:
                            stored++;
                            perSubject[subjectKey.Value] = perSubject.GetValueOrDefault(subjectKey.Value) + 1;
                            break;
                        case StoreOutcome.Superseded:
                            superseded++;
                            perSubject[subjectKey.Value] = perSubject.GetValueOrDefault(subjectKey.Value) + 1;
                            break;
                        case StoreOutcome.Duplicate:
                            break;
                        default:
                            skipped++;
                            break;
                    }

                    highWater = Math.Max(highWater, incoming.TelegramMessageId);

                    // Курсор двигается после сохранения, а не после чтения.
                    await cursors.SaveAsync(new SyncCursor(peerId, highWater, clock.GetUtcNow()), ct);
                }

                if (markAsRead && highWater > lastId)
                {
                    await source.MarkReadAsync(peerId, highWater, ct);
                }
            }
        }
        finally
        {
            foreach (var scope in scopes.Values)
            {
                scope.Dispose();
            }
        }

        return new SyncReport(fetched, stored, quarantined, skipped, superseded, perSubject);
    }

    private enum StoreOutcome { Stored, Duplicate, Superseded, Unsupported }

    private static string? Preview(IncomingMessage message) =>
        message.Text is { Length: > 0 } text
            ? text[..Math.Min(120, text.Length)]
            : message.Kind == MessageKind.Voice ? "[голосовое]" : null;

    private async Task<StoreOutcome> StoreAsync(ISubjectScope scope, IncomingMessage incoming, CancellationToken ct)
    {
        var messages = scope.Resolve<IMessageRepository>();
        var entries = scope.Resolve<IEntryRepository>();
        var uow = scope.Resolve<IUnitOfWork>();

        var existing = await messages.FindByTelegramIdAsync(incoming.PeerId, incoming.TelegramMessageId, ct);
        if (existing is not null)
        {
            // Отредактированное задним числом сообщение обязано попасть на переразбор,
            // иначе исправленная опечатка в еде останется в статистике навсегда.
            var wasEdited = incoming.EditedAtUtc is { } edited &&
                            (existing.EditedAtUtc is null || edited > existing.EditedAtUtc);
            if (!wasEdited)
            {
                return StoreOutcome.Duplicate;
            }

            existing.MarkSuperseded();
            await entries.RemoveBySourceAsync([existing.Id], ct);
        }

        if (incoming.Kind is MessageKind.Other or MessageKind.VideoNote)
        {
            return StoreOutcome.Unsupported;
        }

        VoiceAsset? voice = null;
        if (incoming is { Kind: MessageKind.Voice, Voice: { } info })
        {
            var storage = scope.Resolve<IVoiceStorage>();
            var extension = info.MimeType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ? ".ogg" : ".bin";
            var relative = await storage.SaveAsync(
                incoming.TelegramMessageId, incoming.SentAtUtc, extension,
                (stream, token) => source.DownloadVoiceAsync(incoming, stream, token), ct);

            voice = new VoiceAsset(relative, info.Duration, info.MimeType, info.SizeBytes);
        }
        else if (string.IsNullOrWhiteSpace(incoming.Text))
        {
            return StoreOutcome.Unsupported;
        }

        var message = CapturedMessage.Create(
            incoming.PeerId,
            incoming.TelegramMessageId,
            incoming.SenderId,
            incoming.SentAtUtc,
            incoming.Kind,
            incoming.Text,
            voice,
            incoming.ReplyToTelegramMessageId,
            ExtractHashtags(incoming.Text),
            incoming.EditedAtUtc);

        await messages.AddAsync(message, ct);
        await uow.SaveChangesAsync(ct);

        return existing is null ? StoreOutcome.Stored : StoreOutcome.Superseded;
    }

    internal static IReadOnlyList<string> ExtractHashtags(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        List<string>? tags = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '#')
            {
                continue;
            }

            var start = i + 1;
            var end = start;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            if (end > start)
            {
                (tags ??= []).Add(text[start..end].ToLowerInvariant());
                i = end;
            }
        }

        return tags is null ? [] : tags;
    }
}
