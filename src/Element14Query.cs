using System.Globalization;
using System.Text.RegularExpressions;
using OmpSimple.Models;

namespace OmpSimple;

public enum SpecKind
{
    Capacitor,
    Resistor
}

public static class Element14Query
{
    public static bool IsKeywordSpec(string value) =>
        value.Contains(';') && value.StartsWith("SMT;", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decides what kind of part an "SMT;size;value;..." specification describes.
    /// A value with an R, k, K or M suffix (22R, 8R2, 169k, 4k7, 1M) is a resistor.
    /// Everything else keeps the original capacitor behaviour.
    /// </summary>
    public static SpecKind Classify(string specification)
    {
        var fields = specification.Split(';', StringSplitOptions.TrimEntries);
        return ParseOhms(fields.ElementAtOrDefault(2) ?? "") != null
            ? SpecKind.Resistor
            : SpecKind.Capacitor;
    }

    public static string Build(string value)
    {
        var fields = value.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length < 6)
            return value;

        if (Classify(value) == SpecKind.Resistor)
            return BuildResistor(fields, includeTolerance: true, spacedValue: false);

        var size = fields.ElementAtOrDefault(1) ?? "";
        var capacitance = NormalizeCapacitance(fields.ElementAtOrDefault(2) ?? "");
        var voltage = fields.ElementAtOrDefault(5) ?? "";
        var dielectric = fields.ElementAtOrDefault(6) ?? "";
        var sizeToken = SizeToken(size);

        var parts = new List<string>();
        if (!IsBlank(capacitance)) parts.Add($"any:{capacitance}");
        if (!IsBlank(sizeToken)) parts.Add(sizeToken);
        if (!IsBlank(voltage)) parts.Add(voltage);
        if (!IsBlank(dielectric)) parts.Add(dielectric);
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Query wordings to try in order. The product matcher verifies every result, so a later
    /// wording is only used when an earlier one returned nothing that matched the specification.
    /// </summary>
    public static IReadOnlyList<string> BuildVariants(string value)
    {
        var fields = value.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length >= 6 && Classify(value) == SpecKind.Resistor)
        {
            return
            [
                BuildResistor(fields, includeTolerance: true, spacedValue: false),
                BuildResistor(fields, includeTolerance: false, spacedValue: true)
            ];
        }

        return [Build(value)];
    }

    public static Element14Product? SelectExact(IReadOnlyList<Element14Product> products, string specification)
    {
        return SelectExactCore(products, specification, product => product.DisplayName ?? "");
    }

    public static DigiKeyProduct? SelectExact(IReadOnlyList<DigiKeyProduct> products, string specification)
    {
        return SelectExactCore(products, specification, product =>
            $"{product.ManufacturerProductNumber} {product.ProductDescription}");
    }

    private static T? SelectExactCore<T>(IReadOnlyList<T> products, string specification, Func<T, string> displayName)
    {
        var fields = specification.Split(';', StringSplitOptions.TrimEntries);
        return Classify(specification) == SpecKind.Resistor
            ? SelectResistor(products, fields, displayName)
            : SelectCapacitor(products, fields, displayName);
    }

    // ---------------------------------------------------------------- resistors

    private static string BuildResistor(string[] fields, bool includeTolerance, bool spacedValue)
    {
        var ohms = ParseOhms(fields[2])!.Value;
        var value = spacedValue ? FormatOhms(ohms, spaced: true) : FormatOhms(ohms, spaced: false);
        var parts = new List<string> { spacedValue ? $"any:{value} resistor" : $"any:resistor {value}" };

        var size = SizeToken(fields.ElementAtOrDefault(1) ?? "");
        if (!IsBlank(size)) parts.Add(size);

        var tolerance = ParseTolerance(fields.ElementAtOrDefault(3) ?? "");
        if (includeTolerance && tolerance != null)
            parts.Add(tolerance.Value.ToString("0.####", CultureInfo.InvariantCulture) + "%");

        return string.Join(' ', parts);
    }

    private static T? SelectResistor<T>(IReadOnlyList<T> products, string[] fields, Func<T, string> displayName)
    {
        var wantedOhms = ParseOhms(fields.ElementAtOrDefault(2) ?? "")!.Value;
        var size = SizeToken(fields.ElementAtOrDefault(1) ?? "");
        var tolerance = ParseTolerance(fields.ElementAtOrDefault(3) ?? "");
        var power = ParsePowerMilliwatts(fields.ElementAtOrDefault(4) ?? "");

        return products
            .Select(product => (Product: product, Name: displayName(product)))
            .Where(item => !ResistorArray.IsMatch(item.Name))
            .Where(item => !NotAChipResistor.IsMatch(item.Name))
            .Where(item => OhmsMatch(item.Name, wantedOhms))
            .Where(item => SizeMatches(item.Name, size))
            .Where(item => ToleranceMatches(item.Name, tolerance))
            .Where(item => PowerAcceptable(item.Name, power))
            .OrderByDescending(item => PowerExact(item.Name, power) ? 1 : 0)
            .Select(item => item.Product)
            .FirstOrDefault();
    }

    private static readonly Regex ResistorArray = new(@"(?i)\b(arrays?|networks?)\b", RegexOptions.CultureInvariant);

    // A keyword search for "10kohm 0603 1%" also returns parts that carry the same value, package and
    // tolerance but are not fixed resistors (NTC thermistors, trimmers, fuses) or are not a single part at all
    // (resistor kits such as "RES KIT, 0 OHM TO 10MOHM"). They must never be chosen.
    private static readonly Regex NotAChipResistor = new(
        @"(?i)\b(thermistors?|NTC|PTC|varistors?|potentiometers?|trimmers?|rheostats?|fuses?|inductors?|capacitors?|ferrite|kits?|assortments?)\b",
        RegexOptions.CultureInvariant);

    // 10 kohm, 10K OHM, 10Kohms, 1 Mohm, 22 ohm, 0 Ω. The prefix is case sensitive: M = mega, m = milli.
    private static readonly Regex OhmValue = new(
        @"(?<![\w.,])(\d+(?:[.,]\d+)?)\s*([kKMmG]?)\s*(?i:ohms?|\u03A9|\u2126)",
        RegexOptions.CultureInvariant);

    private static bool OhmsMatch(string name, decimal wanted)
    {
        foreach (Match match in OhmValue.Matches(name))
        {
            var number = ParseDecimal(match.Groups[1].Value);
            if (number == null) continue;
            var multiplier = match.Groups[2].Value switch
            {
                "k" or "K" => 1_000m,
                "M" => 1_000_000m,
                "G" => 1_000_000_000m,
                "m" => 0.001m,
                _ => 1m
            };
            if (SameValue(number.Value * multiplier, wanted))
                return true;
        }

        return false;
    }

    /// <summary>Parses 22R, 0R, 8R2, 169k, 4k7, 1M, 1M5 and 1.5k into ohms.</summary>
    public static decimal? ParseOhms(string value)
    {
        value = value.Trim();
        var compact = Regex.Match(value, @"^(\d+)([RrKkM])(\d+)$");
        if (compact.Success)
        {
            var number = ParseDecimal($"{compact.Groups[1].Value}.{compact.Groups[3].Value}");
            return number == null ? null : number * OhmMultiplier(compact.Groups[2].Value);
        }

        var simple = Regex.Match(value, @"^(\d+(?:[.,]\d+)?)([RrKkM])$");
        if (simple.Success)
        {
            var number = ParseDecimal(simple.Groups[1].Value);
            return number == null ? null : number * OhmMultiplier(simple.Groups[2].Value);
        }

        return null;
    }

    private static decimal OhmMultiplier(string suffix) => suffix switch
    {
        "k" or "K" => 1_000m,
        "M" => 1_000_000m,
        _ => 1m
    };

    private static string FormatOhms(decimal ohms, bool spaced)
    {
        var gap = spaced ? " " : "";
        if (ohms >= 1_000_000m) return Trim(ohms / 1_000_000m) + gap + "Mohm";
        if (ohms >= 1_000m) return Trim(ohms / 1_000m) + gap + "kohm";
        return Trim(ohms) + gap + "ohm";
    }

    private static string Trim(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static decimal? ParsePowerMilliwatts(string value)
    {
        value = value.Trim();
        var fraction = Regex.Match(value, @"(?i)^(\d+)\s*/\s*(\d+)\s*W$");
        if (fraction.Success)
        {
            var numerator = ParseDecimal(fraction.Groups[1].Value);
            var denominator = ParseDecimal(fraction.Groups[2].Value);
            return numerator == null || denominator is null or 0 ? null : numerator * 1000m / denominator;
        }

        var plain = Regex.Match(value, @"^(\d+(?:[.,]\d+)?)\s*(mW|W)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!plain.Success) return null;
        var amount = ParseDecimal(plain.Groups[1].Value);
        if (amount == null) return null;
        return plain.Groups[2].Value == "W" || plain.Groups[2].Value == "w" ? amount * 1000m : amount;
    }

    private static List<decimal> PowersInName(string name)
    {
        var powers = new List<decimal>();
        foreach (Match match in Regex.Matches(name, @"(?<![\w.,/])(\d+)\s*/\s*(\d+)\s*W\b"))
        {
            var numerator = ParseDecimal(match.Groups[1].Value);
            var denominator = ParseDecimal(match.Groups[2].Value);
            if (numerator != null && denominator is not null and not 0)
                powers.Add(numerator.Value * 1000m / denominator.Value);
        }

        foreach (Match match in Regex.Matches(name, @"(?<![\w.,/])(\d+(?:[.,]\d+)?)\s*(mW|W)\b", RegexOptions.CultureInvariant))
        {
            var amount = ParseDecimal(match.Groups[1].Value);
            if (amount == null) continue;
            powers.Add(match.Groups[2].Value == "W" ? amount.Value * 1000m : amount.Value);
        }

        return powers;
    }

    // A part rated for at least the requested power is acceptable. Unknown power in the name is not a reason to reject.
    private static bool PowerAcceptable(string name, decimal? wanted)
    {
        if (wanted == null) return true;
        var powers = PowersInName(name);
        return powers.Count == 0 || powers.Any(power => power >= wanted.Value * 0.95m);
    }

    private static bool PowerExact(string name, decimal? wanted) =>
        wanted != null && PowersInName(name).Any(power => Math.Abs(power - wanted.Value) <= wanted.Value * 0.02m);

    // ---------------------------------------------------------------- capacitors

    private static T? SelectCapacitor<T>(IReadOnlyList<T> products, string[] fields, Func<T, string> displayName)
    {
        var capacitance = NormalizeCapacitance(fields.ElementAtOrDefault(2) ?? "");
        var size = SizeToken(fields.ElementAtOrDefault(1) ?? "");
        var voltage = ParseVoltage(fields.ElementAtOrDefault(5) ?? "");
        var dielectric = fields.ElementAtOrDefault(6) ?? "";
        var tolerance = ParseTolerance(fields.ElementAtOrDefault(3) ?? "");

        return products
            .Select(product => (Product: product, Name: displayName(product)))
            // Value and package must match. Voltage, dielectric and tolerance may miss once.
            .Where(item => CapacitanceMatches(item.Name, capacitance) && SizeMatches(item.Name, size))
            .Select(item => (item.Product, Score: Score(item.Name, voltage, dielectric, tolerance)))
            .Where(item => item.Score >= 2)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Product)
            .FirstOrDefault();
    }

    private static readonly Regex DielectricCode = new(
        @"(?i)(?<![A-Z0-9])(C0G|NP0|U2J|X[5-8][A-Z]|Y5[A-Z]|Z5U)(?![A-Z0-9])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Lists where a chosen capacitor misses the requested specification. Capacitor matching accepts one miss
    /// among voltage, dielectric and tolerance, so the buyer needs to be told which one it was.
    /// Only the requested items are checked and an empty list means the part met every stated requirement.
    /// Resistors are matched strictly and never produce deviations.
    /// </summary>
    public static IReadOnlyList<string> SpecDeviations(string name, string specification, bool includeTolerance = true)
    {
        if (!IsKeywordSpec(specification) || Classify(specification) != SpecKind.Capacitor)
            return [];

        var fields = specification.Split(';', StringSplitOptions.TrimEntries);
        var deviations = new List<string>();

        var voltage = ParseVoltage(fields.ElementAtOrDefault(5) ?? "");
        if (voltage != null)
        {
            var found = VoltagesInName(name).Distinct().ToList();
            if (found.Count == 0)
                deviations.Add($"voltage not stated (wanted {Trim(voltage.Value)}V)");
            else if (!found.Contains(voltage.Value))
                deviations.Add($"voltage {string.Join("/", found.Select(Trim))}V (wanted {Trim(voltage.Value)}V)");
        }

        var dielectric = fields.ElementAtOrDefault(6) ?? "";
        if (!IsBlank(dielectric) && !DielectricMatches(name, dielectric))
        {
            var found = DielectricCode.Matches(name).Select(match => match.Value.ToUpperInvariant()).Distinct().ToList();
            deviations.Add(found.Count == 0
                ? $"dielectric not stated (wanted {dielectric.Trim()})"
                : $"dielectric {string.Join("/", found)} (wanted {dielectric.Trim()})");
        }

        var tolerance = ParseTolerance(fields.ElementAtOrDefault(3) ?? "");
        if (includeTolerance && tolerance != null && !ToleranceMatches(name, tolerance))
        {
            var found = new List<decimal>();
            foreach (Match match in Regex.Matches(name, @"(?<![\w.,])(\d+(?:[.,]\d+)?)\s*%"))
            {
                var value = ParseDecimal(match.Groups[1].Value);
                if (value != null && !found.Contains(value.Value)) found.Add(value.Value);
            }

            deviations.Add($"tolerance {string.Join("/", found.Select(Trim))}% (wanted {Trim(tolerance.Value)}%)");
        }

        return deviations;
    }

    // Same rule as Score: the code may sit anywhere in the name, and C0G is the same dielectric as NP0.
    private static bool DielectricMatches(string name, string dielectric)
    {
        var normalizedName = NormalizeToken(name);
        var wanted = NormalizeToken(dielectric);
        return normalizedName.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
               (wanted == "C0G" && normalizedName.Contains("NP0", StringComparison.OrdinalIgnoreCase)) ||
               (wanted == "NP0" && normalizedName.Contains("C0G", StringComparison.OrdinalIgnoreCase));
    }

    private static int Score(string name, decimal? voltage, string dielectric, decimal? tolerance)
    {
        var score = 0;

        if (voltage == null || VoltagesInName(name).Any(value => value == voltage.Value)) score++;

        if (IsBlank(dielectric) || DielectricMatches(name, dielectric)) score++;

        if (ToleranceMatches(name, tolerance)) score++;
        return score;
    }

    private static decimal? ParseVoltage(string value)
    {
        value = value.Trim();
        if (IsBlank(value)) return null;
        var compact = Regex.Match(value, @"(?i)^(\d+)V(\d+)$");
        if (compact.Success) return ParseDecimal($"{compact.Groups[1].Value}.{compact.Groups[2].Value}");
        var simple = Regex.Match(value, @"(?i)^(\d+(?:[.,]\d+)?)\s*V");
        return simple.Success ? ParseDecimal(simple.Groups[1].Value) : null;
    }

    private static IEnumerable<decimal> VoltagesInName(string name)
    {
        foreach (Match match in Regex.Matches(name, @"(?i)(?<![\w.,])(\d+(?:[.,]\d+)?)\s*V(?:DC|AC)?\b"))
        {
            var value = ParseDecimal(match.Groups[1].Value);
            if (value != null) yield return value.Value;
        }
    }

    private static bool CapacitanceMatches(string displayName, string requested)
    {
        displayName = displayName.Replace('µ', 'u');
        requested = requested.Replace('µ', 'u');
        var requestedMicrofarads = ParseMicrofarads(requested);
        if (requestedMicrofarads == null)
            return NormalizeToken(displayName).Contains(NormalizeToken(requested), StringComparison.OrdinalIgnoreCase);

        var foundExplicitValue = false;
        foreach (Match match in Regex.Matches(displayName, @"(?i)(?<![\w.,])(\d+(?:[.,]\d+)?)\s*(pF|nF|uF|mF)\b"))
        {
            var value = ParseMicrofarads(match.Value);
            if (value == null) continue;
            foundExplicitValue = true;
            if (SameValue(value.Value, requestedMicrofarads.Value))
                return true;
        }

        // The EIA code in a part number (104 = 100 nF) is only a fallback when the name states no value at all.
        if (foundExplicitValue)
            return false;

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
        var match = Regex.Match(value.Trim(), @"(?i)^(\d+(?:[.,]\d+)?)\s*(pF|nF|uF|µF|mF)$");
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

        // Below 1 nF, microfarads lose precision (22 pF would round to 0), so write picofarads.
        if (microfarads > 0 && microfarads < 0.001m)
            return (microfarads * 1_000_000m).ToString("0.####", CultureInfo.InvariantCulture) + "pF";

        return microfarads.ToString("0.######", CultureInfo.InvariantCulture) + "uF";
    }

    // ---------------------------------------------------------------- shared helpers

    private static readonly Dictionary<string, string> MetricAlias = new()
    {
        ["0201"] = "0603",
        ["0402"] = "1005",
        ["0603"] = "1608",
        ["0805"] = "2012",
        ["1206"] = "3216",
        ["1210"] = "3225",
        ["1812"] = "4532",
        ["2010"] = "5025",
        ["2512"] = "6332"
    };

    private static string SizeToken(string size) =>
        Regex.Match(size ?? "", @"(?i)(\d{4})").Groups[1].Value;

    // The imperial size must appear as its own token. A metric code only counts when it says "Metric",
    // because "0603 [1608 Metric]" is an imperial 0603, not an imperial 0201.
    private static bool SizeMatches(string name, string size)
    {
        if (IsBlank(size)) return true;
        if (Regex.IsMatch(name, $@"(?<!\d){size}(?!\d)")) return true;
        return MetricAlias.TryGetValue(size, out var metric) &&
               Regex.IsMatch(name, $@"(?<!\d){metric}\s*(?:mm\s*)?Metric", RegexOptions.IgnoreCase);
    }

    private static decimal? ParseTolerance(string value)
    {
        var match = Regex.Match(value, @"(\d+(?:[.,]\d+)?)\s*%");
        return match.Success ? ParseDecimal(match.Groups[1].Value) : null;
    }

    // Satisfied when no tolerance was requested, when the name states none, or when one of them equals the request.
    private static bool ToleranceMatches(string name, decimal? wanted)
    {
        if (wanted == null) return true;
        var found = new List<decimal>();
        foreach (Match match in Regex.Matches(name, @"(?<![\w.,])(\d+(?:[.,]\d+)?)\s*%"))
        {
            var value = ParseDecimal(match.Groups[1].Value);
            if (value != null) found.Add(value.Value);
        }

        return found.Count == 0 || found.Any(value => value == wanted.Value);
    }

    private static bool SameValue(decimal a, decimal b)
    {
        if (a == b) return true;
        var larger = Math.Max(Math.Abs(a), Math.Abs(b));
        return larger != 0 && Math.Abs(a - b) <= larger * 0.001m;
    }

    private static decimal? ParseDecimal(string value) =>
        decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static bool IsBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() is "-" or "--";

    private static string NormalizeToken(string value) =>
        Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]+", "");
}
