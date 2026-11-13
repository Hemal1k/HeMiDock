using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HeMiDock;

/// <summary>
/// Conversions for the search: units ("10 km in mi", "5 kg", "20 °C") and currencies ("100 EUR in CZK", "50 $"). Without a
/// target unit the usual counterpart is used (km ↔ mi, CZK ↔ EUR …). Exchange rates are the daily reference rates of the
/// European Central Bank, downloaded at most twice a day and kept in the settings folder.
/// </summary>
static class Converter
{
    sealed record Unit(string Kind, string Symbol, double Factor, string Default = null);

    // Factor = how much of the base unit of its kind (m, kg, l, m², m/s, s, B, J, W); temperatures are handled on their own.
    static readonly Unit[] Units =
    {
        new("len", "mm", 0.001), new("len", "cm", 0.01, "in"), new("len", "dm", 0.1), new("len", "m", 1, "ft"), new("len", "km", 1000, "mi"),
        new("len", "in", 0.0254, "cm"), new("len", "ft", 0.3048, "m"), new("len", "yd", 0.9144, "m"), new("len", "mi", 1609.344, "km"),
        new("len", "nmi", 1852, "km"),
        new("mass", "mg", 1e-6), new("mass", "g", 0.001, "oz"), new("mass", "dkg", 0.01), new("mass", "kg", 1, "lb"), new("mass", "t", 1000),
        new("mass", "oz", 0.028349523125, "g"), new("mass", "lb", 0.45359237, "kg"), new("mass", "st", 6.35029318, "kg"),
        new("vol", "ml", 0.001), new("vol", "cl", 0.01), new("vol", "dl", 0.1), new("vol", "l", 1, "gal"), new("vol", "hl", 100), new("vol", "m³", 1000),
        new("vol", "gal", 3.785411784, "l"), new("vol", "qt", 0.946352946, "l"), new("vol", "pt", 0.473176473, "l"),
        new("vol", "fl oz", 0.0295735295625, "ml"), new("vol", "cup", 0.2365882365, "ml"),
        new("area", "cm²", 1e-4), new("area", "m²", 1, "ft²"), new("area", "a", 100), new("area", "ha", 10000, "ac"), new("area", "km²", 1e6, "mi²"),
        new("area", "in²", 0.00064516), new("area", "ft²", 0.09290304, "m²"), new("area", "ac", 4046.8564224, "ha"), new("area", "mi²", 2589988.110336, "km²"),
        new("speed", "m/s", 1, "km/h"), new("speed", "km/h", 1 / 3.6, "mph"), new("speed", "mph", 0.44704, "km/h"), new("speed", "kn", 0.514444, "km/h"),
        new("time", "ms", 0.001), new("time", "s", 1), new("time", "min", 60), new("time", "h", 3600, "min"), new("time", "d", 86400, "h"),
        new("time", "wk", 604800, "d"),
        new("data", "B", 1), new("data", "kB", 1e3), new("data", "MB", 1e6, "MiB"), new("data", "GB", 1e9, "GiB"), new("data", "TB", 1e12, "TiB"),
        new("data", "KiB", 1024), new("data", "MiB", 1048576, "MB"), new("data", "GiB", 1073741824, "GB"), new("data", "TiB", 1099511627776, "TB"),
        new("energy", "J", 1), new("energy", "kJ", 1000, "kcal"), new("energy", "kcal", 4184, "kJ"), new("energy", "Wh", 3600), new("energy", "kWh", 3.6e6, "MJ"),
        new("energy", "MJ", 1e6, "kWh"),
        new("power", "W", 1), new("power", "kW", 1000, "hp"), new("power", "hp", 745.69987158227, "kW"),
        new("temp", "°C", 0, "°F"), new("temp", "°F", 0, "°C"), new("temp", "K", 0, "°C")
    };

    /// <summary>Other ways to write a unit (lower case) → its symbol.</summary>
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["metr"] = "m", ["metru"] = "m", ["metrů"] = "m", ["metry"] = "m", ["kilometr"] = "km", ["kilometrů"] = "km", ["kilometry"] = "km",
        ["inch"] = "in", ["inches"] = "in", ["palec"] = "in", ["palců"] = "in", ["palce"] = "in", ["\""] = "in",
        ["foot"] = "ft", ["feet"] = "ft", ["stopa"] = "ft", ["stop"] = "ft", ["stopy"] = "ft", ["yard"] = "yd", ["yards"] = "yd",
        ["mile"] = "mi", ["miles"] = "mi", ["míle"] = "mi", ["mil"] = "mi", ["mílí"] = "mi",
        ["dag"] = "dkg", ["gram"] = "g", ["gramů"] = "g", ["kilo"] = "kg", ["kila"] = "kg", ["tuna"] = "t", ["tun"] = "t", ["tuny"] = "t",
        ["ounce"] = "oz", ["ounces"] = "oz", ["unce"] = "oz", ["lbs"] = "lb", ["pound"] = "lb", ["pounds"] = "lb", ["libra"] = "lb", ["liber"] = "lb",
        ["litr"] = "l", ["litrů"] = "l", ["litry"] = "l", ["liter"] = "l", ["liters"] = "l", ["litre"] = "l", ["m3"] = "m³",
        ["gallon"] = "gal", ["gallons"] = "gal", ["galon"] = "gal", ["galonů"] = "gal", ["floz"] = "fl oz", ["cups"] = "cup",
        ["cm2"] = "cm²", ["m2"] = "m²", ["km2"] = "km²", ["in2"] = "in²", ["ft2"] = "ft²", ["mi2"] = "mi²", ["acre"] = "ac", ["acres"] = "ac",
        ["kmh"] = "km/h", ["kph"] = "km/h", ["kt"] = "kn", ["knot"] = "kn", ["knots"] = "kn", ["uzel"] = "kn", ["uzlů"] = "kn",
        ["sec"] = "s", ["sekund"] = "s", ["minut"] = "min", ["minuty"] = "min", ["hod"] = "h", ["hodin"] = "h", ["hodiny"] = "h",
        ["den"] = "d", ["dní"] = "d", ["dny"] = "d", ["day"] = "d", ["days"] = "d", ["týden"] = "wk", ["týdnů"] = "wk", ["week"] = "wk", ["weeks"] = "wk",
        ["kb"] = "kB", ["mb"] = "MB", ["gb"] = "GB", ["tb"] = "TB", ["kib"] = "KiB", ["mib"] = "MiB", ["gib"] = "GiB", ["tib"] = "TiB",
        ["kj"] = "kJ", ["kwh"] = "kWh", ["wh"] = "Wh", ["mj"] = "MJ", ["kw"] = "kW", ["ps"] = "hp", ["koní"] = "hp", ["k"] = "K",
        ["c"] = "°C", ["°c"] = "°C", ["f"] = "°F", ["°f"] = "°F", ["celsius"] = "°C", ["fahrenheit"] = "°F", ["kelvin"] = "K"
    };

    /// <summary>Other ways to write a currency (lower case) → its code.</summary>
    static readonly Dictionary<string, string> Money = new()
    {
        ["€"] = "EUR", ["euro"] = "EUR", ["eura"] = "EUR", ["eur"] = "EUR", ["$"] = "USD", ["dolar"] = "USD", ["dolarů"] = "USD", ["dolary"] = "USD",
        ["dollar"] = "USD", ["dollars"] = "USD", ["£"] = "GBP", ["¥"] = "JPY", ["kč"] = "CZK", ["kc"] = "CZK", ["korun"] = "CZK", ["koruna"] = "CZK",
        ["koruny"] = "CZK", ["zł"] = "PLN", ["zlotý"] = "PLN", ["zlotých"] = "PLN", ["forint"] = "HUF", ["forintů"] = "HUF", ["frank"] = "CHF", ["franků"] = "CHF"
    };

    static readonly Regex Query = new(
        @"^\s*(?<num>[-+]?\d[\d\s ]*(?:[.,]\d+)?)\s*(?<from>[^\d\s][^\d]*?)\s*(?:(?:\s(?:to|in|into|na|v|ve|do)\s|->|=|→)\s*(?<to>[^\d]+?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"10 km in mi" → ("6,2137 mi" as text, the number alone for the clipboard, "10 km = 6,2137 mi").</summary>
    public static bool TryConvert(string text, out string result, out string number, out string expression)
    {
        result = number = expression = null;
        var m = Query.Match(text);
        if (!m.Success) return false;
        if (!double.TryParse(Regex.Replace(m.Groups["num"].Value, @"[\s ]", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return false;
        string from = m.Groups["from"].Value.Trim(), to = m.Groups["to"].Success ? m.Groups["to"].Value.Trim() : null;

        // a currency ("100 EUR", "50 $ v Kč")
        if (Currency(from) is { } fromCode)
        {
            var rates = Rates();
            string toCode = to == null ? (fromCode == "CZK" ? "EUR" : "CZK") : Currency(to);
            if (toCode == null || toCode == fromCode || rates == null || !rates.TryGetValue(fromCode, out double rf) || !rates.TryGetValue(toCode, out double rt))
                return false;
            double v = value / rf * rt;
            number = v.ToString("N2", CultureInfo.CurrentCulture);
            result = $"{number} {toCode}";
            expression = $"{Format(value)} {fromCode} = {result}";
            return true;
        }

        // a unit
        var u = Find(from);
        if (u == null) return false;
        var t = to == null ? (u.Default != null ? Find(u.Default) : null) : Find(to);
        if (t == null || t.Kind != u.Kind || t == u) return false;
        double r = u.Kind == "temp" ? Temperature(value, u.Symbol, t.Symbol) : value * u.Factor / t.Factor;
        number = Format(r);
        result = $"{number} {t.Symbol}";
        expression = $"{Format(value)} {u.Symbol} = {result}";
        return true;
    }

    static Unit Find(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('.');
        // exact symbol first (MB vs mb vs Mb are all megabytes, but "m" and "M" … – the case only matters for exact symbols)
        var exact = Units.FirstOrDefault(u => u.Symbol == s);
        if (exact != null) return exact;
        string lower = s.ToLowerInvariant().Replace(" ", "");
        if (Aliases.TryGetValue(lower, out var sym)) return Units.First(u => u.Symbol == sym);
        return Units.FirstOrDefault(u => u.Symbol.Replace(" ", "").Equals(lower, StringComparison.OrdinalIgnoreCase));
    }

    static string Currency(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimEnd('.');
        if (Money.TryGetValue(s.ToLowerInvariant(), out var code)) return code;
        string upper = s.ToUpperInvariant();
        return upper.Length == 3 && Rates() is { } rates && rates.ContainsKey(upper) ? upper : null;
    }

    static double Temperature(double v, string from, string to)
    {
        double c = from switch { "°F" => (v - 32) * 5 / 9, "K" => v - 273.15, _ => v };
        return to switch { "°F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
    }

    /// <summary>Up to four decimals, small numbers with four significant digits.</summary>
    static string Format(double v)
    {
        double a = Math.Abs(v);
        if (a != 0 && a < 0.001) return v.ToString("G4", CultureInfo.CurrentCulture);
        return v.ToString(a >= 1000 ? "#,0.##" : "#,0.####", CultureInfo.CurrentCulture);
    }

    // ───────────────────────── exchange rates ─────────────────────────

    static readonly string RatesFile = AppPaths.Of("exchange-rates.xml");
    const string RatesUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    static Dictionary<string, double> _rates;
    static DateTime _ratesRead;
    static bool _downloading;

    /// <summary>New rates arrived – the search shows the result it could not show before.</summary>
    public static event Action RatesLoaded;

    /// <summary>Units of a currency for 1 EUR (EUR itself included); null while there are none yet (they are downloaded then).</summary>
    static Dictionary<string, double> Rates()
    {
        bool old = !File.Exists(RatesFile) || DateTime.Now - File.GetLastWriteTime(RatesFile) > TimeSpan.FromHours(12);
        if (old) Download();
        if (_rates == null || (File.Exists(RatesFile) && File.GetLastWriteTime(RatesFile) > _ratesRead)) _rates = Read();
        return _rates;
    }

    static Dictionary<string, double> Read()
    {
        try
        {
            if (!File.Exists(RatesFile)) return _rates;
            _ratesRead = File.GetLastWriteTime(RatesFile);
            var doc = XDocument.Load(RatesFile);
            var map = new Dictionary<string, double> { ["EUR"] = 1 };
            foreach (var e in doc.Descendants().Where(e => e.Attribute("currency") != null && e.Attribute("rate") != null))
                if (double.TryParse(e.Attribute("rate").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0)
                    map[e.Attribute("currency").Value] = r;
            return map.Count > 1 ? map : _rates;
        }
        catch (Exception ex) { Log.Write("Converter.Rates", ex); return _rates; }
    }

    static DateTime _lastTry;

    static async void Download()
    {
        if (_downloading || DateTime.Now - _lastTry < TimeSpan.FromMinutes(10)) return;   // offline: not on every key
        _downloading = true;
        _lastTry = DateTime.Now;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string xml = await http.GetStringAsync(RatesUrl);
            XDocument.Parse(xml);                                      // only a valid file replaces the old one
            Directory.CreateDirectory(AppPaths.Folder);
            await File.WriteAllTextAsync(RatesFile, xml);
            _rates = Read();
            RatesLoaded?.Invoke();
        }
        catch (Exception ex) { Log.Write("Converter.Download", ex); }
        finally { _downloading = false; }
    }
}
