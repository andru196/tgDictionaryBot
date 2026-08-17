using System.Text.Json.Nodes;

namespace Diary.Application.Ports;

/// <summary>
/// Инструмент, который модель может вызвать по ходу ответа.
/// </summary>
/// <param name="Name">Имя, которое увидит модель.</param>
/// <param name="Description">Когда инструмент применять — читается моделью дословно.</param>
/// <param name="ParametersSchema">JSON-схема аргументов.</param>
/// <param name="InvokeAsync">Что выполнить; возвращает текст, который уйдёт модели.</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonNode ParametersSchema,
    Func<string, CancellationToken, Task<string>> InvokeAsync);

/// <summary>
/// Свободный ответ с возможностью вызвать инструменты. Отдельный порт от
/// <see cref="IStructuredCompletion"/>: там задача «текст → схема» и агентность вредна,
/// здесь наоборот — модель сама решает, что посмотреть, прежде чем отвечать.
/// </summary>
public interface IToolCallingCompletion
{
    Task<string> CompleteAsync(
        string systemPrompt,
        string userInput,
        IReadOnlyList<ToolDefinition> tools,
        LlmRole role,
        CancellationToken ct);
}
