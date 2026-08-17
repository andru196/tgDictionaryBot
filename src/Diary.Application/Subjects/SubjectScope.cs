using Diary.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Application.Subjects;

/// <summary>
/// Субъект внутри скоупа. Репозитории и модули получают его через DI и потому
/// физически не могут обратиться к чужим данным — ни один их метод не принимает
/// идентификатор человека, значит его нельзя забыть или перепутать.
/// </summary>
public interface ISubjectContext
{
    SubjectDefinition Subject { get; }

    RelativeTimeResolver TimeResolver { get; }
}

internal sealed class SubjectContextHolder : ISubjectContext
{
    private SubjectDefinition? _subject;
    private RelativeTimeResolver? _resolver;

    public SubjectDefinition Subject =>
        _subject ?? throw new InvalidOperationException(
            "Обращение к субъекту вне скоупа. Работа с данными возможна только внутри " +
            "ISubjectScopeFactory.Create(...).");

    public RelativeTimeResolver TimeResolver => _resolver ??= new RelativeTimeResolver(Subject.TimeZone);

    public void Set(SubjectDefinition subject)
    {
        _subject = subject;
        _resolver = null;
    }
}

public interface ISubjectScope : IDisposable
{
    IServiceProvider Services { get; }

    SubjectDefinition Subject { get; }

    T Resolve<T>() where T : notnull;
}

public interface ISubjectScopeFactory
{
    ISubjectScope Create(SubjectKey key);

    ISubjectScope Create(SubjectDefinition subject);
}

/// <summary>Реестр субъектов из конфигурации.</summary>
public interface ISubjectRegistry
{
    IReadOnlyList<SubjectDefinition> All { get; }

    SubjectDefinition Get(SubjectKey key);

    bool TryGet(SubjectKey key, out SubjectDefinition subject);
}

public sealed class SubjectRegistry(IReadOnlyList<SubjectDefinition> subjects) : ISubjectRegistry
{
    public IReadOnlyList<SubjectDefinition> All { get; } = subjects;

    public SubjectDefinition Get(SubjectKey key) =>
        All.FirstOrDefault(s => s.Key == key)
        ?? throw new KeyNotFoundException(
            $"Субъект «{key}» не найден. Известные: {string.Join(", ", All.Select(s => s.Key))}.");

    public bool TryGet(SubjectKey key, out SubjectDefinition subject)
    {
        var found = All.FirstOrDefault(s => s.Key == key);
        subject = found!;
        return found is not null;
    }
}

internal sealed class SubjectScope(IServiceScope scope, SubjectDefinition subject) : ISubjectScope
{
    public IServiceProvider Services => scope.ServiceProvider;

    public SubjectDefinition Subject { get; } = subject;

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose() => scope.Dispose();
}

internal sealed class SubjectScopeFactory(IServiceScopeFactory scopeFactory, ISubjectRegistry registry)
    : ISubjectScopeFactory
{
    public ISubjectScope Create(SubjectKey key) => Create(registry.Get(key));

    public ISubjectScope Create(SubjectDefinition subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var scope = scopeFactory.CreateScope();
        var holder = (SubjectContextHolder)scope.ServiceProvider.GetRequiredService<ISubjectContext>();
        holder.Set(subject);
        return new SubjectScope(scope, subject);
    }
}
