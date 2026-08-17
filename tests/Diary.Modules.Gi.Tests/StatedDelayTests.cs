using System.Text.Json;
using Diary.Domain;
using Diary.Modules.Gi;
using Diary.Modules.Gi.Analysis;
using Shouldly;

namespace Diary.Modules.Gi.Tests;

/// <summary>
/// «Съел X, через два часа стало плохо» — самая частая форма записи про ЖКТ.
/// Сегментатор отдаёт эту фразу фрагменту симптома, поэтому задержка живёт
/// в симптоме, а не в записи о еде.
/// </summary>
public sealed class StatedDelayTests : GiTestBase
{
    private readonly MealSymptomLinker _linker = new();

    private static SymptomObservation SymptomWithDelay(double hoursFromStart, double delayHours) =>
        new(EntryId.New(), Day0.AddHours(hoursFromStart), SymptomKind.Diarrhea, new Severity(4),
            null, null, TimeSpan.FromHours(delayHours));

    [Fact]
    public void НазваннаяЗадержка_НаходитПодходящийПриёмПищи()
    {
        // Обед в 14:00, симптом описан в 16:00 со словами «через два часа».
        var lunch = Meal(14, "фокачча");
        var breakfast = Meal(9, "овсянка");

        var links = _linker.Link(
            [breakfast, lunch], [SymptomWithDelay(16, 2)], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.MealId.ShouldBe(lunch.Id);
        link.Kind.ShouldBe(LinkKind.StatedDelay);
        link.IsConfirmed.ShouldBeTrue();
        link.Lag.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void ЗадержкаТочнееОкна_ДажеЕслиОбаПриёмаВОкне()
    {
        // Диарея: окно 2–24 ч, в него попадают оба приёма. Названные «два часа»
        // указывают на обед, а не на завтрак.
        var breakfast = Meal(9, "овсянка");
        var lunch = Meal(14, "фокачча");

        var links = _linker.Link(
            [breakfast, lunch], [SymptomWithDelay(16, 2)], ExposureWindowPolicy.Default);

        links.ShouldHaveSingleItem().MealId.ShouldBe(lunch.Id);
    }

    [Fact]
    public void ReplyВажнееНазваннойЗадержки()
    {
        var replied = Meal(10, "борщ", messageId: 500);
        var other = Meal(14, "фокачча");

        var symptom = new SymptomObservation(
            EntryId.New(), Day0.AddHours(16), SymptomKind.Diarrhea, new Severity(4),
            null, 500, TimeSpan.FromHours(2));

        var links = _linker.Link([replied, other], [symptom], ExposureWindowPolicy.Default);

        links.ShouldHaveSingleItem().Kind.ShouldBe(LinkKind.Reply);
    }

    [Fact]
    public void СимптомНеОказываетсяРаньшеЕды()
    {
        // Регрессия: «через два часа» модель понимала как «два часа назад»,
        // симптом уезжал перед едой, и связь не находилась вовсе.
        var meal = Meal(14, "фокачча");

        var links = _linker.Link([meal], [SymptomWithDelay(16, 2)], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.Lag.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void ЗадержкаБезПодходящейЕды_ВсёРавноСохраняетсяКакФакт()
    {
        // О еде не записали, но интервал назван — он пригодится калибровке окон.
        var distant = Meal(1, "завтрак");

        var links = _linker.Link([distant], [SymptomWithDelay(20, 2)], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.Kind.ShouldBe(LinkKind.StatedDelay);
        link.Lag.ShouldBe(TimeSpan.FromHours(2));
        // Вес ниже: какая именно еда — неизвестно.
        link.Weight.ShouldBeLessThan(0.85);
    }

    [Fact]
    public void ЕдаИСимптомИзОдногоСообщения_СвязываютсяТочно()
    {
        // «Съел фокаччу, через два часа пронесло» — одно голосовое. Время у обеих
        // записей одинаковое (момент речи), но задержка названа, и она задаёт их
        // взаимный порядок точно.
        var meal = Meal(16, "фокачча", messageId: 900);
        var symptom = new SymptomObservation(
            EntryId.New(), Day0.AddHours(16), SymptomKind.Diarrhea, new Severity(4),
            null, null, TimeSpan.FromHours(2), 900);

        var links = _linker.Link([meal], [symptom], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.Kind.ShouldBe(LinkKind.StatedDelay);
        link.Lag.ShouldBe(TimeSpan.FromHours(2));
        link.IsConfirmed.ShouldBeTrue();
        // Точнее, чем поиск подходящего приёма среди прочих: тут гадать не о чем.
        link.Weight.ShouldBe(0.95);
    }

    [Fact]
    public void ДопускПропорционаленЗадержке()
    {
        // «Часов через восемь» — оценка грубая, попадание в пределах часа приемлемо.
        var meal = Meal(9, "плов");

        var links = _linker.Link([meal], [SymptomWithDelay(18, 8)], ExposureWindowPolicy.Default);

        links.ShouldHaveSingleItem().Weight.ShouldBe(0.85);
    }

    [Fact]
    public void ЗадержкаПереживаетСериализацию()
    {
        var payload = new SymptomPayload(
            SymptomKind.Diarrhea, new Severity(4), null, null, null, TimeSpan.FromHours(2));

        var json = JsonSerializer.Serialize(payload, DiaryJson.Options);
        var restored = JsonSerializer.Deserialize<SymptomPayload>(json, DiaryJson.Options)!;

        restored.DelayAfterMeal.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void ЗадержкаУчаствуетВКалибровкеОкон()
    {
        // Названные интервалы — такой же факт, как reply, и окна по ним настраиваются.
        var kinds = new Dictionary<EntryId, SymptomKind>();
        var links = new List<MealSymptomLink>();

        foreach (var minutes in new[] { 110, 115, 120, 120, 125, 125, 130, 135 })
        {
            var symptomId = EntryId.New();
            kinds[symptomId] = SymptomKind.Diarrhea;
            links.Add(new MealSymptomLink(
                EntryId.New(), symptomId, LinkKind.StatedDelay, 0.85, TimeSpan.FromMinutes(minutes)));
        }

        var calibrated = new ExposureWindowCalibrator().Calibrate(links, kinds, minSamples: 8);

        calibrated.ShouldHaveSingleItem().Suggested.Contains(TimeSpan.FromHours(2)).ShouldBeTrue();
    }
}
