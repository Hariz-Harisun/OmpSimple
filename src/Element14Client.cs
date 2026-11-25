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
        await WaitForRateLimitAsync(ct);

        var keywordSearch = Element14Query.IsKeywordSpec(mpn);
        var searchTerm = keywordSearch ? Element14Query.Build(mpn) : $"manuPartNum:{mpn}";
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
            return new SupplierSearchResult(null, (int)response.StatusCode, null, null, responseBody);

        Element14SearchResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Element14SearchResponse>(responseBody);
        }
        catch (JsonException)
        {
            payload = null;
        }

        var searchReturn = payload?.ManufacturerPartNumberSearchReturn ?? payload?.KeywordSearchReturn;
        var currency = searchReturn?.Currency ?? "MYR";
        var sourceProducts = searchReturn?.Products ?? [];
        var selectedProduct = keywordSearch ? Element14Query.SelectExact(sourceProducts, mpn) : null;
        var products = keywordSearch
            ? selectedProduct == null ? [] : [Element14Mapper.ToDigiKeyProduct(selectedProduct, currency, exactKeywordMatch: true)]
            : sourceProducts.Select(product => Element14Mapper.ToDigiKeyProduct(product, currency)).ToList();

        return new SupplierSearchResult(
            new DigiKeySearchResponse { Products = products },
            (int)response.StatusCode,
            null,
            null,
            responseBody);
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
                ReachStatus = "Not stated"
            },
            MouserCountryOfOrigin = product.CountryOfOrigin
        };
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
