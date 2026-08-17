namespace Diary.Domain;

public enum MessageKind
{
    Text = 0,
    Voice = 1,
    VideoNote = 2,
    Other = 3,
}

/// <summary>
/// Состояние обработки сообщения. Каждый шаг пайплайна работает только со своим входным
/// состоянием — это и даёт идемпотентность, и позволяет продолжить с места падения.
/// </summary>
public enum ProcessingState
{
    /// <summary>Забрано из Telegram, медиа сохранено локально.</summary>
    Captured = 0,

    /// <summary>Голос расшифрован (или сообщение текстовое и расшифровка не нужна).</summary>
    Transcribed = 1,

    /// <summary>Разобрано моделью, записи дневника созданы.</summary>
    Extracted = 2,

    /// <summary>Обработка провалилась; причина в <see cref="CapturedMessage.FailureReason"/>.</summary>
    Failed = 3,

    /// <summary>Сознательно пропущено: пересылка, служебное сообщение, неподдерживаемый тип.</summary>
    Skipped = 4,

    /// <summary>Сообщение отредактировано в Telegram; записи этой версии больше не актуальны.</summary>
    Superseded = 5,
}

/// <summary>Локально сохранённое голосовое сообщение.</summary>
public sealed record VoiceAsset(string RelativePath, TimeSpan Duration, string MimeType, long SizeBytes);

/// <summary>Расшифровка голосового с пометкой, чем и когда она сделана.</summary>
public sealed record Transcript(
    string Text,
    float Confidence,
    string Engine,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Сырьё: сообщение ровно в том виде, в каком пришло из Telegram, плюс расшифровка.
/// Смысл сообщения не меняется никогда — меняется только состояние обработки.
/// Именно поэтому переразбор новой моделью ничего не разрушает.
/// </summary>
public sealed class CapturedMessage
{
    private CapturedMessage()
    {
        RawText = null;
        Hashtags = [];
    }

    public MessageId Id { get; private init; }

    public long PeerId { get; private init; }

    public long TelegramMessageId { get; private init; }

    /// <summary>Автор сообщения. null для постов от имени канала.</summary>
    public long? SenderId { get; private init; }

    public DateTimeOffset SentAtUtc { get; private init; }

    public DateTimeOffset? EditedAtUtc { get; private set; }

    public MessageKind Kind { get; private init; }

    public string? RawText { get; private init; }

    public VoiceAsset? Voice { get; private set; }

    public long? ReplyToTelegramMessageId { get; private init; }

    public IReadOnlyList<string> Hashtags { get; private init; }

    public Transcript? Transcript { get; private set; }

    public ProcessingState State { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>
    /// Текст для разбора: расшифровка для голосового, исходный текст для текстового.
    /// </summary>
    public string? EffectiveText => Transcript?.Text ?? RawText;

    public static CapturedMessage Create(
        long peerId,
        long telegramMessageId,
        long? senderId,
        DateTimeOffset sentAtUtc,
        MessageKind kind,
        string? rawText,
        VoiceAsset? voice,
        long? replyToTelegramMessageId,
        IReadOnlyList<string> hashtags,
        DateTimeOffset? editedAtUtc = null)
    {
        if (kind == MessageKind.Voice && voice is null)
        {
            throw new ArgumentException("Голосовое сообщение должно иметь сохранённый файл.", nameof(voice));
        }

        return new CapturedMessage
        {
            Id = MessageId.New(),
            PeerId = peerId,
            TelegramMessageId = telegramMessageId,
            SenderId = senderId,
            SentAtUtc = sentAtUtc.ToUniversalTime(),
            EditedAtUtc = editedAtUtc?.ToUniversalTime(),
            Kind = kind,
            RawText = rawText,
            Voice = voice,
            ReplyToTelegramMessageId = replyToTelegramMessageId,
            Hashtags = hashtags,
            // Текстовому сообщению расшифровывать нечего — оно сразу готово к разбору.
            State = kind == MessageKind.Voice ? ProcessingState.Captured : ProcessingState.Transcribed,
        };
    }

    public void AttachTranscript(Transcript transcript)
    {
        if (State is not ProcessingState.Captured)
        {
            throw new InvalidOperationException(
                $"Расшифровку можно приложить только к сообщению в состоянии {ProcessingState.Captured}, " +
                $"текущее — {State}.");
        }

        Transcript = transcript;
        State = ProcessingState.Transcribed;
        FailureReason = null;
    }

    public void MarkExtracted()
    {
        if (State is not ProcessingState.Transcribed)
        {
            throw new InvalidOperationException(
                $"Разобранным можно пометить только сообщение в состоянии {ProcessingState.Transcribed}, " +
                $"текущее — {State}.");
        }

        State = ProcessingState.Extracted;
        FailureReason = null;
    }

    public void MarkFailed(string reason)
    {
        State = ProcessingState.Failed;
        FailureReason = reason;
    }

    public void MarkSkipped(string reason)
    {
        State = ProcessingState.Skipped;
        FailureReason = reason;
    }

    /// <summary>
    /// Сообщение отредактировано в Telegram: старые извлечённые записи больше не отражают
    /// содержимое, и его надо разобрать заново.
    /// </summary>
    public void MarkSuperseded() => State = ProcessingState.Superseded;

    /// <summary>Возврат в очередь обработки — для <c>diary reprocess</c>.</summary>
    public void ResetTo(ProcessingState state)
    {
        if (state is not (ProcessingState.Captured or ProcessingState.Transcribed))
        {
            throw new ArgumentException(
                $"Возвращать в обработку можно только в {ProcessingState.Captured} или " +
                $"{ProcessingState.Transcribed}, запрошено {state}.",
                nameof(state));
        }

        if (state == ProcessingState.Captured && Kind != MessageKind.Voice)
        {
            throw new InvalidOperationException("Текстовое сообщение нельзя вернуть на расшифровку.");
        }

        State = state;
        FailureReason = null;
    }
}
