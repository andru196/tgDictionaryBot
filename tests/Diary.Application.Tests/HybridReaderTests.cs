using Diary.Application.Ports;
using Diary.Application.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Diary.Application.Tests;

public sealed class HybridUtteranceReaderTests
{
    private static IOptions<SpeechOptions> Options(float threshold = 0.6f) =>
        Microsoft.Extensions.Options.Options.Create(
            new SpeechOptions { HybridConfidenceThreshold = threshold });

    private static HybridUtteranceReader Build(IUtteranceReader primary, IUtteranceReader fallback) =>
        new(primary, fallback, Options(), NullLogger<HybridUtteranceReader>.Instance);

    private static MemoryStream Audio() => new([1, 2, 3, 4]);

    private static IUtteranceReader Reader(string text, float confidence)
    {
        var reader = Substitute.For<IUtteranceReader>();
        reader.ReadAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Utterance(text, confidence, "тест")));
        return reader;
    }

    [Fact]
    public async Task УвереннаяРасшифровка_НеТребуетПереслушивания()
    {
        var primary = Reader("поужинал жарёхой", 0.92f);
        var fallback = Reader("не должно вызываться", 1.0f);

        var result = await Build(primary, fallback)
            .ReadAsync(Audio(), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("поужинал жарёхой");
        await fallback.DidNotReceive().ReadAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task НизкаяУверенность_ПереслушиваетсяАудиоМоделью()
    {
        var primary = Reader("не разобрал", 0.3f);
        var fallback = Reader("поужинал жарёхой", 0.7f);

        var result = await Build(primary, fallback)
            .ReadAsync(Audio(), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("поужинал жарёхой");
    }

    [Fact]
    public async Task ПустойОтветЗапасного_НеЗатираетПервичный()
    {
        // Хоть какая-то расшифровка лучше пустоты: запись иначе теряется целиком.
        var primary = Reader("что-то неразборчивое", 0.2f);
        var fallback = Reader(string.Empty, 0f);

        var result = await Build(primary, fallback)
            .ReadAsync(Audio(), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("что-то неразборчивое");
    }

    [Fact]
    public async Task ПустаяРасшифровка_ПереслушиваетсяДажеПриВысокойУверенности()
    {
        var primary = Reader(string.Empty, 0.99f);
        var fallback = Reader("тихо сказанное", 0.7f);

        var result = await Build(primary, fallback)
            .ReadAsync(Audio(), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("тихо сказанное");
    }

    [Fact]
    public async Task ПотокПеречитываетсяСНачала()
    {
        var primary = Reader("плохо", 0.1f);
        var fallback = Substitute.For<IUtteranceReader>();
        long positionSeenByFallback = -1;

        fallback.ReadAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                positionSeenByFallback = call.Arg<Stream>().Position;
                return Task.FromResult(new Utterance("хорошо", 0.7f, "тест"));
            });

        var audio = Audio();
        audio.Position = 0;

        await Build(primary, fallback).ReadAsync(audio, TestContext.Current.CancellationToken);

        positionSeenByFallback.ShouldBe(0);
    }
}
