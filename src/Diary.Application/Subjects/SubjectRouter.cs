using Diary.Application.Ports;
using Diary.Domain;

namespace Diary.Application.Subjects;

public enum UnassignedReason
{
    /// <summary>Отправитель есть, но в конфиге его нет.</summary>
    UnknownSender = 0,

    /// <summary>Пересылка, а политика — пропускать.</summary>
    Forwarded = 1,

    /// <summary>Пост от имени канала, а канал не объявлен эксклюзивным.</summary>
    NoSenderInChannel = 2,

    /// <summary>Чат вообще не упомянут ни у одного субъекта.</summary>
    UnknownPeer = 3,
}

public abstract record SubjectRouting
{
    public sealed record Assigned(SubjectKey Subject) : SubjectRouting;

    public sealed record Unassigned(UnassignedReason Reason, long? SenderId, string Description) : SubjectRouting;
}

/// <summary>
/// Определяет, чью запись мы читаем. Чат субъекта не определяет: в общую группу пишут
/// несколько человек, в канал может влезть кто угодно, а пересланное написано третьим лицом.
/// Решает пара (чат, отправитель), причём отправитель важнее.
/// </summary>
public interface ISubjectRouter
{
    SubjectRouting Route(IncomingMessage message);
}

public sealed class SubjectRouter : ISubjectRouter
{
    private readonly Dictionary<long, List<(SubjectKey Subject, SubjectSource Source)>> _byPeer = [];
    private readonly ForwardPolicy _forwardPolicy;

    /// <param name="resolvedPeers">
    /// Peer из конфига, уже разрешённый в числовой id. Резолвинг делается один раз при
    /// подключении: если резолвить каждый запуск, смена ника молча переклеит дневник
    /// на другого человека.
    /// </param>
    public SubjectRouter(
        IReadOnlyList<SubjectDefinition> subjects,
        IReadOnlyDictionary<string, long> resolvedPeers,
        ForwardPolicy forwardPolicy)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(resolvedPeers);

        _forwardPolicy = forwardPolicy;

        foreach (var subject in subjects)
        {
            foreach (var source in subject.Sources)
            {
                if (!resolvedPeers.TryGetValue(source.Peer, out var peerId))
                {
                    continue;
                }

                if (!_byPeer.TryGetValue(peerId, out var list))
                {
                    _byPeer[peerId] = list = [];
                }

                list.Add((subject.Key, source));
            }
        }
    }

    public SubjectRouting Route(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_byPeer.TryGetValue(message.PeerId, out var candidates))
        {
            return new SubjectRouting.Unassigned(
                UnassignedReason.UnknownPeer, message.SenderId,
                $"Чат {message.PeerId} не привязан ни к одному субъекту.");
        }

        // Пересылка: по умолчанию это чужой материал, а не собственная запись.
        var senderId = message.SenderId;
        if (message.IsForwarded)
        {
            switch (_forwardPolicy)
            {
                case ForwardPolicy.Skip:
                    return new SubjectRouting.Unassigned(
                        UnassignedReason.Forwarded, senderId,
                        "Пересланное сообщение, политика Forwarded=Skip.");
                case ForwardPolicy.OriginalAuthor:
                    senderId = message.ForwardedFromId ?? senderId;
                    break;
                case ForwardPolicy.Forwarder:
                default:
                    break;
            }
        }

        if (senderId is { } id)
        {
            foreach (var (subject, source) in candidates)
            {
                if (source.SenderIds.Contains(id))
                {
                    return new SubjectRouting.Assigned(subject);
                }
            }

            // Эксклюзивный чат забирает и тех отправителей, которых явно не перечислили:
            // владелец канала может писать с разных аккаунтов.
            foreach (var (subject, source) in candidates)
            {
                if (source.Exclusive)
                {
                    return new SubjectRouting.Assigned(subject);
                }
            }

            return new SubjectRouting.Unassigned(
                UnassignedReason.UnknownSender, id,
                $"Отправитель {id} в чате {message.PeerId} не сопоставлен ни одному субъекту.");
        }

        // Отправителя нет — пост от имени канала. Годится только эксклюзивная привязка.
        foreach (var (subject, source) in candidates)
        {
            if (source.Exclusive)
            {
                return new SubjectRouting.Assigned(subject);
            }
        }

        return new SubjectRouting.Unassigned(
            UnassignedReason.NoSenderInChannel, null,
            $"Сообщение в чате {message.PeerId} без отправителя, а чат не объявлен Exclusive.");
    }
}
