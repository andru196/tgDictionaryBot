using Diary.Domain;

namespace Diary.Application.Subjects;

/// <summary>Что делать с сообщением после того, как оно полностью разобрано.</summary>
public enum RetentionMode
{
    /// <summary>Не трогать (по умолчанию).</summary>
    Keep = 0,

    /// <summary>Поставить реакцию — видно с телефона, что учтено, ничего не теряется.</summary>
    React = 1,

    /// <summary>Удалить из Telegram. Единственная необратимая операция в системе.</summary>
    Delete = 2,
}

/// <summary>Как поступать с пересланными сообщениями.</summary>
public enum ForwardPolicy
{
    /// <summary>Переслать себе рецепт — не значит его съесть. Дефолт.</summary>
    Skip = 0,

    /// <summary>Записать на того, кто переслал.</summary>
    Forwarder = 1,

    /// <summary>Записать на исходного автора, если он сопоставлен субъекту.</summary>
    OriginalAuthor = 2,
}

/// <summary>
/// Источник сообщений субъекта: чат и, при необходимости, конкретные отправители внутри него.
/// </summary>
/// <param name="Peer">Идентификатор чата: <c>@channel</c>, числовой id или ссылка.</param>
/// <param name="SenderIds">Кого из этого чата считать данным субъектом. Пусто — см. <paramref name="Exclusive"/>.</param>
/// <param name="Exclusive">
/// Чат целиком принадлежит субъекту, включая посты без отправителя (от имени канала).
/// Такой чат не может использоваться другими субъектами.
/// </param>
public sealed record SubjectSource(string Peer, IReadOnlyList<long> SenderIds, bool Exclusive)
{
    public SubjectSource() : this(string.Empty, [], false) { }
}

/// <summary>Настройки удаления. Обвешаны предохранителями: операция необратима.</summary>
public sealed record RetentionSettings
{
    public RetentionMode Mode { get; init; } = RetentionMode.Keep;

    /// <summary>Удалять только доведённое до этого состояния.</summary>
    public ProcessingState RequiresState { get; init; } = ProcessingState.Extracted;

    /// <summary>И не раньше, чем через этот срок после отправки.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Провалившееся не удаляется никогда — иначе потеряется то, что не удалось разобрать.</summary>
    public bool KeepFailed { get; init; } = true;

    /// <summary>Только напечатать, что было бы удалено.</summary>
    public bool DryRun { get; init; } = true;
}

/// <summary>Пороги статистики. Задаются глобально, переопределяются на субъекта.</summary>
public sealed record AnalysisSettings
{
    /// <summary>Минимум наблюдений, ниже которого вывод не делается.</summary>
    public int MinSupport { get; init; } = 3;

    /// <summary>Минимальное отношение к базовой частоте, чтобы попасть в подозреваемые.</summary>
    public double MinLift { get; init; } = 1.5;

    /// <summary>Минимум наблюдений для списка «переносится хорошо».</summary>
    public int ToleratedMinSupport { get; init; } = 5;

    /// <summary>Верхняя граница отношения для «переносится хорошо».</summary>
    public double ToleratedMaxLift { get; init; } = 0.7;

    /// <summary>Использовать окна, рассчитанные по подтверждённым связкам, вместо табличных.</summary>
    public bool UseCalibratedWindows { get; init; } = true;

    /// <summary>Сколько подтверждённых связок нужно, чтобы доверять калибровке.</summary>
    public int CalibrationMinSamples { get; init; } = 8;
}

/// <summary>
/// Человек, за которым ведётся наблюдение. У каждого своя база, свои настройки и свой отчёт.
/// </summary>
public sealed class SubjectDefinition
{
    public required SubjectKey Key { get; init; }

    public required string DisplayName { get; init; }

    public required TimeZoneInfo TimeZone { get; init; }

    /// <summary>Каталог с базой и голосовыми этого субъекта.</summary>
    public required string DataDirectory { get; init; }

    public required IReadOnlyList<SubjectSource> Sources { get; init; }

    /// <summary>Какие модули включены. Сужает и разбор, и промпт сегментации, и отчёт.</summary>
    public required IReadOnlyList<string> Modules { get; init; }

    public RetentionSettings Retention { get; init; } = new();

    public AnalysisSettings Analysis { get; init; } = new();

    public string DatabasePath => Path.Combine(DataDirectory, "diary.db");

    public string VoiceDirectory => Path.Combine(DataDirectory, "voice");
}
