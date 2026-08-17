using System.Text.Json;
using Diary.Application.Evaluation;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Application.UseCases;
using Diary.Cli.Configuration;
using Diary.Domain;
using Diary.Infrastructure.Persistence;
using Diary.Modules.Notes;
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
        var report = await Services.GetRequiredService<SyncHandler>().RunAsync(subjects, ct);

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

        await ExecuteCommandsAsync(report.Commands, ct);
        return 0;
    }

    /// <summary>
    /// Возвращает в обработку то, что осело в карантине: конфигурация исправлена,
    /// и теперь отправитель узнаётся. Курсор откатывается до самого раннего
    /// карантинного сообщения, и оно перечитывается из Telegram вместе с медиа —
    /// в карантине лежит только след, без файлов.
    /// </summary>
    public async Task<int> RequeueAsync(CancellationToken ct)
    {
        var subjects = await PrepareAsync(null, ct);

        using var shared = Services.CreateScope();
        var quarantine = shared.ServiceProvider.GetRequiredService<IQuarantineStore>();
        var cursors = shared.ServiceProvider.GetRequiredService<ISyncCursorStore>();

        var pending = await quarantine.GetAllAsync(ct);
        if (pending.Count == 0)
        {
            Console.WriteLine("Карантин пуст.");
            return 0;
        }

        foreach (var group in pending.GroupBy(q => q.PeerId))
        {
            var earliest = group.Min(q => q.TelegramMessageId);
            var cursor = await cursors.GetAsync(group.Key, ct);

            if (cursor is null || cursor.LastProcessedMessageId < earliest)
            {
                continue;
            }

            await cursors.SaveAsync(
                cursor with { LastProcessedMessageId = earliest - 1 },
                ct);

            Console.WriteLine(
                $"  чат {group.Key}: курсор отодвинут до {earliest - 1}, будет перечитано {group.Count()} сообщ.");
        }

        var cleared = await quarantine.ClearAsync(ct);
        Console.WriteLine($"  карантин очищен ({cleared}).");

        // Повторно уже сохранённые сообщения не задвоятся: приём идемпотентен
        // по паре (чат, id сообщения).
        return await SyncAsync(null, ct);
    }

    /// <summary>Исполняет команды, присланные в чат, и отвечает туда же.</summary>
    private async Task ExecuteCommandsAsync(IReadOnlyList<PendingCommand> commands, CancellationToken ct)
    {
        if (commands.Count == 0)
        {
            return;
        }

        var factory = Services.GetRequiredService<ISubjectScopeFactory>();
        var config = Services.GetRequiredService<DiaryConfig>();

        foreach (var command in commands)
        {
            using var scope = factory.Create(command.Subject);
            await scope.Resolve<ChatCommandHandler>().ExecuteAsync(command, config.ReportDirectory, ct);

            Console.WriteLine($"  команда от «{command.Subject}»: {command.Command.GetType().Name}");
        }
    }

    /// <summary>
    /// Висит и реагирует на новые сообщения. Опроса нет: Telegram сам присылает события,
    /// поэтому в простое трафика тоже нет.
    /// </summary>
    public async Task<int> WatchAsync(string? subjectKey, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);

        if (Services.GetRequiredService<IMessageSource>() is not IIncomingMessageWatcher watcher)
        {
            Console.Error.WriteLine("Этот источник не умеет ждать события. Убери --source file:…");
            return 1;
        }

        // Первый проход: забрать накопившееся и заодно разрешить peer'ы,
        // без которых слушать нечего.
        await ProcessOnceAsync(subjects, ct);

        var source = Services.GetRequiredService<IMessageSource>();
        var peerNames = subjects.SelectMany(s => s.Sources).Select(s => s.Peer).Distinct().ToArray();
        var peers = await source.ResolvePeersAsync(peerNames, ct);

        await watcher.ListenAsync(
            [.. peers.Values.Distinct()],
            async token => await ProcessOnceAsync(subjects, token),
            ct);

        return 0;
    }

    private async Task ProcessOnceAsync(IReadOnlyList<SubjectDefinition> subjects, CancellationToken ct)
    {
        var report = await Services.GetRequiredService<SyncHandler>().RunAsync(subjects, ct);

        if (report.Stored > 0 || report.Superseded > 0)
        {
            Console.WriteLine($"Новых сообщений: {report.Stored + report.Superseded}.");

            var factory = Services.GetRequiredService<ISubjectScopeFactory>();
            foreach (var subject in subjects)
            {
                using var scope = factory.Create(subject);
                await scope.Resolve<TranscribeHandler>().RunAsync(16, ct);
                await scope.Resolve<ExtractHandler>().RunAsync(16, ct);
            }
        }

        await ExecuteCommandsAsync(report.Commands, ct);
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

            if (report.Interrupted)
            {
                Console.WriteLine(
                    $"{subject.Key}: модель недоступна — остальное осталось в очереди " +
                    "и разберётся при следующем запуске.");
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

            // Провалившиеся возвращаются вместе с разобранными: чаще всего они упали
            // не по своей вине, и оставлять их вне очереди — значит терять данные молча.
            var count = await messages.ResetStateAsync(ProcessingState.Extracted, state, since, ct)
                      + await messages.ResetStateAsync(ProcessingState.Failed, state, since, ct);

            await uow.SaveChangesAsync(ct);

            Console.WriteLine($"{subject.Key}: возвращено в обработку {count} сообщений (в {state}).");
        }

        return 0;
    }

    public async Task<int> AnswerAsync(string? subjectKey, DateRange period, bool reanswer, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        foreach (var subject in subjects)
        {
            if (!subject.Modules.Contains("notes", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            using var scope = factory.Create(subject);
            var report = await scope.Resolve<AnswerQuestionsHandler>().RunAsync(period, reanswer, ct);

            Console.WriteLine(
                $"{subject.Key}: отвечено {report.Answered}, уже с ответом {report.AlreadyHadAnswers}, " +
                $"ошибок {report.Failed}.");
        }

        return 0;
    }

    public async Task<int> EvaluateAsync(string? subjectKey, string setPath, bool asJson, CancellationToken ct)
    {
        var subjects = await PrepareAsync(subjectKey, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();
        var cases = await EvaluationRunner.LoadAsync(setPath, ct);

        using var scope = factory.Create(subjects[0]);
        var report = await scope.Resolve<EvaluationRunner>().RunAsync(cases, ct);
        var metrics = report.Metrics;

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(metrics, DiaryJson.Indented));
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  Модель: {metrics.Model}");
        Console.WriteLine($"  Кейсов: {metrics.Cases}");
        Console.WriteLine();
        Row("число фрагментов угадано", metrics.FragmentCountAccuracy, "склейка и потеря коротких хвостов");
        Row("категории, F1", metrics.CategoryF1, "путаница «идея ↔ вопрос»");
        Row("продукты, F1", metrics.FoodF1, "пропущенная и выдуманная еда");
        Row("продукты, точность", metrics.FoodPrecision, "сколько названного действительно было");
        Row("продукты, полнота", metrics.FoodRecall, "сколько бывшего названо");
        Row("свойства пищи, F1", metrics.TagF1, "теги для статистики на малой выборке");
        Row("вид симптома", metrics.SymptomKindAccuracy, "рефлюкс против изжоги");
        Console.WriteLine($"  {"ошибка тяжести (MAE)",-28} {metrics.SeverityMae,6:F2}   плывущая шкала");
        Row("доля провалов", metrics.FailureRate, "невалидный JSON после ремонта");
        Console.WriteLine($"  {"секунд на сообщение",-28} {metrics.SecondsPerCase,6:F1}   цена прогона");
        Console.WriteLine();

        var worst = report.Results
            .Where(r => r.Error is not null || r.ActualCategories.Count != r.Case.Categories.Count)
            .Take(5)
            .ToArray();

        if (worst.Length > 0)
        {
            Console.WriteLine("  Где разошлось:");
            foreach (var result in worst)
            {
                var expected = string.Join(", ", result.Case.Categories);
                var actual = result.Error ?? string.Join(", ", result.ActualCategories);
                Console.WriteLine($"    «{Shorten(result.Case.Text)}»");
                Console.WriteLine($"      ждали [{expected}], получили [{actual}]");
            }

            Console.WriteLine();
        }

        return 0;

        static void Row(string name, double value, string meaning) =>
            Console.WriteLine($"  {name,-28} {value,6:P0}   {meaning}");

        static string Shorten(string text) => text.Length <= 70 ? text : text[..70] + "…";
    }

    public async Task<int> SetupChatAsync(
        string title, IReadOnlyList<string> invite, string subjectKey, CancellationToken ct)
    {
        if (Services.GetService<IMessageSource>() is not IChatAdministration administration)
        {
            Console.Error.WriteLine(
                "Создание чата доступно только через Telegram. Убери --source file:…");
            return 1;
        }

        var chat = await administration.CreateGroupAsync(title, invite, ct);

        Console.WriteLine();
        Console.WriteLine($"  Чат «{chat.Title}» создан, id {chat.PeerId}.");

        if (chat.NotInvited.Count > 0)
        {
            Console.WriteLine(
                $"  Не удалось пригласить: {string.Join(", ", chat.NotInvited)} — " +
                "настройки приватности. Пригласи ссылкой вручную.");
        }

        Console.WriteLine();
        Console.WriteLine("  Впиши в docker/appsettings.Local.json:");
        Console.WriteLine();
        Console.WriteLine($$"""
            {
              "Key": "{{subjectKey}}",
              "DisplayName": "Я",
              "TimeZone": "Europe/Moscow",
              "Modules": [ "gi", "notes" ],
              "Sources": [
                { "Peer": "{{chat.PeerId}}", "SenderIds": [ {{chat.MyUserId}} ] }
              ],
              "Retention": { "Mode": "Keep", "DryRun": true }
            }
        """);
        Console.WriteLine();

        return 0;
    }

    public async Task<int> StatusAsync(bool details, CancellationToken ct)
    {
        var subjects = await PrepareAsync(null, ct);
        var factory = Services.GetRequiredService<ISubjectScopeFactory>();

        Console.WriteLine();
        foreach (var subject in subjects)
        {
            using var scope = factory.Create(subject);
            var messages = scope.Resolve<IMessageRepository>();
            var counts = await messages.CountByStateAsync(ct);
            var entries = await scope.Resolve<IEntryRepository>().CountAsync(ct);

            var parts = counts.Count == 0
                ? "пусто"
                : string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{Describe(c.Key)} {c.Value}"));

            Console.WriteLine($"  {subject.Key,-10} {parts}; записей {entries}");

            var problems = counts.GetValueOrDefault(ProcessingState.Failed)
                         + counts.GetValueOrDefault(ProcessingState.Skipped);

            if (problems == 0)
            {
                continue;
            }

            if (!details)
            {
                Console.WriteLine($"             причины: diary status --details");
                continue;
            }

            foreach (var message in await messages.GetProblematicAsync(10, ct))
            {
                var local = TimeZoneInfo.ConvertTime(message.SentAtUtc, subject.TimeZone);
                Console.WriteLine(
                    $"             #{message.TelegramMessageId} {local:dd.MM.yyyy HH:mm} " +
                    $"— {message.FailureReason ?? "без причины"}");
                Console.WriteLine($"               {Preview(message.EffectiveText)}");
            }
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

            Console.WriteLine("      добавь SenderId в Subjects[].Sources[].SenderIds и запусти: diary requeue");
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

        static string Preview(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "(пусто)";
            }

            var single = text.ReplaceLineEndings(" ").Trim();
            return single.Length <= 90 ? single : single[..90] + "…";
        }
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

