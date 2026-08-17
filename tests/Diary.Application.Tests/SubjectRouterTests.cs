using Diary.Application;
using Diary.Application.Ports;
using Diary.Application.Subjects;
using Diary.Application.UseCases;
using Diary.Domain;
using Shouldly;

namespace Diary.Application.Tests;

public sealed class SubjectRouterTests
{
    private const long SharedChatId = 500;

    private static readonly IReadOnlyDictionary<string, long> Peers = new Dictionary<string, long>
    {
        ["@my_channel"] = 100,
        ["shared_group"] = SharedChatId,
    };

    private static SubjectDefinition Subject(string key, params SubjectSource[] sources) => new()
    {
        Key = new SubjectKey(key),
        DisplayName = key,
        TimeZone = TimeZoneInfo.Utc,
        DataDirectory = $"data/{key}",
        Sources = sources,
        Modules = ["gi"],
    };

    private static IncomingMessage Message(long peerId, long? senderId, bool forwarded = false,
        long? forwardedFrom = null) => new()
        {
            PeerId = peerId,
            TelegramMessageId = 1,
            SenderId = senderId,
            SentAtUtc = DateTimeOffset.UnixEpoch,
            Kind = MessageKind.Text,
            Text = "текст",
            IsForwarded = forwarded,
            ForwardedFromId = forwardedFrom,
        };

    private static SubjectRouter Build(ForwardPolicy policy, params SubjectDefinition[] subjects) =>
        new(subjects, Peers, policy);

    [Fact]
    public void ОдинЧатНаДвоих_РазводитсяПоОтправителю()
    {
        var router = Build(
            ForwardPolicy.Skip,
            Subject("me", new SubjectSource("shared_group", [777], false)),
            Subject("mom", new SubjectSource("shared_group", [888], false)));

        router.Route(Message(SharedChatId, 777)).ShouldBeOfType<SubjectRouting.Assigned>()
            .Subject.Value.ShouldBe("me");
        router.Route(Message(SharedChatId, 888)).ShouldBeOfType<SubjectRouting.Assigned>()
            .Subject.Value.ShouldBe("mom");
    }

    [Fact]
    public void НеизвестныйОтправитель_УходитВКарантин_АНеКБлижайшему()
    {
        var router = Build(ForwardPolicy.Skip, Subject("me", new SubjectSource("shared_group", [777], false)));

        var routing = router.Route(Message(SharedChatId, 999)).ShouldBeOfType<SubjectRouting.Unassigned>();

        routing.Reason.ShouldBe(UnassignedReason.UnknownSender);
        routing.SenderId.ShouldBe(999);
    }

    [Fact]
    public void ПостБезОтправителя_ПринимаетсяТолькоЭксклюзивнымЧатом()
    {
        var exclusive = Build(ForwardPolicy.Skip, Subject("me", new SubjectSource("@my_channel", [], true)));
        exclusive.Route(Message(100, null)).ShouldBeOfType<SubjectRouting.Assigned>();

        var shared = Build(ForwardPolicy.Skip, Subject("me", new SubjectSource("shared_group", [777], false)));
        shared.Route(Message(SharedChatId, null)).ShouldBeOfType<SubjectRouting.Unassigned>()
            .Reason.ShouldBe(UnassignedReason.NoSenderInChannel);
    }

    [Fact]
    public void НеизвестныйЧат_НеПривязываетсяНиККому() =>
        Build(ForwardPolicy.Skip, Subject("me", new SubjectSource("@my_channel", [], true)))
            .Route(Message(999999, 777))
            .ShouldBeOfType<SubjectRouting.Unassigned>()
            .Reason.ShouldBe(UnassignedReason.UnknownPeer);

    [Fact]
    public void ПересылкаПоУмолчанию_Пропускается() =>
        Build(ForwardPolicy.Skip, Subject("me", new SubjectSource("@my_channel", [], true)))
            .Route(Message(100, 777, forwarded: true))
            .ShouldBeOfType<SubjectRouting.Unassigned>()
            .Reason.ShouldBe(UnassignedReason.Forwarded);

    [Fact]
    public void ПересылкаПоАвторуОригинала_ПривязываетсяКНему()
    {
        var router = Build(
            ForwardPolicy.OriginalAuthor,
            Subject("mom", new SubjectSource("shared_group", [888], false)));

        router.Route(Message(SharedChatId, 777, forwarded: true, forwardedFrom: 888))
            .ShouldBeOfType<SubjectRouting.Assigned>()
            .Subject.Value.ShouldBe("mom");
    }

    [Fact]
    public void ЭксклюзивныйЧат_ПринимаетИНеперечисленногоОтправителя() =>
        Build(ForwardPolicy.Skip, Subject("me", new SubjectSource("@my_channel", [], true)))
            .Route(Message(100, 12345))
            .ShouldBeOfType<SubjectRouting.Assigned>();
}

public sealed class SubjectValidationTests
{
    private static SubjectDefinition Subject(string key, string directory, params SubjectSource[] sources) => new()
    {
        Key = new SubjectKey(key),
        DisplayName = key,
        TimeZone = TimeZoneInfo.Utc,
        DataDirectory = directory,
        Sources = sources,
        Modules = ["gi"],
    };

    [Fact]
    public void ПустойСписокСубъектов_Отвергается() =>
        Should.Throw<InvalidOperationException>(() => DependencyInjection.Validate([]));

    [Fact]
    public void ОдинОтправительУДвухСубъектов_Отвергается() =>
        Should.Throw<InvalidOperationException>(() => DependencyInjection.Validate(
        [
            Subject("me", "data/me", new SubjectSource("g", [777], false)),
            Subject("mom", "data/mom", new SubjectSource("g", [777], false)),
        ])).Message.ShouldContain("777");

    [Fact]
    public void ОбщийКаталогДанных_Отвергается() =>
        Should.Throw<InvalidOperationException>(() => DependencyInjection.Validate(
        [
            Subject("me", "data/shared", new SubjectSource("a", [777], false)),
            Subject("mom", "data/shared", new SubjectSource("b", [888], false)),
        ])).Message.ShouldContain("Каталог");

    [Fact]
    public void ЭксклюзивныйЧатСЧужимиОтправителями_Отвергается() =>
        Should.Throw<InvalidOperationException>(() => DependencyInjection.Validate(
        [
            Subject("me", "data/me", new SubjectSource("g", [], true)),
            Subject("mom", "data/mom", new SubjectSource("g", [888], false)),
        ]));

    [Fact]
    public void НеэксклюзивныйИсточникБезОтправителей_Отвергается() =>
        Should.Throw<InvalidOperationException>(() => DependencyInjection.Validate(
        [
            Subject("me", "data/me", new SubjectSource("g", [], false)),
        ])).Message.ShouldContain("SenderIds");

    [Fact]
    public void КорректнаяКонфигурация_Проходит() =>
        Should.NotThrow(() => DependencyInjection.Validate(
        [
            Subject("me", "data/me", new SubjectSource("@channel", [], true)),
            Subject("mom", "data/mom", new SubjectSource("g", [888], false)),
        ]));
}

public sealed class HashtagTests
{
    [Theory]
    [InlineData("Съел борщ #еда и записал #идея", new[] { "еда", "идея" })]
    [InlineData("без тегов", new string[0])]
    [InlineData("#ВОПРОС в верхнем регистре", new[] { "вопрос" })]
    [InlineData("решётка без слова # и #тег_2", new[] { "тег_2" })]
    public void Хэштеги_ИзвлекаютсяИПриводятсяКНижнемуРегистру(string text, string[] expected) =>
        SyncHandler.ExtractHashtags(text).ShouldBe(expected);
}
