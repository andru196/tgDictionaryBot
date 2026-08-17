using Diary.Domain;

namespace Diary.Application.Ports;

/// <summary>Сообщение в том виде, в каком его отдал транспорт, до привязки к субъекту.</summary>
public sealed record IncomingMessage
{
    public required long PeerId { get; init; }

    public required long TelegramMessageId { get; init; }

    /// <summary>null для постов от имени канала.</summary>
    public long? SenderId { get; init; }

    public required DateTimeOffset SentAtUtc { get; init; }

    public DateTimeOffset? EditedAtUtc { get; init; }

    public required MessageKind Kind { get; init; }

    public string? Text { get; init; }

    public long? ReplyToTelegramMessageId { get; init; }

    public bool IsForwarded { get; init; }

    public long? ForwardedFromId { get; init; }

    public VoiceInfo? Voice { get; init; }

    /// <summary>Непрозрачная для приложения ссылка на медиа, понятная только транспорту.</summary>
    public object? MediaHandle { get; init; }
}

public sealed record VoiceInfo(TimeSpan Duration, string MimeType, long SizeBytes);

/// <summary>
/// Источник сообщений. Реализуется транспортом (MTProto) и файловым импортом для отладки
/// и тестов — весь пайплайн прогоняется без единого обращения к сети.
/// </summary>
public interface IMessageSource : IAsyncDisposable
{
    /// <summary>Разрешает peer'ы из конфига в числовые id. Делается один раз при подключении.</summary>
    Task<IReadOnlyDictionary<string, long>> ResolvePeersAsync(
        IReadOnlyCollection<string> peers,
        CancellationToken ct);

    /// <summary>
    /// Сообщения новее курсора, по возрастанию id. Первый запуск с нулевым курсором
    /// вычитывает историю целиком.
    /// </summary>
    IAsyncEnumerable<IncomingMessage> FetchAsync(long peerId, long afterMessageId, CancellationToken ct);

    Task DownloadVoiceAsync(IncomingMessage message, Stream destination, CancellationToken ct);

    /// <summary>Поставить реакцию — режим <see cref="Subjects.RetentionMode.React"/>.</summary>
    Task ReactAsync(long peerId, long messageId, string emoji, CancellationToken ct);

    Task DeleteAsync(long peerId, IReadOnlyList<long> messageIds, CancellationToken ct);

    Task MarkReadAsync(long peerId, long uptoMessageId, CancellationToken ct);
}

/// <summary>
/// Докуда дочитан чат. Хранится локально, а не в Telegram: прочитанность в мессенджере
/// меняется с телефона и к состоянию обработки отношения не имеет.
/// </summary>
public sealed record SyncCursor(long PeerId, long LastProcessedMessageId, DateTimeOffset LastSyncAtUtc);

public interface ISyncCursorStore
{
    Task<SyncCursor?> GetAsync(long peerId, CancellationToken ct);

    Task SaveAsync(SyncCursor cursor, CancellationToken ct);
}
