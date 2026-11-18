using System;
using System.Globalization;
using System.Text;

public static class TextWrapUtility
{
    public static string WrapByCharacters(
        string text,
        int maxCharsPerLine = 20,
        int maxLineBreaks = 4,
        bool truncateOverflow = true)
    {
        if (maxCharsPerLine <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCharsPerLine));

        if (maxLineBreaks < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLineBreaks));

        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Убираем прежнее разбиение на строки.
        // Пробел сохраняет разделение между соседними словами.
        text = text
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        var result = new StringBuilder(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text);

        int charsInLine = 0;
        int lineBreaks = 0;

        while (elements.MoveNext())
        {
            if (charsInLine == maxCharsPerLine)
            {
                if (lineBreaks < maxLineBreaks)
                {
                    result.Append('\n');
                    charsInLine = 0;
                    lineBreaks++;
                }
                else if (truncateOverflow)
                {
                    // Достигнуты оба лимита — остаток отсекаем.
                    break;
                }
            }

            result.Append(elements.GetTextElement());
            charsInLine++;
        }

        return result.ToString();
    }
}
