using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.UseCases;

/// <param name="Interrupted">
/// Модель оказалась недоступна, и шаг прервался. Необработанное осталось в очереди
/// нетронутым и разберётся при следующем запуске.
/// </param>
public sealed record ExtractReport(int Messages, int Entries, int Failed, int Skipped, bool Interrupted = false);

/// <summary>
/// Превращает расшифровки в записи дневника: сегментация — потом извлечение по категориям.
/// Одно сообщение даёт 1..N записей.
/// </summary>
public sealed class ExtractHandler(
    IMessageRepository messages,
    IEntryRepository entries,
    IEntrySegmenter segmenter,
    IEnumerable<IEntryExtractor> extractors,
    IModuleRegistry modules,
    ISubjectContext subjectContext,
    IStructuredCompletion llm,
    IUnitOfWork uow,
    ILogger<ExtractHandler> logger)
{
    public async Task<ExtractReport> RunAsync(int batchSize, CancellationToken ct)
    {
        var subject = subjectContext.Subject;
        var categories = modules.CategoriesFor(subject.Modules);
        if (categories.Count == 0)
        {
            logger.LogWarning("У субъекта «{Subject}» не включён ни один модуль — разбирать нечем.", subject.Key);
            return new ExtractReport(0, 0, 0, 0);
        }

        var byCategory = extractors
            .Where(e => categories.Any(c => c.Key == e.Category))
            .ToDictionary(e => e.Category);

        var baseVersion = llm.ModelFor(LlmRole.Extraction);
        int handled = 0, produced = 0, failed = 0, skipped = 0;
        var interrupted = false;

        while (!ct.IsCancellationRequested && !interrupted)
        {
            var pending = await messages.GetByStateAsync(ProcessingState.Transcribed, batchSize, ct);
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var message in pending)
            {
                ct.ThrowIfCancellationRequested();

                var text = message.EffectiveText;
                if (string.IsNullOrWhiteSpace(text))
                {
                    message.MarkSkipped("Нет текста для разбора.");
                    skipped++;
                    continue;
                }

                try
                {
                    var fragments = await SegmentAsync(text, message.Hashtags, categories, ct);
                    if (fragments.Count == 0)
                    {
                        message.MarkSkipped("Сегментатор не нашёл ни одного фрагмента.");
                        skipped++;
                        continue;
                    }

                    var context = new ExtractionContext(
                        message.Id,
                        message.SentAtUtc,
                        subjectContext.TimeResolver,
                        baseVersion,
                        message.ReplyToTelegramMessageId);

                    var extracted = new List<DiaryEntry>();
                    foreach (var fragment in fragments)
                    {
                        if (!byCategory.TryGetValue(fragment.Category, out var extractor))
                        {
                            logger.LogDebug(
                                "Категория «{Category}» не поддержана включёнными модулями, фрагмент пропущен.",
                                fragment.Category);
                            continue;
                        }

                        extracted.Add(await extractor.ExtractAsync(fragment, context, ct));
                    }

                    if (extracted.Count > 0)
                    {
                        await entries.AddRangeAsync(extracted, ct);
                        produced += extracted.Count;
                    }

                    message.MarkExtracted();
                    handled++;
                }
                catch (LlmUnavailableException ex)
                {
                    // Сообщение остаётся в очереди: «модель была выключена» не должно
                    // превращаться в «данные сломаны» и требовать ручного возврата.
                    logger.LogWarning(
                        "Модель недоступна ({Reason}). Разбор прерван, {Count} сообщений ждут следующего запуска.",
                        ex.Message, pending.Count - handled);

                    interrupted = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Не удалось разобрать сообщение {Id}.", message.TelegramMessageId);
                    message.MarkFailed($"Разбор: {ex.Message}");
                    failed++;
                }
            }

            // Сохраняем и при обрыве: то, что успели разобрать до падения сервера,
            // разбирать заново незачем.
            await uow.SaveChangesAsync(ct);

            if (pending.Count < batchSize)
            {
                break;
            }
        }

        return new ExtractReport(handled, produced, failed, skipped, interrupted);
    }

    private async Task<IReadOnlyList<EntryFragment>> SegmentAsync(
        string text,
        IReadOnlyList<string> hashtags,
        IReadOnlyList<CategoryDescriptor> categories,
        CancellationToken ct)
    {
        // Один явный хэштег — категория известна точно, модель не нужна вовсе.
        var tagged = MatchByHashtag(hashtags, categories);
        if (tagged.Count == 1)
        {
            return [new EntryFragment(text, tagged[0], Confidence.Certain)];
        }

        try
        {
            return await segmenter.SegmentAsync(text, categories, ct);
        }
        catch (StructuredCompletionException ex)
        {
            // Модель не завелась — хэштег остаётся рабочим запасным вариантом.
            if (tagged.Count > 0)
            {
                logger.LogWarning(ex, "Сегментация не удалась, используем хэштег «{Category}».", tagged[0]);
                return [new EntryFragment(text, tagged[0], new Confidence(0.5))];
            }

            throw;
        }
    }

    private static List<CategoryKey> MatchByHashtag(
        IReadOnlyList<string> hashtags,
        IReadOnlyList<CategoryDescriptor> categories)
    {
        var result = new List<CategoryKey>();
        foreach (var descriptor in categories)
        {
            foreach (var tag in descriptor.Hashtags)
            {
                var normalized = tag.TrimStart('#').ToLowerInvariant();
                if (hashtags.Contains(normalized) && !result.Contains(descriptor.Key))
                {
                    result.Add(descriptor.Key);
                }
            }
        }

        return result;
    }
}
