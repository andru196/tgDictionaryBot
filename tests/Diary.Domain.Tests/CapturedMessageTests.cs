using Diary.Domain;
using Shouldly;

namespace Diary.Domain.Tests;

public sealed class CapturedMessageTests
{
    private static DateTimeOffset Now => new(2026, 8, 4, 18, 40, 0, TimeSpan.Zero);

    private static CapturedMessage Text(string text = "поужинал жарёхой") =>
        CapturedMessage.Create(1, 100, 777, Now, MessageKind.Text, text, null, null, []);

    private static CapturedMessage Voice() =>
        CapturedMessage.Create(1, 101, 777, Now, MessageKind.Voice, null,
            new VoiceAsset("2026/08/101.ogg", TimeSpan.FromSeconds(9), "audio/ogg", 4096), null, []);

    [Fact]
    public void ТекстовоеСообщение_СразуГотовоКРазбору() =>
        Text().State.ShouldBe(ProcessingState.Transcribed);

    [Fact]
    public void Голосовое_ЖдётРасшифровки() =>
        Voice().State.ShouldBe(ProcessingState.Captured);

    [Fact]
    public void ГолосовоеБезФайла_НеСоздаётся() =>
        Should.Throw<ArgumentException>(() =>
            CapturedMessage.Create(1, 102, 777, Now, MessageKind.Voice, null, null, null, []));

    [Fact]
    public void Расшифровка_ПереводитВСледующееСостояние()
    {
        var message = Voice();

        message.AttachTranscript(new Transcript("поужинал жарёхой", 0.9f, "whisper", Now));

        message.State.ShouldBe(ProcessingState.Transcribed);
        message.EffectiveText.ShouldBe("поужинал жарёхой");
    }

    [Fact]
    public void ПовторнаяРасшифровка_Запрещена()
    {
        var message = Voice();
        message.AttachTranscript(new Transcript("раз", 0.9f, "whisper", Now));

        Should.Throw<InvalidOperationException>(() =>
            message.AttachTranscript(new Transcript("два", 0.9f, "whisper", Now)));
    }

    [Fact]
    public void РазборБезРасшифровки_Запрещён() =>
        Should.Throw<InvalidOperationException>(() => Voice().MarkExtracted());

    [Fact]
    public void ТекстовоеНельзяВернутьНаРасшифровку()
    {
        var message = Text();
        message.MarkExtracted();

        Should.Throw<InvalidOperationException>(() => message.ResetTo(ProcessingState.Captured));
    }

    [Fact]
    public void ВозвратВОбработку_СбрасываетПричинуОшибки()
    {
        var message = Text();
        message.MarkFailed("модель не ответила");

        message.ResetTo(ProcessingState.Transcribed);

        message.State.ShouldBe(ProcessingState.Transcribed);
        message.FailureReason.ShouldBeNull();
    }

    [Fact]
    public void ВозвратВПроизвольноеСостояние_Запрещён() =>
        Should.Throw<ArgumentException>(() => Text().ResetTo(ProcessingState.Extracted));
}
