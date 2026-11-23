using System.Globalization;
using System.Text.RegularExpressions;

namespace Mirage.Core;

/// <summary>Offline unit conversion: "10 km to mi", "72 f in c", "1.5 gb to mb".</summary>
public static partial class Units
{
    record Unit(string Dim, double Factor, double Offset = 0);

    static readonly Dictionary<string, Unit> Map = Build();

    static Dictionary<string, Unit> Build()
    {
        var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string dim, double f, params string[] names) { foreach (var n in names) d[n] = new Unit(dim, f); }
        // length (metres)
        Add("len", 1e-3, "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Add("len", 1e-2, "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Add("len", 1, "m", "meter", "meters", "metre", "metres");
        Add("len", 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add("len", 0.0254, "in", "inch", "inches");
        Add("len", 0.3048, "ft", "foot", "feet");
        Add("len", 0.9144, "yd", "yard", "yards");
        Add("len", 1609.344, "mi", "mile", "miles");
        Add("len", 1852, "nmi", "nauticalmile");
        // mass (kg)
        Add("mass", 1e-6, "mg");
        Add("mass", 1e-3, "g", "gram", "grams");
        Add("mass", 1, "kg", "kilo", "kilos", "kilogram", "kilograms");
        Add("mass", 1000, "t", "tonne", "tonnes");
        Add("mass", 0.028349523125, "oz", "ounce", "ounces");
        Add("mass", 0.45359237, "lb", "lbs", "pound", "pounds");
        Add("mass", 6.35029318, "st", "stone");
        // volume (litres)
        Add("vol", 1e-3, "ml", "milliliter", "milliliters");
        Add("vol", 1, "l", "liter", "liters", "litre", "litres");
        Add("vol", 3.785411784, "gal", "gallon", "gallons");
        Add("vol", 0.946352946, "qt", "quart", "quarts");
        Add("vol", 0.2365882365, "cup", "cups");
        Add("vol", 0.0295735295625, "floz");
        // data (bytes)
        Add("data", 1, "b", "byte", "bytes");
        Add("data", 1e3, "kb"); Add("data", 1e6, "mb"); Add("data", 1e9, "gb"); Add("data", 1e12, "tb");
        Add("data", 1024, "kib"); Add("data", 1048576, "mib"); Add("data", 1073741824, "gib"); Add("data", 1099511627776, "tib");
        // time (seconds)
        Add("time", 1e-3, "ms");
        Add("time", 1, "s", "sec", "secs", "second", "seconds");
        Add("time", 60, "min", "mins", "minute", "minutes");
        Add("time", 3600, "h", "hr", "hrs", "hour", "hours");
        Add("time", 86400, "d", "day", "days");
        Add("time", 604800, "wk", "week", "weeks");
        // speed (m/s)
        Add("speed", 1, "mps"); Add("speed", 1 / 3.6, "kmh", "kph"); Add("speed", 0.44704, "mph"); Add("speed", 0.514444, "kn", "knots");
        // area (m2)
        Add("area", 1, "m2", "sqm"); Add("area", 1e6, "km2"); Add("area", 0.09290304, "ft2", "sqft");
        Add("area", 4046.8564224, "acre", "acres"); Add("area", 10000, "ha", "hectare", "hectares");
        // temperature: value_in_kelvin = v * factor + offset
        d["c"] = d["celsius"] = new Unit("temp", 1, 273.15);
        d["k"] = d["kelvin"] = new Unit("temp", 1, 0);
        d["f"] = d["fahrenheit"] = new Unit("temp", 5.0 / 9.0, 273.15 - 32 * 5.0 / 9.0);
        return d;
    }

    [GeneratedRegex(@"^\s*(-?[\d.,_]+)\s*([a-zA-Z°0-9]+)\s+(?:to|in|as|=|->)\s+([a-zA-Z°0-9]+)\s*$")]
    private static partial Regex Pattern();

    public static bool TryConvert(string input, out string result, out double value)
    {
        result = ""; value = 0;
        var m = Pattern().Match(input);
        if (!m.Success) return false;
        var numText = m.Groups[1].Value.Replace(",", "").Replace("_", "");
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        string from = m.Groups[2].Value.Replace("°", ""), to = m.Groups[3].Value.Replace("°", "");
        if (!Map.TryGetValue(from, out var a) || !Map.TryGetValue(to, out var b) || a.Dim != b.Dim) return false;
        var baseValue = v * a.Factor + a.Offset;
        value = (baseValue - b.Offset) / b.Factor;
        result = $"{Calculator.Format(value, true)} {to}";
        return true;
    }
}
