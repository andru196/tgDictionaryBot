using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diary.Modules.Gi;

/// <summary>
/// Свойства пищи. Ради них теги вообще заведены: конкретный «борщ» за месяц встретится
/// три раза и статистики не даст, а «жареное» — сорок, и сигнал виден на второй неделе.
/// </summary>
public enum FoodTag
{
    Fatty = 0,
    Fried = 1,
    Spicy = 2,
    Acidic = 3,
    Dairy = 4,
    Gluten = 5,
    Caffeine = 6,
    Alcohol = 7,
    Carbonated = 8,
    Legumes = 9,
    RawVegetables = 10,
    Sweet = 11,
    Processed = 12,
}

public enum MealType
{
    Unspecified = 0,
    Breakfast = 1,
    Lunch = 2,
    Dinner = 3,
    Snack = 4,
    Drink = 5,
}

public enum SymptomKind
{
    Other = 0,
    Reflux = 1,
    Heartburn = 2,
    Bloating = 3,
    Gas = 4,
    Diarrhea = 5,
    Constipation = 6,
    Nausea = 7,
    AbdominalPain = 8,
    Belching = 9,
}

/// <summary>Тяжесть по десятибалльной шкале.</summary>
/// <remarks>
/// Конвертер обязателен, а не удобен: без него System.Text.Json собирает структуру
/// конструктором по умолчанию и молча теряет значение — вся статистика тяжести
/// становится нулевой, не роняя при этом ничего.
/// </remarks>
[JsonConverter(typeof(SeverityJsonConverter))]
public readonly record struct Severity
{
    public Severity(int value)
    {
        if (value is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Тяжесть задаётся числом от 0 до 10.");
        }

        Value = value;
    }

    public int Value { get; }

    /// <summary>Когда число не названо — модель ставит по прилагательным, и шкала плывёт.</summary>
    public static Severity Unknown => new(0);

    public override string ToString() => $"{Value}/10";
}

/// <summary>Пишет тяжесть числом: и компактно, и payload читается глазами при отладке.</summary>
public sealed class SeverityJsonConverter : JsonConverter<Severity>
{
    public override Severity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Совместимость со старыми записями, где структура была разложена в объект.
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.TryGetProperty("value", out var property)
                ? new Severity(Math.Clamp(property.GetInt32(), 0, 10))
                : Severity.Unknown;
        }

        return new Severity(Math.Clamp(reader.GetInt32(), 0, 10));
    }

    public override void Write(Utf8JsonWriter writer, Severity value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.Value);
    }
}

/// <param name="CanonicalName">Приведённое название: «картофель жареный».</param>
/// <param name="RawName">Как было сказано: «жарёха».</param>
public sealed record FoodItem(
    string CanonicalName,
    string RawName,
    string? Quantity,
    IReadOnlyList<FoodTag> Tags);

public sealed record MealPayload(IReadOnlyList<FoodItem> Items, MealType Type);

/// <param name="SuspectedFoodMention">
/// «после вчерашнего борща» — прямое указание на еду в тексте. Даёт связку точнее,
/// чем попадание в окно, и не требует reply.
/// </param>
public sealed record SymptomPayload(
    SymptomKind Kind,
    Severity Severity,
    TimeSpan? Duration,
    string? SuspectedFoodMention,
    string? Notes);

public static class GiCategories
{
    public const string Meal = "meal";
    public const string Symptom = "symptom";
    public const string ModuleKey = "gi";
}
