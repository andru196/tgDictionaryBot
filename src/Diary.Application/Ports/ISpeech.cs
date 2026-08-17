namespace Diary.Application.Ports;

/// <summary>
/// Результат распознавания. Порт намеренно шире, чем «текст»: аудио-модели дают
/// просодию, которой в расшифровке нет, и подключаются за этим же интерфейсом.
/// </summary>
public sealed record Utterance(string Text, float Confidence, string Engine, string? Prosody = null);

/// <summary>Декодер сжатого аудио в PCM 16 кГц моно — то, что ждёт Whisper.</summary>
public interface IAudioDecoder
{
    Task<float[]> DecodeToPcm16kMonoAsync(Stream compressed, CancellationToken ct);
}

/// <summary>Распознавание речи. Каскад через Whisper либо нативная аудио-модель.</summary>
public interface IUtteranceReader
{
    Task<Utterance> ReadAsync(Stream audio, CancellationToken ct);
}

/// <summary>Хранилище голосовых файлов субъекта.</summary>
public interface IVoiceStorage
{
    /// <summary>Возвращает путь относительно каталога субъекта.</summary>
    Task<string> SaveAsync(long telegramMessageId, DateTimeOffset sentAt, string extension,
        Func<Stream, CancellationToken, Task> write, CancellationToken ct);

    Stream OpenRead(string relativePath);

    bool Exists(string relativePath);
}
