using Diary.Application.Ports;
using Diary.Domain;
using Microsoft.Extensions.Logging;

namespace Diary.Application.UseCases;

public sealed record TranscribeReport(int Processed, int Failed, TimeSpan Audio);

/// <summary>
/// Расшифровывает накопленные голосовые. Работает только с состоянием <see cref="ProcessingState.Captured"/>,
/// поэтому падение на трёхсотом файле из пятисот не заставляет пересчитывать первые триста.
/// </summary>
public sealed class TranscribeHandler(
    IMessageRepository messages,
    IVoiceStorage storage,
    IUtteranceReader reader,
    IUnitOfWork uow,
    TimeProvider clock,
    ILogger<TranscribeHandler> logger)
{
    public async Task<TranscribeReport> RunAsync(int batchSize, CancellationToken ct)
    {
        var processed = 0;
        var failed = 0;
        var audio = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            var pending = await messages.GetByStateAsync(ProcessingState.Captured, batchSize, ct);
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var message in pending)
            {
                ct.ThrowIfCancellationRequested();

                if (message.Voice is not { } voice)
                {
                    message.MarkSkipped("Состояние Captured без голосового файла.");
                    continue;
                }

                try
                {
                    if (!storage.Exists(voice.RelativePath))
                    {
                        message.MarkFailed($"Файл {voice.RelativePath} отсутствует на диске.");
                        failed++;
                        continue;
                    }

                    await using var stream = storage.OpenRead(voice.RelativePath);
                    var utterance = await reader.ReadAsync(stream, ct);

                    if (string.IsNullOrWhiteSpace(utterance.Text))
                    {
                        message.MarkSkipped("Расшифровка пустая — вероятно, тишина.");
                        continue;
                    }

                    message.AttachTranscript(new Transcript(
                        utterance.Text.Trim(), utterance.Confidence, utterance.Engine, clock.GetUtcNow()));

                    processed++;
                    audio += voice.Duration;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Не удалось расшифровать сообщение {Id}.", message.TelegramMessageId);
                    message.MarkFailed($"Расшифровка: {ex.Message}");
                    failed++;
                }
            }

            // Состояние двигается в той же транзакции, что и результат.
            await uow.SaveChangesAsync(ct);

            if (pending.Count < batchSize)
            {
                break;
            }
        }

        return new TranscribeReport(processed, failed, audio);
    }
}
