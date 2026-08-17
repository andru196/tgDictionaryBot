using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;

namespace Diary.Application.UseCases;

/// <summary>
/// Инструмент «поискать в собственном дневнике». Даёт модели заглянуть в накопленное,
/// прежде чем отвечать: часть вопросов человек задаёт про свои же записи.
/// </summary>
public sealed class DiaryQueryTool(IEntryRepository entries, ISubjectContext subjectContext, TimeProvider clock)
{
    private sealed record Arguments(string? Query, string? Category, int? Days);

    public ToolDefinition Definition => new(
        "search_diary",
        "Поиск по записям дневника этого человека. Применяй, когда вопрос касается его " +
        "собственных записей: что он ел, когда были симптомы, о чём он уже думал. " +
        "Для общих вопросов о мире инструмент не нужен.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Слово или фраза для поиска по тексту записей.",
                },
                ["category"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Ограничить категорией: meal, symptom, idea, question.",
                },
                ["days"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "За сколько последних дней искать. По умолчанию 90.",
                },
            },
            ["required"] = new JsonArray("query"),
        },
        InvokeAsync);

    private async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct)
    {
        Arguments? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Arguments>(argumentsJson, DiaryJson.Options);
        }
        catch (JsonException)
        {
            return "Аргументы инструмента не разобрались. Ожидается объект с полем query.";
        }

        var query = arguments?.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Не задано, что искать.";
        }

        var period = DateRange.FromDays(clock.GetUtcNow(), Math.Clamp(arguments?.Days ?? 90, 1, 3650));
        var found = await entries.GetAsync(period, ct);

        var matches = found
            .Where(e => string.IsNullOrWhiteSpace(arguments?.Category) ||
                        e.Category.Value.Equals(arguments!.Category, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.RawFragment.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.PayloadJson.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(20)
            .ToArray();

        if (matches.Length == 0)
        {
            return $"По запросу «{query}» записей не найдено.";
        }

        var zone = subjectContext.Subject.TimeZone;
        var result = new StringBuilder();
        result.Append(CultureInfo.InvariantCulture, $"Найдено записей: {matches.Length}.");
        result.AppendLine();

        foreach (var entry in matches)
        {
            var local = TimeZoneInfo.ConvertTime(entry.OccurredAtUtc, zone);
            result.Append(CultureInfo.InvariantCulture,
                $"- {local:yyyy-MM-dd HH:mm} [{entry.Category}] {Shorten(entry.RawFragment)}");
            result.AppendLine();
        }

        return result.ToString();
    }

    private static string Shorten(string text) =>
        text.Length <= 160 ? text : text[..160] + "…";
}
