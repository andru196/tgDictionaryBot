using System.Text;
using Diary.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace Diary.Infrastructure.Speech;

public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    /// <summary>Whisper | NativeAudio | Hybrid. Сейчас реализован каскад через Whisper.</summary>
    public string Reader { get; set; } = "Whisper";

    public string ModelPath { get; set; } = "models/ggml-large-v3-turbo.bin";

    /// <summary>Автодетект на коротких записях промахивается, поэтому язык фиксируется.</summary>
    public string Language { get; set; } = "ru";

    /// <summary>
    /// Подсказка словаря. Заметно поднимает точность на терминах, которые модель
    /// иначе распознаёт как похожие обиходные слова.
    /// </summary>
    public string InitialPrompt { get; set; } =
        "Дневник питания и самочувствия. Изжога, рефлюкс, заброс, вздутие, отрыжка, тошнота, " +
        "метеоризм, диарея, запор, тяжесть в желудке.";
}

/// <summary>
/// Каскад: OGG/Opus → PCM → Whisper. Реализация по умолчанию, потому что распознавание
/// речи — отдельная специализированная задача, а транскрипт нужен в любом случае:
/// для архива, поиска и переразбора без повторного прогона аудио.
/// </summary>
public sealed class WhisperUtteranceReader : IUtteranceReader, IDisposable
{
    private readonly SpeechOptions _options;
    private readonly IAudioDecoder _decoder;
    private readonly ILogger<WhisperUtteranceReader> _logger;
    private readonly Lazy<WhisperFactory> _factory;

    public WhisperUtteranceReader(
        IOptions<SpeechOptions> options,
        IAudioDecoder decoder,
        ILogger<WhisperUtteranceReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _decoder = decoder;
        _logger = logger;

        // Модель весит больше гигабайта — грузим при первом голосовом, а не на старте CLI.
        _factory = new Lazy<WhisperFactory>(() =>
        {
            if (!File.Exists(_options.ModelPath))
            {
                throw new FileNotFoundException(
                    $"Модель Whisper не найдена: {Path.GetFullPath(_options.ModelPath)}. " +
                    "Скачай ggml-модель и укажи путь в Speech:ModelPath.",
                    _options.ModelPath);
            }

            _logger.LogInformation("Загружаю модель Whisper из {Path}.", _options.ModelPath);
            return WhisperFactory.FromPath(_options.ModelPath);
        });
    }

    public async Task<Utterance> ReadAsync(Stream audio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var samples = await _decoder.DecodeToPcm16kMonoAsync(audio, ct);
        if (samples.Length == 0)
        {
            return new Utterance(string.Empty, 0f, Engine);
        }

        await using var processor = _factory.Value.CreateBuilder()
            .WithLanguage(_options.Language)
            .WithPrompt(_options.InitialPrompt)
            .Build();

        var text = new StringBuilder();
        var probabilities = new List<float>();

        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            text.Append(segment.Text);
            probabilities.Add(segment.Probability);
        }

        var confidence = probabilities.Count == 0 ? 0f : probabilities.Average();
        return new Utterance(text.ToString().Trim(), confidence, Engine);
    }

    private string Engine => $"whisper/{Path.GetFileNameWithoutExtension(_options.ModelPath)}";

    public void Dispose()
    {
        if (_factory.IsValueCreated)
        {
            _factory.Value.Dispose();
        }
    }
}
