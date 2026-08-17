using Diary.Application.Ports;
using Diary.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diary.Infrastructure.Persistence;

public sealed class MessageRepository(DiaryDbContext db) : IMessageRepository
{
    public Task<CapturedMessage?> FindByTelegramIdAsync(long peerId, long telegramMessageId, CancellationToken ct) =>
        db.Messages.FirstOrDefaultAsync(
            m => m.PeerId == peerId && m.TelegramMessageId == telegramMessageId, ct);

    public async Task<IReadOnlyList<CapturedMessage>> GetByStateAsync(
        ProcessingState state, int limit, CancellationToken ct) =>
        await db.Messages
            .Where(m => m.State == state)
            .OrderBy(m => m.SentAtUtc)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CapturedMessage>> GetByPeriodAsync(DateRange period, CancellationToken ct) =>
        await db.Messages
            .Where(m => m.SentAtUtc >= period.Start && m.SentAtUtc < period.End)
            .OrderBy(m => m.SentAtUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CapturedMessage>> GetForRetentionAsync(
        ProcessingState requiredState, DateTimeOffset sentBefore, CancellationToken ct) =>
        await db.Messages
            .Where(m => m.State == requiredState && m.SentAtUtc < sentBefore)
            .OrderBy(m => m.SentAtUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<ProcessingState, int>> CountByStateAsync(CancellationToken ct) =>
        await db.Messages
            .GroupBy(m => m.State)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task<IReadOnlyList<CapturedMessage>> GetProblematicAsync(int limit, CancellationToken ct) =>
        await db.Messages
            .Where(m => m.State == ProcessingState.Failed || m.State == ProcessingState.Skipped)
            .OrderByDescending(m => m.SentAtUtc)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(CapturedMessage message, CancellationToken ct) =>
        await db.Messages.AddAsync(message, ct);

    public async Task<CapturedMessage?> FindAsync(MessageId id, CancellationToken ct) =>
        await db.Messages.FindAsync([id], ct);

    public async Task<int> ResetStateAsync(
        ProcessingState from, ProcessingState target, DateTimeOffset? since, CancellationToken ct)
    {
        var query = db.Messages.Where(m => m.State == from);
        if (since is { } moment)
        {
            query = query.Where(m => m.SentAtUtc >= moment);
        }

        var affected = await query.ToListAsync(ct);
        foreach (var message in affected)
        {
            // Текстовое сообщение расшифровывать нечего — его возвращаем только к разбору.
            var destination = target == ProcessingState.Captured && message.Kind != MessageKind.Voice
                ? ProcessingState.Transcribed
                : target;

            message.ResetTo(destination);
        }

        return affected.Count;
    }
}

public sealed class EntryRepository(DiaryDbContext db) : IEntryRepository
{
    public async Task<IReadOnlyList<DiaryEntry>> GetAsync(DateRange period, CancellationToken ct) =>
        await db.Entries
            .Where(e => e.OccurredAtUtc >= period.Start && e.OccurredAtUtc < period.End)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DiaryEntry>> GetByCategoryAsync(
        string moduleKey, CategoryKey category, DateRange period, CancellationToken ct)
    {
        var key = category.Value;
        return await db.Entries
            .Where(e => e.ModuleKey == moduleKey
                     && e.Category == new CategoryKey(key)
                     && e.OccurredAtUtc >= period.Start
                     && e.OccurredAtUtc < period.End)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync(ct);
    }

    public async Task AddRangeAsync(IReadOnlyList<DiaryEntry> entries, CancellationToken ct) =>
        await db.Entries.AddRangeAsync(entries, ct);

    public async Task RemoveBySourceAsync(IReadOnlyList<MessageId> sourceMessageIds, CancellationToken ct)
    {
        if (sourceMessageIds.Count == 0)
        {
            return;
        }

        var stale = await db.Entries
            .Where(e => sourceMessageIds.Contains(e.SourceMessageId))
            .ToListAsync(ct);

        db.Entries.RemoveRange(stale);
    }

    public Task<int> CountAsync(CancellationToken ct) => db.Entries.CountAsync(ct);
}

public sealed class DeletionLog(DiaryDbContext db) : IDeletionLog
{
    public async Task AddRangeAsync(IReadOnlyList<DeletionRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);

        var rows = records.Select(r => new DeletionLogRow
        {
            PeerId = r.PeerId,
            TelegramMessageId = r.TelegramMessageId,
            DeletedAtUtc = r.DeletedAtUtc,
            TranscriptLength = r.TranscriptLength,
            ContentHash = r.ContentHash,
        });

        await db.Deletions.AddRangeAsync(rows, ct);
    }
}

public sealed class UnitOfWork(DiaryDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class SyncCursorStore(SyncDbContext db) : ISyncCursorStore
{
    public async Task<SyncCursor?> GetAsync(long peerId, CancellationToken ct)
    {
        var row = await db.Cursors.FindAsync([peerId], ct);
        return row is null ? null : new SyncCursor(row.PeerId, row.LastProcessedMessageId, row.LastSyncAtUtc);
    }

    public async Task SaveAsync(SyncCursor cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var row = await db.Cursors.FindAsync([cursor.PeerId], ct);
        if (row is null)
        {
            db.Cursors.Add(new SyncCursorRow
            {
                PeerId = cursor.PeerId,
                LastProcessedMessageId = cursor.LastProcessedMessageId,
                LastSyncAtUtc = cursor.LastSyncAtUtc,
            });
        }
        else
        {
            row.LastProcessedMessageId = cursor.LastProcessedMessageId;
            row.LastSyncAtUtc = cursor.LastSyncAtUtc;
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class QuarantineStore(SyncDbContext db) : IQuarantineStore
{
    public async Task AddAsync(QuarantinedMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var exists = await db.Quarantine.AnyAsync(
            q => q.PeerId == message.PeerId && q.TelegramMessageId == message.TelegramMessageId, ct);

        if (exists)
        {
            return;
        }

        db.Quarantine.Add(new QuarantineRow
        {
            PeerId = message.PeerId,
            TelegramMessageId = message.TelegramMessageId,
            SenderId = message.SenderId,
            SentAtUtc = message.SentAtUtc,
            Reason = message.Reason,
            Preview = message.Preview,
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<QuarantinedMessage>> GetAllAsync(CancellationToken ct) =>
        await db.Quarantine
            .OrderBy(q => q.SentAtUtc)
            .Select(q => new QuarantinedMessage(
                q.PeerId, q.TelegramMessageId, q.SenderId, q.SentAtUtc, q.Reason, q.Preview))
            .ToListAsync(ct);

    public Task<int> CountAsync(CancellationToken ct) => db.Quarantine.CountAsync(ct);

    public async Task<int> ClearAsync(CancellationToken ct)
    {
        var rows = await db.Quarantine.ToListAsync(ct);
        db.Quarantine.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
