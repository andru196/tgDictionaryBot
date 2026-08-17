using System.Collections.Concurrent;
using System.Reflection;

namespace Diary.Application.Prompts;

/// <summary>
/// Промпты лежат встроенными ресурсами рядом с кодом, который их использует: промпт —
/// это код, он версионируется в git и ревьюится в PR. Версия пишется в БД рядом
/// с результатом, чтобы было понятно, что переразбирать после правки.
/// </summary>
public static class PromptLoader
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Load(Assembly assembly, string fileName)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var key = $"{assembly.FullName}|{fileName}";
        return Cache.GetOrAdd(key, _ =>
        {
            var resource = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (resource is null)
            {
                throw new InvalidOperationException(
                    $"Промпт «{fileName}» не найден в сборке {assembly.GetName().Name}. " +
                    $"Доступные ресурсы: {string.Join(", ", assembly.GetManifestResourceNames())}");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var readerStream = new StreamReader(stream);
            return readerStream.ReadToEnd();
        });
    }
}
