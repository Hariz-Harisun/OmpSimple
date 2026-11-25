using System.Globalization;
using System.Text.RegularExpressions;

namespace OmpSimple;

public static class Element14Query
{
    public static bool IsKeywordSpec(string value) =>
        value.Contains(';') && value.StartsWith("SMT;", StringComparison.OrdinalIgnoreCase);

    public static string Build(string value)
    {
        var fields = value.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length < 6)
            return value;

        var size = fields.ElementAtOrDefault(1) ?? "";
        var capacitance = NormalizeCapacitance(fields.ElementAtOrDefault(2) ?? "");
        var voltage = fields.ElementAtOrDefault(5) ?? "";
        var dielectric = fields.ElementAtOrDefault(6) ?? "";
        var sizeToken = Regex.Match(size, @"(?i)(\d{4})").Groups[1].Value;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(capacitance)) parts.Add($"any:{capacitance}");
        if (!string.IsNullOrWhiteSpace(sizeToken)) parts.Add(sizeToken);
        if (!string.IsNullOrWhiteSpace(voltage)) parts.Add(voltage);
        if (!string.IsNullOrWhiteSpace(dielectric)) parts.Add(dielectric);
        return string.Join(' ', parts);
    }

    public static Element14Product? SelectExact(IReadOnlyList<Element14Product> products, string specification)
    {
        var fields = specification.Split(';', StringSplitOptions.TrimEntries);
        var capacitance = NormalizeCapacitance(fields.ElementAtOrDefault(2) ?? "");
        var size = Regex.Match(fields.ElementAtOrDefault(1) ?? "", @"(?i)(\d{4})").Groups[1].Value;
        var voltage = NormalizeToken(fields.ElementAtOrDefault(5) ?? "");
        var dielectric = NormalizeToken(fields.ElementAtOrDefault(6) ?? "");
        var tolerance = NormalizeToken(fields.ElementAtOrDefault(3) ?? "") + NormalizeToken(fields.ElementAtOrDefault(4) ?? "");

        return products
            .Select(product => new
            {
                Product = product,
                Name = NormalizeToken(product.DisplayName ?? ""),
                Score = Score(product.DisplayName ?? "", capacitance, size, voltage, dielectric, tolerance)
            })
            .Where(item => item.Score >= 4)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Product)
            .FirstOrDefault();
    }

    private static int Score(string displayName, string capacitance, string size, string voltage, string dielectric, string tolerance)
    {
        var name = NormalizeToken(displayName);
        var score = 0;
        if (CapacitanceMatches(displayName, capacitance)) score++;
        if (name.Contains(size, StringComparison.OrdinalIgnoreCase) ||
            (name.Contains("1608", StringComparison.OrdinalIgnoreCase) && size == "0603")) score++;
        if (name.Contains(NormalizeToken(voltage), StringComparison.OrdinalIgnoreCase)) score++;
        if (name.Contains(dielectric, StringComparison.OrdinalIgnoreCase)) score++;
        if (!string.IsNullOrEmpty(tolerance) && name.Contains("10", StringComparison.OrdinalIgnoreCase)) score++;
        return score;
    }

    private static bool CapacitanceMatches(string displayName, string requested)
    {
        var requestedMicrofarads = ParseMicrofarads(requested);
        if (requestedMicrofarads == null)
            return NormalizeToken(displayName).Contains(NormalizeToken(requested), StringComparison.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(displayName, @"(?i)(\d+(?:[.,]\d+)?)\s*(pF|nF|uF|µF|mF)"))
        {
            var value = ParseMicrofarads(match.Value);
            if (value != null && Math.Abs(value.Value - requestedMicrofarads.Value) < 0.000001m)
                return true;
        }

        var picoFarads = requestedMicrofarads.Value * 1_000_000m;
        if (picoFarads >= 10 && picoFarads == decimal.Truncate(picoFarads))
        {
            var digits = picoFarads.ToString("0", CultureInfo.InvariantCulture);
            if (digits.Length >= 3)
            {
                var eiaCode = digits[..2] + (digits.Length - 2).ToString(CultureInfo.InvariantCulture);
                if (NormalizeToken(displayName).Contains(eiaCode, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static decimal? ParseMicrofarads(string value)
    {
        var match = Regex.Match(value.Trim(), @"(?i)^(\d+(?:[.,]\d+)?)(pF|nF|uF|µF|mF)$");
        if (!match.Success) return null;
        var number = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "pf" => number / 1_000_000m,
            "nf" => number / 1_000m,
            "uf" or "µf" => number,
            "mf" => number * 1_000m,
            _ => null
        };
    }

    private static string NormalizeCapacitance(string value)
    {
        var compact = Regex.Match(value.Trim(), @"^(\d+)([pnumkµu])(\d+)$", RegexOptions.IgnoreCase);
        if (compact.Success)
            value = $"{compact.Groups[1].Value}.{compact.Groups[3].Value}{compact.Groups[2].Value}";

        var match = Regex.Match(value.Trim(), @"^(\d+(?:[.,]\d+)?)([pnumkµu]?)$", RegexOptions.IgnoreCase);
        if (!match.Success) return value.Trim();
        var number = decimal.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var suffix = match.Groups[2].Value.ToLowerInvariant();
        var microfarads = suffix switch
        {
            "p" => number / 1_000_000m,
            "n" => number / 1_000m,
            "u" or "µ" => number,
            "m" => number * 1_000m,
            "k" => number * 1_000m,
            _ => number
        };
        var formatted = microfarads.ToString("0.####", CultureInfo.InvariantCulture);
        return $"{formatted}uF";
    }

    private static string NormalizeToken(string value) =>
        Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]+", "");
}
