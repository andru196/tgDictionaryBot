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

            if (confirmed)
            {
                continue;
            }

            // 3. Окно экспозиции.
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
