using Diary.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Diary.Cli;

/// <summary>
/// Заглушка ответа для источников, которым отвечать некуда — например, файлового.
/// Печатает в консоль то, что ушло бы в чат: команды отлаживаются без Telegram.
/// </summary>
public sealed class ConsoleChatResponder(ILogger<ConsoleChatResponder> logger) : IChatResponder
{
    public Task SendTextAsync(long peerId, string text, long? replyToMessageId, CancellationToken ct)
    {
        logger.LogInformation("[в чат {Peer}]\n{Text}", peerId, text);
        return Task.CompletedTask;
    }

    public Task SendDocumentAsync(
        long peerId, string filePath, string caption, long? replyToMessageId, CancellationToken ct)
    {
        logger.LogInformation("[в чат {Peer}] файл {Path}: {Caption}", peerId, filePath, caption);
        return Task.CompletedTask;
    }
}
