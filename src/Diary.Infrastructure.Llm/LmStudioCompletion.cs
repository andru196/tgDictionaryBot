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
/// Единственная точка, где модули получают структуру из модели. Никакого свободного
/// текста: ответ обязан лечь в схему, выведенную из типа результата.
/// </summary>
public sealed class LmStudioCompletion(
    LmStudioChatClient client,
    IOptions<LlmOptions> options,
    ILogger<LmStudioCompletion> logger) : IStructuredCompletion
{
    private readonly LlmOptions _options = options.Value;

    public string ModelFor(LlmRole role) => _options.For(role).Model;

    public async Task<T> CompleteAsync<T>(
        string systemPrompt, string userInput, LlmRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        var roleOptions = _options.For(role);
        var messages = new List<ChatMessagePayload>
        {
            ChatMessagePayload.Text("system", systemPrompt),
            ChatMessagePayload.Text("user", userInput),
        };

        string? lastRaw = null;

        for (var attempt = 0; attempt <= _options.RepairAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var reply = await client.SendAsync(
                new ChatRequest
                {
                    Model = roleOptions.Model,
                    Messages = messages,
                    Temperature = roleOptions.Temperature,
                    Seed = roleOptions.Seed,
                    MaxTokens = roleOptions.MaxOutputTokens,
                    JsonSchema = SchemaCache.For<T>(),
                    JsonSchemaName = typeof(T).Name,
                    // Думающая модель без этого тратит сотни токенов на рассуждения
                    // и кладёт ответ не в то поле.
                    ReasoningEffort = _options.DisableThinking ? _options.ReasoningEffort : null,
                },
                ct);

            lastRaw = reply.Text;

            if (ResponseCleaner.TryDeserialize<T>(lastRaw, DiaryJson.Options, out var value) && value is not null)
            {
                return value;
            }

            if (attempt == _options.RepairAttempts)
            {
                break;
            }

            logger.LogWarning(
                "Модель {Model} вернула невалидный JSON для {Type}, пробуем починить. Ответ: {Raw}",
                roleOptions.Model, typeof(T).Name, LmStudioChatClient.Shorten(lastRaw));

            // Ремонтный проход: говорим, что именно не так, вместо слепого повтора.
            messages.Add(ChatMessagePayload.Text("assistant", lastRaw));
            messages.Add(ChatMessagePayload.Text("user",
                "Ответ не разобрался как JSON нужной схемы. Верни только корректный JSON-объект " +
                "без пояснений, без markdown-ограждения и без рассуждений."));
        }

        throw new StructuredCompletionException(
            $"Модель {roleOptions.Model} не вернула валидный JSON для {typeof(T).Name} " +
            $"за {_options.RepairAttempts + 1} попыт(ки).",
            lastRaw);
    }

    /// <summary>Схема выводится из типа один раз: генерация не бесплатна, а тип не меняется.</summary>
    internal static class SchemaCache
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

        public static JsonNode For<T>() => For(typeof(T));

        public static JsonNode For(Type type)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(type, out var schema))
                {
                    schema = SchemaOptions.GetJsonSchemaAsNode(type);
                    Sanitize(schema);
                    Cache[type] = schema;
                }

                return schema.DeepClone();
            }
        }

        /// <summary>
        /// Убирает ключевые слова, которые локальные движки грамматик не поддерживают.
        /// Ограничения из них всё равно продублированы в промпте.
        /// </summary>
        internal static void Sanitize(JsonNode? node)
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
}
