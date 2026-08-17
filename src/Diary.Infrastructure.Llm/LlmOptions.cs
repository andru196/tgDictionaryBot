using Diary.Application.Ports;

namespace Diary.Infrastructure.Llm;

public sealed class RoleOptions
{
    public string Model { get; set; } = string.Empty;

    public float Temperature { get; set; } = 0.1f;

    public long? Seed { get; set; } = 42;

    public int? MaxOutputTokens { get; set; }
}

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Endpoint { get; set; } = "http://localhost:1234/v1";

    /// <summary>LM Studio не проверяет ключ, но OpenAI SDK требует непустой.</summary>
    public string ApiKey { get; set; } = "lm-studio";

    /// <summary>
    /// Гибридные модели пишут служебные рассуждения перед ответом. Для строгого JSON
    /// это помеха: сотни лишних токенов, а ответ приходит в поле reasoning_content
    /// вместо content.
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    /// <summary>
    /// Что отправлять в <c>reasoning_effort</c>, когда рассуждения отключены.
    /// «none» — то, что понимает LM Studio; у другого сервера значение может отличаться.
    /// </summary>
    public string ReasoningEffort { get; set; } = "none";

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Сколько раз чинить невалидный ответ, прежде чем признать разбор проваленным.</summary>
    public int RepairAttempts { get; set; } = 1;

    public Dictionary<string, RoleOptions> Roles { get; } = [];

    public RoleOptions For(LlmRole role)
    {
        if (Roles.TryGetValue(role.ToString(), out var options) && !string.IsNullOrWhiteSpace(options.Model))
        {
            return options;
        }

        // Одна модель на все роли — нормальная конфигурация, когда памяти в обрез.
        var fallback = Roles.Values.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Model));
        return fallback ?? throw new InvalidOperationException(
            $"Для роли {role} не задана модель. Заполни секцию {SectionName}:Roles в appsettings.json.");
    }
}
