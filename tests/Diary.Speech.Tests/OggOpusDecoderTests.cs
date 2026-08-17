using Diary.Infrastructure.Speech;
using Shouldly;

namespace Diary.Speech.Tests;

/// <summary>
/// Разбор OGG написан вручную, потому что Concentus даёт кодек без контейнера.
/// Самое хрупкое место — лейсинг: пакет длиной ровно 255 байт продолжается
/// следующим сегментом, и пакеты переходят через границы страниц.
/// </summary>
public sealed class OggPageParsingTests
{
    /// <summary>Собирает страницу OGG с заданными сегментами.</summary>
    private static byte[] Page(params byte[][] segments)
    {
        var table = segments.Select(s => (byte)s.Length).ToArray();
        var payload = segments.SelectMany(s => s).ToArray();

        var page = new List<byte>(27 + table.Length + payload.Length);
        page.AddRange("OggS"u8.ToArray());
        page.Add(0);                       // версия
        page.Add(0);                       // тип страницы
        page.AddRange(new byte[8]);        // granule position
        page.AddRange(new byte[4]);        // serial
        page.AddRange(new byte[4]);        // номер страницы
        page.AddRange(new byte[4]);        // контрольная сумма
        page.Add((byte)table.Length);      // число сегментов
        page.AddRange(table);
        page.AddRange(payload);

        return [.. page];
    }

    private static byte[] Segment(int length, byte fill = 0xAB) =>
        Enumerable.Repeat(fill, length).ToArray();

    [Fact]
    public void ОдинСегмент_ЭтоОдинПакет()
    {
        var packets = OggOpusDecoder.ReadOggPackets(Page(Segment(10)));

        packets.ShouldHaveSingleItem().Length.ShouldBe(10);
    }

    [Fact]
    public void НесколькоСегментов_ЭтоНесколькоПакетов()
    {
        var packets = OggOpusDecoder.ReadOggPackets(Page(Segment(10), Segment(20), Segment(5)));

        packets.Select(p => p.Length).ShouldBe([10, 20, 5]);
    }

    [Fact]
    public void СегментВ255Байт_ПродолжаетсяСледующим()
    {
        // Ровно 255 означает «пакет не кончился»: 255 + 40 склеиваются в один пакет.
        var packets = OggOpusDecoder.ReadOggPackets(Page(Segment(255), Segment(40)));

        packets.ShouldHaveSingleItem().Length.ShouldBe(295);
    }

    [Fact]
    public void ПакетРовноВ255Байт_ЗакрываетсяНулевымСегментом()
    {
        var packets = OggOpusDecoder.ReadOggPackets(Page(Segment(255), Segment(0)));

        packets.ShouldHaveSingleItem().Length.ShouldBe(255);
    }

    [Fact]
    public void ПакетПродолжаетсяЧерезГраницуСтраницы()
    {
        var stream = Page(Segment(255)).Concat(Page(Segment(30))).ToArray();

        var packets = OggOpusDecoder.ReadOggPackets(stream);

        packets.ShouldHaveSingleItem().Length.ShouldBe(285);
    }

    [Fact]
    public void НесколькоСтраниц_ЧитаютсяПодряд()
    {
        var stream = Page(Segment(10), Segment(20))
            .Concat(Page(Segment(30)))
            .Concat(Page(Segment(40), Segment(50)))
            .ToArray();

        var packets = OggOpusDecoder.ReadOggPackets(stream);

        packets.Select(p => p.Length).ShouldBe([10, 20, 30, 40, 50]);
    }

    [Fact]
    public void СодержимоеПакетаСохраняетсяПриСклейке()
    {
        var first = Segment(255, 0x11);
        var second = Segment(3, 0x22);

        var packet = OggOpusDecoder.ReadOggPackets(Page(first, second)).ShouldHaveSingleItem();

        packet.Length.ShouldBe(258);
        packet[0].ShouldBe((byte)0x11);
        packet[254].ShouldBe((byte)0x11);
        packet[255].ShouldBe((byte)0x22);
        packet[257].ShouldBe((byte)0x22);
    }

    [Fact]
    public void МусорПередЗаголовком_Пропускается()
    {
        var stream = new byte[] { 0x00, 0xFF, 0x42 }.Concat(Page(Segment(10))).ToArray();

        OggOpusDecoder.ReadOggPackets(stream).ShouldHaveSingleItem().Length.ShouldBe(10);
    }

    [Fact]
    public void ОбрезанныйПоток_НеРоняетРазбор()
    {
        // Заявлено 100 байт, а в файле их нет: пишущая сторона оборвалась.
        var page = Page(Segment(10)).ToList();
        page[26] = 2;
        page.Insert(27, 100);

        Should.NotThrow(() => OggOpusDecoder.ReadOggPackets([.. page]));
    }

    [Fact]
    public void ПустойВход_ДаётПустойРезультат()
    {
        OggOpusDecoder.ReadOggPackets([]).ShouldBeEmpty();
        OggOpusDecoder.ReadOggPackets([0x01, 0x02]).ShouldBeEmpty();
    }
}
