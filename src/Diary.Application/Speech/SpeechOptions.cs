namespace Diary.Application.Speech;

/// <summary>Как именно превращать голос в текст.</summary>
public enum SpeechReaderKind
{
    /// <summary>Каскад: декодер → Whisper. Выбор по умолчанию.</summary>
    Whisper = 0,

    /// <summary>Мультимодальная модель слушает звук сама.</summary>
    NativeAudio = 1,

    /// <summary>Слова от Whisper, сомнительные записи переслушивает аудио-модель.</summary>
    Hybrid = 2,
}

/// <summary>
/// Настройки распознавания живут в ядре, а не в реализации: их читают и каскад,
/// и мультимодальный путь, а они лежат в разных сборках.
/// </summary>
public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    public SpeechReaderKind Reader { get; set; } = SpeechReaderKind.Whisper;

    public string ModelPath { get; set; } = "models/ggml-large-v3-turbo.bin";

    /// <summary>Автодетект на коротких записях промахивается, поэтому язык фиксируется.</summary>
    public string Language { get; set; } = "ru";

    /// <summary>
    /// Подсказка словаря. Заметно поднимает точность на терминах, которые модель
    /// иначе распознаёт как похожие обиходные слова.
    /// </summary>
    public string InitialPrompt { get; set; } =
        "Дневник питания и самочувствия. Изжога, рефлюкс, заброс, вздутие, отрыжка, тошнота, " +
        "метеоризм, диарея, запор, тяжесть в желудке.";

    /// <summary>Модель с аудио-входом. Пусто — берётся модель роли извлечения.</summary>
    public string NativeAudioModel { get; set; } = string.Empty;

    /// <summary>
    /// Ниже этой уверенности гибридный режим переслушивает запись аудио-моделью.
    /// Применяется к единицам процентов записей, поэтому почти бесплатен.
    /// </summary>
    public float HybridConfidenceThreshold { get; set; } = 0.6f;
}
