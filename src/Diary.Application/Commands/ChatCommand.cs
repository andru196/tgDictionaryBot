using System.Globalization;
using Diary.Domain;

namespace Diary.Application.Commands;

/// <summary>Команда, присланная в сам чат дневника.</summary>
public abstract record ChatCommand
{
    /// <param name="Period">Готовый интервал.</param>
    /// <param name="Compare">Сравнить с предыдущим периодом такой же длины.</param>
    public sealed record Report(DateRange Period, bool Compare, Granularity Granularity) : ChatCommand;

    public sealed record Status : ChatCommand;

    public sealed record Help : ChatCommand;

    /// <param name="Hint">Что именно не разобралось — уходит человеку в ответ.</param>
    public sealed record Unknown(string Hint) : ChatCommand;
}

/// <summary>
/// Разбирает команды вида <c>/report month compare</c>. Детерминированно и без модели:
/// команда — это управление, и угадывать здесь нечего.
/// </summary>
public static class ChatCommandParser
{
    public const string HelpText =
        """
        Команды дневника:

        /report — отчёт за неделю
        /report month — за месяц; ещё бывает day, week, year, all
        /report month compare — со сравнением с предыдущим таким же периодом
        /report 2026-01-01 2026-03-31 — за произвольный интервал
        /report month by-day — разбить тренд по дням (ещё by-week, by-month)
        /status — что накоплено и что не разобралось
        /help — это сообщение

        Всё остальное считается записью дневника: еда, самочувствие, мысли, вопросы.
        """;

    /// <summary>Текст является командой, а не записью дневника.</summary>
    public static bool IsCommand(string? text) =>
        text is { Length: > 1 } && text.TrimStart().StartsWith('/');

    public static ChatCommand? TryParse(string? text, DateTimeOffset now)
    {
        if (!IsCommand(text))
        {
            return null;
        }

        var parts = text!.Trim()
            .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // В группах Telegram дописывает к команде имя адресата: /report@bot.
        var name = parts[0].TrimStart('/').Split('@')[0].ToLowerInvariant();
        var arguments = parts.Skip(1).Select(a => a.ToLowerInvariant()).ToArray();

        return name switch
        {
            "report" or "отчёт" or "отчет" => ParseReport(arguments, now),
            "status" or "статус" => new ChatCommand.Status(),
            "help" or "start" or "помощь" => new ChatCommand.Help(),
            _ => new ChatCommand.Unknown($"Команда «/{name}» неизвестна."),
        };
    }

    private static ChatCommand ParseReport(string[] arguments, DateTimeOffset now)
    {
        var compare = arguments.Contains("compare") || arguments.Contains("сравнить");
        var granularity = Granularity.Week;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        string? period = null;

        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case "compare" or "сравнить":
                    continue;

                case "by-day" or "by-week" or "by-month":
                    granularity = argument switch
                    {
                        "by-day" => Granularity.Day,
                        "by-month" => Granularity.Month,
                        _ => Granularity.Week,
                    };
                    continue;

                case "day" or "week" or "month" or "year" or "all"
                    or "день" or "неделя" or "месяц" or "год" or "всё" or "все":
                    period = argument;
                    continue;

                default:
                    if (DateTimeOffset.TryParse(
                            argument, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        if (from is null)
                        {
                            from = parsed;
                        }
                        else
                        {
                            to = parsed;
                        }

                        continue;
                    }

                    return new ChatCommand.Unknown(
                        $"Не понял «{argument}». Ожидается period (day/week/month/year/all), " +
                        "дата в формате ГГГГ-ММ-ДД, compare или by-day/by-week/by-month.");
            }
        }

        if (from is { } start)
        {
            // Конец включительно: «по 31 марта» должно захватывать весь день.
            var end = to?.AddDays(1) ?? now;
            if (end <= start)
            {
                return new ChatCommand.Unknown("Конец периода раньше начала.");
            }

            return new ChatCommand.Report(new DateRange(start, end), compare, granularity);
        }

        var range = period switch
        {
            "day" or "день" => DateRange.FromDays(now, 1),
            "month" or "месяц" => DateRange.FromDays(now, 30),
            "year" or "год" => DateRange.FromDays(now, 365),
            "all" or "всё" or "все" => new DateRange(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), now),
            _ => DateRange.FromDays(now, 7),
        };

        return new ChatCommand.Report(range, compare, granularity);
    }
}
