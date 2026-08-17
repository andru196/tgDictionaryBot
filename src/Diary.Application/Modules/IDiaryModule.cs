using Diary.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Application.Modules;

/// <summary>
/// Описание категории для сегментатора. Промпт сегментации собирается из этих описаний,
/// поэтому новый модуль появляется в списке категорий сам, без правки промпта.
/// </summary>
/// <param name="Key">Ключ категории, например <c>meal</c>.</param>
/// <param name="Title">Человекочитаемое название.</param>
/// <param name="WhenToUse">Когда категорию применять — попадает в промпт дословно.</param>
/// <param name="Examples">Три-пять реальных фраз. Дают больше, чем любое описание.</param>
/// <param name="Hashtags">Хэштеги, дающие категорию в обход модели.</param>
public sealed record CategoryDescriptor(
    CategoryKey Key,
    string Title,
    string WhenToUse,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> Hashtags);

/// <summary>Кусок сообщения, отнесённый сегментатором к одной категории.</summary>
public sealed record EntryFragment(string Text, CategoryKey Category, Confidence Confidence);

/// <summary>Всё, что нужно экстрактору помимо самого текста.</summary>
public sealed record ExtractionContext(
    MessageId SourceMessageId,
    DateTimeOffset SentAtUtc,
    RelativeTimeResolver TimeResolver,
    string ExtractorVersion,
    long? ReplyToTelegramMessageId);

/// <summary>
/// Превращает фрагмент в типизированную запись. По одной реализации на категорию:
/// узкий промпт для еды не содержит ни слова про идеи, и модели проще.
/// </summary>
public interface IEntryExtractor
{
    CategoryKey Category { get; }

    Task<DiaryEntry> ExtractAsync(EntryFragment fragment, ExtractionContext context, CancellationToken ct);
}

/// <summary>Разбивает сообщение на фрагменты и назначает каждому категорию.</summary>
public interface IEntrySegmenter
{
    Task<IReadOnlyList<EntryFragment>> SegmentAsync(
        string text,
        IReadOnlyList<CategoryDescriptor> categories,
        CancellationToken ct);
}

/// <summary>
/// Вертикальный срез функциональности: свои категории, свои экстракторы, своя секция отчёта.
/// Модуль не знает ни про инфраструктуру, ни про других модулей, ни про субъектов.
/// </summary>
public interface IDiaryModule
{
    string Key { get; }

    string Title { get; }

    IReadOnlyList<CategoryDescriptor> Categories { get; }

    /// <summary>Регистрирует экстракторы, аналитику и секцию отчёта.</summary>
    void ConfigureServices(IServiceCollection services);
}

/// <summary>Собранные модули. Ядро работает только с реестром и про конкретные модули не знает.</summary>
public interface IModuleRegistry
{
    IReadOnlyList<IDiaryModule> All { get; }

    /// <summary>Модули, включённые у субъекта, в порядке регистрации.</summary>
    IReadOnlyList<IDiaryModule> For(IReadOnlyList<string> enabledKeys);

    /// <summary>Категории включённых модулей — из них строится промпт сегментации.</summary>
    IReadOnlyList<CategoryDescriptor> CategoriesFor(IReadOnlyList<string> enabledKeys);
}

public sealed class ModuleRegistry(IEnumerable<IDiaryModule> modules) : IModuleRegistry
{
    public IReadOnlyList<IDiaryModule> All { get; } = [.. modules];

    public IReadOnlyList<IDiaryModule> For(IReadOnlyList<string> enabledKeys)
    {
        ArgumentNullException.ThrowIfNull(enabledKeys);
        return [.. All.Where(m => enabledKeys.Contains(m.Key, StringComparer.OrdinalIgnoreCase))];
    }

    public IReadOnlyList<CategoryDescriptor> CategoriesFor(IReadOnlyList<string> enabledKeys) =>
        [.. For(enabledKeys).SelectMany(m => m.Categories)];
}
