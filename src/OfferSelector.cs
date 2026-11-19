using System.Text.RegularExpressions;
using OmpSimple.Models;

namespace OmpSimple;

public static class OfferSelector
{
    public static (Offer? offer, string remarks) SelectBest(DigiKeyProduct product, int qty, string originalMpn)
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

        bool lowStockFallback = false;
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
                best = BuildOffer(product, v, price, moq, eq);
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

        // COO: always blank with "COO missing" in remarks
        remarks.Add("COO missing");

        var altMatch = Regex.Match(originalMpn, @"[/\r\n]");
        if (altMatch.Success)
        {
            var parts = Regex.Split(originalMpn, @"[/\r\n]+").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (parts.Count > 1)
                remarks.Add($"Alternates: {string.Join(", ", parts.Skip(1))}");
        }

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
        return norm1 == norm2;
    }

    private static decimal GetPriceForQty(DigiKeyVariation v, int qty)
    {
        var applicable = v.StandardPricing.Where(p => p.BreakQuantity <= qty).ToList();
        if (applicable.Count == 0) return v.UnitPrice;
        return applicable.MaxBy(p => p.BreakQuantity)!.UnitPrice;
    }

    private static Offer BuildOffer(DigiKeyProduct product, DigiKeyVariation v, decimal price, int moq, int effectiveQty)
    {
        var packagingName = v.Packaging?.Name ?? "";
        string packaging = packagingName switch
        {
            "Tape & Reel" or "TR" => "T&R",
            "Cut Tape" or "CT" => "CT",
            "Digi-Reel" => "Digi-Reel",
            _ => packagingName
        };

        int? spq = v.StandardPackage > 0 ? v.StandardPackage : null;
        if (!spq.HasValue && (packagingName.Contains("Tape & Reel") || packagingName == "TR"))
            spq = moq;

        int leadWeeks = 0;
        if (!string.IsNullOrWhiteSpace(v.ManufacturerLeadWeeks))
        {
            var match = Regex.Match(v.ManufacturerLeadWeeks, @"(\d+(?:\.\d+)?)");
            if (match.Success && decimal.TryParse(match.Groups[1].Value, out var w))
                leadWeeks = (int)Math.Ceiling(w);
        }

        return new Offer
        {
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
            CancellationWeeks = 2,
            RescheduleWeeks = 8,
            CxlResDbPb = "PB"
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
        
        // Normalize whitespace in each part
        parts = parts.Select(p => Regex.Replace(p.Trim(), @"\s+", " ")).ToList();
        
        if (parts.Count == 0) return ("", "");
        return (parts[0], parts.Count > 1 ? string.Join(", ", parts.Skip(1)) : "");
    }
}