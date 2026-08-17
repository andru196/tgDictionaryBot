using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Diary.Application.Ports;
using Microsoft.Extensions.Options;

namespace Diary.Infrastructure.Llm;

/// <summary>Сообщение чата. Содержимое — строка или массив частей (текст + аудио).</summary>
public sealed class ChatMessagePayload
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";

    [JsonPropertyName("content")] public JsonNode? Content { get; set; }

    public static ChatMessagePayload Text(string role, string text) =>
        new() { Role = role, Content = JsonValue.Create(text) };

    /// <summary>Текст плюс аудио — формат мультимодального сообщения OpenAI.</summary>
    public static ChatMessagePayload WithAudio(string role, string text, string base64Wav) =>
        new()
        {
            Role = role,
            Content = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "input_audio",
                    ["input_audio"] = new JsonObject { ["data"] = base64Wav, ["format"] = "wav" },
                }),
        };
}

public sealed record ChatRequest
{
    public required string Model { get; init; }

    public required List<ChatMessagePayload> Messages { get; init; }

    public float Temperature { get; init; } = 0.1f;

    public long? Seed { get; init; }

    public int? MaxTokens { get; init; }

    public JsonNode? JsonSchema { get; init; }

    public string? JsonSchemaName { get; init; }

    public string? ReasoningEffort { get; init; }

    public JsonArray? Tools { get; init; }
}

public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatReply(string Text, IReadOnlyList<ChatToolCall> ToolCalls, JsonNode? RawMessage);

/// <summary>
/// Тонкий HTTP-слой над OpenAI-совместимым API LM Studio. Прямой HTTP вместо клиентского
/// SDK — сознательно: локальные серверы отличаются в мелочах, которые SDK прячет.
/// Думающие модели кладут ответ в <c>reasoning_content</c>, оставляя <c>content</c> пустым,
/// а выключается это нестандартным полем запроса.
/// </summary>
public sealed class LmStudioChatClient : IDisposable
{
    private readonly HttpClient _http;

    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LmStudioChatClient(IOptions<LlmOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.Endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
        };

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", settings.ApiKey);
        }
    }

    public async Task<ChatReply> SendAsync(ChatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["temperature"] = request.Temperature,
            ["messages"] = JsonSerializer.SerializeToNode(request.Messages, Json),
        };

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        if (request.ReasoningEffort is { Length: > 0 } effort)
        {
            body["reasoning_effort"] = effort;
        }

        if (request.JsonSchema is { } schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.JsonSchemaName ?? "Result",
                    // strict=false намеренно: строгий режим требует все поля в required,
                    // а половина полей опциональна по смыслу — модель обязана иметь право
                    // вернуть null вместо выдуманного значения.
                    ["strict"] = false,
                    ["schema"] = schema.DeepClone(),
                },
            };
        }

        if (request.Tools is { } tools)
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("chat/completions", body, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            // Сервер не поднят или сеть отвалилась — ждать, а не сдаваться.
            throw new LlmUnavailableException(
                $"Модель недоступна по адресу {_http.BaseAddress}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException(
                $"Модель не ответила за {_http.Timeout.TotalSeconds:F0} с — вероятно, занята.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);

                // 5xx и 429 — это «попробуй позже», а 4xx — «ты неправильно спросил».
                if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
                {
                    throw new LlmUnavailableException(
                        $"Сервер модели ответил {(int)response.StatusCode}: {Shorten(error)}");
                }

                throw new StructuredCompletionException(
                    $"LM Studio ответил {(int)response.StatusCode}: {Shorten(error)}", error);
            }

            return await ReadReplyAsync(response, ct);
        }
    }

    private static async Task<ChatReply> ReadReplyAsync(HttpResponseMessage response, CancellationToken ct)
    {

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
        var message = payload?["choices"]?[0]?["message"];

        // content пуст, а reasoning_content полон — обычная ситуация с думающими моделями:
        // сервер решил, что весь вывод был рассуждением.
        var text = message?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = message?["reasoning_content"]?.GetValue<string>();
        }

        return new ChatReply(text ?? string.Empty, ParseToolCalls(message), message);
    }

    private static IReadOnlyList<ChatToolCall> ParseToolCalls(JsonNode? message)
    {
        if (message?["tool_calls"] is not JsonArray calls || calls.Count == 0)
        {
            return [];
        }

        var result = new List<ChatToolCall>(calls.Count);
        foreach (var call in calls)
        {
            var function = call?["function"];
            var name = function?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result.Add(new ChatToolCall(
                call?["id"]?.GetValue<string>() ?? name!,
                name!,
                function?["arguments"]?.GetValue<string>() ?? "{}"));
        }

        return result;
    }

    internal static string Shorten(string? text) =>
        text is null ? "(пусто)" : text.Length <= 300 ? text : text[..300] + "…";

    public void Dispose() => _http.Dispose();
}

