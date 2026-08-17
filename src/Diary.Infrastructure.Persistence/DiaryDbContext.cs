using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Diary.Infrastructure.Persistence;

/// <summary>
/// База одного субъекта. Колонки SubjectId здесь нет: она была бы всегда одинаковой,
/// а запрос физически не может достать чужие данные, потому что их нет в файле.
/// </summary>
public sealed class DiaryDbContext(DbContextOptions<DiaryDbContext> options) : DbContext(options)
{
    public DbSet<CapturedMessage> Messages => Set<CapturedMessage>();

    public DbSet<DiaryEntry> Entries => Set<DiaryEntry>();

    public DbSet<DeletionLogRow> Deletions => Set<DeletionLogRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        DateTimeOffsetTicks.Apply(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var messageId = new ValueConverter<MessageId, Guid>(v => v.Value, v => new MessageId(v));
        var entryId = new ValueConverter<EntryId, Guid>(v => v.Value, v => new EntryId(v));
        var categoryKey = new ValueConverter<CategoryKey, string>(v => v.Value, v => new CategoryKey(v));
        var confidence = new ValueConverter<Confidence, double>(v => v.Value, v => new Confidence(v));

        modelBuilder.Entity<CapturedMessage>(entity =>
        {
            entity.ToTable("messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasConversion(messageId);

            // Идемпотентность приёма: повторный проход по истории не создаёт дублей.
            entity.HasIndex(m => new { m.PeerId, m.TelegramMessageId }).IsUnique();
            entity.HasIndex(m => m.State);
            entity.HasIndex(m => m.SentAtUtc);

            entity.Property(m => m.RawText);
            entity.Property(m => m.FailureReason);

            entity.Property(m => m.Hashtags)
                .HasConversion(
                    v => string.Join(' ', v),
                    v => v.Length == 0
                        ? new List<string>()
                        : v.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList())
                .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()));

            entity.OwnsOne(m => m.Voice, voice =>
            {
                voice.Property(v => v.RelativePath).HasColumnName("voice_path");
                voice.Property(v => v.Duration).HasColumnName("voice_duration");
                voice.Property(v => v.MimeType).HasColumnName("voice_mime");
                voice.Property(v => v.SizeBytes).HasColumnName("voice_size");
            });

            entity.OwnsOne(m => m.Transcript, transcript =>
            {
                transcript.Property(t => t.Text).HasColumnName("transcript_text");
                transcript.Property(t => t.Confidence).HasColumnName("transcript_confidence");
                transcript.Property(t => t.Engine).HasColumnName("transcript_engine");
                transcript.Property(t => t.CreatedAtUtc).HasColumnName("transcript_created_at");
            });

            entity.Ignore(m => m.EffectiveText);
        });

        modelBuilder.Entity<DiaryEntry>(entity =>
        {
            entity.ToTable("entries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasConversion(entryId);
            entity.Property(e => e.SourceMessageId).HasConversion(messageId);
            entity.Property(e => e.Category).HasConversion(categoryKey);
            entity.Property(e => e.Confidence).HasConversion(confidence);

            // Свойства без сеттеров EF не подхватывает по соглашению, а без них не может
            // связать конструктор — приходится объявлять явно.
            entity.Property(e => e.ModuleKey);
            entity.Property(e => e.OccurredAtUtc);
            entity.Property(e => e.TimeCertainty);
            entity.Property(e => e.RawFragment);
            entity.Property(e => e.PayloadJson);
            entity.Property(e => e.ExtractorVersion);

            // По этому индексу режутся любые периоды — статистика за произвольный срок
            // не должна упираться в полный скан.
            entity.HasIndex(e => e.OccurredAtUtc);
            entity.HasIndex(e => new { e.ModuleKey, e.Category, e.OccurredAtUtc });
            entity.HasIndex(e => e.SourceMessageId);
        });

        modelBuilder.Entity<DeletionLogRow>(entity =>
        {
            entity.ToTable("deletion_log");
            entity.HasKey(d => d.Id);
        });
    }
}

/// <summary>Строка журнала удалений: что исчезло из Telegram и когда.</summary>
public sealed class DeletionLogRow
{
    public int Id { get; set; }

    public long PeerId { get; set; }

    public long TelegramMessageId { get; set; }

    public DateTimeOffset DeletedAtUtc { get; set; }

    public int TranscriptLength { get; set; }

    public string ContentHash { get; set; } = string.Empty;
}

/// <summary>
/// SQLite не умеет сравнивать и сортировать DateTimeOffset, поэтому время хранится
/// как UTC-тики. Смещение при этом не теряется: в домене всё время и так в UTC.
/// </summary>
internal static class DateTimeOffsetTicks
{
    public static void Apply(ModelConfigurationBuilder builder) =>
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
}

/// <summary>Общая служебная база: курсоры и карантин привязаны к чату, а не к человеку.</summary>
public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public DbSet<SyncCursorRow> Cursors => Set<SyncCursorRow>();

    public DbSet<QuarantineRow> Quarantine => Set<QuarantineRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        DateTimeOffsetTicks.Apply(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SyncCursorRow>(entity =>
        {
            entity.ToTable("cursors");
            entity.HasKey(c => c.PeerId);
        });

        modelBuilder.Entity<QuarantineRow>(entity =>
        {
            entity.ToTable("quarantine");
            entity.HasKey(q => q.Id);
            entity.HasIndex(q => new { q.PeerId, q.TelegramMessageId }).IsUnique();
        });
    }
}

public sealed class SyncCursorRow
{
    public long PeerId { get; set; }

    public long LastProcessedMessageId { get; set; }

    public DateTimeOffset LastSyncAtUtc { get; set; }
}

public sealed class QuarantineRow
{
    public int Id { get; set; }

    public long PeerId { get; set; }

    public long TelegramMessageId { get; set; }

    public long? SenderId { get; set; }

    public DateTimeOffset SentAtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Preview { get; set; }
}

/// <summary>Маркер для регистрации <see cref="RetentionSettings"/>-независимых сервисов хранения.</summary>
internal static class PersistenceMarker;
