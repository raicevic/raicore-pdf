using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Formatting;

/// <summary>
/// A locale's symbols and patterns for picture clauses (XFA 3.3, "Localization and Canonicalization"): month, day,
/// meridiem and era names, the date, time and number patterns, and the number and currency symbols.
/// </summary>
internal sealed class XfaLocale
{
    public required string Name { get; init; }

    public string[] Months { get; init; } = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public string[] MonthAbbreviations { get; init; } = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Day names from Sunday.</summary>
    public string[] Days { get; init; } = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public string[] DayAbbreviations { get; init; } = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public string[] Meridiems { get; init; } = ["AM", "PM"];

    public string[] Eras { get; init; } = ["BC", "AD"];

    public Dictionary<string, string> DatePatterns { get; init; } = new(StringComparer.Ordinal)
    {
        ["full"] = "EEEE, MMMM D, YYYY",
        ["long"] = "MMMM D, YYYY",
        ["med"] = "MMM D, YYYY",
        ["short"] = "M/D/YY",
    };

    public Dictionary<string, string> TimePatterns { get; init; } = new(StringComparer.Ordinal)
    {
        ["full"] = "h:MM:SS A Z",
        ["long"] = "h:MM:SS A Z",
        ["med"] = "h:MM:SS A",
        ["short"] = "h:MM A",
    };

    public Dictionary<string, string> NumberPatterns { get; init; } = new(StringComparer.Ordinal)
    {
        ["numeric"] = "z,zz9.zzz",
        ["currency"] = "$z,zz9.99|($z,zz9.99)",
        ["percent"] = "z,zz9%",
    };

    public string Decimal { get; init; } = ".";

    public string Grouping { get; init; } = ",";

    public string Percent { get; init; } = "%";

    public string Minus { get; init; } = "-";

    public string CurrencySymbol { get; init; } = "$";

    /// <summary>The form's default locale when it names none: US English.</summary>
    public static XfaLocale EnUs { get; } = new() { Name = "en_US" };

    /// <summary>Reads a locale from a <c>localeSet</c> packet's <c>locale</c> element.</summary>
    public static XfaLocale Read(XElement locale)
    {
        var calendar = locale.Child("calendarSymbols");
        string[] Names(string list, string item, bool abbreviated, string[] fallback)
        {
            var names = calendar.Children(list).FirstOrDefault(l => (l.Attr("abbr") == "1") == abbreviated)?.Children(item).Select(e => e.Value).ToArray();
            return names is not null && names.Length == fallback.Length ? names : fallback;
        }

        Dictionary<string, string> Patterns(string list, string item, Dictionary<string, string> fallback)
        {
            var patterns = new Dictionary<string, string>(fallback, StringComparer.Ordinal);
            foreach (var pattern in locale.Child(list).Children(item))
            {
                if (pattern.Attr("name") is { } name)
                {
                    patterns[name] = pattern.Value;
                }
            }

            return patterns;
        }

        string Symbol(string list, string item, string name, string fallback) =>
            locale.Child(list).Children(item).FirstOrDefault(e => e.Attr("name") == name)?.Value is { Length: > 0 } value ? value : fallback;

        var basis = EnUs;
        return new XfaLocale
        {
            Name = locale.Attr("name") ?? "en_US",
            Months = Names("monthNames", "month", false, basis.Months),
            MonthAbbreviations = Names("monthNames", "month", true, basis.MonthAbbreviations),
            Days = Names("dayNames", "day", false, basis.Days),
            DayAbbreviations = Names("dayNames", "day", true, basis.DayAbbreviations),
            Meridiems = Names("meridiemNames", "meridiem", false, basis.Meridiems),
            Eras = Names("eraNames", "era", false, basis.Eras),
            DatePatterns = Patterns("datePatterns", "datePattern", basis.DatePatterns),
            TimePatterns = Patterns("timePatterns", "timePattern", basis.TimePatterns),
            NumberPatterns = Patterns("numberPatterns", "numberPattern", basis.NumberPatterns),
            Decimal = Symbol("numberSymbols", "numberSymbol", "decimal", basis.Decimal),
            Grouping = Symbol("numberSymbols", "numberSymbol", "grouping", basis.Grouping),
            Percent = Symbol("numberSymbols", "numberSymbol", "percent", basis.Percent),
            Minus = Symbol("numberSymbols", "numberSymbol", "minus", basis.Minus),
            CurrencySymbol = Symbol("currencySymbols", "currencySymbol", "symbol", basis.CurrencySymbol),
        };
    }

    /// <summary>A locale the form's locale set does not have, from .NET's culture data where there is one.</summary>
    public static XfaLocale FromCulture(string name)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(name.Replace('_', '-'));
            if (culture.Equals(CultureInfo.InvariantCulture) || culture.DateTimeFormat.MonthNames.Length < 12)
            {
                return EnUs;
            }

            var format = culture.DateTimeFormat;
            var numbers = culture.NumberFormat;
            return new XfaLocale
            {
                Name = name,
                Months = format.MonthNames[..12],
                MonthAbbreviations = format.AbbreviatedMonthNames[..12],
                Days = format.DayNames,
                DayAbbreviations = format.AbbreviatedDayNames,
                Meridiems = [format.AMDesignator.Length > 0 ? format.AMDesignator : "AM", format.PMDesignator.Length > 0 ? format.PMDesignator : "PM"],
                Decimal = numbers.NumberDecimalSeparator,
                Grouping = numbers.NumberGroupSeparator,
                Percent = numbers.PercentSymbol,
                Minus = numbers.NegativeSign,
                CurrencySymbol = numbers.CurrencySymbol,
            };
        }
        catch (CultureNotFoundException)
        {
            return EnUs;
        }
    }
}

/// <summary>The locales of a form: those of its locale set, then .NET's, with US English as the default.</summary>
internal sealed class LocaleSet
{
    private readonly Dictionary<string, XfaLocale> _locales = new(StringComparer.OrdinalIgnoreCase);

    public LocaleSet(XElement? localeSet)
    {
        foreach (var locale in localeSet.Children("locale").Take(256))
        {
            if (locale.Attr("name") is { Length: > 0 } name)
            {
                _locales.TryAdd(name, XfaLocale.Read(locale));
            }
        }
    }

    /// <summary>A locale by its XFA name (<c>en_US</c>); <c>ambient</c>, empty or unknown names give a default.</summary>
    public XfaLocale Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "ambient")
        {
            name = "en_US";
        }

        if (!_locales.TryGetValue(name, out var locale))
        {
            locale = name.Equals("en_US", StringComparison.OrdinalIgnoreCase) ? XfaLocale.EnUs : XfaLocale.FromCulture(name);
            if (_locales.Count < 512)
            {
                _locales[name] = locale;
            }
        }

        return locale;
    }
}
