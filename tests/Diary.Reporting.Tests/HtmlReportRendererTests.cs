using Diary.Application.Reporting;
using Diary.Application.Subjects;
using Diary.Domain;
using Diary.Infrastructure.Reporting;
using Shouldly;

namespace Diary.Reporting.Tests;

public sealed class HtmlReportRendererTests
{
    private static readonly DateTimeOffset Generated = new(2026, 8, 17, 6, 14, 0, TimeSpan.Zero);

    private static SubjectDefinition Subject(string displayName = "Андрей") => new()
    {
        Key = new SubjectKey("me"),
        DisplayName = displayName,
        TimeZone = TimeZoneInfo.Utc,
        DataDirectory = "data/me",
        Sources = [new SubjectSource("@channel", [], true)],
        Modules = ["gi", "notes"],
    };

    private static DateRange Period =>
        new(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));

    private static string Render(
        IReadOnlyList<ReportSection> sections, SubjectDefinition? subject = null, DateRange? compareTo = null) =>
        new HtmlReportRenderer().Render(
            subject ?? Subject(), Period, compareTo,
            new ReportHeaderStats(47, 18, TimeSpan.FromMinutes(24), 63),
            sections, Generated);

    [Fact]
    public void ОтчётСамодостаточен_БезВнешнихЗапросов()
    {
        var html = Render([new ReportSection("Пищеварение", "5 симптомов", "<p>тело</p>")]);

        html.ShouldStartWith("<!doctype html>");
        html.ShouldContain("<style>");
        // Ни одной ссылки наружу: файл должен открываться офлайн и через десять лет.
        html.ShouldNotContain("http://");
        html.ShouldNotContain("https://");
        html.ShouldNotContain("<script");
    }

    [Fact]
    public void ТекстИзСообщений_Экранируется()
    {
        // Транскрипт может содержать что угодно, включая угловые скобки.
        var html = Render([], Subject("<script>alert(1)</script>"));

        html.ShouldNotContain("<script>alert(1)</script>");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void РазметкаСекции_ВставляетсяКакЕсть()
    {
        // Секции строят модули: их HTML уже прошёл экранирование внутри.
        var html = Render([new ReportSection("Идеи", null, "<div class=\"cards\">карточка</div>")]);

        html.ShouldContain("<div class=\"cards\">карточка</div>");
    }

    [Fact]
    public void ШапкаСодержитИмяСубъектаИСводку()
    {
        var html = Render([]);

        html.ShouldContain("Андрей");
        html.ShouldContain(">47<");
        html.ShouldContain("сообщений");
        html.ShouldContain("голосовых");
    }

    [Fact]
    public void СекцииРендерятсяВПереданномПорядке()
    {
        var html = Render(
        [
            new ReportSection("Пищеварение", null, "<p>первая</p>"),
            new ReportSection("Идеи", null, "<p>вторая</p>"),
        ]);

        html.IndexOf("первая", StringComparison.Ordinal)
            .ShouldBeLessThan(html.IndexOf("вторая", StringComparison.Ordinal));
    }

    [Fact]
    public void СравнениеПериодов_ПопадаетВШапку()
    {
        var html = Render([], compareTo: Period.Previous());

        html.ShouldContain("сравнение с");
    }

    [Fact]
    public void ПустойОтчёт_ВсёРавноКорректныйДокумент()
    {
        var html = Render([]);

        html.ShouldEndWith("</html>" + Environment.NewLine);
        html.ShouldContain("</body>");
    }
}

public sealed class HtmlHelperTests
{
    [Theory]
    [InlineData("<b>", "&lt;b&gt;")]
    [InlineData("a & b", "a &amp; b")]
    [InlineData("\"кавычки\"", "&quot;кавычки&quot;")]
    [InlineData("", "")]
    [InlineData("обычный текст", "обычный текст")]
    public void Экранирование(string input, string expected) => Html.Enc(input).ShouldBe(expected);

    [Fact]
    public void Длительность_ЧитаетсяПоРусски()
    {
        Html.Duration(TimeSpan.FromMinutes(130)).ShouldBe("2 ч 10 м");
        Html.Duration(TimeSpan.FromMinutes(45)).ShouldBe("45 м");
        Html.Duration(TimeSpan.FromSeconds(20)).ShouldBe("меньше минуты");
    }

    [Fact]
    public void Проценты_ОкругляютсяДоЦелых() => Html.Percent(0.734).ShouldBe("73 %");
}
