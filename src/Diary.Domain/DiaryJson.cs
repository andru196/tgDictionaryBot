using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Diary.Domain;

/// <summary>
/// Единые настройки сериализации payload'ов. Важны две вещи: перечисления пишутся строками
/// (иначе вставка нового значения в середину enum молча переинтерпретирует старые записи),
/// и кириллица не экранируется — payload читается глазами при отладке.
/// </summary>
public static class DiaryJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },

        // Задан явно: из этих же настроек выводится JSON-схема для модели,
        // а экспортёр схем требует резолвер и не довольствуется неявным.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Те же настройки, но с отступами — для дампов и снапшот-тестов.</summary>
    public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };
}
