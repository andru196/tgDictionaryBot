using Diary.Application.Evaluation;
using Diary.Application.Modules;
using Diary.Application.Subjects;
using Diary.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Регистрирует ядро: реестры, скоупы субъектов и сценарии. Инфраструктура
    /// подставляется отдельно в композиционном корне.
    /// </summary>
    public static IServiceCollection AddDiaryApplication(
        this IServiceCollection services,
        IReadOnlyList<SubjectDefinition> subjects)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(subjects);

        Validate(subjects);

        services.AddSingleton<ISubjectRegistry>(new SubjectRegistry(subjects));
        services.AddSingleton<IModuleRegistry, ModuleRegistry>();
        services.AddSingleton<ISubjectScopeFactory, SubjectScopeFactory>();
        services.AddScoped<SubjectContextHolder>();
        services.AddScoped<ISubjectContext>(sp => sp.GetRequiredService<SubjectContextHolder>());

        services.AddScoped<TranscribeHandler>();
        services.AddScoped<ExtractHandler>();
        services.AddScoped<RetentionHandler>();
        services.AddScoped<ReportHandler>();
        services.AddScoped<DiaryQueryTool>();
        services.AddScoped<EvaluationRunner>();

        return services;
    }

    public static IServiceCollection AddDiaryModule<TModule>(this IServiceCollection services)
        where TModule : class, IDiaryModule, new()
    {
        var module = new TModule();
        services.AddSingleton<IDiaryModule>(module);
        module.ConfigureServices(services);
        return services;
    }

    /// <summary>
    /// Ошибки конфигурации субъектов проявились бы не сразу, а через месяц кривой
    /// статистикой, поэтому процесс не поднимается.
    /// </summary>
    internal static void Validate(IReadOnlyList<SubjectDefinition> subjects)
    {
        if (subjects.Count == 0)
        {
            throw new InvalidOperationException(
                "В конфигурации нет ни одного субъекта. Заполни секцию Subjects в appsettings.json.");
        }

        var duplicateKeys = subjects
            .GroupBy(s => s.Key.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicateKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"Ключи субъектов повторяются: {string.Join(", ", duplicateKeys)}.");
        }

        var duplicateDirs = subjects
            .GroupBy(s => Path.GetFullPath(s.DataDirectory), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicateDirs.Length > 0)
        {
            throw new InvalidOperationException(
                $"Каталог данных используется несколькими субъектами: {string.Join(", ", duplicateDirs)}. " +
                "Разделение по каталогам — то, что не даёт данным перемешаться.");
        }

        var claims = new Dictionary<(string Peer, long Sender), string>();
        var exclusive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subject in subjects)
        {
            if (subject.Sources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"У субъекта «{subject.Key}» не задан ни один источник сообщений.");
            }

            foreach (var source in subject.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Peer))
                {
                    throw new InvalidOperationException($"У субъекта «{subject.Key}» пустой Peer.");
                }

                if (source.Exclusive)
                {
                    if (exclusive.TryGetValue(source.Peer, out var owner) && owner != subject.Key.Value)
                    {
                        throw new InvalidOperationException(
                            $"Чат «{source.Peer}» объявлен эксклюзивным у «{owner}» и «{subject.Key}» одновременно.");
                    }

                    exclusive[source.Peer] = subject.Key.Value;
                    continue;
                }

                if (source.SenderIds.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Источник «{source.Peer}» субъекта «{subject.Key}» не эксклюзивен и не перечисляет " +
                        "SenderIds — непонятно, чьи сообщения считать его записями.");
                }

                foreach (var sender in source.SenderIds)
                {
                    if (claims.TryGetValue((source.Peer, sender), out var owner))
                    {
                        throw new InvalidOperationException(
                            $"Отправитель {sender} в чате «{source.Peer}» заявлен и «{owner}», и «{subject.Key}».");
                    }

                    claims[(source.Peer, sender)] = subject.Key.Value;
                }
            }
        }

        // Эксклюзивный чат забирает всё, поэтому делить его с пофамильными привязками нельзя.
        foreach (var ((peer, sender), owner) in claims)
        {
            if (exclusive.TryGetValue(peer, out var exclusiveOwner) && exclusiveOwner != owner)
            {
                throw new InvalidOperationException(
                    $"Чат «{peer}» объявлен эксклюзивным у «{exclusiveOwner}», но «{owner}» " +
                    $"claim'ит в нём отправителя {sender}.");
            }
        }
    }
}
