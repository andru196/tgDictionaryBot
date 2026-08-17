using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Diary.Application.Ports;
using Diary.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diary.Infrastructure.Llm;

/// <summary>
/// Единственная точка, где приложение разговаривает с моделью.
/// </summary>
/// <remarks>
/// Прямой HTTP вместо клиентского SDK — сознательно. Локальные серверы отличаются от
/// облачного OpenAI в мелочах, которые SDK прячет: думающие модели кладут ответ
/// в <c>reasoning_content</c>, оставляя <c>content</c> пустым, а выключается это
/// нестандартным полем запроса. Через SDK до обоих не дотянуться.
/// </remarks>
public sealed class LmStudioCompletion : IStructuredCompletion, IDisposable
{
    private readonly LlmOptions _options;
    private readonly ILogger<LmStudioCompletion> _logger;
    private readonly HttpClient _http;

    public LmStudioCompletion(IOptions<LlmOptions> options, ILogger<LmStudioCompletion> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger;

        var endpoint = _options.Endpoint.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(endpoint + "/"),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", _options.ApiKey);
        }
    }

    public string ModelFor(LlmRole role) => _options.For(role).Model;

    public async Task<T> CompleteAsync<T>(
        string systemPrompt, string userInput, LlmRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        var roleOptions = _options.For(role);
        var messages = new List<ChatMessagePayload>
        {
            new("system", systemPrompt),
            new("user", userInput),
        };

        string? lastRaw = null;

        for (var attempt = 0; attempt <= _options.RepairAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            lastRaw = await SendAsync<T>(roleOptions, messages, ct);

            if (ResponseCleaner.TryDeserialize<T>(lastRaw, DiaryJson.Options, out var value) && value is not null)
            {
                return value;
            }

            if (attempt == _options.RepairAttempts)
            {
                break;
            }

            _logger.LogWarning(
                "Модель {Model} вернула невалидный JSON для {Type}, пробуем починить. Ответ: {Raw}",
                roleOptions.Model, typeof(T).Name, Shorten(lastRaw));

            // Ремонтный проход: говорим, что именно не так, вместо слепого повтора.
            messages.Add(new ChatMessagePayload("assistant", lastRaw ?? string.Empty));
            messages.Add(new ChatMessagePayload("user",
                "Ответ не разобрался как JSON нужной схемы. Верни только корректный JSON-объект " +
                "без пояснений, без markdown-ограждения и без рассуждений."));
        }

        throw new StructuredCompletionException(
            $"Модель {roleOptions.Model} не вернула валидный JSON для {typeof(T).Name} " +
            $"за {_options.RepairAttempts + 1} попыт(ки).",
            lastRaw);
    }

    private async Task<string> SendAsync<T>(
        RoleOptions roleOptions, List<ChatMessagePayload> messages, CancellationToken ct)
    {
        var request = new ChatCompletionRequest
        {
            Model = roleOptions.Model,
            Messages = messages,
            Temperature = roleOptions.Temperature,
            Seed = roleOptions.Seed,
            MaxTokens = roleOptions.MaxOutputTokens,
            ResponseFormat = new ResponseFormatPayload
            {
                JsonSchema = new JsonSchemaPayload
                {
                    Name = typeof(T).Name,
                    Schema = SchemaCache.For<T>(),
                },
            },
            // Думающая модель без этого тратит сотни токенов на рассуждения и кладёт
            // ответ не в то поле. Значение нестандартное, но именно оно работает.
            ReasoningEffort = _options.DisableThinking ? _options.ReasoningEffort : null,
        };

        using var response = await _http.PostAsJsonAsync("chat/completions", request, RequestJson, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new StructuredCompletionException(
                $"LM Studio ответил {(int)response.StatusCode}: {Shorten(error)}", error);
        }

        var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(RequestJson, ct);
        var message = payload?.Choices?.FirstOrDefault()?.Message;

        // content пуст, а reasoning_content полон — обычная ситуация с думающими моделями:
        // сервер решил, что весь вывод был рассуждением.
        var content = message?.Content;
        return string.IsNullOrWhiteSpace(content) ? message?.ReasoningContent ?? string.Empty : content;
    }

    private static string Shorten(string? text) =>
        text is null ? "(пусто)" : text.Length <= 300 ? text : text[..300] + "…";

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Схема выводится из типа один раз: генерация не бесплатна, а тип не меняется.</summary>
    private static class SchemaCache
    {
        private static readonly Dictionary<Type, JsonNode> Cache = [];

        /// <summary>
        /// Web-настройки разрешают читать числа из строк, и экспортёр честно добавляет
        /// в схему регулярку — а движок грамматик в LM Studio такой синтаксис не принимает
        /// и отвечает 400. Для схемы числа должны быть просто числами.
        /// </summary>
        private static readonly JsonSerializerOptions SchemaOptions = new(DiaryJson.Options)
        {
            NumberHandling = JsonNumberHandling.Strict,
        };

        public static JsonNode For<T>()
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(typeof(T), out var schema))
                {
                    schema = SchemaOptions.GetJsonSchemaAsNode(typeof(T));
                    Sanitize(schema);
                    Cache[typeof(T)] = schema;
                }

                return schema.DeepClone();
            }
        }

        /// <summary>
        /// Убирает ключевые слова, которые локальные движки грамматик не поддерживают.
        /// Ограничения из них всё равно продублированы в промпте.
        /// </summary>
        private static void Sanitize(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    obj.Remove("pattern");
                    obj.Remove("format");
                    obj.Remove("$schema");
                    foreach (var property in obj.ToArray())
                    {
                        Sanitize(property.Value);
                    }

                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        Sanitize(item);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record ChatMessagePayload(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")] public List<ChatMessagePayload> Messages { get; set; } = [];

        [JsonPropertyName("temperature")] public float Temperature { get; set; }

        [JsonPropertyName("seed")] public long? Seed { get; set; }

        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }

        [JsonPropertyName("response_format")] public ResponseFormatPayload? ResponseFormat { get; set; }

        [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; set; }
    }

    private sealed class ResponseFormatPayload
    {
        [JsonPropertyName("type")] public string Type => "json_schema";

        [JsonPropertyName("json_schema")] public JsonSchemaPayload? JsonSchema { get; set; }
    }

    private sealed class JsonSchemaPayload
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

        /// <summary>
        /// strict=false намеренно: строгий режим требует, чтобы каждое поле было в required,
        /// а у нас половина полей опциональна по смыслу — модель обязана иметь право
        /// вернуть null вместо выдуманного значения.
        /// </summary>
        [JsonPropertyName("strict")] public bool Strict => false;

        [JsonPropertyName("schema")] public JsonNode? Schema { get; set; }
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }

        public sealed class Choice
        {
            [JsonPropertyName("message")] public ResponseMessage? Message { get; set; }
        }

        public sealed class ResponseMessage
        {
            [JsonPropertyName("content")] public string? Content { get; set; }

            [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
        }
    }
}
