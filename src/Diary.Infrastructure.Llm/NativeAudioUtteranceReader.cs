using System.Buffers.Binary;
using Diary.Application.Ports;
using Diary.Application.Speech;
using Microsoft.Extensions.Options;

namespace Diary.Infrastructure.Llm;

/// <summary>
/// Расшифровка мультимодальной моделью: она слушает звук сама, без отдельного
/// распознавателя. Даёт то, что теряется в тексте — интонацию, паузы, неуверенность.
/// </summary>
/// <remarks>
/// Цена — часть параметров модели уходит на модальность, поэтому при равном размере
/// текстовое качество ниже, чем у специализированного распознавателя. Плюс поддержка
/// аудио-входа в локальных серверах отстаёт от релизов моделей: проверять надо на месте.
/// Каскад через Whisper остаётся выбором по умолчанию.
/// </remarks>
public sealed class NativeAudioUtteranceReader(
    LmStudioChatClient client,
    IAudioDecoder decoder,
    IOptions<LlmOptions> llmOptions,
    IOptions<SpeechOptions> speechOptions) : IUtteranceReader
{
    private readonly LlmOptions _llm = llmOptions.Value;
    private readonly SpeechOptions _speech = speechOptions.Value;

    private const string Instruction =
        "Расшифруй речь на русском языке дословно. Верни только текст расшифровки, " +
        "без пояснений, без кавычек и без описания того, что ты слышишь. " +
        "Если речи нет — верни пустую строку.";

    public async Task<Utterance> ReadAsync(Stream audio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);

        // Модели ждут WAV, а Telegram отдаёт OGG/Opus — переиспользуем тот же декодер,
        // что и каскад.
        var samples = await decoder.DecodeToPcm16kMonoAsync(audio, ct);
        if (samples.Length == 0)
        {
            return new Utterance(string.Empty, 0f, Engine);
        }

        var wav = Convert.ToBase64String(ToWav(samples));
        var role = _llm.For(LlmRole.Extraction);

        var reply = await client.SendAsync(
            new ChatRequest
            {
                Model = string.IsNullOrWhiteSpace(_speech.NativeAudioModel) ? role.Model : _speech.NativeAudioModel,
                Messages = [ChatMessagePayload.WithAudio("user", Instruction, wav)],
                Temperature = 0.0f,
                ReasoningEffort = _llm.DisableThinking ? _llm.ReasoningEffort : null,
            },
            ct);

        // Уверенности мультимодальная модель не сообщает; ставим срединное значение,
        // чтобы гибридный режим не считал такую расшифровку заведомо надёжной.
        return new Utterance(reply.Text.Trim(), 0.7f, Engine);
    }

    private string Engine => "native-audio/" +
        (string.IsNullOrWhiteSpace(_speech.NativeAudioModel)
            ? _llm.For(LlmRole.Extraction).Model
            : _speech.NativeAudioModel);

    /// <summary>Оборачивает PCM 16 кГц моно в минимальный WAV-контейнер.</summary>
    internal static byte[] ToWav(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        const int sampleRate = 16_000;
        const short bitsPerSample = 16;
        const short channels = 1;

        var dataSize = samples.Length * sizeof(short);
        var buffer = new byte[44 + dataSize];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);              // размер блока fmt
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);               // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], value);
        }

        return buffer;
    }
}
