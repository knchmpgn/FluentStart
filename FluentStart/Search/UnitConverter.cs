using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentStart;

public static class UnitConverter
{
    private static readonly Dictionary<string, (double Factor, string Category)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        // Length
        ["mm"] = (0.001, "Length"), ["millimeter"] = (0.001, "Length"), ["millimeters"] = (0.001, "Length"),
        ["cm"] = (0.01, "Length"), ["centimeter"] = (0.01, "Length"), ["centimeters"] = (0.01, "Length"),
        ["m"] = (1.0, "Length"), ["meter"] = (1.0, "Length"), ["meters"] = (1.0, "Length"),
        ["km"] = (1000.0, "Length"), ["kilometer"] = (1000.0, "Length"), ["kilometers"] = (1000.0, "Length"),
        ["in"] = (0.0254, "Length"), ["inch"] = (0.0254, "Length"), ["inches"] = (0.0254, "Length"),
        ["ft"] = (0.3048, "Length"), ["foot"] = (0.3048, "Length"), ["feet"] = (0.3048, "Length"),
        ["yd"] = (0.9144, "Length"), ["yard"] = (0.9144, "Length"), ["yards"] = (0.9144, "Length"),
        ["mi"] = (1609.344, "Length"), ["mile"] = (1609.344, "Length"), ["miles"] = (1609.344, "Length"),

        // Mass
        ["mg"] = (0.000001, "Mass"), ["milligram"] = (0.000001, "Mass"), ["milligrams"] = (0.000001, "Mass"),
        ["g"] = (0.001, "Mass"), ["gram"] = (0.001, "Mass"), ["grams"] = (0.001, "Mass"),
        ["kg"] = (1.0, "Mass"), ["kilogram"] = (1.0, "Mass"), ["kilograms"] = (1.0, "Mass"),
        ["oz"] = (0.0283495, "Mass"), ["ounce"] = (0.0283495, "Mass"), ["ounces"] = (0.0283495, "Mass"),
        ["lb"] = (0.453592, "Mass"), ["pound"] = (0.453592, "Mass"), ["pounds"] = (0.453592, "Mass"),

        // Temperature (special handling needed)
        ["c"] = (1.0, "Temperature"), ["celsius"] = (1.0, "Temperature"),
        ["f"] = (1.0, "Temperature"), ["fahrenheit"] = (1.0, "Temperature"),
        ["k"] = (1.0, "Temperature"), ["kelvin"] = (1.0, "Temperature"),

        // Speed
        ["m/s"] = (1.0, "Speed"), ["m/s"] = (1.0, "Speed"),
        ["km/h"] = (0.277778, "Speed"), ["kmh"] = (0.277778, "Speed"),
        ["mph"] = (0.44704, "Speed"),

        // Area
        ["mm2"] = (0.000001, "Area"), ["cm2"] = (0.0001, "Area"), ["m2"] = (1.0, "Area"), ["km2"] = (1000000.0, "Area"),
        ["in2"] = (0.00064516, "Area"), ["ft2"] = (0.092903, "Area"), ["yd2"] = (0.836127, "Area"), ["acre"] = (4046.86, "Area"), ["ha"] = (10000.0, "Area"),

        // Volume
        ["ml"] = (0.000001, "Volume"), ["milliliter"] = (0.000001, "Volume"),
        ["l"] = (0.001, "Volume"), ["liter"] = (0.001, "Volume"), ["liters"] = (0.001, "Volume"),
        ["m3"] = (1.0, "Volume"),
        ["in3"] = (0.0000163871, "Volume"), ["ft3"] = (0.0283168, "Volume"),
        ["gal"] = (0.00378541, "Volume"), ["gallon"] = (0.00378541, "Volume"),

        // Data
        ["b"] = (1.0, "Data"), ["bit"] = (1.0, "Data"),
        ["kb"] = (1000.0, "Data"), ["kib"] = (1024.0, "Data"),
        ["mb"] = (1000000.0, "Data"), ["mib"] = (1048576.0, "Data"),
        ["gb"] = (1000000000.0, "Data"), ["gib"] = (1073741824.0, "Data"),
        ["tb"] = (1000000000000.0, "Data"), ["tib"] = (1099511627776.0, "Data"),

        // Time
        ["ms"] = (0.001, "Time"), ["millisecond"] = (0.001, "Time"),
        ["s"] = (1.0, "Time"), ["sec"] = (1.0, "Time"), ["second"] = (1.0, "Time"), ["seconds"] = (1.0, "Time"),
        ["min"] = (60.0, "Time"), ["minute"] = (60.0, "Time"), ["minutes"] = (60.0, "Time"),
        ["h"] = (3600.0, "Time"), ["hr"] = (3600.0, "Time"), ["hour"] = (3600.0, "Time"), ["hours"] = (3600.0, "Time"),
        ["d"] = (86400.0, "Time"), ["day"] = (86400.0, "Time"), ["days"] = (86400.0, "Time"),

        // Pressure
        ["pa"] = (1.0, "Pressure"), ["pascal"] = (1.0, "Pressure"),
        ["kpa"] = (1000.0, "Pressure"), ["mpa"] = (1000000.0, "Pressure"),
        ["bar"] = (100000.0, "Pressure"), ["atm"] = (101325.0, "Pressure"),
        ["psi"] = (6894.76, "Pressure"),

        // Energy
        ["j"] = (1.0, "Energy"), ["joule"] = (1.0, "Energy"),
        ["kj"] = (1000.0, "Energy"), ["cal"] = (4.184, "Energy"), ["kcal"] = (4184.0, "Energy"),
        ["wh"] = (3600.0, "Energy"), ["kwh"] = (3600000.0, "Energy"),

        // Power
        ["w"] = (1.0, "Power"), ["watt"] = (1.0, "Power"),
        ["kw"] = (1000.0, "Power"), ["hp"] = (745.7, "Power"),

        // Angle
        ["deg"] = (Math.PI / 180.0, "Angle"), ["degree"] = (Math.PI / 180.0, "Angle"),
        ["rad"] = (1.0, "Angle"), ["radian"] = (1.0, "Angle"),

        // Frequency
        ["hz"] = (1.0, "Frequency"), ["hertz"] = (1.0, "Frequency"),
        ["khz"] = (1000.0, "Frequency"), ["mhz"] = (1000000.0, "Frequency"), ["ghz"] = (1000000000.0, "Frequency"),
    };

    public static bool TryConvert(string input, out List<string> results)
    {
        results = new List<string>();
        input = input.Trim();

        // Handle "X unit to unit" or "X unit in unit" or "X unit = unit"
        var match = Regex.Match(input, @"^([\d.,]+)\s*([a-zA-Z°]+)\s*(?:->|→|to|in|=)\s*([a-zA-Z°]+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (double.TryParse(match.Groups[1].Value.Replace(',', '.'), out double value))
            {
                string from = match.Groups[2].Value.Trim().ToLowerInvariant();
                string to = match.Groups[3].Value.Trim().ToLowerInvariant();
                if (TryConvertSingle(value, from, to, out double result))
                {
                    results.Add($"{value} {from} = {result:G6} {to}");
                    return true;
                }
            }
            return false;
        }

        // Handle "X unit" - show all conversions
        match = Regex.Match(input, @"^([\d.,]+)\s*([a-zA-Z°]+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (double.TryParse(match.Groups[1].Value.Replace(',', '.'), out double value))
            {
                string unit = match.Groups[2].Value.Trim().ToLowerInvariant();
                if (Units.TryGetValue(unit, out var info))
                {
                    var baseValue = value * info.Factor;
                    foreach (var kvp in Units.Where(u => u.Value.Category == info.Category && u.Key != unit))
                    {
                        double converted = baseValue / kvp.Value.Factor;
                        results.Add($"{value} {unit} = {converted:G6} {kvp.Key}");
                    }
                    // Sort by magnitude
                    results = results.OrderBy(r => 
                    {
                        var m = Regex.Match(r, @"= ([\d.]+)");
                        return m.Success && double.TryParse(m.Groups[1].Value, out double v) ? v : 0;
                    }).ToList();
                    return true;
                }
            }
            return false;
        }

        // Handle temperature conversions like "25 c to f"
        match = Regex.Match(input, @"^([\d.,]+)\s*([cfk])\s*(?:->|→|to|in|=)\s*([cfk])$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (double.TryParse(match.Groups[1].Value.Replace(',', '.'), out double value))
            {
                string from = match.Groups[2].Value.ToLowerInvariant();
                string to = match.Groups[3].Value.ToLowerInvariant();
                double result = ConvertTemperature(value, from, to);
                results.Add($"{value}°{from.ToUpper()} = {result:F2}°{to.ToUpper()}");
                return true;
            }
        }

        return false;
    }

    private static bool TryConvertSingle(double value, string from, string to, out double result)
    {
        result = 0;
        if (!Units.TryGetValue(from, out var fromInfo) || !Units.TryGetValue(to, out var toInfo)) return false;
        if (fromInfo.Category != toInfo.Category) return false;

        if (fromInfo.Category == "Temperature")
        {
            result = ConvertTemperature(value, from, to);
            return true;
        }

        double baseValue = value * fromInfo.Factor;
        result = baseValue / toInfo.Factor;
        return true;
    }

    private static double ConvertTemperature(double value, string from, string to)
    {
        // Convert to Celsius first
        double celsius = from switch
        {
            "c" => value,
            "f" => (value - 32) * 5.0 / 9.0,
            "k" => value - 273.15,
            _ => value
        };

        // Convert from Celsius to target
        return to switch
        {
            "c" => celsius,
            "f" => celsius * 9.0 / 5.0 + 32,
            "k" => celsius + 273.15,
            _ => celsius
        };
    }
}