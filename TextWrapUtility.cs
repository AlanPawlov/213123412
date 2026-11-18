using System;
using System.Globalization;
using System.Text;

public static class TextWrapUtility
{
    public static string WrapByWords(
        string text,
        int maxCharsPerLine = 20,
        int maxLineBreaks = 4,
        bool truncateOverflow = true,
        string separators = ".,;:!?_-–—/\\|")
    {
        if (maxCharsPerLine <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCharsPerLine));

        if (maxLineBreaks < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLineBreaks));

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        separators = separators ?? string.Empty;

        // Старые переносы, табуляцию и повторяющиеся пробелы
        // заменяем одиночными пробелами между фрагментами.
        text = string.Join(" ", text.Split(
            Array.Empty<char>(),
            StringSplitOptions.RemoveEmptyEntries));

        int[] starts = StringInfo.ParseCombiningCharacters(text);

        var result = new StringBuilder(text.Length);

        int position = 0;
        int lineBreaks = 0;

        while (position < starts.Length)
        {
            int remaining = starts.Length - position;

            // Остаток помещается, либо его нужно целиком сохранить
            // в последней разрешённой строке.
            if (remaining <= maxCharsPerLine ||
                (!truncateOverflow && lineBreaks == maxLineBreaks))
            {
                int from = starts[position];
                result.Append(text, from, text.Length - from);
                break;
            }

            // По умолчанию — принудительное разбиение по лимиту.
            int lineEnd = position + maxCharsPerLine;

            // Если сразу после окна стоит пробел, граница уже подходит.
            // Иначе ищем последнюю подходящую границу внутри окна.
            if (text[starts[lineEnd]] != ' ')
            {
                for (int i = lineEnd - 1; i > position; i--)
                {
                    char character = text[starts[i]];

                    if (character == ' ')
                    {
                        // Пробел не включаем в конец строки.
                        lineEnd = i;
                        break;
                    }

                    if (separators.IndexOf(character) >= 0)
                    {
                        // Знак-разделитель сохраняем.
                        lineEnd = i + 1;
                        break;
                    }
                }
            }

            int startIndex = starts[position];
            int endIndex = starts[lineEnd];

            result.Append(text, startIndex, endIndex - startIndex);

            position = lineEnd;

            // Следующую строку начинаем без пробелов.
            while (position < starts.Length &&
                   text[starts[position]] == ' ')
            {
                position++;
            }

            if (position == starts.Length || lineBreaks == maxLineBreaks)
                break;

            result.Append('\n');
            lineBreaks++;
        }

        return result.ToString();
    }
}
