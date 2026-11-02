namespace ServiceLib.Common;

/// <summary>
/// FireVPN: страна сервера по флагу-эмодзи или городу в названии (флаги-эмодзи Windows не рисует — рисуем сами)
/// </summary>
public static class FireCountries
{
    private static readonly Dictionary<string, string> CountryNames = new()
    {
        ["fi"] = "Финляндия", ["nl"] = "Нидерланды", ["de"] = "Германия", ["se"] = "Швеция", ["no"] = "Норвегия",
        ["dk"] = "Дания", ["ru"] = "Россия", ["pl"] = "Польша", ["lv"] = "Латвия", ["lt"] = "Литва", ["ee"] = "Эстония",
        ["fr"] = "Франция", ["it"] = "Италия", ["at"] = "Австрия", ["ch"] = "Швейцария", ["gb"] = "Великобритания",
        ["us"] = "США", ["jp"] = "Япония", ["tr"] = "Турция", ["kz"] = "Казахстан", ["ua"] = "Украина", ["be"] = "Бельгия",
        ["ro"] = "Румыния", ["bg"] = "Болгария", ["hu"] = "Венгрия", ["ie"] = "Ирландия", ["es"] = "Испания", ["cz"] = "Чехия",
    };

    private static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Хельсинки"] = "fi", ["Амстердам"] = "nl", ["Франкфурт"] = "de", ["Берлин"] = "de", ["Стокгольм"] = "se",
        ["Москва"] = "ru", ["Варшава"] = "pl", ["Рига"] = "lv", ["Таллин"] = "ee", ["Вильнюс"] = "lt", ["Париж"] = "fr",
        ["Лондон"] = "gb", ["Нью-Йорк"] = "us", ["Токио"] = "jp", ["Стамбул"] = "tr", ["Вена"] = "at", ["Цюрих"] = "ch",
        ["Осло"] = "no", ["Копенгаген"] = "dk", ["Милан"] = "it", ["Мадрид"] = "es", ["Прага"] = "cz", ["Алматы"] = "kz",
    };

    /// <summary>
    /// Код страны: из флага-эмодзи (🇫🇮 → fi) или по городу в названии
    /// </summary>
    public static string? CountryCode(string? remarks)
    {
        var letters = new StringBuilder();
        foreach (var r in (remarks ?? "").EnumerateRunes())
        {
            if (r.Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                letters.Append((char)('a' + r.Value - 0x1F1E6));
            }
        }
        if (letters.Length >= 2)
        {
            return letters.ToString(0, 2);
        }
        return Cities.FirstOrDefault(c => (remarks ?? "").Contains(c.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static string CountryName(string? code)
    {
        return code != null && CountryNames.TryGetValue(code, out var n) ? n : "";
    }

    /// <summary>
    /// Название без флага-эмодзи
    /// </summary>
    public static string StripFlags(string? name)
    {
        var sb = new StringBuilder();
        foreach (var r in (name ?? "").EnumerateRunes())
        {
            if (r.Value is < 0x1F1E6 or > 0x1F1FF)
            {
                sb.Append(r.ToString());
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// «🇫🇮 Хельсинки» → «Хельсинки · Финляндия»
    /// </summary>
    public static string ServerLabel(string? remarks)
    {
        var name = StripFlags(remarks);
        var country = CountryName(CountryCode(remarks));
        return country.IsNotEmpty() && country != name ? $"{name} · {country}" : name;
    }

    // «[VLESS] 🇫🇮 Хельсинки(168***127:2096)» — так v2rayN сообщает о запуске сервера; человеку нужно только название
    private static readonly System.Text.RegularExpressions.Regex ServerSummary =
        new(@"^\[\w+\]\s+(?:\[\w+\])?(?<name>.+?)\([^()]*\)$");

    /// <summary>
    /// Текст уведомления для простого экрана FireVPN
    /// </summary>
    public static string Humanize(string text)
    {
        text = text.Trim();
        var m = ServerSummary.Match(text);
        return m.Success ? $"Сервер: {ServerLabel(m.Groups["name"].Value)}" : text;
    }
}
