using Diary.Domain;

namespace Diary.Application.Ports;

/// <summary>
/// Хранилище сообщений субъекта. Методы не принимают SubjectId: репозиторий уже
/// привязан к базе конкретного человека, поэтому подставить чужого нельзя.
/// </summary>
public interface IMessageRepository
{
    Task<CapturedMessage?> FindByTelegramIdAsync(long peerId, long telegramMessageId, CancellationToken ct);

    Task<IReadOnlyList<CapturedMessage>> GetByStateAsync(ProcessingState state, int limit, CancellationToken ct);

    Task<IReadOnlyList<CapturedMessage>> GetByPeriodAsync(DateRange period, CancellationToken ct);

    Task<IReadOnlyList<CapturedMessage>> GetForRetentionAsync(
        ProcessingState requiredState, DateTimeOffset sentBefore, CancellationToken ct);

    Task<IReadOnlyDictionary<ProcessingState, int>> CountByStateAsync(CancellationToken ct);

    /// <summary>
    /// Сообщения, которые не дошли до конца, с причинами. Без этого «пропущено 1»
    /// в сводке — тупик: непонятно, что случилось и надо ли что-то делать.
    /// </summary>
    Task<IReadOnlyList<CapturedMessage>> GetProblematicAsync(int limit, CancellationToken ct);

    Task AddAsync(CapturedMessage message, CancellationToken ct);

    Task<CapturedMessage?> FindAsync(MessageId id, CancellationToken ct);

    /// <summary>Пометить как отредактированное и вернуть на разбор.</summary>
    Task<int> ResetStateAsync(
        ProcessingState from, ProcessingState target, DateTimeOffset? since, CancellationToken ct);
}

public interface IEntryRepository
{
    Task<IReadOnlyList<DiaryEntry>> GetAsync(DateRange period, CancellationToken ct);

    Task<IReadOnlyList<DiaryEntry>> GetByCategoryAsync(
        string moduleKey, CategoryKey category, DateRange period, CancellationToken ct);

    Task AddRangeAsync(IReadOnlyList<DiaryEntry> entries, CancellationToken ct);

    /// <summary>Удалить записи, извлечённые из указанных сообщений — перед переразбором.</summary>
    Task RemoveBySourceAsync(IReadOnlyList<MessageId> sourceMessageIds, CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>
/// Сообщение, которое не удалось привязать к субъекту. Складывается сюда, а не
/// выбрасывается и не приписывается «ближайшему»: потерять запись плохо, загрязнить
/// чужую статистику — хуже.
/// </summary>
public sealed record QuarantinedMessage(
    long PeerId,
    long TelegramMessageId,
    long? SenderId,
    DateTimeOffset SentAtUtc,
    string Reason,
    string? Preview);

public interface IQuarantineStore
{
    Task AddAsync(QuarantinedMessage message, CancellationToken ct);

    Task<IReadOnlyList<QuarantinedMessage>> GetAllAsync(CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>Журнал удалений: даже если файл потом потеряется, видно, что исчезло и когда.</summary>
public sealed record DeletionRecord(
    long PeerId,
    long TelegramMessageId,
    DateTimeOffset DeletedAtUtc,
    int TranscriptLength,
    string ContentHash);

public interface IDeletionLog
{
    Task AddRangeAsync(IReadOnlyList<DeletionRecord> records, CancellationToken ct);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}
