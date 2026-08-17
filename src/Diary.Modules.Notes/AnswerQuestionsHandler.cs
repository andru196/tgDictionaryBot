using Diary.Application.Ports;
using Diary.Application.UseCases;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Modules.Notes;

public sealed record AnswerReport(int Answered, int Failed, int AlreadyHadAnswers);

/// <summary>
/// Отвечает на накопленные вопросы. Единственный шаг, где модели разрешено быть
/// агентной: она сама решает, заглянуть ли в дневник, прежде чем отвечать.
/// </summary>
public sealed class AnswerQuestionsHandler(
    IEntryRepository entries,
    IToolCallingCompletion llm,
    DiaryQueryTool queryTool,
    IUnitOfWork uow,
    ILogger<AnswerQuestionsHandler> logger)
{
    private const string SystemPrompt =
        """
        Ты отвечаешь на вопросы, которые человек отложил на потом, потому что гуглить
        в моменте было лень.

        Правила:
        1. Отвечай коротко — два-три предложения. Это подсказка, с чего начать, а не статья.
        2. Пиши по-русски, спокойным тоном, без вводных вроде «отличный вопрос».
        3. Если вопрос про его собственные записи — сначала загляни в дневник инструментом.
        4. Если не знаешь ответа — так и напиши. Выдуманный ответ хуже отсутствующего,
           потому что его не станут проверять.
        5. Не давай медицинских рекомендаций: можно объяснить механизм, но не назначать лечение.

        Верни только текст ответа, без заголовков и без markdown-разметки.
        """;

    public async Task<AnswerReport> RunAsync(DateRange period, bool reanswer, CancellationToken ct)
    {
        var questions = await entries.GetByCategoryAsync(
            NotesCategories.ModuleKey, NotesCategories.Question, period, ct);

        int answered = 0, failed = 0, skipped = 0;

        foreach (var entry in questions)
        {
            ct.ThrowIfCancellationRequested();

            var payload = entry.Payload<QuestionPayload>();
            if (!reanswer && !string.IsNullOrWhiteSpace(payload.Answer))
            {
                skipped++;
                continue;
            }

            try
            {
                var answer = await llm.CompleteAsync(
                    SystemPrompt, payload.Question, [queryTool.Definition], LlmRole.Answering, ct);

                if (string.IsNullOrWhiteSpace(answer))
                {
                    failed++;
                    continue;
                }

                entry.UpdatePayload(payload with { Answer = answer.Trim() });
                answered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Один неотвеченный вопрос не должен ронять остальные.
                logger.LogError(ex, "Не удалось ответить на вопрос: {Question}", payload.Question);
                failed++;
            }
        }

        await uow.SaveChangesAsync(ct);
        return new AnswerReport(answered, failed, skipped);
    }
}
