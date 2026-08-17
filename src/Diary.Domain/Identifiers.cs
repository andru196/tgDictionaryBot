namespace Diary.Domain;

/// <summary>Идентификатор захваченного сообщения. Guid v7 — сортируется по времени создания.</summary>
public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Идентификатор извлечённой записи дневника.</summary>
public readonly record struct EntryId(Guid Value)
{
    public static EntryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Ключ субъекта наблюдения. Используется в путях к данным и в аргументе <c>--subject</c>,
/// поэтому ограничен безопасным для файловой системы алфавитом.
/// </summary>
public readonly record struct SubjectKey
{
    private readonly string? _value;

    public SubjectKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                throw new ArgumentException(
                    $"Ключ субъекта «{value}» содержит недопустимый символ '{c}'. " +
                    "Разрешены латинские буквы, цифры, дефис и подчёркивание.",
                    nameof(value));
            }
        }

        _value = value;
    }

    public string Value => _value ?? throw new InvalidOperationException("SubjectKey не инициализирован.");

    public override string ToString() => Value;
}

/// <summary>Ключ категории записи, например <c>meal</c> или <c>idea</c>. Задаётся модулем.</summary>
public readonly record struct CategoryKey
{
    private readonly string? _value;

    public CategoryKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? throw new InvalidOperationException("CategoryKey не инициализирован.");

    public static implicit operator CategoryKey(string value) => new(value);

    public override string ToString() => Value;
}
