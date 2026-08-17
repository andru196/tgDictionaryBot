using Diary.Application.Modules;
using Diary.Application.Reporting;
using Diary.Modules.Gi.Analysis;
using Diary.Modules.Gi.Extraction;
using Diary.Modules.Gi.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Modules.Gi;

/// <summary>
/// Еда, симптомы и всё, что из них считается. Про инфраструктуру, другие модули
/// и субъектов не знает ничего.
/// </summary>
public sealed class GiModule : IDiaryModule
{
    public string Key => GiCategories.ModuleKey;

    public string Title => "Пищеварение";

    public IReadOnlyList<CategoryDescriptor> Categories { get; } =
    [
        new CategoryDescriptor(
            GiCategories.Meal,
            "Приём пищи",
            "человек говорит, что он съел или выпил",
            [
                "поужинал жарёхой с котлетой",
                "выпил кофе натощак",
                "взял шаурму на обед",
                "вчера вечером ещё картошку жарил, забыл записать",
            ],
            ["#еда", "#ел", "#съел"]),

        new CategoryDescriptor(
            GiCategories.Symptom,
            "Симптом ЖКТ",
            "человек описывает самочувствие: изжогу, рефлюкс, вздутие, боль, стул, тошноту",
            [
                "изжога, на четвёрку где-то",
                "вздутие сильное, пятёрка, и урчит",
                "всю ночь заброс, спать не мог",
                "живот крутит после обеда",
            ],
            ["#симптом", "#жкт", "#изжога"]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<MealSymptomLinker>();
        services.AddSingleton<ExposureWindowCalibrator>();
        services.AddSingleton<GiStatisticsCalculator>();

        services.AddScoped<IEntryExtractor, MealExtractor>();
        services.AddScoped<IEntryExtractor, SymptomExtractor>();

        services.AddScoped<GiAnalysisService>();
        services.AddScoped<IReportSectionProvider, GiSectionProvider>();
    }
}
