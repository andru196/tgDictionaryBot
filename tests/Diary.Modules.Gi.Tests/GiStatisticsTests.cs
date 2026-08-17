using System.Text.Json;
using Diary.Application.Subjects;
using Diary.Domain;
using Diary.Modules.Gi;
using Diary.Modules.Gi.Analysis;
using Shouldly;

namespace Diary.Modules.Gi.Tests;

/// <summary>Общая обвязка: время, наблюдения и период.</summary>
public abstract class GiTestBase
{
    protected static readonly DateTimeOffset Day0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    protected static DateRange Period => new(Day0, Day0.AddDays(30));

    protected static MealObservation Meal(
        double hoursFromStart, string food, long? messageId = null, params FoodTag[] tags) =>
        new(EntryId.New(), Day0.AddHours(hoursFromStart),
            [new FoodItem(food, food, null, tags)], MealType.Unspecified, messageId);

    protected static SymptomObservation Symptom(
        double hoursFromStart, int severity = 5,
        SymptomKind kind = SymptomKind.Heartburn,
        long? replyTo = null, string? mention = null) =>
        new(EntryId.New(), Day0.AddHours(hoursFromStart), kind, new Severity(severity), mention, replyTo);
}

public sealed class FisherExactTestTests
{
    [Fact]
    public void ПолноеРазделение_ДаётМалоеP()
    {
        // Продукт съеден 6 раз и все 6 раз с симптомом; без него симптомов не было ни разу.
        var p = FisherExactTest.RightTailPValue(6, 0, 0, 6);

        p.ShouldBeLessThan(0.01);
    }

    [Fact]
    public void ОтсутствиеСвязи_ДаётБольшоеP()
    {
        var p = FisherExactTest.RightTailPValue(5, 5, 5, 5);

        p.ShouldBeGreaterThan(0.4);
    }

    [Fact]
    public void ВырожденнаяТаблица_ДаётЕдиницу()
    {
        FisherExactTest.RightTailPValue(0, 0, 0, 0).ShouldBe(1.0);
        FisherExactTest.RightTailPValue(3, 2, 0, 0).ShouldBe(1.0);
    }

    [Fact]
    public void PValue_ВсегдаВДопустимомДиапазоне()
    {
        for (var a = 0; a <= 6; a++)
        {
            for (var b = 0; b <= 6; b++)
            {
                var p = FisherExactTest.RightTailPValue(a, b, 6 - a, 6 - b);
                p.ShouldBeInRange(0.0, 1.0);
            }
        }
    }
}

public sealed class MealSymptomLinkerTests : GiTestBase
{
    private readonly MealSymptomLinker _linker = new();

    [Fact]
    public void Reply_ДаётПодтверждённуюСвязьСМаксимальнымВесом()
    {
        var meal = Meal(19, "картофель жареный", messageId: 1004);
        var symptom = Symptom(21, replyTo: 1004);

        var links = _linker.Link([meal], [symptom], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.Kind.ShouldBe(LinkKind.Reply);
        link.Weight.ShouldBe(1.0);
        link.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void УпоминаниеЕдыВТексте_СвязываетБезReply()
    {
        var beer = Meal(20, "пиво");
        var other = Meal(14, "гречка");
        var symptom = Symptom(22, mention: "пиво");

        var links = _linker.Link([other, beer], [symptom], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.MealId.ShouldBe(beer.Id);
        link.Kind.ShouldBe(LinkKind.TextualReference);
        link.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public void ПодтверждённаяСвязь_ОтменяетДогадкиПоОкну()
    {
        // Оба приёма попадают в окно, но reply указывает на конкретный.
        var suspect = Meal(19, "картофель жареный", messageId: 1004);
        var innocent = Meal(20, "гречка", messageId: 1005);

        var links = _linker.Link([suspect, innocent], [Symptom(21, replyTo: 1004)], ExposureWindowPolicy.Default);

        links.ShouldHaveSingleItem().MealId.ShouldBe(suspect.Id);
    }

    [Fact]
    public void БезПодтверждения_РаботаетОкноЭкспозиции()
    {
        var meal = Meal(19, "картофель жареный");

        var links = _linker.Link([meal], [Symptom(21)], ExposureWindowPolicy.Default);

        var link = links.ShouldHaveSingleItem();
        link.Kind.ShouldBe(LinkKind.TemporalWindow);
        link.IsConfirmed.ShouldBeFalse();
        link.Weight.ShouldBeInRange(0.3, 0.6);
    }

    [Fact]
    public void ЕдаПослеСимптома_НеСвязывается()
    {
        var links = _linker.Link([Meal(22, "борщ")], [Symptom(21)], ExposureWindowPolicy.Default);

        links.ShouldBeEmpty();
    }

    [Fact]
    public void ЗаПределамиОкна_НеСвязывается()
    {
        // Изжога: окно 0–4 ч, приём был за 10 часов до.
        var links = _linker.Link([Meal(11, "борщ")], [Symptom(21)], ExposureWindowPolicy.Default);

        links.ShouldBeEmpty();
    }

    [Fact]
    public void ОкноЗависитОтВидаСимптома()
    {
        var meal = Meal(10, "шаурма");

        // Через 6 часов: для изжоги поздно, для вздутия — в самый раз.
        _linker.Link([meal], [Symptom(16, kind: SymptomKind.Heartburn)], ExposureWindowPolicy.Default)
            .ShouldBeEmpty();
        _linker.Link([meal], [Symptom(16, kind: SymptomKind.Bloating)], ExposureWindowPolicy.Default)
            .ShouldHaveSingleItem();
    }

    [Fact]
    public void ВесУбываетККонцуОкна()
    {
        var early = _linker.Link([Meal(20, "еда")], [Symptom(20.5)], ExposureWindowPolicy.Default)[0];
        var late = _linker.Link([Meal(20, "еда")], [Symptom(23.5)], ExposureWindowPolicy.Default)[0];

        early.Weight.ShouldBeGreaterThan(late.Weight);
    }
}

public sealed class GiStatisticsCalculatorTests : GiTestBase
{
    private readonly GiStatisticsCalculator _calculator = new(new MealSymptomLinker());

    private static AnalysisSettings Settings => new() { MinSupport = 2, MinLift = 1.5 };

    private GiStatistics Calculate(
        IReadOnlyList<MealObservation> meals,
        IReadOnlyList<SymptomObservation> symptoms,
        AnalysisSettings? settings = null,
        DateRange? period = null) =>
        _calculator.Calculate(
            period ?? Period, meals, symptoms, ExposureWindowPolicy.Default,
            settings ?? Settings, TimeZoneInfo.Utc);

    [Fact]
    public void БазоваяЧастота_СчитаетсяПоПриёмамВнутриПериода()
    {
        // Четыре приёма, после двух — симптом.
        var meals = new[] { Meal(10, "а"), Meal(20, "б"), Meal(30, "в"), Meal(40, "г") };
        var symptoms = new[] { Symptom(11), Symptom(21) };

        var stats = Calculate(meals, symptoms);

        stats.Summary.Meals.ShouldBe(4);
        stats.Summary.BaseRate.ShouldBe(0.5, 0.001);
    }

    [Fact]
    public void Lift_ОтражаетПревышениеНадБазовойЧастотой()
    {
        // Жареная картошка: 3 приёма, все 3 с симптомом. Гречка: 3 приёма, ни одного.
        var meals = new List<MealObservation>();
        var symptoms = new List<SymptomObservation>();

        for (var day = 0; day < 3; day++)
        {
            meals.Add(Meal((day * 24) + 19, "картофель жареный"));
            symptoms.Add(Symptom((day * 24) + 21));
            meals.Add(Meal((day * 24) + 13, "гречка"));
        }

        var stats = Calculate(meals, symptoms);

        var fried = stats.Suspects.First(s => s.Name == "картофель жареный");
        fried.Support.ShouldBe(3);
        fried.WithSymptom.ShouldBe(3);
        fried.Probability.ShouldBe(1.0, 0.001);
        // Базовая частота 3/6 = 0.5, значит lift = 1.0/0.5 = 2.
        fried.Lift.ShouldBe(2.0, 0.001);
        fried.Strength.ShouldBe(SignalStrength.Strong);
    }

    [Fact]
    public void МалоНаблюдений_ПомечаетсяОтдельно_АНеВыдаётсяЗаВывод()
    {
        var meals = new[] { Meal(19, "шаурма"), Meal(43, "гречка"), Meal(67, "рис") };
        var symptoms = new[] { Symptom(21) };

        var stats = Calculate(meals, symptoms, new AnalysisSettings { MinSupport = 3 });

        stats.Suspects.First(s => s.Name == "шаурма").Strength.ShouldBe(SignalStrength.LowData);
    }

    [Fact]
    public void ПриёмыДоНачалаПериода_НеПопадаютВЗнаменатель_НоСвязьДают()
    {
        // Ужин 31 июля в 22:00, симптом 1 августа в 01:00 — приём вне периода,
        // но именно он объясняет симптом в первые часы.
        var period = new DateRange(Day0, Day0.AddDays(7));
        var meals = new[] { Meal(-2, "картофель жареный"), Meal(13, "гречка") };
        var symptoms = new[] { Symptom(1) };

        var stats = Calculate(meals, symptoms, period: period);

        // В знаменателе только гречка: приём из запаса не считается наблюдением периода.
        stats.Summary.Meals.ShouldBe(1);
        stats.Suspects.ShouldNotContain(s => s.Name == "картофель жареный" && s.Support > 0);
    }

    [Fact]
    public void КорреляцииСчитаютсяИПоСвойствамПищи()
    {
        var meals = new List<MealObservation>();
        var symptoms = new List<SymptomObservation>();

        // Три разных жареных блюда — по одному разу каждое: по названиям статистики нет,
        // а по тегу «жареное» набирается три наблюдения.
        var names = new[] { "картофель жареный", "котлета", "сырники" };
        for (var i = 0; i < names.Length; i++)
        {
            meals.Add(Meal((i * 24) + 19, names[i], null, FoodTag.Fried));
            symptoms.Add(Symptom((i * 24) + 21));
            meals.Add(Meal((i * 24) + 13, "гречка"));
        }

        var stats = Calculate(meals, symptoms, new AnalysisSettings { MinSupport = 3 });

        var fried = stats.TagSuspects.First(t => t.Name == "жареное");
        fried.Support.ShouldBe(3);
        fried.Strength.ShouldBe(SignalStrength.Strong);

        stats.Suspects.Where(s => names.Contains(s.Name))
            .ShouldAllBe(s => s.Strength == SignalStrength.LowData);
    }

    [Fact]
    public void ПереносимыеПродукты_ЭтоЧастыеИБезСимптомов()
    {
        var meals = new List<MealObservation>();
        var symptoms = new List<SymptomObservation>();

        for (var day = 0; day < 6; day++)
        {
            meals.Add(Meal((day * 24) + 13, "гречка"));
        }

        for (var day = 0; day < 3; day++)
        {
            meals.Add(Meal((day * 24) + 19, "картофель жареный"));
            symptoms.Add(Symptom((day * 24) + 21));
        }

        var stats = Calculate(meals, symptoms, new AnalysisSettings
        {
            MinSupport = 2,
            ToleratedMinSupport = 5,
            ToleratedMaxLift = 0.7,
        });

        stats.Tolerated.ShouldContain(t => t.Name == "гречка");
        stats.Tolerated.ShouldNotContain(t => t.Name == "картофель жареный");
    }

    [Fact]
    public void СредняяТяжесть_НеТеряетсяПриСериализацииPayload()
    {
        // Регрессия: структура Severity без конвертера собиралась конструктором
        // по умолчанию, и вся статистика тяжести молча становилась нулевой.
        var payload = new SymptomPayload(SymptomKind.Reflux, new Severity(7), null, null, null);

        var json = JsonSerializer.Serialize(payload, DiaryJson.Options);
        var restored = JsonSerializer.Deserialize<SymptomPayload>(json, DiaryJson.Options)!;

        restored.Severity.Value.ShouldBe(7);
        json.ShouldContain("\"severity\":7");
    }

    [Fact]
    public void СводкаСчитаетНочныеЭпизодыИЧистыеДни()
    {
        var period = new DateRange(Day0, Day0.AddDays(5));
        var meals = new[] { Meal(13, "гречка") };
        // 02:00 первого дня — ночной эпизод.
        var symptoms = new[] { Symptom(2) };

        var stats = Calculate(meals, symptoms, period: period);

        stats.Summary.NightEpisodes.ShouldBe(1);
        stats.Summary.Episodes.ShouldBe(1);
        // Дни 2–5 без симптомов.
        stats.Summary.MaxCleanDayStreak.ShouldBe(4);
    }

    [Fact]
    public void ТрендРазбиваетПериодНаИнтервалы()
    {
        var period = new DateRange(Day0, Day0.AddDays(21));
        var stats = _calculator.Calculate(
            period, [Meal(13, "гречка")], [Symptom(14)],
            ExposureWindowPolicy.Default, Settings, TimeZoneInfo.Utc, Granularity.Week);

        stats.Trend.Count.ShouldBe(3);
        stats.Trend[0].Episodes.ShouldBe(1);
        stats.Trend[1].Episodes.ShouldBe(0);
    }
}

public sealed class ExposureWindowCalibratorTests : GiTestBase
{
    private readonly ExposureWindowCalibrator _calibrator = new();

    [Fact]
    public void КалибровкаСужаетОкноПоПодтверждённымСвязкам()
    {
        var kinds = new Dictionary<EntryId, SymptomKind>();
        var links = new List<MealSymptomLink>();

        // Восемь подтверждённых связок с задержкой около двух часов.
        foreach (var minutes in new[] { 105, 110, 115, 120, 120, 125, 130, 135 })
        {
            var symptomId = EntryId.New();
            kinds[symptomId] = SymptomKind.Heartburn;
            links.Add(new MealSymptomLink(
                EntryId.New(), symptomId, LinkKind.Reply, 1.0, TimeSpan.FromMinutes(minutes)));
        }

        var calibrated = _calibrator.Calibrate(links, kinds, minSamples: 8);

        var window = calibrated.ShouldHaveSingleItem();
        window.Kind.ShouldBe(SymptomKind.Heartburn);
        window.Samples.ShouldBe(8);
        // Табличное окно 0–4 ч, откалиброванное должно быть заметно уже.
        (window.Suggested.To - window.Suggested.From)
            .ShouldBeLessThan(window.Default.To - window.Default.From);
        window.Suggested.Contains(TimeSpan.FromMinutes(120)).ShouldBeTrue();
    }

    [Fact]
    public void СвязкиПоОкну_ВКалибровкуНеИдут()
    {
        // Иначе окна обучались бы на самих себе.
        var kinds = new Dictionary<EntryId, SymptomKind>();
        var links = new List<MealSymptomLink>();

        for (var i = 0; i < 20; i++)
        {
            var symptomId = EntryId.New();
            kinds[symptomId] = SymptomKind.Heartburn;
            links.Add(new MealSymptomLink(
                EntryId.New(), symptomId, LinkKind.TemporalWindow, 0.5, TimeSpan.FromMinutes(60)));
        }

        _calibrator.Calibrate(links, kinds, minSamples: 8).ShouldBeEmpty();
    }

    [Fact]
    public void МалоПодтверждений_КалибровкаНеПрименяется()
    {
        var symptomId = EntryId.New();
        var kinds = new Dictionary<EntryId, SymptomKind> { [symptomId] = SymptomKind.Heartburn };
        var links = new[]
        {
            new MealSymptomLink(EntryId.New(), symptomId, LinkKind.Reply, 1.0, TimeSpan.FromHours(2)),
        };

        _calibrator.Calibrate(links, kinds, minSamples: 8).ShouldBeEmpty();
    }

    [Fact]
    public void ПрименениеКалибровки_МеняетТолькоНазванныеСимптомы()
    {
        var calibrated = new[]
        {
            new CalibratedWindow(
                SymptomKind.Heartburn,
                new ExposureWindow(TimeSpan.FromMinutes(30), TimeSpan.FromHours(3)),
                ExposureWindowPolicy.Default.For(SymptomKind.Heartburn),
                10,
                TimeSpan.FromHours(2)),
        };

        var policy = ExposureWindowCalibrator.Apply(ExposureWindowPolicy.Default, calibrated);

        policy.IsCalibrated.ShouldBeTrue();
        policy.For(SymptomKind.Heartburn).To.ShouldBe(TimeSpan.FromHours(3));
        policy.For(SymptomKind.Diarrhea).ShouldBe(ExposureWindowPolicy.Default.For(SymptomKind.Diarrhea));
    }
}
