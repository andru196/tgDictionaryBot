using Diary.Application.Commands;
using Diary.Domain;
using Shouldly;

namespace Diary.Application.Tests;

public sealed class ChatCommandParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static ChatCommand Parse(string text) =>
        ChatCommandParser.TryParse(text, Now).ShouldNotBeNull();

    [Theory]
    [InlineData("/report", true)]
    [InlineData("/help", true)]
    [InlineData("поужинал жарёхой", false)]
    [InlineData("", false)]
    [InlineData("/", false)]
    public void КомандаОтличаетсяОтЗаписи(string text, bool expected) =>
        ChatCommandParser.IsCommand(text).ShouldBe(expected);

    [Fact]
    public void ОбычныйТекст_НеРазбираетсяКакКоманда() =>
        ChatCommandParser.TryParse("съел борщ", Now).ShouldBeNull();

    [Fact]
    public void ReportБезАргументов_ЭтоНеделя()
    {
        var report = Parse("/report").ShouldBeOfType<ChatCommand.Report>();

        report.Period.Days.ShouldBe(7);
        report.Compare.ShouldBeFalse();
        report.Granularity.ShouldBe(Granularity.Week);
    }

    [Theory]
    [InlineData("/report day", 1)]
    [InlineData("/report week", 7)]
    [InlineData("/report month", 30)]
    [InlineData("/report year", 365)]
    [InlineData("/report месяц", 30)]
    public void ПериодЗадаётсяСловом(string text, int days) =>
        Parse(text).ShouldBeOfType<ChatCommand.Report>().Period.Days.ShouldBe(days);

    [Fact]
    public void ФлагСравнения_Распознаётся() =>
        Parse("/report month compare").ShouldBeOfType<ChatCommand.Report>().Compare.ShouldBeTrue();

    [Fact]
    public void ШагТренда_Распознаётся() =>
        Parse("/report month by-day").ShouldBeOfType<ChatCommand.Report>()
            .Granularity.ShouldBe(Granularity.Day);

    [Fact]
    public void ПроизвольныйИнтервал_БерётсяВключительно()
    {
        var report = Parse("/report 2026-01-01 2026-01-31").ShouldBeOfType<ChatCommand.Report>();

        report.Period.Start.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        // Конец включительно: последний день должен попасть в период целиком.
        report.Period.End.ShouldBe(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ОднаДата_ЗадаётПериодДоСейчас()
    {
        var report = Parse("/report 2026-08-01").ShouldBeOfType<ChatCommand.Report>();

        report.Period.Start.ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        report.Period.End.ShouldBe(Now);
    }

    [Fact]
    public void ПеревёрнутыйИнтервал_Отвергается() =>
        Parse("/report 2026-03-01 2026-01-01").ShouldBeOfType<ChatCommand.Unknown>()
            .Hint.ShouldContain("раньше начала");

    [Fact]
    public void НепонятныйАргумент_ДаётПодсказку() =>
        Parse("/report позавчера").ShouldBeOfType<ChatCommand.Unknown>()
            .Hint.ShouldContain("позавчера");

    [Fact]
    public void ИмяБотаПослеКоманды_Отбрасывается()
    {
        // В группах Telegram дописывает адресата: /report@diary_bot
        Parse("/report@diary_bot month").ShouldBeOfType<ChatCommand.Report>().Period.Days.ShouldBe(30);
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/статус")]
    public void Статус(string text) => Parse(text).ShouldBeOfType<ChatCommand.Status>();

    [Theory]
    [InlineData("/help")]
    [InlineData("/start")]
    public void Справка(string text) => Parse(text).ShouldBeOfType<ChatCommand.Help>();

    [Fact]
    public void НеизвестнаяКоманда_НеМолчит() =>
        Parse("/выгрузи-всё").ShouldBeOfType<ChatCommand.Unknown>().Hint.ShouldContain("неизвестна");

    [Fact]
    public void СправкаПеречисляетВсеКоманды()
    {
        ChatCommandParser.HelpText.ShouldContain("/report");
        ChatCommandParser.HelpText.ShouldContain("/status");
        ChatCommandParser.HelpText.ShouldContain("/help");
    }
}
