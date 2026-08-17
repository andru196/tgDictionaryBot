using System.CommandLine;
using System.Globalization;
using Diary.Cli;
using Diary.Cli.Commands;
using Diary.Domain;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var subjectOption = new Option<string?>("--subject", "-s")
{
    Description = "Ключ субъекта. Без него команда идёт по всем субъектам из конфигурации.",
};

// Recursive: обе опции нужны любой подкоманде, а не только корню.
var sourceOption = new Option<string?>("--source")
{
    Description = "Источник сообщений: telegram (по умолчанию) или file:путь/к/messages.jsonl.",
    Recursive = true,
};

var verboseOption = new Option<bool>("--verbose", "-v")
{
    Description = "Подробный лог.",
    Recursive = true,
};

var periodOption = new Option<string>("--period")
{
    Description = "day | week | month | year | all.",
    DefaultValueFactory = _ => "week",
};

var fromOption = new Option<DateTimeOffset?>("--from") { Description = "Начало периода, ГГГГ-ММ-ДД." };
var toOption = new Option<DateTimeOffset?>("--to") { Description = "Конец периода включительно, ГГГГ-ММ-ДД." };
var compareOption = new Option<bool>("--compare")
{
    Description = "Сравнить с предыдущим периодом такой же длины.",
};
var granularityOption = new Option<string>("--group-by")
{
    Description = "day | week | month — шаг разбиения для тренда.",
    DefaultValueFactory = _ => "week",
};
var openOption = new Option<bool>("--open") { Description = "Открыть готовый отчёт в браузере." };

var root = new RootCommand("Личный дневник в Telegram: разбор локальной моделью и HTML-отчёт.");
root.Options.Add(sourceOption);
root.Options.Add(verboseOption);

Runner CreateRunner(ParseResult parse) =>
    new(HostFactory.Build(parse.GetValue(sourceOption), parse.GetValue(verboseOption)));

var sync = new Command("sync", "Забрать новые сообщения и разложить по субъектам.");
sync.Options.Add(subjectOption);
sync.SetAction((parse, ct) => CreateRunner(parse).SyncAsync(parse.GetValue(subjectOption), ct));

var transcribe = new Command("transcribe", "Расшифровать накопленные голосовые.");
transcribe.Options.Add(subjectOption);
transcribe.SetAction((parse, ct) => CreateRunner(parse).TranscribeAsync(parse.GetValue(subjectOption), ct));

var extract = new Command("extract", "Разобрать расшифровки моделью в записи дневника.");
extract.Options.Add(subjectOption);
extract.SetAction((parse, ct) => CreateRunner(parse).ExtractAsync(parse.GetValue(subjectOption), ct));

var report = new Command("report", "Собрать HTML-отчёт за период.");
foreach (var option in new Option[]
         { subjectOption, periodOption, fromOption, toOption, compareOption, granularityOption, openOption })
{
    report.Options.Add(option);
}

report.SetAction(async (parse, ct) =>
{
    var period = ResolvePeriod(parse.GetValue(periodOption), parse.GetValue(fromOption), parse.GetValue(toOption));
    var compare = parse.GetValue(compareOption) ? period.Previous() : (DateRange?)null;
    var granularity = ParseGranularity(parse.GetValue(granularityOption));

    return await CreateRunner(parse).ReportAsync(
        parse.GetValue(subjectOption), period, compare, granularity, parse.GetValue(openOption), ct);
});

var run = new Command("run", "Всё сразу: sync → transcribe → extract, при --report ещё и отчёт.");
var reportFlag = new Option<bool>("--report") { Description = "Собрать отчёт после разбора." };
run.Options.Add(subjectOption);
run.Options.Add(periodOption);
run.Options.Add(reportFlag);
run.Options.Add(openOption);
run.SetAction(async (parse, ct) =>
{
    var runner = CreateRunner(parse);
    var subject = parse.GetValue(subjectOption);

    var code = await runner.SyncAsync(subject, ct);
    if (code != 0)
    {
        return code;
    }

    code = await runner.TranscribeAsync(subject, ct);
    if (code != 0)
    {
        return code;
    }

    code = await runner.ExtractAsync(subject, ct);
    if (code != 0 || !parse.GetValue(reportFlag))
    {
        return code;
    }

    var period = ResolvePeriod(parse.GetValue(periodOption), null, null);
    return await runner.ReportAsync(subject, period, null, Granularity.Week, parse.GetValue(openOption), ct);
});

var status = new Command("status", "Что накоплено, в каком состоянии и что в карантине.");
status.SetAction((parse, ct) => CreateRunner(parse).StatusAsync(ct));

var retention = new Command("retention", "Реакции или удаление разобранных сообщений в Telegram.");
var modeOption = new Option<string?>("--mode") { Description = "Keep | React | Delete." };
var dryRunOption = new Option<bool?>("--dry-run") { Description = "Только показать, что будет затронуто." };
var retentionSubject = new Option<string>("--subject", "-s")
{
    Description = "Обязателен: операция меняет данные в Telegram, «у всех сразу» не должно набираться случайно.",
    Required = true,
};
retention.Options.Add(retentionSubject);
retention.Options.Add(modeOption);
retention.Options.Add(dryRunOption);
retention.SetAction((parse, ct) => CreateRunner(parse).RetentionAsync(
    parse.GetValue(retentionSubject)!, parse.GetValue(modeOption), parse.GetValue(dryRunOption), ct));

var reprocess = new Command("reprocess", "Вернуть сообщения в обработку — например, после смены модели.");
var fromStateOption = new Option<string>("--from-state")
{
    Description = "Captured (с расшифровки) или Transcribed (только разбор).",
    DefaultValueFactory = _ => "Transcribed",
};
var sinceOption = new Option<DateTimeOffset?>("--since") { Description = "Только сообщения не старше даты." };
reprocess.Options.Add(subjectOption);
reprocess.Options.Add(fromStateOption);
reprocess.Options.Add(sinceOption);
reprocess.SetAction((parse, ct) => CreateRunner(parse).ReprocessAsync(
    parse.GetValue(subjectOption), parse.GetValue(fromStateOption)!, parse.GetValue(sinceOption), ct));

foreach (var command in new[] { sync, transcribe, extract, report, run, status, retention, reprocess })
{
    root.Subcommands.Add(command);
}

return await root.Parse(args).InvokeAsync();

static DateRange ResolvePeriod(string? period, DateTimeOffset? from, DateTimeOffset? to)
{
    var now = DateTimeOffset.UtcNow;

    if (from is { } start)
    {
        // --to задаётся включительно: «по 31 августа» должно захватывать весь день.
        var end = to?.AddDays(1) ?? now;
        return new DateRange(start, end);
    }

    return (period?.ToLowerInvariant()) switch
    {
        "day" => DateRange.FromDays(now, 1),
        "month" => DateRange.FromDays(now, 30),
        "year" => DateRange.FromDays(now, 365),
        "all" => new DateRange(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), now),
        _ => DateRange.FromDays(now, 7),
    };
}

static Granularity ParseGranularity(string? value) =>
    (value?.ToLowerInvariant()) switch
    {
        "day" => Granularity.Day,
        "month" => Granularity.Month,
        _ => Granularity.Week,
    };
