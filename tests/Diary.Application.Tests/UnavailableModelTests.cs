using Diary.Application.Modules;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Application.UseCases;
using Diary.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Diary.Application.Tests;

/// <summary>
/// Разница между «модель выключена» и «модель ответила чушью» принципиальна:
/// первое проходит само, второе требует человека. Спутать их — значит превратить
/// выключенный на ночь LM Studio в гору записей, которые молча не обработаются.
/// </summary>
public sealed class UnavailableModelTests
{
    private sealed class FakeMessageRepository(List<CapturedMessage> messages) : IMessageRepository
    {
        public Task<IReadOnlyList<CapturedMessage>> GetByStateAsync(
            ProcessingState state, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapturedMessage>>(
                [.. messages.Where(m => m.State == state).Take(limit)]);

        public Task<CapturedMessage?> FindByTelegramIdAsync(long peerId, long id, CancellationToken ct) =>
            Task.FromResult<CapturedMessage?>(null);

        public Task<IReadOnlyList<CapturedMessage>> GetByPeriodAsync(DateRange period, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapturedMessage>>(messages);

        public Task<IReadOnlyList<CapturedMessage>> GetForRetentionAsync(
            ProcessingState state, DateTimeOffset before, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapturedMessage>>([]);

        public Task<IReadOnlyDictionary<ProcessingState, int>> CountByStateAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<ProcessingState, int>>(
                messages.GroupBy(m => m.State).ToDictionary(g => g.Key, g => g.Count()));

        public Task<IReadOnlyList<CapturedMessage>> GetProblematicAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapturedMessage>>([]);

        public Task AddAsync(CapturedMessage message, CancellationToken ct) => Task.CompletedTask;

        public Task<CapturedMessage?> FindAsync(MessageId id, CancellationToken ct) =>
            Task.FromResult<CapturedMessage?>(null);

        public Task<int> ResetStateAsync(
            ProcessingState from, ProcessingState target, DateTimeOffset? since, CancellationToken ct) =>
            Task.FromResult(0);
    }

    private static CapturedMessage Text(long id) =>
        CapturedMessage.Create(1, id, 777, DateTimeOffset.UnixEpoch, MessageKind.Text,
            "поужинал жарёхой", null, null, []);

    private static ExtractHandler Build(List<CapturedMessage> messages, Exception failure)
    {
        var segmenter = Substitute.For<IEntrySegmenter>();
        segmenter.SegmentAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CategoryDescriptor>>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<EntryFragment>>>(_ => throw failure);

        var modules = Substitute.For<IModuleRegistry>();
        modules.CategoriesFor(Arg.Any<IReadOnlyList<string>>())
            .Returns([new CategoryDescriptor("meal", "Еда", "про еду", [], [])]);

        var subject = Substitute.For<ISubjectContext>();
        subject.Subject.Returns(new SubjectDefinition
        {
            Key = new SubjectKey("me"),
            DisplayName = "Я",
            TimeZone = TimeZoneInfo.Utc,
            DataDirectory = "data/me",
            Sources = [new SubjectSource("@c", [], true)],
            Modules = ["gi"],
        });
        subject.TimeResolver.Returns(new RelativeTimeResolver(TimeZoneInfo.Utc));

        var llm = Substitute.For<IStructuredCompletion>();
        llm.ModelFor(Arg.Any<LlmRole>()).Returns("test-model");

        return new ExtractHandler(
            new FakeMessageRepository(messages),
            Substitute.For<IEntryRepository>(),
            segmenter,
            [],
            modules,
            subject,
            llm,
            Substitute.For<IUnitOfWork>(),
            NullLogger<ExtractHandler>.Instance);
    }

    [Fact]
    public async Task НедоступнаяМодель_НеПомечаетСообщенияПровалившимися()
    {
        var messages = new List<CapturedMessage> { Text(1), Text(2), Text(3) };
        var handler = Build(messages, new LlmUnavailableException("сервер не поднят"));

        var report = await handler.RunAsync(16, TestContext.Current.CancellationToken);

        report.Interrupted.ShouldBeTrue();
        report.Failed.ShouldBe(0);
        // Всё осталось в очереди и разберётся при следующем запуске.
        messages.ShouldAllBe(m => m.State == ProcessingState.Transcribed);
    }

    [Fact]
    public async Task НедоступнаяМодель_ПрерываетШагСразу()
    {
        // Ломиться дальше по очереди, когда сервер лежит, — только жечь время.
        var messages = new List<CapturedMessage> { Text(1), Text(2), Text(3) };
        var handler = Build(messages, new LlmUnavailableException("сервер не поднят"));

        var report = await handler.RunAsync(16, TestContext.Current.CancellationToken);

        report.Messages.ShouldBe(0);
        report.Interrupted.ShouldBeTrue();
    }

    [Fact]
    public async Task НевалидныйОтвет_ЭтоПостояннаяОшибка_ИПомечаетсяКакFailed()
    {
        var messages = new List<CapturedMessage> { Text(1) };
        var handler = Build(messages, new StructuredCompletionException("не разобралось", "{мусор"));

        var report = await handler.RunAsync(16, TestContext.Current.CancellationToken);

        report.Interrupted.ShouldBeFalse();
        report.Failed.ShouldBe(1);
        messages[0].State.ShouldBe(ProcessingState.Failed);
        messages[0].FailureReason.ShouldNotBeNull();
    }

    [Fact]
    public async Task ПостояннаяОшибкаНаОдномСообщении_НеОстанавливаетОстальные()
    {
        var messages = new List<CapturedMessage> { Text(1), Text(2), Text(3) };
        var handler = Build(messages, new StructuredCompletionException("не разобралось"));

        var report = await handler.RunAsync(16, TestContext.Current.CancellationToken);

        report.Failed.ShouldBe(3);
        report.Interrupted.ShouldBeFalse();
    }
}
