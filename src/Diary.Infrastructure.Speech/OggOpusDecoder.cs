using Concentus;
using Diary.Application.Ports;

namespace Diary.Infrastructure.Speech;

/// <summary>
/// Разбирает OGG-контейнер и декодирует Opus в PCM 16 кГц моно — то, что ждёт Whisper.
/// </summary>
/// <remarks>
/// Concentus 2.x даёт только кодек, без контейнера, поэтому страницы OGG разбираются здесь.
/// Это ~150 строк против внешнего ffmpeg в зависимостях: у пользователя ничего не должно
/// ломаться от того, что бинарника нет в PATH.
/// </remarks>
public sealed class OggOpusDecoder : IAudioDecoder
{
    private const int OpusSampleRate = 48_000;
    private const int TargetSampleRate = 16_000;
    private const int Decimation = OpusSampleRate / TargetSampleRate;

    public async Task<float[]> DecodeToPcm16kMonoAsync(Stream compressed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(compressed);

        using var buffer = new MemoryStream();
        await compressed.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        var packets = ReadOggPackets(bytes);
        if (packets.Count < 2)
        {
            throw new InvalidDataException("В OGG-потоке нет ни одного аудиопакета Opus.");
        }

        // Первые два пакета — заголовки OpusHead и OpusTags, аудио начинается с третьего.
        var (channels, preSkip) = ParseOpusHead(packets[0]);

        using var decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, channels);
        var pcm = new List<float>(capacity: packets.Count * 960 * channels);
        var frame = new float[OpusSampleRate / 1000 * 120 * channels];

        for (var i = 2; i < packets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            int decoded;
            try
            {
                decoded = decoder.Decode(packets[i], frame.AsSpan(), frame.Length / channels, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Битый пакет в середине записи не должен обесценивать остальную расшифровку.
                continue;
            }

            for (var s = 0; s < decoded; s++)
            {
                if (channels == 1)
                {
                    pcm.Add(frame[s]);
                    continue;
                }

                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += frame[(s * channels) + c];
                }

                pcm.Add(sum / channels);
            }
        }

        return Downsample(pcm, preSkip);
    }

    /// <summary>48 кГц → 16 кГц усреднением троек: для речи достаточно и заодно сглаживает.</summary>
    private static float[] Downsample(List<float> pcm, int preSkip)
    {
        var start = Math.Min(preSkip, pcm.Count);
        var length = (pcm.Count - start) / Decimation;
        if (length <= 0)
        {
            return [];
        }

        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var offset = start + (i * Decimation);
            result[i] = (pcm[offset] + pcm[offset + 1] + pcm[offset + 2]) / 3f;
        }

        return result;
    }

    private static (int Channels, int PreSkip) ParseOpusHead(byte[] header)
    {
        if (header.Length < 12 ||
            header[0] != 'O' || header[1] != 'p' || header[2] != 'u' || header[3] != 's')
        {
            // Не OpusHead — вероятно, файл не тот, что заявлен. Один канал, без пропуска.
            return (1, 0);
        }

        var channels = header[9];
        var preSkip = header[10] | (header[11] << 8);
        return (channels == 0 ? 1 : channels, preSkip);
    }

    /// <summary>
    /// Собирает пакеты из страниц OGG по таблице сегментов. Сегмент длиной 255 означает,
    /// что пакет продолжается в следующем — в том числе через границу страницы.
    /// </summary>
    internal static List<byte[]> ReadOggPackets(byte[] data)
    {
        var packets = new List<byte[]>();
        var current = new List<byte>();
        var offset = 0;

        while (offset + 27 <= data.Length)
        {
            if (data[offset] != 'O' || data[offset + 1] != 'g' ||
                data[offset + 2] != 'g' || data[offset + 3] != 'S')
            {
                offset++;
                continue;
            }

            var segmentCount = data[offset + 26];
            var tableOffset = offset + 27;
            if (tableOffset + segmentCount > data.Length)
            {
                break;
            }

            var payloadOffset = tableOffset + segmentCount;
            var cursor = payloadOffset;

            for (var i = 0; i < segmentCount; i++)
            {
                var size = data[tableOffset + i];
                if (cursor + size > data.Length)
                {
                    return packets;
                }

                current.AddRange(data.AsSpan(cursor, size).ToArray());
                cursor += size;

                if (size < 255)
                {
                    if (current.Count > 0)
                    {
                        packets.Add([.. current]);
                        current.Clear();
                    }
                }
            }

            offset = cursor;
        }

        if (current.Count > 0)
        {
            packets.Add([.. current]);
        }

        return packets;
    }
}
