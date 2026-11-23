using System.Text.Json.Serialization;

namespace OmpSimple.Models;

public sealed class InputRow
{
    public int RowNumber { get; set; }
    public string PnNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mfg { get; set; } = "";
    public string Mpn { get; set; } = "";
    public string Qty { get; set; } = "";
    public string Eau { get; set; } = "";
}

public sealed class OutputRow
{
    public InputRow Input { get; set; } = new();
    public Offer? ChosenOffer { get; set; }
    public string Remarks { get; set; } = "";
}

public sealed class Offer
{
    public string Supplier { get; set; } = "DigiKey";
    public string Mfg { get; set; } = "";
    public string Mpn { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal UnitPrice { get; set; }
    public int? Spq { get; set; }
    public int Moq { get; set; }
    public int? LeadTimeWeeks { get; set; }
    public int CancellationWeeks { get; set; } = 2;
    public int RescheduleWeeks { get; set; } = 8;
    public string CxlResDbPb { get; set; } = "PB";
    public string Packaging { get; set; } = "";
    public string AuthorisedDistributor { get; set; } = "Yes";
    public string RoHS_REACH { get; set; } = "";
    public string Coo { get; set; } = "";
    // For remarks logic
    public bool TariffActive { get; set; }
    public bool Ncnr { get; set; }
    public bool Discontinued { get; set; }
    public bool EndOfLife { get; set; }
}

public sealed class DigiKeyTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";
}

public sealed class DigiKeySearchResponse
{
    [JsonPropertyName("Products")]
    public List<DigiKeyProduct> Products { get; set; } = [];
}

public sealed class DigiKeyProduct
{
    [JsonPropertyName("Manufacturer")]
    public DigiKeyManufacturer? Manufacturer { get; set; }

    [JsonPropertyName("ManufacturerProductNumber")]
    public string ManufacturerProductNumber { get; set; } = "";

    [JsonPropertyName("ManufacturerLeadWeeks")]
    public string? ManufacturerLeadWeeks { get; set; }

    [JsonPropertyName("ProductVariations")]
    public List<DigiKeyVariation> ProductVariations { get; set; } = [];

    [JsonPropertyName("Classifications")]
    public DigiKeyClassifications? Classifications { get; set; }
}

public sealed class DigiKeyManufacturer
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";
}

public sealed class DigiKeyVariation
{
    [JsonPropertyName("MarketPlace")]
    public bool MarketPlace { get; set; }

    [JsonPropertyName("MinimumOrderQuantity")]
    public int MinimumOrderQuantity { get; set; }

    [JsonPropertyName("StandardPackage")]
    public int StandardPackage { get; set; }

    [JsonPropertyName("Packaging")]
    public DigiKeyPackaging? Packaging { get; set; }

    [JsonPropertyName("PackageType")]
    public DigiKeyPackaging? PackageType { get; set; }

    [JsonIgnore]
    public string PackagingName => (PackageType ?? Packaging)?.Name ?? "";

    [JsonPropertyName("QuantityAvailableforPackageType")]
    public int QuantityAvailableforPackageType { get; set; }

    [JsonPropertyName("UnitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("ManufacturerLeadWeeks")]
    public string? ManufacturerLeadWeeks { get; set; }

    [JsonPropertyName("StandardPricing")]
    public List<DigiKeyStandardPricing> StandardPricing { get; set; } = [];

    [JsonPropertyName("TariffActive")]
    public bool TariffActive { get; set; }

    [JsonPropertyName("Ncnr")]
    public bool Ncnr { get; set; }

    [JsonPropertyName("Discontinued")]
    public bool Discontinued { get; set; }

    [JsonPropertyName("EndOfLife")]
    public bool EndOfLife { get; set; }
}

public sealed class DigiKeyPackaging
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";
}

public sealed class DigiKeyStandardPricing
{
    [JsonPropertyName("BreakQuantity")]
    public int BreakQuantity { get; set; }

    [JsonPropertyName("UnitPrice")]
    public decimal UnitPrice { get; set; }
}

public sealed class DigiKeyClassifications
{
    [JsonPropertyName("RohsStatus")]
    public string? RohsStatus { get; set; }

    [JsonPropertyName("ReachStatus")]
    public string? ReachStatus { get; set; }
}