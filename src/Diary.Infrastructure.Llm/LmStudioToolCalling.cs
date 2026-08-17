using System.Text.Json.Nodes;
using Diary.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diary.Infrastructure.Llm;

/// <summary>
/// Свободный ответ с вызовом инструментов. Крутит цикл «модель просит инструмент →
/// выполняем → отдаём результат» до готового ответа.
/// </summary>
public sealed class LmStudioToolCalling(
    LmStudioChatClient client,
    IOptions<LlmOptions> options,
    ILogger<LmStudioToolCalling> logger) : IToolCallingCompletion
{
    private readonly LlmOptions _options = options.Value;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userInput,
        IReadOnlyList<ToolDefinition> tools,
        LlmRole role,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(tools);

        var roleOptions = _options.For(role);
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var messages = new List<ChatMessagePayload>
        {
            ChatMessagePayload.Text("system", systemPrompt),
            ChatMessagePayload.Text("user", userInput),
        };

        var toolSchema = tools.Count == 0 ? null : BuildToolSchema(tools);

        for (var round = 0; round < _options.MaxToolRounds; round++)
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
                    Tools = toolSchema,
                    ReasoningEffort = _options.DisableThinking ? _options.ReasoningEffort : null,
                },
                ct);

            if (reply.ToolCalls.Count == 0)
            {
                return ResponseCleaner.StripThinking(reply.Text).Trim();
            }

            // Ответ модели с запросом инструментов возвращаем в диалог как есть:
            // без него сервер не свяжет результаты с вызовами.
            if (reply.RawMessage is JsonNode raw)
            {
                messages.Add(new ChatMessagePayload
                {
                    Role = "assistant",
                    Content = raw["content"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                });
            }

            foreach (var call in reply.ToolCalls)
            {
                if (!byName.TryGetValue(call.Name, out var tool))
                {
                    logger.LogWarning("Модель запросила неизвестный инструмент {Tool}.", call.Name);
                    messages.Add(ChatMessagePayload.Text("user",
                        $"Инструмента «{call.Name}» не существует. Доступны: {string.Join(", ", byName.Keys)}."));
                    continue;
                }

                logger.LogDebug("Вызов инструмента {Tool} с аргументами {Arguments}.", call.Name, call.ArgumentsJson);

                string result;
                try
                {
                    result = await tool.InvokeAsync(call.ArgumentsJson, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Сбой инструмента — это данные для модели, а не конец разговора.
                    logger.LogError(ex, "Инструмент {Tool} упал.", call.Name);
                    result = $"Инструмент завершился ошибкой: {ex.Message}";
                }

                messages.Add(ChatMessagePayload.Text("user", $"Результат {call.Name}:\n{result}"));
            }
        }

        logger.LogWarning(
            "Модель не уложилась в {Rounds} раундов вызова инструментов.", _options.MaxToolRounds);

        return string.Empty;
    }

    private static JsonArray BuildToolSchema(IReadOnlyList<ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var parameters = tool.ParametersSchema.DeepClone();
            LmStudioCompletion.SchemaCache.Sanitize(parameters);

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters,
                },
            });
        }

        return array;
    }
}
