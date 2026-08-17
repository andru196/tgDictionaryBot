using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Application.UseCases;
using Diary.Cli.Configuration;
using Diary.Domain;
using Diary.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Diary.Cli.Commands;

/// <summary>Сценарии CLI: подготовка хранилища, выбор субъектов и вызов обработчиков.</summary>
public sealed class Runner(IHost host)
{
    private IServiceProvider Services => host.Services;

    public async Task<IReadOnlyList<SubjectDefinition>> PrepareAsync(string? subjectKey, CancellationToken ct)
    {
        await PersistenceRegistration.EnsureSchemaAsync(Services, ct);

        var registry = Services.GetRequiredService<ISubjectRegistry>();
        if (string.IsNullOrWhiteSpace(subjectKey))
        {
            return registry.All;
        }

        return [registry.Get(new SubjectKey(subjectKey))];
    }

    public async Task<int> SyncAsync(string? subjectKey, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var handler = Services.GetRequiredService<SyncHandler>();

        var report = await handler.RunAsync(subjects, ct);

        Console.WriteLine(
            $"Синхронизация: получено {report.Fetched}, сохранено {report.Stored}, " +
            $"обновлено {report.Superseded}, пропущено {report.Skipped}, в карантине {report.Quarantined}.");

        foreach (var (subject, count) in report.PerSubject)
        {
            Console.WriteLine($"  {subject}: {count}");
        }

        if (report.Quarantined > 0)
        {
            Console.WriteLine("  Разобрать карантин: diary status");
        }

        return 0;
    }

    public async Task<int> TranscribeAsync(string? subjectKey, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);
            var report = await scope.Resolve<TranscribeHandler>().RunAsync(16, ct);

            if (report.Processed > 0 || report.Failed > 0)
            {
                Console.WriteLine(
                    $"{subject.Key}: расшифровано {report.Processed} " +
                    $"({report.Audio.TotalMinutes:F0} мин), ошибок {report.Failed}.");
            }
        }

        return 0;
    }

    public async Task<int> ExtractAsync(string? subjectKey, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);
            var report = await scope.Resolve<ExtractHandler>().RunAsync(16, ct);

            if (report.Messages > 0 || report.Failed > 0)
            {
                Console.WriteLine(
                    $"{subject.Key}: разобрано {report.Messages} сообщений → {report.Entries} записей, " +
                    $"пропущено {report.Skipped}, ошибок {report.Failed}.");
            }
        }

        return 0;
    }

    public async Task<int> ReportAsync(
        string? subjectKey, DateRange period, DateRange? compareTo,
        Granularity granularity, bool open, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();
        var config = Services.GetRequiredService<DiaryConfig>();

        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);
            var result = await scope.Resolve<ReportHandler>()
                .RunAsync(period, compareTo, granularity, config.ReportDirectory, ct);

            Console.WriteLine(
                $"{subject.Key}: {result.Path} " +
                $"({result.Sections} секц., {result.Stats.Entries} записей за период)");

            if (open)
            {
                OpenInBrowser(result.Path);
            }
        }

        return 0;
    }

    public async Task<int> RetentionAsync(string subjectKey, string? mode, bool? dryRun, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);

            var settings = subject.Retention;
            if (!string.IsNullOrWhiteSpace(mode) &&
                Enum.TryParse<RetentionMode>(mode, ignoreCase: true, out var parsed))
            {
                settings = settings with { Mode = parsed };
            }

            if (dryRun is { } dry)
            {
                settings = settings with { DryRun = dry };
            }

            var report = await scope.Resolve<RetentionHandler>().RunAsync(settings, ct);

            Console.WriteLine(
                $"{subject.Key}: режим {report.Mode}, затронуто {report.Affected}" +
                (report.DryRun ? " (пробный прогон, ничего не изменено)" : string.Empty));

            foreach (var line in report.Preview)
            {
                Console.WriteLine($"    {line}");
            }
        }

        return 0;
    }

    public async Task<int> ReprocessAsync(
        string? subjectKey, string fromState, DateTimeOffset? since, CancellationToken ct)
    {
        if (!Enum.TryParse<ProcessingState>(fromState, ignoreCase: true, out var state))
        {
            Console.Error.WriteLine($"Неизвестное состояние «{fromState}».");
            return 1;
        }

        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);
            var messages = scope.Resolve<IMessageRepository>();
            var entries = scope.Resolve<IEntryRepository>();
            var uow = scope.Resolve<IUnitOfWork>();

            // Старые записи убираем до возврата в очередь: иначе после переразбора
            // в статистике окажутся и новые, и прежние.
            var period = new DateRange(
                since ?? DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddYears(1));
            var affectedMessages = await messages.GetByPeriodAsync(period, ct);
            await entries.RemoveBySourceAsync([.. affectedMessages.Select(m => m.Id)], ct);

            var count = await messages.ResetStateAsync(ProcessingState.Extracted, state, since, ct);
            await uow.SaveChangesAsync(ct);

            Console.WriteLine($"{subject.Key}: возвращено в обработку {count} сообщений (в {state}).");
        }

        return 0;
    }

    public async Task<int> StatusAsync(CancellationToken ct)
    {
        var subjects = await PrepareAsync(null, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        Console.WriteLine();
        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);
            var counts = await scope.Resolve<IMessageRepository>().CountByStateAsync(ct);
            var entries = await scope.Resolve<IEntryRepository>().CountAsync(ct);

            var parts = counts.Count == 0
                ? "пусто"
                : string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{Describe(c.Key)} {c.Value}"));

            Console.WriteLine($"  {subject.Key,-10} {parts}; записей {entries}");
        }

        using var shared = Services.CreateScope();
        var quarantine = await shared.ServiceProvider.GetRequiredService<IQuarantineStore>().GetAllAsync(ct);

        if (quarantine.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Карантин: {quarantine.Count} сообщений от неопознанных отправителей");

            foreach (var group in quarantine.GroupBy(q => q.SenderId).OrderByDescending(g => g.Count()))
            {
                var sender = group.Key?.ToString(Html.Culture) ?? "без отправителя";
                var first = group.Min(q => q.SentAtUtc);
                Console.WriteLine(
                    $"      SenderId {sender,-14} · {group.Count()} сообщ. · первое {first:dd.MM.yyyy}");
            }

            Console.WriteLine("      добавь SenderId в Subjects[].Sources[].SenderIds и запусти sync заново");
        }

        Console.WriteLine();
        return 0;

        static string Describe(ProcessingState state) => state switch
        {
            ProcessingState.Captured => "ждут расшифровки",
            ProcessingState.Transcribed => "ждут разбора",
            ProcessingState.Extracted => "готово",
            ProcessingState.Failed => "ошибок",
            ProcessingState.Skipped => "пропущено",
            ProcessingState.Superseded => "устарело",
            _ => state.ToString(),
        };
    }

    private static void OpenInBrowser(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Не удалось открыть отчёт: {ex.Message}");
        }
    }
}

file static class Html
{
    public static readonly System.Globalization.CultureInfo Culture =
        System.Globalization.CultureInfo.InvariantCulture;
}
