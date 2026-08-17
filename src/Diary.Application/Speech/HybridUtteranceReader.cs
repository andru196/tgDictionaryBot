using Diary.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diary.Application.Speech;

/// <summary>
/// Слова берутся у распознавателя, а записи, в которых он не уверен, переслушивает
/// аудио-модель. Дороже каскада, но применяется к единицам процентов записей.
/// </summary>
public sealed class HybridUtteranceReader(
    IUtteranceReader primary,
    IUtteranceReader fallback,
    IOptions<SpeechOptions> options,
    ILogger<HybridUtteranceReader> logger) : IUtteranceReader
{
    private readonly float _threshold = options.Value.HybridConfidenceThreshold;

    public async Task<Utterance> ReadAsync(Stream audio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var start = audio.CanSeek ? audio.Position : 0;
        var utterance = await primary.ReadAsync(audio, ct);

        if (utterance.Confidence >= _threshold && !string.IsNullOrWhiteSpace(utterance.Text))
        {
            return utterance;
        }

        if (!audio.CanSeek)
        {
            // Переслушать нечего: поток одноразовый. Отдаём то, что есть, — это лучше,
            // чем потерять запись целиком.
            logger.LogDebug("Уверенность {Confidence:F2} низкая, но поток непереигрываемый.",
                utterance.Confidence);
            return utterance;
        }

        logger.LogInformation(
            "Уверенность распознавания {Confidence:F2} ниже порога {Threshold:F2} — переслушиваем аудио-моделью.",
            utterance.Confidence, _threshold);

        audio.Position = start;
        var second = await fallback.ReadAsync(audio, ct);

        return string.IsNullOrWhiteSpace(second.Text) ? utterance : second;
    }
}
