using System.Text.Json;

namespace Diary.Infrastructure.Llm;

/// <summary>
/// Приводит ответ модели к чистому JSON. Три типичные помехи: блок рассуждений,
/// markdown-ограждение и текст вокруг объекта. Дешевле вырезать, чем уговаривать промптом.
/// </summary>
public static class ResponseCleaner
{
    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = StripThinking(raw);
        text = StripCodeFence(text);
        return ExtractJsonObject(text).Trim();
    }

    public static bool TryDeserialize<T>(string raw, JsonSerializerOptions options, out T? value)
    {
        value = default;

        var cleaned = Clean(raw);
        if (cleaned.Length == 0)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(cleaned, options);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string StripThinking(string text)
    {
        // Незакрытый <think> означает, что модель не дописала ответ — оставляем как есть,
        // пусть падает на разборе с сохранённым сырым текстом.
        var result = text;
        int open;
        while ((open = result.IndexOf("<think>", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var close = result.IndexOf("</think>", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                break;
            }

            result = result.Remove(open, close - open + "</think>".Length);
        }

        return result;
    }

    internal static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewLine + 1)..];
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        return fenceEnd >= 0 ? body[..fenceEnd].Trim() : body.Trim();
    }

    /// <summary>Берёт первый сбалансированный объект, игнорируя скобки внутри строк.</summary>
    internal static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return text[start..];
    }
}
