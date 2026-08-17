using System.Runtime.CompilerServices;
using Diary.Application.Ports;
using Diary.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TL;

namespace Diary.Infrastructure.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public int ApiId { get; set; }

    public string ApiHash { get; set; } = string.Empty;

    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>
    /// Пароль двухфакторной аутентификации. Задаётся переменной окружения, а не файлом
    /// конфигурации: в контейнере терминала может не быть, а спросить его больше негде.
    /// Пусто — спросим в терминале.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Файл сессии равен доступу к аккаунту — обращаться как с паролем.</summary>
    public string SessionFile { get; set; } = "data/telegram.session";

    public bool MarkAsRead { get; set; }

    /// <summary>Сколько сообщений тянуть за один запрос истории.</summary>
    public int PageSize { get; set; } = 100;
}

/// <summary>
/// Чтение истории через MTProto под личным аккаунтом. Именно это снимает ограничение
/// Bot API в 24 часа: история читается целиком, а медиа скачивается спустя годы.
/// </summary>
public sealed class TelegramMessageSource : IMessageSource
{
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramMessageSource> _logger;
    private readonly WTelegram.Client _client;
    private readonly Dictionary<long, InputPeer> _peers = [];
    private bool _loggedIn;

    public TelegramMessageSource(IOptions<TelegramOptions> options, ILogger<TelegramMessageSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _logger = logger;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.SessionFile))!);

        // Библиотека по умолчанию пишет весь обмен MTProto прямо в консоль. Это заливает
        // вывод и, что хуже, топит приглашение ввести код при первом входе. Уводим
        // в обычный логгер на Debug — при --verbose всё по-прежнему видно.
        WTelegram.Helpers.Log = (level, message) => logger.Log(
            level switch
            {
                4 => LogLevel.Error,
                3 => LogLevel.Warning,
                2 => LogLevel.Debug,
                _ => LogLevel.Trace,
            },
            "{Message}", message);

        _client = new WTelegram.Client(ConfigValue);
    }

    private string? ConfigValue(string what) => what switch
    {
        "api_id" => _options.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "api_hash" => _options.ApiHash,
        "phone_number" => _options.PhoneNumber,
        "session_pathname" => Path.GetFullPath(_options.SessionFile),
        // Код приходит в момент входа — его нельзя ни задать заранее, ни угадать.
        "verification_code" => Prompt("Код из Telegram: "),
        "password" => string.IsNullOrEmpty(_options.Password)
            ? Prompt("Пароль двухфакторной аутентификации: ")
            : _options.Password,
        _ => null,
    };

    private static string Prompt(string message)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                $"Требуется ввод: {message.TrimEnd(' ', ':')}. Терминала нет — запусти команду " +
                "интерактивно: docker compose run --rm diary sync");
        }

        Console.Write(message);
        return Console.ReadLine() ?? string.Empty;
    }

    public async Task<IReadOnlyDictionary<string, long>> ResolvePeersAsync(
        IReadOnlyCollection<string> peers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(peers);
        await EnsureLoggedInAsync();

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var dialogs = await _client.Messages_GetAllDialogs();

        foreach (var peer in peers)
        {
            ct.ThrowIfCancellationRequested();

            var resolved = ResolveFromDialogs(dialogs, peer) ?? await ResolveByUsernameAsync(peer);
            if (resolved is null)
            {
                _logger.LogError("Чат «{Peer}» не найден среди диалогов этого аккаунта.", peer);
                continue;
            }

            var id = resolved.ID;
            _peers[id] = resolved;
            result[peer] = id;
        }

        return result;
    }

    private static InputPeer? ResolveFromDialogs(Messages_Dialogs dialogs, string peer)
    {
        var trimmed = peer.TrimStart('@');

        if (long.TryParse(peer, System.Globalization.CultureInfo.InvariantCulture, out var numeric))
        {
            // Telegram показывает id каналов с префиксом -100; принимаем оба написания.
            var bare = Math.Abs(numeric);
            if (bare > 1_000_000_000_000L)
            {
                bare -= 1_000_000_000_000L;
            }

            foreach (var chat in dialogs.chats.Values)
            {
                if (chat.ID == bare)
                {
                    return chat.ToInputPeer();
                }
            }

            foreach (var user in dialogs.users.Values)
            {
                if (user.ID == bare)
                {
                    return user.ToInputPeer();
                }
            }

            return null;
        }

        foreach (var chat in dialogs.chats.Values)
        {
            if (chat is Channel channel &&
                string.Equals(channel.MainUsername, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return channel.ToInputPeer();
            }

            if (string.Equals(chat.Title, peer, StringComparison.OrdinalIgnoreCase))
            {
                return chat.ToInputPeer();
            }
        }

        foreach (var user in dialogs.users.Values)
        {
            if (string.Equals(user.MainUsername, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return user.ToInputPeer();
            }
        }

        return null;
    }

    private async Task<InputPeer?> ResolveByUsernameAsync(string peer)
    {
        var trimmed = peer.TrimStart('@');
        if (trimmed.Length == 0 || !char.IsLetter(trimmed[0]))
        {
            return null;
        }

        try
        {
            var resolved = await _client.Contacts_ResolveUsername(trimmed);
            return resolved.UserOrChat?.ToInputPeer();
        }
        catch (RpcException ex)
        {
            _logger.LogDebug(ex, "Не удалось разрешить username «{Peer}».", peer);
            return null;
        }
    }

    public async IAsyncEnumerable<IncomingMessage> FetchAsync(
        long peerId, long afterMessageId, [EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureLoggedInAsync();

        if (!_peers.TryGetValue(peerId, out var peer))
        {
            yield break;
        }

        // История отдаётся от новых к старым, поэтому копим страницу и разворачиваем:
        // курсор обязан двигаться по возрастанию, иначе падение посреди прогона
        // потеряет всё, что между.
        var offsetId = 0;
        var collected = new List<IncomingMessage>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var history = await _client.Messages_GetHistory(
                peer, offset_id: offsetId, limit: _options.PageSize, min_id: (int)afterMessageId);

            if (history.Messages.Length == 0)
            {
                break;
            }

            var reachedCursor = false;
            foreach (var message in history.Messages)
            {
                if (message is not Message full)
                {
                    continue;
                }

                if (full.id <= afterMessageId)
                {
                    reachedCursor = true;
                    continue;
                }

                collected.Add(Convert(full, peerId));
            }

            offsetId = history.Messages[^1].ID;

            if (reachedCursor || history.Messages.Length < _options.PageSize)
            {
                break;
            }
        }

        foreach (var message in collected.OrderBy(m => m.TelegramMessageId))
        {
            yield return message;
        }
    }

    private static IncomingMessage Convert(Message message, long peerId)
    {
        var voice = message.media as MessageMediaDocument;
        var document = voice?.document as Document;
        var audio = document?.GetAttribute<DocumentAttributeAudio>();
        var isVoice = audio?.flags.HasFlag(DocumentAttributeAudio.Flags.voice) == true;

        return new IncomingMessage
        {
            PeerId = peerId,
            TelegramMessageId = message.id,
            SenderId = message.from_id?.ID,
            SentAtUtc = message.date.ToUniversalTime(),
            EditedAtUtc = message.edit_date == default ? null : message.edit_date.ToUniversalTime(),
            Kind = isVoice ? MessageKind.Voice : string.IsNullOrWhiteSpace(message.message)
                ? MessageKind.Other
                : MessageKind.Text,
            Text = message.message,
            ReplyToTelegramMessageId = (message.reply_to as MessageReplyHeader)?.reply_to_msg_id,
            IsForwarded = message.fwd_from is not null,
            ForwardedFromId = message.fwd_from?.from_id?.ID,
            Voice = isVoice && document is not null
                ? new VoiceInfo(
                    TimeSpan.FromSeconds(audio!.duration),
                    document.mime_type ?? "audio/ogg",
                    document.size)
                : null,
            MediaHandle = document,
        };
    }

    public async Task DownloadVoiceAsync(IncomingMessage message, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.MediaHandle is not Document document)
        {
            throw new InvalidOperationException(
                $"У сообщения {message.TelegramMessageId} нет документа для скачивания.");
        }

        await _client.DownloadFileAsync(document, destination);
        _ = ct;
    }

    public async Task ReactAsync(long peerId, long messageId, string emoji, CancellationToken ct)
    {
        if (!_peers.TryGetValue(peerId, out var peer))
        {
            return;
        }

        await _client.Messages_SendReaction(peer, (int)messageId, reaction: [new ReactionEmoji { emoticon = emoji }]);
        _ = ct;
    }

    public async Task DeleteAsync(long peerId, IReadOnlyList<long> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        if (messageIds.Count == 0 || !_peers.TryGetValue(peerId, out var peer))
        {
            return;
        }

        var ids = messageIds.Select(id => (int)id).ToArray();

        if (peer is InputPeerChannel channel)
        {
            await _client.Channels_DeleteMessages(new InputChannel(channel.channel_id, channel.access_hash), ids);
        }
        else
        {
            await _client.DeleteMessages(peer, ids);
        }

        _logger.LogInformation("Удалено {Count} сообщений из чата {Peer}.", ids.Length, peerId);
        _ = ct;
    }

    public async Task MarkReadAsync(long peerId, long uptoMessageId, CancellationToken ct)
    {
        if (!_options.MarkAsRead || !_peers.TryGetValue(peerId, out var peer))
        {
            return;
        }

        await _client.ReadHistory(peer, (int)uptoMessageId);
        _ = ct;
    }

    private async Task EnsureLoggedInAsync()
    {
        if (_loggedIn)
        {
            return;
        }

        var user = await _client.LoginUserIfNeeded();
        _logger.LogInformation("Вход выполнен: {User} (id {Id}).", user.MainUsername ?? user.first_name, user.ID);
        _loggedIn = true;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
