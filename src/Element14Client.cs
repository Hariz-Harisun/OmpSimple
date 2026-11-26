using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmpSimple.Models;

namespace OmpSimple;

public sealed class Element14Client : IDisposable, IDistributorClient
{
    private readonly HttpClient _http;
    private readonly DistributorRateTracker _rateTracker;
    private readonly string _apiKey;
    private readonly string _storeId;
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    public Element14Client(
        HttpClient http,
        string apiKey,
        string storeId = "my.element14.com",
        DistributorRateTracker? rateTracker = null)
    {
        _http = http;
        _apiKey = apiKey;
        _storeId = string.IsNullOrWhiteSpace(storeId) ? "my.element14.com" : storeId;
        _rateTracker = rateTracker ?? DistributorRateTracker.CreateDefault();
        _http.BaseAddress = new Uri("https://api.element14.com/");
    }

    public async Task<SupplierSearchResult> SearchAsync(string mpn, CancellationToken ct = default)
    {
        var keywordSearch = Element14Query.IsKeywordSpec(mpn);
        IReadOnlyList<string> searchTerms = keywordSearch
            ? Element14Query.BuildVariants(mpn)
            : [$"manuPartNum:{mpn}"];

        SupplierSearchResult? last = null;
        for (var attempt = 0; attempt < searchTerms.Count; attempt++)
        {
            await WaitForRateLimitAsync(ct);
            var (statusCode, responseBody, payload) = await SendAsync(searchTerms[attempt], ct);

            if (statusCode is < 200 or >= 300)
                return new SupplierSearchResult(null, statusCode, null, null, responseBody);

            var searchReturn = payload?.ManufacturerPartNumberSearchReturn ?? payload?.KeywordSearchReturn;
            var currency = searchReturn?.Currency ?? "MYR";
            var sourceProducts = searchReturn?.Products ?? [];
            var selectedProduct = keywordSearch ? Element14Query.SelectExact(sourceProducts, mpn) : null;
            var products = keywordSearch
                ? selectedProduct == null ? [] : [Element14Mapper.ToDigiKeyProduct(selectedProduct, currency, exactKeywordMatch: true)]
                : sourceProducts.Select(product => Element14Mapper.ToDigiKeyProduct(product, currency)).ToList();

            last = new SupplierSearchResult(
                new DigiKeySearchResponse { Products = products },
                statusCode,
                null,
                null,
                responseBody);

            // Keyword searches try the next wording only when nothing matched the specification.
            if (!keywordSearch || products.Count > 0)
                break;
        }

        return last!;
    }

    private async Task<(int StatusCode, string Body, Element14SearchResponse? Payload)> SendAsync(
        string searchTerm, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["term"] = searchTerm,
            ["storeInfo.id"] = _storeId,
            ["resultsSettings.offset"] = "0",
            ["resultsSettings.numberOfResults"] = "10",
            ["resultsSettings.responseGroup"] = "large",
            ["callInfo.responseDataFormat"] = "JSON",
            ["callInfo.apiKey"] = _apiKey
        };

        using var response = await _http.GetAsync(BuildQuery("catalog/products", query), ct);
        _rateTracker.RecordCalls("Element14", 1);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return ((int)response.StatusCode, responseBody, null);

        Element14SearchResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Element14SearchResponse>(responseBody);
        }
        catch (JsonException)
        {
            payload = null;
        }

        return ((int)response.StatusCode, responseBody, payload);
    }

    private async Task WaitForRateLimitAsync(CancellationToken ct)
    {
        await _rateLimitGate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromMilliseconds(500) - (DateTimeOffset.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastCall = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLimitGate.Release();
        }
    }

    private static string BuildQuery(string path, IReadOnlyDictionary<string, string> values) =>
        path + "?" + string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    public void Dispose()
    {
        _rateLimitGate.Dispose();
        _http.Dispose();
    }
}

public static class Element14Mapper
{
    public static DigiKeyProduct ToDigiKeyProduct(Element14Product product, string currency, bool exactKeywordMatch = false)
    {
        var moq = product.TranslatedMinimumOrderQuality > 0 ? product.TranslatedMinimumOrderQuality : 1;
        var prices = product.Prices ?? [];
        var stock = product.Inv > 0 ? product.Inv : product.Stock?.Level ?? 0;
        var leadWeeks = product.Stock?.LeastLeadTime > 0
            ? (int)Math.Ceiling(product.Stock.LeastLeadTime / 7d)
            : 0;

        return new DigiKeyProduct
        {
            Currency = string.IsNullOrWhiteSpace(currency) ? "GBP" : currency,
            ExactKeywordMatch = exactKeywordMatch,
            Manufacturer = new DigiKeyManufacturer { Name = product.BrandName ?? "" },
            ManufacturerProductNumber = product.TranslatedManufacturerPartNumber ?? "",
            ProductDescription = product.DisplayName ?? "",
            ManufacturerLeadWeeks = leadWeeks > 0 ? leadWeeks.ToString(CultureInfo.InvariantCulture) : null,
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = moq,
                    StandardPackage = product.PackSize > 0 ? product.PackSize : moq,
                    Packaging = new DigiKeyPackaging { Name = product.UnitOfMeasure ?? "" },
                    QuantityAvailableforPackageType = stock,
                    UnitPrice = prices.FirstOrDefault()?.Cost ?? 0,
                    StandardPricing = prices.Select(price => new DigiKeyStandardPricing
                    {
                        BreakQuantity = Math.Max(1, price.From),
                        UnitPrice = price.Cost
                    }).ToList()
                }
            ],
            Classifications = new DigiKeyClassifications
            {
                RohsStatus = product.RohsStatusCode == "YES" ? "RoHS Compliant" : product.RohsStatusCode,
                ReachStatus = ReachFromSvhc(product)
            },
            MouserCountryOfOrigin = product.CountryOfOrigin
        };
    }

    // element14 returns no REACH flag, only an SVHC attribute such as "No SVHC (27-Jun-2018)".
    // "No SVHC" is treated as REACH unaffected; anything else stays "Not stated".
    private static string ReachFromSvhc(Element14Product product)
    {
        var svhc = product.Attributes?
            .FirstOrDefault(attribute => string.Equals(attribute.AttributeLabel?.Trim(), "SVHC", StringComparison.OrdinalIgnoreCase))
            ?.AttributeValue;
        return svhc != null && svhc.TrimStart().StartsWith("No SVHC", StringComparison.OrdinalIgnoreCase)
            ? "REACH Unaffected"
            : "Not stated";
    }
}

public sealed class Element14SearchResponse
{
    [JsonPropertyName("manufacturerPartNumberSearchReturn")]
    public Element14SearchReturn? ManufacturerPartNumberSearchReturn { get; set; }

    [JsonPropertyName("keywordSearchReturn")]
    public Element14SearchReturn? KeywordSearchReturn { get; set; }
}

public sealed class Element14SearchReturn
{
    [JsonPropertyName("products")]
    public List<Element14Product> Products { get; set; } = [];

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

public sealed class Element14Product
{
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("brandName")]
    public string? BrandName { get; set; }

    [JsonPropertyName("translatedManufacturerPartNumber")]
    public string? TranslatedManufacturerPartNumber { get; set; }

    [JsonPropertyName("translatedMinimumOrderQuality")]
    public int TranslatedMinimumOrderQuality { get; set; }

    [JsonPropertyName("packSize")]
    public int PackSize { get; set; }

    [JsonPropertyName("unitOfMeasure")]
    public string? UnitOfMeasure { get; set; }

    [JsonPropertyName("inv")]
    public int Inv { get; set; }

    [JsonPropertyName("countryOfOrigin")]
    public string? CountryOfOrigin { get; set; }

    [JsonPropertyName("prices")]
    public List<Element14Price> Prices { get; set; } = [];

    [JsonPropertyName("stock")]
    public Element14Stock? Stock { get; set; }

    [JsonPropertyName("rohsStatusCode")]
    public string? RohsStatusCode { get; set; }

    [JsonPropertyName("attributes")]
    public List<Element14Attribute>? Attributes { get; set; }
}

public sealed class Element14Attribute
{
    [JsonPropertyName("attributeLabel")]
    public string? AttributeLabel { get; set; }

    [JsonPropertyName("attributeValue")]
    public string? AttributeValue { get; set; }
}

public sealed class Element14Price
{
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; set; }
}

public sealed class Element14Stock
{
    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("leastLeadTime")]
    public int LeastLeadTime { get; set; }
}
