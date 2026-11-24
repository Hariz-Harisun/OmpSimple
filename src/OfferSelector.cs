using System.Text.RegularExpressions;
using OmpSimple.Models;

namespace OmpSimple;

public static class OfferSelector
{
    public static (Offer? offer, string remarks) SelectBest(IReadOnlyList<DigiKeyProduct> products, int qty, string originalMpn, string originalMfg = "", string supplier = "DigiKey")
    {
        var candidates = products
            .Select((product, index) =>
            {
                var result = SelectBest(product, qty, originalMpn, originalMfg, supplier);
                return (product, index, result.offer, result.remarks);
            })
            .Where(candidate => candidate.offer != null)
            .OrderByDescending(candidate => MfgMatch(originalMfg, candidate.product.Manufacturer?.Name ?? ""))
            .ThenBy(candidate => candidate.offer!.UnitPrice)
            .ThenBy(candidate => candidate.index)
            .ToList();

        if (candidates.Count > 0)
        {
            var selected = candidates[0];
            return (selected.offer, selected.remarks);
        }

        if (products.Count == 0)
            return (null, "Not found");

        return SelectBest(products[0], qty, originalMpn, originalMfg, supplier);
    }

    public static (Offer? offer, string remarks) SelectBest(DigiKeyProduct product, int qty, string originalMpn, string originalMfg = "", string supplier = "DigiKey")
    {
        var remarks = new List<string>();

        if (product.ProductVariations.Count == 0)
            return (null, "No variations");

        // MPN match check
        var returnedMpn = product.ManufacturerProductNumber ?? "";
        if (!MpnMatch(cleanedMpn: originalMpn, returnedMpn))
        {
            return (null, $"Not found (closest: {returnedMpn})");
        }

        // Manufacturer alignment is a confidence signal only; never reject on mismatch.
        _ = MfgMatch(originalMfg, product.Manufacturer?.Name ?? "");

        var validVariations = product.ProductVariations.Where(v => !v.MarketPlace).ToList();
        if (validVariations.Count == 0)
        {
            validVariations = product.ProductVariations.ToList();
            remarks.Add("Marketplace only");
        }

        // Filter variations that have sufficient stock for effective qty (max of requested qty and MOQ)
        var variationsWithStock = new List<DigiKeyVariation>();
        foreach (var v in validVariations)
        {
            var eq = Math.Max(qty, v.MinimumOrderQuantity);
            if (v.QuantityAvailableforPackageType >= eq)
                variationsWithStock.Add(v);
        }

        if (variationsWithStock.Count == 0)
        {
            variationsWithStock = validVariations;
            remarks.Add("Low stock");
        }

        Offer? best = null;
        decimal bestPrice = decimal.MaxValue;
        int bestMoq = int.MaxValue;

        foreach (var v in variationsWithStock)
        {
            var moq = v.MinimumOrderQuantity;
            var eq = Math.Max(qty, moq);
            var price = GetPriceForQty(v, eq);
            if (price <= 0) continue;

            if (price < bestPrice || (price == bestPrice && moq < bestMoq))
            {
                bestPrice = price;
                bestMoq = moq;
                best = BuildOffer(product, v, price, moq, eq, supplier);
            }
        }

        if (best == null)
            return (null, "No valid pricing");

        if (bestMoq > qty)
            remarks.Add($"MOQ {bestMoq} above Qty {qty}");

        // RoHS/REACH: Yes only if RohsStatus contains "compliant" and NOT "non"/"not", and ReachStatus contains "unaffected" or "compliant" and NOT "non"/"not"
        if (product.Classifications != null)
        {
            var rohs = product.Classifications.RohsStatus?.ToLowerInvariant() ?? "";
            var reach = product.Classifications.ReachStatus?.ToLowerInvariant() ?? "";
            bool rohsOk = rohs.Contains("compliant") && !rohs.Contains("non") && !rohs.Contains("not");
            bool reachOk = (reach.Contains("unaffected") || reach.Contains("compliant")) && !reach.Contains("non") && !reach.Contains("not");
            best.RoHS_REACH = (rohsOk && reachOk) ? "Yes" : "No";
        }
        else
        {
            best.RoHS_REACH = "No";
        }

        // DigiKey search does not state COO.
        best.Coo = product.MouserCountryOfOrigin ?? "Not stated.";
        if (string.IsNullOrWhiteSpace(product.MouserCountryOfOrigin))
            remarks.Add("COO missing");

        if (best.TariffActive) remarks.Add("Tariff active");
        if (best.Ncnr) remarks.Add("NCNR");
        if (best.Discontinued || best.EndOfLife) remarks.Add("Discontinued");
        if (best.CancellationWeeks == 2 && best.RescheduleWeeks == 8 && best.CxlResDbPb == "PB")
            remarks.Add("Terms: default");

        return (best, string.Join("; ", remarks));
    }

    private static bool MpnMatch(string cleanedMpn, string returnedMpn)
    {
        if (string.IsNullOrWhiteSpace(cleanedMpn) || string.IsNullOrWhiteSpace(returnedMpn))
            return false;
        var norm1 = Regex.Replace(cleanedMpn, @"[\s\-,\.]", "").ToUpperInvariant();
        var norm2 = Regex.Replace(returnedMpn, @"[\s\-,\.]", "").ToUpperInvariant();
        if (norm1 == norm2) return true;
        // Allow prefix/suffix differences (e.g., "BAT54CWT1G" vs "SBAT54CWT1G")
        return norm1.Contains(norm2) || norm2.Contains(norm1);
    }

    private static bool MfgMatch(string inputMfg, string returnedMfg)
    {
        if (string.IsNullOrWhiteSpace(inputMfg))
            return true; // No input manufacturer specified — accept any
        if (string.IsNullOrWhiteSpace(returnedMfg))
            return false; // DigiKey returned no manufacturer

        var normInput = NormalizeMfg(inputMfg);
        var normReturned = NormalizeMfg(returnedMfg);

        if (normInput == normReturned) return true;

        // Known aliases: input -> list of acceptable DigiKey names
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["TI"] = ["TEXAS INSTRUMENTS", "TEXAS INSTRUMENTS INC", "TI"],
            ["TEXAS INSTRUMENTS"] = ["TI", "TEXAS INSTRUMENTS INC"],
            ["HIROSE"] = ["HIROSE ELECTRIC", "HIROSE ELECTRIC CO LTD", "HIROSE ELECTRIC CO., LTD"],
            ["HIROSE ELECTRIC"] = ["HIROSE", "HIROSE ELECTRIC CO LTD"],
            ["EPSON"] = ["SEIKO EPSON", "EPSON", "SEIKO EPSON CORP"],
            ["SEIKO EPSON"] = ["EPSON"],
            ["TDK"] = ["TDK CORPORATION", "TDK CORP", "TDK"],
            ["TDK CORPORATION"] = ["TDK", "TDK CORP"],
            ["ONSEMI"] = ["ONSEMI", "ON SEMICONDUCTOR", "ON SEMICONDUCTOR CORP"],
            ["ON SEMICONDUCTOR"] = ["ONSEMI"],
            ["ST"] = ["STMICROELECTRONICS", "ST MICROELECTRONICS", "ST"],
            ["STMICROELECTRONICS"] = ["ST"],
            ["NXP"] = ["NXP SEMICONDUCTORS", "NXP SEMICONDUCTORS NV"],
            ["NXP SEMICONDUCTORS"] = ["NXP"],
            ["INFINEON"] = ["INFINEON TECHNOLOGIES", "INFINEON TECHNOLOGIES AG"],
            ["INFINEON TECHNOLOGIES"] = ["INFINEON"],
            ["ANALOG DEVICES"] = ["ADI", "ANALOG DEVICES INC"],
            ["ADI"] = ["ANALOG DEVICES", "ANALOG DEVICES INC"],
            ["MICROCHIP"] = ["MICROCHIP TECHNOLOGY", "MICROCHIP TECHNOLOGY INC"],
            ["MICROCHIP TECHNOLOGY"] = ["MICROCHIP"],
            ["MAXIM"] = ["MAXIM INTEGRATED", "MAXIM INTEGRATED PRODUCTS"],
            ["MAXIM INTEGRATED"] = ["MAXIM"],
            ["VISHAY"] = ["VISHAY INTERTECHNOLOGY", "VISHAY INTERTECHNOLOGY INC"],
            ["VISHAY INTERTECHNOLOGY"] = ["VISHAY"],
            ["ROHM"] = ["ROHM SEMICONDUCTOR", "ROHM CO LTD"],
            ["ROHM SEMICONDUCTOR"] = ["ROHM"],
            ["PANASONIC"] = ["PANASONIC CORPORATION", "PANASONIC INDUSTRIAL"],
            ["PANASONIC CORPORATION"] = ["PANASONIC"],
            ["MURATA"] = ["MURATA MANUFACTURING", "MURATA MFG CO LTD"],
            ["MURATA MANUFACTURING"] = ["MURATA"],
            ["SAMSUNG"] = ["SAMSUNG ELECTRO-MECHANICS", "SAMSUNG ELECTRONICS"],
            ["YAGEO"] = ["YAGEO CORPORATION"],
            ["YAGEO CORPORATION"] = ["YAGEO"],
            ["WURTH"] = ["WURTH ELEKTRONIK", "WURTH ELECTRONICS"],
            ["WURTH ELEKTRONIK"] = ["WURTH"],
            ["TE"] = ["TE CONNECTIVITY", "TYCO ELECTRONICS"],
            ["TE CONNECTIVITY"] = ["TE", "TYCO ELECTRONICS"],
            ["MOLEX"] = ["MOLEX LLC", "MOLEX INC"],
            ["MOLEX LLC"] = ["MOLEX"],
            ["AMS"] = ["AMS AG", "AMS-OSRAM"],
            ["AMS OSRAM"] = ["AMS"],
            ["RENESAS"] = ["RENESAS ELECTRONICS", "RENESAS ELECTRONICS CORP"],
            ["RENESAS ELECTRONICS"] = ["RENESAS"],
            ["LITTELFUSE"] = ["LITTELFUSE INC"],
            ["BOURNS"] = ["BOURNS INC"],
            ["KEMET"] = ["KEMET CORPORATION"],
            ["KEMET CORPORATION"] = ["KEMET"],
            ["AVX"] = ["AVX CORPORATION", "KYOCERA AVX"],
            ["KYOCERA AVX"] = ["AVX"],
            ["DIODES"] = ["DIODES INCORPORATED", "DIODES INC"],
            ["DIODES INCORPORATED"] = ["DIODES"],
            ["FAIRCHILD"] = ["FAIRCHILD SEMICONDUCTOR", "ON SEMICONDUCTOR"],
            ["NATIONAL"] = ["NATIONAL SEMICONDUCTOR", "TEXAS INSTRUMENTS"],
            ["LINEAR"] = ["LINEAR TECHNOLOGY", "ANALOG DEVICES"],
            ["LINEAR TECHNOLOGY"] = ["LINEAR", "ANALOG DEVICES"],
            ["XILINX"] = ["AMD XILINX", "AMD"],
            ["ALTERA"] = ["INTEL ALTERA", "INTEL"],
            ["CYPRESS"] = ["CYPRESS SEMICONDUCTOR", "INFINEON"],
            ["CYPRESS SEMICONDUCTOR"] = ["CYPRESS", "INFINEON"],
            ["BROADCOM"] = ["BROADCOM INC", "BROADCOM LIMITED"],
            ["QUALCOMM"] = ["QUALCOMM INC", "QUALCOMM TECHNOLOGIES"],
            ["SKYWORKS"] = ["SKYWORKS SOLUTIONS", "SKYWORKS SOLUTIONS INC"],
            ["QORVO"] = ["QORVO INC", "RF MICRO DEVICES"],
            ["RF MICRO DEVICES"] = ["QORVO"],
            ["MACOM"] = ["MACOM TECHNOLOGY SOLUTIONS", "M/A-COM"],
            ["M/A-COM"] = ["MACOM"],
            ["ANALOG"] = ["ANALOG DEVICES", "ADI"],
            ["MICRON"] = ["MICRON TECHNOLOGY", "MICRON TECHNOLOGY INC"],
            ["MICRON TECHNOLOGY"] = ["MICRON"],
            ["SAMTEC"] = ["SAMTEC INC"],
            ["JST"] = ["JST MFG", "JAPAN SOLDERLESS TERMINAL"],
            ["JST MFG"] = ["JST"],
            ["MOUSER"] = ["MOUSER ELECTRONICS"],
            ["DIGIKEY"] = ["DIGI-KEY", "DIGIKEY ELECTRONICS"],
        };

        // Check direct alias match
        if (aliases.TryGetValue(normInput, out var acceptable))
        {
            foreach (var a in acceptable)
            {
                if (normReturned.Contains(a) || a.Contains(normReturned))
                    return true;
            }
        }

        // Reverse check: returned manufacturer in aliases
        foreach (var kvp in aliases)
        {
            if (normReturned == kvp.Key || aliases.Values.Any(v => v.Contains(normReturned)))
            {
                if (kvp.Value.Contains(normInput) || normInput.Contains(kvp.Key))
                    return true;
            }
        }

        // Fuzzy: one contains the other (e.g., "TDK" in "TDK CORPORATION")
        return normInput.Contains(normReturned) || normReturned.Contains(normInput);
    }

    private static string NormalizeMfg(string mfg)
    {
        if (string.IsNullOrWhiteSpace(mfg)) return "";
        // Remove common suffixes/prefixes, normalize whitespace, uppercase
        var s = mfg.ToUpperInvariant();
        s = Regex.Replace(s, @"\s+(INC|CORP|CORPORATION|LTD|LLC|CO|CO\.|LIMITED|TECHNOLOGIES|TECHNOLOGY|SEMICONDUCTOR|SEMICONDUCTORS|ELECTRONICS|ELECTRIC|INDUSTRIAL|MF(G|G\.)?|MANUFACTURING|AG|NV|SA|GMBH|KG|CO\.?\s*LTD?\.?)\b", "");
        s = Regex.Replace(s, @"\b(CO|INC|CORP|LTD|LLC)\.?\s*$", "");
        s = Regex.Replace(s, @"[^\w\s]", " "); // replace punctuation with space
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static decimal GetPriceForQty(DigiKeyVariation v, int qty)
    {
        var applicable = v.StandardPricing.Where(p => p.BreakQuantity <= qty).ToList();
        if (applicable.Count == 0) return v.UnitPrice;
        return applicable.MaxBy(p => p.BreakQuantity)!.UnitPrice;
    }

    private static Offer BuildOffer(DigiKeyProduct product, DigiKeyVariation v, decimal price, int moq, int effectiveQty, string supplier)
    {
        var packagingName = v.PackagingName;
        string packaging = packagingName switch
        {
            var name when name.StartsWith("Tape & Reel", StringComparison.OrdinalIgnoreCase) || name.Equals("TR", StringComparison.OrdinalIgnoreCase) => "T&R",
            var name when name.StartsWith("Cut Tape", StringComparison.OrdinalIgnoreCase) || name.Equals("CT", StringComparison.OrdinalIgnoreCase) => "CT",
            var name when name.StartsWith("Digi-Reel", StringComparison.OrdinalIgnoreCase) => "Digi-Reel",
            _ => packagingName
        };

        int? spq = v.StandardPackage > 0 ? v.StandardPackage : null;
        if (!spq.HasValue && (packagingName.Contains("Tape & Reel") || packagingName == "TR"))
            spq = moq;

        int leadWeeks = 0;
        var manufacturerLeadWeeks = string.IsNullOrWhiteSpace(product.ManufacturerLeadWeeks)
            ? v.ManufacturerLeadWeeks
            : product.ManufacturerLeadWeeks;
        if (!string.IsNullOrWhiteSpace(manufacturerLeadWeeks))
        {
            var match = Regex.Match(manufacturerLeadWeeks, @"(\d+(?:\.\d+)?)");
            if (match.Success && decimal.TryParse(match.Groups[1].Value, out var w))
                leadWeeks = (int)Math.Ceiling(w);
        }

        var isMouser = string.Equals(supplier, "Mouser", StringComparison.OrdinalIgnoreCase);
        var cancellationWeeks = isMouser ? 0 : 4;
        var rescheduleWeeks = isMouser ? 0 : 8;
        if (!isMouser && v.Ncnr)
        {
            cancellationWeeks = leadWeeks;
            rescheduleWeeks = leadWeeks;
        }
        else if (!isMouser && v.QuantityAvailableforPackageType > 0)
        {
            cancellationWeeks = 0;
            rescheduleWeeks = 0;
        }

        return new Offer
        {
            Supplier = supplier,
            Mfg = product.Manufacturer?.Name ?? "",
            Mpn = product.ManufacturerProductNumber,
            UnitPrice = price,
            Spq = spq,
            Moq = moq,
            LeadTimeWeeks = leadWeeks > 0 ? leadWeeks : null,
            Packaging = packaging,
            TariffActive = v.TariffActive,
            Ncnr = v.Ncnr,
            Discontinued = v.Discontinued,
            EndOfLife = v.EndOfLife,
            CancellationWeeks = cancellationWeeks,
            RescheduleWeeks = rescheduleWeeks,
            CxlResDbPb = isMouser ? "N/A" : "PB"
        };
    }
}

public static class MpnCleaner
{
    public static (string cleaned, string alternates) Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ("", "");

        var s = raw.Trim();
        s = Regex.Replace(s, @"\[.*?\]", ""); // remove brackets

        // Split on / or any newline sequence (\r\n, \r, \n) BEFORE normalizing whitespace
        var parts = Regex.Split(s, @"(?:/\s*)|(?:\r\n|\r|\n)\s*").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        
        // Normalize whitespace and remove the Excel trailing lowercase "x" marker.
        parts = parts
            .Select(p => Regex.Replace(p.Trim(), @"\s+", " "))
            .Select(p => Regex.Replace(p, @"x+$", ""))
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        
        if (parts.Count == 0) return ("", "");
        return (parts[0], parts.Count > 1 ? string.Join(", ", parts.Skip(1)) : "");
    }
}