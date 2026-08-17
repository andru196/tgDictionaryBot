using System.Globalization;
using Diary.Application.Ports;
using Diary.Application.Subjects;

namespace Diary.Infrastructure.Speech;

/// <summary>
/// Голосовые лежат в каталоге субъекта, разложенные по годам и месяцам. Аудио хранится
/// навсегда: десятки килобайт на сообщение против возможности переслушать спорное место
/// и переразобрать запись новой моделью.
/// </summary>
public sealed class FileSystemVoiceStorage(ISubjectContext subjectContext) : IVoiceStorage
{
    private string Root => subjectContext.Subject.VoiceDirectory;

    public async Task<string> SaveAsync(
        long telegramMessageId,
        DateTimeOffset sentAt,
        string extension,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        var relative = Path.Combine(
            sentAt.ToString("yyyy", CultureInfo.InvariantCulture),
            sentAt.ToString("MM", CultureInfo.InvariantCulture),
            $"{telegramMessageId}{extension}");

        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Пишем во временный файл и переименовываем: прерванная загрузка не должна
        // оставить обрезанный файл, который потом примут за целый.
        var temporary = full + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await write(stream, ct);
        }

        File.Move(temporary, full, overwrite: true);
        return relative;
    }

    public Stream OpenRead(string relativePath) =>
        File.OpenRead(Path.Combine(Root, relativePath));

    public bool Exists(string relativePath) =>
        File.Exists(Path.Combine(Root, relativePath));
}
