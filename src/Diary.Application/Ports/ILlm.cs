namespace Diary.Application.Ports;

/// <summary>
/// Роль вызова. Шаги пайплайна неравноценны: сегментация проста, извлечение решает
/// качество итога. Модель под роль назначается конфигом, чтобы не грузить один вес на всё.
/// </summary>
public enum LlmRole
{
    Segmentation = 0,
    Extraction = 1,
    Answering = 2,
}

/// <summary>
/// Единственный способ, которым модули общаются с моделью. Никакого свободного текста:
/// ответ обязан соответствовать JSON-схеме, выведенной из <typeparamref name="T"/>.
/// </summary>
public interface IStructuredCompletion
{
    Task<T> CompleteAsync<T>(
        string systemPrompt,
        string userInput,
        LlmRole role,
        CancellationToken ct);

    /// <summary>Идентификатор модели для роли — пишется рядом с результатом как часть версии извлечения.</summary>
    string ModelFor(LlmRole role);
}

/// <summary>Модель ответила не тем, что требует схема, и починить это не удалось.</summary>
/// <remarks>
/// Это <b>постоянная</b> ошибка: повтор через час ничего не изменит, виноват промпт
/// или модель. Сообщение уходит в <c>Failed</c> и ждёт человека.
/// </remarks>
public sealed class StructuredCompletionException(string message, string? rawResponse = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Сырой ответ сохраняется: по нему видно, промпт виноват или модель.</summary>
    public string? RawResponse { get; } = rawResponse;
}

/// <summary>
/// Сервер с моделью недоступен: не запущен, не отвечает, перегружен.
/// </summary>
/// <remarks>
/// Это <b>временная</b> ошибка, и разница принципиальна. Пометить такие сообщения
/// как провалившиеся — значит превратить «LM Studio была выключена» в «данные сломаны»
/// и потребовать ручного вмешательства там, где достаточно подождать. Поэтому шаг
/// прерывается, а сообщения остаются в очереди нетронутыми.
/// </remarks>
public sealed class LlmUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
