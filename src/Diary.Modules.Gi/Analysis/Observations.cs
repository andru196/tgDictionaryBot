using Diary.Domain;

namespace Diary.Modules.Gi.Analysis;

/// <summary>Приём пищи, подготовленный к счёту.</summary>
public sealed record MealObservation(
    EntryId Id,
    DateTimeOffset At,
    IReadOnlyList<FoodItem> Items,
    MealType Type,
    long? SourceTelegramMessageId);

/// <summary>Эпизод симптома, подготовленный к счёту.</summary>
public sealed record SymptomObservation(
    EntryId Id,
    DateTimeOffset At,
    SymptomKind Kind,
    Severity Severity,
    string? SuspectedFoodMention,
    long? ReplyToTelegramMessageId);

/// <summary>Насколько надёжно связаны приём пищи и симптом.</summary>
public enum LinkKind
{
    /// <summary>Ответ на сообщение о еде. Редко, но железно.</summary>
    Reply = 0,

    /// <summary>«После вчерашнего борща» — прямое упоминание в тексте.</summary>
    TextualReference = 1,

    /// <summary>Попадание в окно экспозиции. Основная масса связок.</summary>
    TemporalWindow = 2,
}

public sealed record MealSymptomLink(EntryId MealId, EntryId SymptomId, LinkKind Kind, double Weight, TimeSpan Lag)
{
    /// <summary>Связка опирается на факт, а не на предположение о времени.</summary>
    public bool IsConfirmed => Kind is LinkKind.Reply or LinkKind.TextualReference;
}
