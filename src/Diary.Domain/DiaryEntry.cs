using System.Text.Json;

namespace Diary.Domain;

/// <summary>Уверенность модели в извлечённой записи, 0..1.</summary>
public readonly record struct Confidence
{
    public Confidence(double value)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Уверенность должна лежать в [0, 1].");
        }

        Value = value;
    }

    public double Value { get; }

    public static Confidence Certain => new(1.0);

    public override string ToString() => Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Насколько точно известно время события.</summary>
public enum TimeCertainty
{
    /// <summary>Время события — время отправки сообщения.</summary>
    Exact = 0,

    /// <summary>Относительное указание разрешено в абсолютное («вчера вечером» → 20:00).</summary>
    Resolved = 1,

    /// <summary>Время не названо и восстановлено приблизительно.</summary>
    Approximate = 2,
}

/// <summary>
/// Смысловая единица дневника. Одно сообщение даёт 1..N записей: «съел борщ, кстати идея…»
/// — это две записи из одного голосового.
/// </summary>
/// <remarks>
/// Тип один на все виды записей: специфика лежит в <see cref="Payload"/> и типизируется модулем.
/// Новый вид записей не требует ни новой таблицы, ни миграции.
/// </remarks>
public sealed class DiaryEntry
{
    private DiaryEntry(
        EntryId id,
        MessageId sourceMessageId,
        string moduleKey,
        CategoryKey category,
        DateTimeOffset occurredAtUtc,
        TimeCertainty timeCertainty,
        string rawFragment,
        Confidence confidence,
        string payloadJson,
        string extractorVersion)
    {
        Id = id;
        SourceMessageId = sourceMessageId;
        ModuleKey = moduleKey;
        Category = category;
        OccurredAtUtc = occurredAtUtc;
        TimeCertainty = timeCertainty;
        RawFragment = rawFragment;
        Confidence = confidence;
        PayloadJson = payloadJson;
        ExtractorVersion = extractorVersion;
    }

    public EntryId Id { get; }

    /// <summary>Исходное сообщение — из записи всегда можно вернуться к тому, что было сказано.</summary>
    public MessageId SourceMessageId { get; }

    public string ModuleKey { get; }

    public CategoryKey Category { get; }

    /// <summary>Когда событие произошло. Может отличаться от времени отправки сообщения.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    public TimeCertainty TimeCertainty { get; }

    /// <summary>Фрагмент расшифровки, породивший запись.</summary>
    public string RawFragment { get; }

    public Confidence Confidence { get; }

    public string PayloadJson { get; private set; }

    /// <summary>Модель и версия промпта — чтобы знать, что именно переразбирать после апгрейда.</summary>
    public string ExtractorVersion { get; }

    public static DiaryEntry Create<TPayload>(
        MessageId sourceMessageId,
        string moduleKey,
        CategoryKey category,
        DateTimeOffset occurredAtUtc,
        TimeCertainty timeCertainty,
        string rawFragment,
        Confidence confidence,
        TPayload payload,
        string extractorVersion)
        where TPayload : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentNullException.ThrowIfNull(payload);

        return new DiaryEntry(
            EntryId.New(),
            sourceMessageId,
            moduleKey,
            category,
            occurredAtUtc.ToUniversalTime(),
            timeCertainty,
            rawFragment,
            confidence,
            JsonSerializer.Serialize(payload, DiaryJson.Options),
            extractorVersion);
    }

    /// <summary>Восстановление из хранилища.</summary>
    public static DiaryEntry Rehydrate(
        EntryId id,
        MessageId sourceMessageId,
        string moduleKey,
        CategoryKey category,
        DateTimeOffset occurredAtUtc,
        TimeCertainty timeCertainty,
        string rawFragment,
        Confidence confidence,
        string payloadJson,
        string extractorVersion) =>
        new(id, sourceMessageId, moduleKey, category, occurredAtUtc, timeCertainty,
            rawFragment, confidence, payloadJson, extractorVersion);

    public TPayload Payload<TPayload>() =>
        JsonSerializer.Deserialize<TPayload>(PayloadJson, DiaryJson.Options)
        ?? throw new InvalidOperationException(
            $"Не удалось разобрать payload записи {Id} как {typeof(TPayload).Name}.");

    /// <summary>
    /// Дополняет запись тем, что стало известно позже: например, ответом на вопрос.
    /// Категорию, время и исходник не трогает — они пришли из сообщения и неизменны.
    /// </summary>
    public void UpdatePayload<TPayload>(TPayload payload)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        PayloadJson = JsonSerializer.Serialize(payload, DiaryJson.Options);
    }
}
