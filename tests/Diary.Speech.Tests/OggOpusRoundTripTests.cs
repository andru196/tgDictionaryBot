using Concentus;
using Concentus.Enums;
using Diary.Infrastructure.Speech;
using Shouldly;

namespace Diary.Speech.Tests;

/// <summary>
/// Сквозная проверка: закодировать звук Opus'ом, упаковать в OGG и раскодировать
/// собственным декодером. Ловит то, чего не видят тесты разбора страниц —
/// заголовок, pre-skip, склейку каналов и понижение частоты.
/// </summary>
public sealed class OggOpusRoundTripTests
{
    private const int OpusRate = 48_000;
    private const int FrameSamples = 960;   // 20 мс
    private const int PreSkip = 312;

    [Fact]
    public async Task ЗакодированныйЗвук_ДекодируетсяВPcm16кГцМоно()
    {
        var seconds = 2;
        var ogg = BuildOgg(Tone(OpusRate * seconds), channels: 1);

        using var stream = new MemoryStream(ogg);
        var pcm = await new OggOpusDecoder().DecodeToPcm16kMonoAsync(stream, TestContext.Current.CancellationToken);

        // 2 секунды на 16 кГц — около 32 000 отсчётов; допускаем потери на pre-skip
        // и на неполный последний кадр.
        pcm.Length.ShouldBeInRange(30_000, 32_000);
        pcm.ShouldContain(v => Math.Abs(v) > 0.05f, "декодированный сигнал не должен быть тишиной");
        pcm.ShouldAllBe(v => Math.Abs(v) <= 1.01f);
    }

    [Fact]
    public async Task Стерео_СводитсяВМоно()
    {
        var ogg = BuildOgg(Tone(OpusRate, channels: 2), channels: 2);

        using var stream = new MemoryStream(ogg);
        var pcm = await new OggOpusDecoder().DecodeToPcm16kMonoAsync(stream, TestContext.Current.CancellationToken);

        // Секунда стерео тоже даёт около 16 000 отсчётов моно, а не вдвое больше.
        pcm.Length.ShouldBeInRange(15_000, 16_100);
    }

    [Fact]
    public async Task ПотокБезАудиопакетов_ДаётПонятнуюОшибку()
    {
        using var stream = new MemoryStream([1, 2, 3]);

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await new OggOpusDecoder().DecodeToPcm16kMonoAsync(stream, TestContext.Current.CancellationToken));
    }

    private static float[] Tone(int samples, int channels = 1)
    {
        var pcm = new float[samples * channels];
        for (var i = 0; i < samples; i++)
        {
            var value = (float)(0.6 * Math.Sin(2 * Math.PI * 440 * i / OpusRate));
            for (var c = 0; c < channels; c++)
            {
                pcm[(i * channels) + c] = value;
            }
        }

        return pcm;
    }

    private static byte[] BuildOgg(float[] pcm, int channels)
    {
        var encoder = OpusCodecFactory.CreateEncoder(OpusRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        var packets = new List<byte[]> { OpusHead(channels), OpusTags() };

        var buffer = new byte[4000];
        var frame = FrameSamples * channels;

        for (var offset = 0; offset + frame <= pcm.Length; offset += frame)
        {
            var written = encoder.Encode(pcm.AsSpan(offset, frame), FrameSamples, buffer, buffer.Length);
            packets.Add(buffer[..written]);
        }

        return Pack(packets);
    }

    private static byte[] OpusHead(int channels)
    {
        var head = new List<byte>(19);
        head.AddRange("OpusHead"u8.ToArray());
        head.Add(1);                                     // версия
        head.Add((byte)channels);
        head.AddRange(BitConverter.GetBytes((ushort)PreSkip));
        head.AddRange(BitConverter.GetBytes(OpusRate));
        head.AddRange(BitConverter.GetBytes((short)0));  // gain
        head.Add(0);                                     // mapping family
        return [.. head];
    }

    private static byte[] OpusTags()
    {
        var tags = new List<byte>();
        tags.AddRange("OpusTags"u8.ToArray());
        tags.AddRange(BitConverter.GetBytes(4));
        tags.AddRange("test"u8.ToArray());
        tags.AddRange(BitConverter.GetBytes(0));
        return [.. tags];
    }

    /// <summary>Раскладывает пакеты по страницам, соблюдая лейсинг.</summary>
    private static byte[] Pack(List<byte[]> packets)
    {
        var output = new List<byte>();
        var pageIndex = 0;

        foreach (var packet in packets)
        {
            var segments = new List<byte>();
            var remaining = packet.Length;

            while (remaining >= 255)
            {
                segments.Add(255);
                remaining -= 255;
            }

            segments.Add((byte)remaining);

            output.AddRange("OggS"u8.ToArray());
            output.Add(0);
            output.Add(0);
            output.AddRange(new byte[8]);
            output.AddRange(BitConverter.GetBytes(1));            // serial
            output.AddRange(BitConverter.GetBytes(pageIndex++));
            output.AddRange(new byte[4]);                          // контрольная сумма не проверяется
            output.Add((byte)segments.Count);
            output.AddRange(segments);
            output.AddRange(packet);
        }

        return [.. output];
    }
}
