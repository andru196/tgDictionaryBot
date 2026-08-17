using System.Buffers.Binary;
using System.Text.Json;
using Diary.Domain;
using Diary.Infrastructure.Llm;
using Shouldly;

namespace Diary.Llm.Tests;

/// <summary>
/// Три типичные помехи в ответе модели: блок рассуждений, markdown-ограждение
/// и текст вокруг объекта. Дешевле вырезать, чем уговаривать промптом.
/// </summary>
public sealed class ResponseCleanerTests
{
    private sealed record Probe(string? Name, int Value);

    [Fact]
    public void ЧистыйJson_ОстаётсяКакЕсть() =>
        ResponseCleaner.Clean("""{"name":"борщ","value":1}""")
            .ShouldBe("""{"name":"борщ","value":1}""");

    [Fact]
    public void БлокРассуждений_Вырезается()
    {
        var raw = "<think>Так, тут еда и симптом…</think>\n{\"name\":\"борщ\",\"value\":1}";

        ResponseCleaner.Clean(raw).ShouldBe("""{"name":"борщ","value":1}""");
    }

    [Fact]
    public void НезакрытыйБлокРассуждений_НеЛомаетРазбор()
    {
        // Модель не дописала ответ: пусть падает на разборе, но с сохранённым сырым текстом.
        var raw = "<think>рассуждаю и не закончил";

        Should.NotThrow(() => ResponseCleaner.Clean(raw));
    }

    [Fact]
    public void MarkdownОграждение_Снимается()
    {
        var raw = "```json\n{\"name\":\"борщ\",\"value\":1}\n```";

        ResponseCleaner.Clean(raw).ShouldBe("""{"name":"борщ","value":1}""");
    }

    [Fact]
    public void ТекстВокругОбъекта_Отбрасывается()
    {
        var raw = "Вот результат:\n{\"name\":\"борщ\",\"value\":1}\nНадеюсь, помог!";

        ResponseCleaner.Clean(raw).ShouldBe("""{"name":"борщ","value":1}""");
    }

    [Fact]
    public void ФигурныеСкобкиВнутриСтроки_НеСбиваютБаланс()
    {
        var raw = """{"name":"текст с } скобкой","value":1}""";

        ResponseCleaner.Clean(raw).ShouldBe(raw);
    }

    [Fact]
    public void ЭкранированнаяКавычка_НеЗакрываетСтроку()
    {
        var raw = """{"name":"он сказал \"да\" и ушёл","value":2}""";

        var cleaned = ResponseCleaner.Clean(raw);

        JsonSerializer.Deserialize<Probe>(cleaned, DiaryJson.Options)!.Value.ShouldBe(2);
    }

    [Fact]
    public void ВложенныеОбъекты_СохраняютсяЦеликом()
    {
        var raw = """{"name":"a","value":1,"inner":{"deep":{"x":2}}}""";

        ResponseCleaner.Clean(raw).ShouldBe(raw);
    }

    [Fact]
    public void РазборСВырезаниемПомех_Удаётся()
    {
        var raw = "<think>думаю</think>```json\n{\"name\":\"борщ\",\"value\":7}\n```";

        ResponseCleaner.TryDeserialize<Probe>(raw, DiaryJson.Options, out var value).ShouldBeTrue();
        value!.Value.ShouldBe(7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("совсем не json")]
    [InlineData("{битый")]
    public void НеразбираемоеСодержимое_ДаётFalse(string raw) =>
        ResponseCleaner.TryDeserialize<Probe>(raw, DiaryJson.Options, out _).ShouldBeFalse();
}

public sealed class WavEncodingTests
{
    [Fact]
    public void PcmОборачиваетсяВКорректныйWav()
    {
        var samples = new[] { 0f, 0.5f, -0.5f, 1f };

        var wav = NativeAudioUtteranceReader.ToWav(samples);

        wav.Length.ShouldBe(44 + (samples.Length * 2));
        System.Text.Encoding.ASCII.GetString(wav, 0, 4).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).ShouldBe("WAVE");
        System.Text.Encoding.ASCII.GetString(wav, 36, 4).ShouldBe("data");

        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)).ShouldBe((short)1);       // моно
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)).ShouldBe(16_000);         // 16 кГц
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)).ShouldBe((short)16);      // разрядность
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)).ShouldBe(samples.Length * 2);
    }

    [Fact]
    public void ЗначенияЗаПределамиДиапазона_Обрезаются()
    {
        var wav = NativeAudioUtteranceReader.ToWav([5f, -5f]);

        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)).ShouldBe(short.MaxValue);
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(46)).ShouldBe(short.MinValue);
    }

    [Fact]
    public void ПустойСигнал_ДаётТолькоЗаголовок() =>
        NativeAudioUtteranceReader.ToWav([]).Length.ShouldBe(44);
}

public sealed class SchemaSanitizationTests
{
    private sealed record WithNumbers(double? Ratio, int Count, string? Name);

    [Fact]
    public void СхемаНеСодержитКлючевыхСлов_КоторыеНеПереваритДвижокГрамматик()
    {
        // Регрессия: Web-настройки разрешают числа из строк, экспортёр добавляет pattern
        // с регуляркой, и LM Studio отвечает 400.
        var schema = LmStudioCompletion.SchemaCache.For<WithNumbers>().ToJsonString();

        schema.ShouldNotContain("pattern");
        schema.ShouldNotContain("$schema");
        schema.ShouldContain("ratio");
        schema.ShouldContain("count");
    }
}
