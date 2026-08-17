using System.Globalization;
using System.Text;

namespace Diary.Application.Reporting;

/// <summary>
/// Минимальный построитель HTML с обязательным экранированием. Сознательно вместо Razor:
/// секции отчёта строят модули, а тянуть FrameworkReference на ASP.NET Core в каждый
/// модуль ради статической разметки — плата больше пользы.
/// </summary>
public sealed class Html
{
    private readonly StringBuilder _sb = new();

    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Экранирует текст. Всё, что пришло из сообщений или от модели, идёт только сюда.</summary>
    public static string Enc(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => sb.Append("&amp;"),
                '<' => sb.Append("&lt;"),
                '>' => sb.Append("&gt;"),
                '"' => sb.Append("&quot;"),
                '\'' => sb.Append("&#39;"),
                _ => sb.Append(c),
            };
        }

        return sb.ToString();
    }

    public static string Num(double value, int decimals = 1) =>
        value.ToString($"F{decimals}", Culture);

    public static string Percent(double fraction) =>
        (fraction * 100).ToString("F0", Culture) + " %";

    /// <summary>Длительность словами: «2 ч 10 м».</summary>
    public static string Duration(TimeSpan span)
    {
        var total = span.Duration();
        if (total.TotalMinutes < 1)
        {
            return "меньше минуты";
        }

        return total.TotalHours >= 1
            ? $"{(int)total.TotalHours} ч {total.Minutes:00} м"
            : $"{total.Minutes} м";
    }

    /// <summary>Добавляет уже готовую разметку. Вызывающий отвечает за её безопасность.</summary>
    public Html Raw(string markup)
    {
        _sb.Append(markup);
        return this;
    }

    /// <summary>Добавляет текст с экранированием.</summary>
    public Html Text(string? text)
    {
        _sb.Append(Enc(text));
        return this;
    }

    public Html Open(string tag, string? cssClass = null, string? attributes = null)
    {
        _sb.Append('<').Append(tag);
        if (cssClass is not null)
        {
            _sb.Append(" class=\"").Append(Enc(cssClass)).Append('"');
        }

        if (attributes is not null)
        {
            _sb.Append(' ').Append(attributes);
        }

        _sb.Append('>');
        return this;
    }

    public Html Close(string tag)
    {
        _sb.Append("</").Append(tag).Append('>');
        return this;
    }

    public Html Element(string tag, string? text, string? cssClass = null, string? attributes = null)
    {
        Open(tag, cssClass, attributes);
        Text(text);
        return Close(tag);
    }

    public override string ToString() => _sb.ToString();
}
