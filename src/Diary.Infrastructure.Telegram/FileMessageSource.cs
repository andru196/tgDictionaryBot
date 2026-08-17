using System.Text.Json;
using Diary.Application.Ports;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Infrastructure.Telegram;

/// <summary>Строка JSONL-файла с сообщениями.</summary>
public sealed record FileMessageRecord
{
    public required string Peer { get; init; }

    public required long MessageId { get; init; }

    public long? SenderId { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    public DateTimeOffset? EditedAt { get; init; }

    public string? Text { get; init; }

    /// <summary>Путь к файлу с голосовым относительно каталога JSONL.</summary>
    public string? VoicePath { get; init; }

    public double? VoiceSeconds { get; init; }

    public long? ReplyTo { get; init; }

    public bool Forwarded { get; init; }

    public long? ForwardedFrom { get; init; }
}

/// <summary>
/// Тот же порт, но источник — файл. Нужен не для продакшена, а чтобы весь пайплайн —
/// маршрутизация, разбор моделью, статистика, отчёт — прогонялся без единого обращения
/// к сети и без учётной записи Telegram.
/// </summary>
public sealed class FileMessageSource(string path, ILogger<FileMessageSource> logger) : IMessageSource
{
    private readonly Dictionary<string, long> _peerIds = [];
    private List<FileMessageRecord>? _records;

    public async Task<IReadOnlyDictionary<string, long>> ResolvePeersAsync(
        IReadOnlyCollection<string> peers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peers);
        await EnsureLoadedAsync(ct);

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in peers)
        {
            if (_peerIds.TryGetValue(peer, out var id))
            {
                result[peer] = id;
            }
            else
            {
                logger.LogWarning("В файле нет сообщений для чата «{Peer}».", peer);
            }
        }

        return result;
    }

    public async IAsyncEnumerable<IncomingMessage> FetchAsync(
        long peerId, long afterMessageId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);

        foreach (var record in _records!.OrderBy(r => r.MessageId))
        {
            ct.ThrowIfCancellationRequested();

            if (_peerIds[record.Peer] != peerId || record.MessageId <= afterMessageId)
            {
                continue;
            }

            var hasVoice = !string.IsNullOrWhiteSpace(record.VoicePath);
            var voiceFullPath = hasVoice
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, record.VoicePath!)
                : null;

            yield return new IncomingMessage
            {
                PeerId = peerId,
                TelegramMessageId = record.MessageId,
                SenderId = record.SenderId,
                SentAtUtc = record.SentAt.ToUniversalTime(),
                EditedAtUtc = record.EditedAt?.ToUniversalTime(),
                Kind = hasVoice ? MessageKind.Voice : MessageKind.Text,
                Text = record.Text,
                ReplyToTelegramMessageId = record.ReplyTo,
                IsForwarded = record.Forwarded,
                ForwardedFromId = record.ForwardedFrom,
                Voice = hasVoice
                    ? new VoiceInfo(
                        TimeSpan.FromSeconds(record.VoiceSeconds ?? 0),
                        "audio/ogg",
                        voiceFullPath is not null && File.Exists(voiceFullPath)
                            ? new FileInfo(voiceFullPath).Length
                            : 0)
                    : null,
                MediaHandle = voiceFullPath,
            };
        }
    }

    public async Task DownloadVoiceAsync(IncomingMessage message, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.MediaHandle is not string source || !File.Exists(source))
        {
            throw new FileNotFoundException(
                $"Файл голосового для сообщения {message.TelegramMessageId} не найден.",
                message.MediaHandle as string ?? "(не задан)");
        }

        await using var stream = File.OpenRead(source);
        await stream.CopyToAsync(destination, ct);
    }

    public Task ReactAsync(long peerId, long messageId, string emoji, CancellationToken ct)
    {
        logger.LogInformation("[файл] реакция {Emoji} на сообщение {Id}.", emoji, messageId);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long peerId, IReadOnlyList<long> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        // Файловый источник ничего не удаляет: единственная необратимая операция в системе
        // не должна срабатывать в отладочном режиме.
        logger.LogWarning("[файл] удаление {Count} сообщений пропущено.", messageIds.Count);
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(long peerId, long uptoMessageId, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_records is not null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Файл с сообщениями не найден: {Path.GetFullPath(path)}", path);
        }

        var records = new List<FileMessageRecord>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<FileMessageRecord>(line, DiaryJson.Options);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        var nextId = 1L;
        foreach (var peer in records.Select(r => r.Peer).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _peerIds[peer] = nextId++;
        }

        _records = records;
        logger.LogInformation(
            "Загружено {Count} сообщений из {Path} ({Peers} чат(ов)).",
            records.Count, path, _peerIds.Count);
    }
}
