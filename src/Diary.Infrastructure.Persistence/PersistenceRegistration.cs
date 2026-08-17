using Diary.Application.Ports;
using Diary.Application.Subjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    /// <param name="sharedDataDirectory">
    /// Каталог служебной базы. Курсоры и карантин привязаны к чату, а не к человеку:
    /// в общей группе один peer обслуживает нескольких субъектов.
    /// </param>
    public static IServiceCollection AddDiaryPersistence(
        this IServiceCollection services, string sharedDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);

        Directory.CreateDirectory(sharedDataDirectory);

        services.AddDbContext<SyncDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(sharedDataDirectory, "sync.db")}"));

        // База субъекта известна только внутри скоупа — путь берётся из его контекста.
        services.AddDbContext<DiaryDbContext>((provider, options) =>
        {
            var subject = provider.GetRequiredService<ISubjectContext>().Subject;
            Directory.CreateDirectory(subject.DataDirectory);
            options.UseSqlite($"Data Source={subject.DatabasePath}");
        });

        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IEntryRepository, EntryRepository>();
        services.AddScoped<IDeletionLog, DeletionLog>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<ISyncCursorStore, SyncCursorStore>();
        services.AddScoped<IQuarantineStore, QuarantineStore>();

        return services;
    }

    /// <summary>
    /// Создаёт схему, если её ещё нет.
    /// </summary>
    /// <remarks>
    /// Сознательно <c>EnsureCreated</c>, а не миграции: схема пока не менялась ни разу,
    /// и генерировать первую миграцию нечего. Как только появится второе изменение схемы,
    /// это место переводится на <c>Database.Migrate()</c> — иначе накопленные базы
    /// придётся пересоздавать.
    /// </remarks>
    public static async Task EnsureSchemaAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
        await sync.Database.EnsureCreatedAsync(ct);

        var registry = scope.ServiceProvider.GetRequiredService<ISubjectRegistry>();
        var scopeFactory = scope.ServiceProvider.GetRequiredService<ISubjectScopeFactory>();

        foreach (var subject in registry.All)
        {
            using var subjectScope = scopeFactory.Create(subject);
            var db = subjectScope.Resolve<DiaryDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
        }
    }
}
