namespace Diary.Application.Ports;

/// <param name="PeerId">Числовой идентификатор для конфигурации субъекта.</param>
/// <param name="MyUserId">Собственный id — его же надо прописать в SenderIds.</param>
public sealed record CreatedChat(long PeerId, string Title, long MyUserId, IReadOnlyList<string> NotInvited);

/// <summary>
/// Заведение чата под дневник. Отдельный порт от чтения сообщений: это разовая
/// настройка, а не часть пайплайна, и смешивать их в одном интерфейсе незачем.
/// </summary>
public interface IChatAdministration
{
    Task<CreatedChat> CreateGroupAsync(
        string title, IReadOnlyList<string> usernames, CancellationToken ct);
}
