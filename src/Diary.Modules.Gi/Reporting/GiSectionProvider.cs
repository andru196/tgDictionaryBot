using System.Globalization;
using System.Text;
using Diary.Application.Reporting;
using Diary.Modules.Gi.Analysis;

namespace Diary.Modules.Gi.Reporting;

/// <summary>
/// Секция «Пищеварение». Ничего не вычисляет — раскладывает готовые числа
/// в разметку, включая координаты SVG.
/// </summary>
public sealed class GiSectionProvider(GiAnalysisService analysis) : IReportSectionProvider
{
    public string ModuleKey => GiCategories.ModuleKey;

    public int Order => 10;

    public async Task<ReportSection?> BuildAsync(ReportContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await analysis.AnalyzeAsync(context.Period, context.CompareTo, context.Granularity, ct);
        var stats = result.Statistics;

        if (stats.Summary.Meals == 0 && stats.Summary.Episodes == 0)
        {
            return null;
        }

        var html = new Html();

        html.Raw("<p class=\"lead\">")
            .Text($"Период задан произвольно, все цифры пересчитаны под него: базовая частота, " +
                  $"окна и пороги считаются внутри интервала, а приёмы пищи подгружаются с запасом " +
                  $"до его начала — иначе первые часы периода систематически выглядели бы чистыми.")
            .Raw("</p>");

        AppendTrend(html, stats);
        AppendHeatmap(html, stats, context);
        AppendSuspects(html, stats);
        AppendTags(html, stats);
        AppendCalibration(html, result.Calibration);
        AppendTolerated(html, stats);
        AppendTotals(html, stats);
        AppendDisclaimer(html, stats);

        var count = $"{stats.Summary.Meals} приёмов пищи · {stats.Summary.Episodes} симптомов · " +
                    $"{context.Period.Days} дней";

        return new ReportSection("Пищеварение", count, html.ToString());
    }

    private static void AppendTrend(Html html, GiStatistics stats)
    {
        if (stats.Trend.Count == 0)
        {
            return;
        }

        html.Raw("<div class=\"card\"><h3>Динамика <span class=\"tag\">эпизодов · средняя тяжесть</span></h3>");

        const int left = 64, right = 940, baseline = 150, top = 30;
        var maxEpisodes = Math.Max(1, stats.Trend.Max(p => p.Episodes));
        var slot = (right - left) / (double)stats.Trend.Count;
        var barWidth = Math.Max(6, Math.Min(44, slot * 0.55));
        var scale = (baseline - top) / (double)maxEpisodes;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg class=\"trend\" viewBox=\"0 0 960 186\" role=\"img\" aria-label=\"Динамика симптомов\">");
        svg.Append(CultureInfo.InvariantCulture,
            $"<line class=\"tr-base\" x1=\"{left}\" y1=\"{baseline}\" x2=\"{right}\" y2=\"{baseline}\"/>");
        svg.Append(CultureInfo.InvariantCulture,
            $"<g class=\"tr-lbl\"><text x=\"52\" y=\"{top + 6}\" text-anchor=\"end\">{maxEpisodes}</text>" +
            $"<text x=\"52\" y=\"{baseline + 4}\" text-anchor=\"end\">0</text></g>");

        var points = new StringBuilder();
        for (var i = 0; i < stats.Trend.Count; i++)
        {
            var point = stats.Trend[i];
            var centre = left + (slot * i) + (slot / 2);
            var height = point.Episodes * scale;

            svg.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"tr-bar\" x=\"{centre - (barWidth / 2):F1}\" y=\"{baseline - height:F1}\" " +
                $"width=\"{barWidth:F1}\" height=\"{height:F1}\" rx=\"3\"/>");

            var severityY = baseline - (point.AverageSeverity / 10.0 * (baseline - top));
            points.Append(CultureInfo.InvariantCulture, $"{centre:F1},{severityY:F1} ");
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"<polyline class=\"tr-line\" points=\"{points.ToString().Trim()}\"/>");
        svg.Append("</svg>");
        html.Raw(svg.ToString());

        if (stats.Comparison is { } previous)
        {
            html.Raw("<div class=\"compare\">");
            AppendComparisonCell(html, "Эпизодов на 10 приёмов",
                Html.Num(stats.Summary.EpisodesPer10Meals),
                stats.Summary.EpisodesPer10Meals - previous.EpisodesPer10Meals, lowerIsBetter: true);
            AppendComparisonCell(html, "Средняя тяжесть",
                Html.Num(stats.Summary.AverageSeverity),
                stats.Summary.AverageSeverity - previous.AverageSeverity, lowerIsBetter: true);
            AppendComparisonCell(html, "Чистых дней подряд, макс",
                stats.Summary.MaxCleanDayStreak.ToString(Html.Culture),
                stats.Summary.MaxCleanDayStreak - previous.MaxCleanDayStreak, lowerIsBetter: false);
            AppendComparisonCell(html, "Ночных эпизодов",
                stats.Summary.NightEpisodes.ToString(Html.Culture),
                stats.Summary.NightEpisodes - previous.NightEpisodes, lowerIsBetter: true);
            html.Raw("</div>");
        }

        html.Raw("<p class=\"hint\">Столбцы — число эпизодов за интервал, линия — средняя тяжесть " +
                 "по десятибалльной шкале.</p></div>");
    }

    private static void AppendComparisonCell(Html html, string title, string value, double delta, bool lowerIsBetter)
    {
        var improved = lowerIsBetter ? delta < 0 : delta > 0;
        var cssClass = Math.Abs(delta) < 0.05 ? string.Empty : improved ? " good" : " bad";
        var sign = delta > 0 ? "+" : string.Empty;

        html.Raw("<div class=\"cmp\"><div class=\"k\">").Text(title).Raw("</div>")
            .Raw("<div class=\"v\">").Text(value).Raw("</div>")
            .Raw($"<div class=\"d{cssClass}\">").Text($"{sign}{Html.Num(delta)} к прошлому периоду").Raw("</div></div>");
    }

    private static void AppendHeatmap(Html html, GiStatistics stats, ReportContext context)
    {
        if (stats.Daily.Count == 0)
        {
            return;
        }

        var byDay = stats.Daily.ToDictionary(d => d.Day, d => d.MaxSeverity);
        var zone = context.Subject.TimeZone;
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Period.Start, zone).DateTime);
        var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(context.Period.End, zone).DateTime);

        html.Raw("<div class=\"card\"><h3>Тяжесть по дням</h3><div class=\"heatmap\">");

        // Выравниваем на понедельник, чтобы строки сетки читались как дни недели.
        var cursor = start.AddDays(-((int)start.DayOfWeek + 6) % 7);
        for (var day = cursor; day < end; day = day.AddDays(1))
        {
            var level = byDay.TryGetValue(day, out var severity) ? Level(severity) : 0;
            var cssClass = level == 0 ? string.Empty : $" class=\"l{level}\"";
            html.Raw($"<i{cssClass} title=\"{day:dd.MM.yyyy}\"></i>");
        }

        html.Raw("</div><div class=\"hm-legend\"><span>меньше</span>" +
                 "<i style=\"background:var(--h0)\"></i><i style=\"background:var(--h1)\"></i>" +
                 "<i style=\"background:var(--h2)\"></i><i style=\"background:var(--h3)\"></i>" +
                 "<i style=\"background:var(--h4)\"></i><span>больше</span>" +
                 "<span style=\"margin-left:auto\">строка — день недели, столбец — неделя</span></div></div>");

        static int Level(int severity) => severity switch
        {
            <= 0 => 0,
            <= 2 => 1,
            <= 4 => 2,
            <= 6 => 3,
            _ => 4,
        };
    }

    private static void AppendSuspects(Html html, GiStatistics stats)
    {
        if (stats.Suspects.Count == 0)
        {
            return;
        }

        html.Raw("<div class=\"card\"><h3>Подозреваемые продукты <span class=\"tag\">за период</span></h3>");
        AppendSuspectTable(html, stats.Suspects, showConfirmed: true);
        html.Raw("<p class=\"hint\">Базовая частота симптома после произвольного приёма пищи в этом периоде — <b>")
            .Text(Html.Percent(stats.Summary.BaseRate))
            .Raw("</b>. «Отношение» (lift) показывает, во сколько раз чаще симптом случается именно после " +
                 "этого продукта. «Подтверждено» — сколько связок опираются на reply или прямое упоминание " +
                 "в тексте, а не на попадание в окно.</p></div>");
    }

    private static void AppendTags(Html html, GiStatistics stats)
    {
        if (stats.TagSuspects.Count == 0)
        {
            return;
        }

        html.Raw("<div class=\"card\"><h3>Свойства пищи — сигнал накапливается быстрее, чем по блюдам</h3>");
        AppendSuspectTable(html, stats.TagSuspects, showConfirmed: false);
        html.Raw("<p class=\"hint\">Конкретное блюдо за месяц встретится пару раз — статистики не будет. " +
                 "«Жареное» — десятки раз, и сигнал виден заметно раньше. Поэтому корреляции считаются " +
                 "на двух уровнях сразу.</p></div>");
    }

    private static void AppendSuspectTable(Html html, IReadOnlyList<SuspectRow> rows, bool showConfirmed)
    {
        var maxLift = Math.Max(1.0, rows.Max(r => r.Lift));

        html.Raw("<table><thead><tr><th>Название</th><th>Приёмов</th><th>С симптомом</th><th>P(симптом)</th>" +
                 "<th>Отношение</th><th>Задержка</th>");
        if (showConfirmed)
        {
            html.Raw("<th>Подтверждено</th>");
        }

        html.Raw("<th>Оценка</th></tr></thead><tbody>");

        foreach (var row in rows.Take(15))
        {
            var width = Math.Clamp(row.Lift / maxLift * 100, 4, 100);

            html.Raw("<tr><td class=\"food\">").Text(row.Name).Raw("</td>")
                .Raw("<td class=\"num\">").Text(row.Support.ToString(Html.Culture)).Raw("</td>")
                .Raw("<td class=\"num\">").Text(row.WithSymptom.ToString(Html.Culture)).Raw("</td>")
                .Raw("<td class=\"num\">").Text(Html.Percent(row.Probability)).Raw("</td>")
                .Raw($"<td class=\"liftbar\"><em style=\"width:{width.ToString("F0", CultureInfo.InvariantCulture)}%\"></em><b>")
                .Text($"{Html.Num(row.Lift)}×").Raw("</b></td>")
                .Raw("<td class=\"num\">").Text(row.MedianLag is { } lag ? Html.Duration(lag) : "—").Raw("</td>");

            if (showConfirmed)
            {
                html.Raw("<td class=\"conf\">");
                if (row.Confirmed > 0)
                {
                    html.Raw("<b>").Text(row.Confirmed.ToString(Html.Culture)).Raw("</b>")
                        .Text($" / {row.WithSymptom}");
                }
                else
                {
                    html.Text($"0 / {row.WithSymptom}");
                }

                html.Raw("</td>");
            }

            html.Raw($"<td><span class=\"tag {StrengthClass(row.Strength)}\">")
                .Text(StrengthLabel(row))
                .Raw("</span></td></tr>");
        }

        html.Raw("</tbody></table>");
    }

    private static string StrengthClass(SignalStrength strength) => strength switch
    {
        SignalStrength.Strong => "solid",
        SignalStrength.None => "ok",
        _ => string.Empty,
    };

    private static string StrengthLabel(SuspectRow row) => row.Strength switch
    {
        SignalStrength.Strong => "сильный сигнал",
        SignalStrength.Weak => $"слабый · p = {row.PValue.ToString("F2", Html.Culture)}",
        SignalStrength.LowData => $"мало данных · n = {row.Support}",
        _ => "связи нет",
    };

    private static void AppendCalibration(Html html, IReadOnlyList<CalibratedWindow> calibration)
    {
        if (calibration.Count == 0)
        {
            return;
        }

        var samples = calibration.Sum(c => c.Samples);

        html.Raw("<div class=\"card\"><h3>Окна экспозиции <span class=\"tag ok\">")
            .Text($"откалиброваны по {samples} подтверждённым связкам")
            .Raw("</span></h3><div class=\"calib\">");

        const double scaleHours = 24.0;
        foreach (var window in calibration)
        {
            var defLeft = Math.Clamp(window.Default.From.TotalHours / scaleHours * 100, 0, 100);
            var defWidth = Math.Clamp((window.Default.To - window.Default.From).TotalHours / scaleHours * 100, 1, 100);
            var newLeft = Math.Clamp(window.Suggested.From.TotalHours / scaleHours * 100, 0, 100);
            var newWidth = Math.Clamp((window.Suggested.To - window.Suggested.From).TotalHours / scaleHours * 100, 1, 100);

            html.Raw("<div class=\"cal-row\"><span>")
                .Text(GiStatisticsCalculator.SymptomName(window.Kind))
                .Raw("</span><span class=\"cal-track\">")
                .Raw($"<i class=\"cal-def\" style=\"left:{Pct(defLeft)};width:{Pct(defWidth)}\"></i>")
                .Raw($"<i class=\"cal-new\" style=\"left:{Pct(newLeft)};width:{Pct(newWidth)}\"></i>")
                .Raw("</span><span class=\"cal-val\">")
                .Text(window.Suggested.ToString())
                .Raw("<br>")
                .Text($"было {window.Default}")
                .Raw("</span></div>");
        }

        html.Raw("</div><p class=\"hint\">Серым — окно из общей физиологии, оранжевым — рассчитанное " +
                 "по подтверждённым связкам. Reply случается редко, но каждый уточняет окно, " +
                 "а суженное окно режет ложные совпадения по всей остальной выборке.</p></div>");

        static string Pct(double value) => value.ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    private static void AppendTolerated(Html html, GiStatistics stats)
    {
        if (stats.Tolerated.Count == 0)
        {
            return;
        }

        html.Raw("<div class=\"card\"><h3>Переносится хорошо</h3><div class=\"chips clean\">");
        foreach (var row in stats.Tolerated.Take(20))
        {
            html.Raw("<span>").Text(row.Name).Raw(" <b>")
                .Text(row.Support.ToString(Html.Culture)).Raw("</b></span>");
        }

        html.Raw("</div><p class=\"hint\">Продукты, съеденные достаточно часто, после которых симптом " +
                 "случался реже базовой частоты. Список «что можно» на практике полезнее списка " +
                 "«чего избегать».</p></div>");
    }

    private static void AppendTotals(Html html, GiStatistics stats)
    {
        if (stats.SymptomTotals.Count == 0)
        {
            return;
        }

        html.Raw("<div class=\"card\"><h3>Симптомы за период</h3><div class=\"totals\">");
        foreach (var total in stats.SymptomTotals)
        {
            html.Raw("<div class=\"total\"><b>").Text(total.Episodes.ToString(Html.Culture)).Raw("</b><span>")
                .Text(GiStatisticsCalculator.SymptomName(total.Kind)).Raw("</span><span class=\"sev\">")
                .Text($"средняя тяжесть {Html.Num(total.AverageSeverity)}").Raw("</span></div>");
        }

        html.Raw("</div></div>");
    }

    private static void AppendDisclaimer(Html html, GiStatistics stats)
    {
        html.Raw("<p class=\"note\" style=\"margin-top:18px\"><b>Как это читать.</b> ")
            .Text("Это дневник наблюдений, а не диагностика. Совпадение во времени не означает причину: ")
            .Raw("в этом отчёте проверено <b>")
            .Text(stats.HypothesesTested.ToString(Html.Culture))
            .Raw("</b> ")
            .Text("гипотез (продукты и свойства), и при пороге p = 0.05 часть из них сработает случайно. " +
                  "Строки с пометкой «мало данных» показаны не как вывод, а чтобы было видно, чего именно " +
                  "не хватает. Чем короче выбранный период, тем меньше наблюдений и тем осторожнее стоит " +
                  "относиться к находкам. Если сигнал держится на разных периодах — это повод обсудить " +
                  "его с врачом, а не повод самому исключать продукты.")
            .Raw("</p>");
    }
}
