namespace Diary.Modules.Gi.Analysis;

/// <summary>
/// Связывает симптомы с приёмами пищи. Reply — самый точный сигнал, но рассчитывать на него
/// нельзя: человек пишет на ходу и свайпать будет не всегда. Поэтому уровня три, и уровень
/// фиксируется в данных, чтобы в отчёте было видно, где вывод опирается на факт.
/// </summary>
public sealed class MealSymptomLinker
{
    /// <summary>
    /// Для каждого симптома возвращает связки со всеми подходящими приёмами.
    /// Если нашлась подтверждённая связка, предположения по окну для этого симптома
    /// не добавляются — они только размыли бы точное знание.
    /// </summary>
    public IReadOnlyList<MealSymptomLink> Link(
        IReadOnlyList<MealObservation> meals,
        IReadOnlyList<SymptomObservation> symptoms,
        ExposureWindowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(meals);
        ArgumentNullException.ThrowIfNull(symptoms);
        ArgumentNullException.ThrowIfNull(policy);

        var ordered = meals.OrderBy(m => m.At).ToArray();
        var links = new List<MealSymptomLink>();

        foreach (var symptom in symptoms)
        {
            var window = policy.For(symptom.Kind);
            var confirmed = false;

            // 1. Ответ на сообщение о еде.
            if (symptom.ReplyToTelegramMessageId is { } replyTo)
            {
                foreach (var meal in ordered)
                {
                    if (meal.SourceTelegramMessageId == replyTo && meal.At <= symptom.At)
                    {
                        links.Add(new MealSymptomLink(
                            meal.Id, symptom.Id, LinkKind.Reply, 1.0, symptom.At - meal.At));
                        confirmed = true;
                    }
                }
            }

            // 2. Прямое упоминание еды в тексте симптома.
            if (!confirmed && !string.IsNullOrWhiteSpace(symptom.SuspectedFoodMention))
            {
                var mention = Normalize(symptom.SuspectedFoodMention);
                MealObservation? best = null;

                foreach (var meal in ordered)
                {
                    if (meal.At > symptom.At)
                    {
                        break;
                    }

                    if (symptom.At - meal.At > policy.MaxLookback)
                    {
                        continue;
                    }

                    if (meal.Items.Any(i => Mentions(i, mention)))
                    {
                        // Ближайший подходящий приём: «после вчерашнего борща» о том борще,
                        // который был последним, а не о позапрошлом.
                        best = meal;
                    }
                }

                if (best is not null)
                {
                    links.Add(new MealSymptomLink(
                        best.Id, symptom.Id, LinkKind.TextualReference, 0.8, symptom.At - best.At));
                    confirmed = true;
                }
            }

            // 3. Еда и симптом названы одним сообщением, и задержка между ними озвучена —
            // «съел X, через два часа стало плохо». Самый частый способ записи и самый
            // точный после reply: и что съел, и через сколько, сказано прямо.
            if (!confirmed && symptom is { DelayAfterMeal: { } sameMessageDelay, SourceTelegramMessageId: { } source })
            {
                foreach (var meal in ordered)
                {
                    if (meal.SourceTelegramMessageId == source)
                    {
                        links.Add(new MealSymptomLink(
                            meal.Id, symptom.Id, LinkKind.StatedDelay, 0.95, sameMessageDelay));
                        confirmed = true;
                    }
                }
            }

            // 4. Задержка названа, но еда — из другого сообщения. Сам интервал всё ещё
            // факт; остаётся понять, к какому приёму он относится.
            if (!confirmed && symptom.DelayAfterMeal is { } delay)
            {
                MealObservation? closest = null;
                var smallestMiss = TimeSpan.MaxValue;

                foreach (var meal in ordered)
                {
                    if (meal.At > symptom.At)
                    {
                        break;
                    }

                    // Названная задержка отсчитывается от еды, но человек говорит уже после,
                    // поэтому цель — приём примерно на таком расстоянии от момента симптома.
                    var miss = (symptom.At - meal.At - delay).Duration();
                    if (miss < smallestMiss)
                    {
                        smallestMiss = miss;
                        closest = meal;
                    }
                }

                // «Два часа» редко значит ровно 120 минут, но и не четыре часа.
                var tolerance = TimeSpan.FromMinutes(Math.Max(45, delay.TotalMinutes * 0.5));

                if (closest is not null && smallestMiss <= tolerance)
                {
                    links.Add(new MealSymptomLink(
                        closest.Id, symptom.Id, LinkKind.StatedDelay, 0.85, symptom.At - closest.At));
                    confirmed = true;
                }
                else if (closest is not null)
                {
                    // Подходящего приёма нет — вероятно, о еде просто не записали.
                    // Задержку всё равно сохраняем как факт: она пригодится калибровке.
                    links.Add(new MealSymptomLink(
                        closest.Id, symptom.Id, LinkKind.StatedDelay, 0.6, delay));
                    confirmed = true;
                }
            }

            if (confirmed)
            {
                continue;
            }

            // 5. Окно экспозиции.
            foreach (var meal in ordered)
            {
                if (meal.At > symptom.At)
                {
                    break;
                }

                var lag = symptom.At - meal.At;
                if (!window.Contains(lag))
                {
                    continue;
                }

                links.Add(new MealSymptomLink(
                    meal.Id, symptom.Id, LinkKind.TemporalWindow, WindowWeight(window, lag), lag));
            }
        }

        return links;
    }

    /// <summary>Ближе к началу окна — правдоподобнее, поэтому вес убывает от 0.6 к 0.3.</summary>
    private static double WindowWeight(ExposureWindow window, TimeSpan lag)
    {
        var span = (window.To - window.From).TotalMinutes;
        if (span <= 0)
        {
            return 0.45;
        }

        var position = Math.Clamp((lag - window.From).TotalMinutes / span, 0, 1);
        return 0.6 - (0.3 * position);
    }

    private static bool Mentions(FoodItem item, string mention) =>
        Normalize(item.CanonicalName).Contains(mention, StringComparison.Ordinal) ||
        Normalize(item.RawName).Contains(mention, StringComparison.Ordinal) ||
        mention.Contains(Normalize(item.CanonicalName), StringComparison.Ordinal);

    /// <summary>
    /// Грубое приведение к основе: русские окончания различаются («борща» / «борщ»),
    /// а полноценная лемматизация ради сравнения двух слов не окупается.
    /// </summary>
    internal static string Normalize(string value)
    {
        var lower = value.Trim().ToLowerInvariant().Replace('ё', 'е');
        return lower.Length > 5 ? lower[..^1] : lower;
    }
}

