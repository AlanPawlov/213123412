private static string FormatName(
    string fullName,
    int prefixSize = 6,
    int nameSize = 3)
{
    if (string.IsNullOrEmpty(fullName))
        return string.Empty;

    string[] parts = fullName.Split('.');

    // Строку без префикса ФО считаем обычным именем.
    if (parts[0] != "ФО")
        return $"<size={nameSize}>{fullName}</size>";

    // Пропускаем ФО и все следующие числовые сегменты.
    int nameStart = 1;

    while (nameStart < parts.Length && IsNumericCode(parts[nameStart]))
        nameStart++;

    string prefix = string.Join(".", parts, 0, nameStart);

    // Если после кодов имени нет — вся строка размера prefixSize.
    if (nameStart == parts.Length)
        return $"<size={prefixSize}>{prefix}</size>";

    // Всё оставшееся — имя, включая внутренние точки.
    string name = string.Join(
        ".", parts, nameStart, parts.Length - nameStart);

    return $"<size={prefixSize}>{prefix}.</size>" +
           $"<size={nameSize}>{name}</size>";
}

private static bool IsNumericCode(string part)
{
    if (part.Length == 0)
        return false;

    foreach (char c in part)
    {
        if (c < '0' || c > '9')
            return false;
    }

    return true;
}
