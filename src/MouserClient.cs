using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using OmpSimple.Models;

namespace OmpSimple;

public sealed class MouserClient : IDisposable, IDistributorClient
{
    private const int MaxCallsPerMinute = 30;
    private readonly DistributorRateTracker _rateTracker;
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Queue<DateTimeOffset> _callTimes = new();
    private readonly SemaphoreSlim _rateLimitGate = new(1, 1);

    public MouserClient(HttpClient http, string apiKey, DistributorRateTracker? rateTracker = null)
    {
        _http = http;
        _apiKey = apiKey;
        _rateTracker = rateTracker ?? DistributorRateTracker.CreateDefault();
        _http.BaseAddress = new Uri("https://api.mouser.com/");
    }

    private async Task WaitForMouserWindowAsync(CancellationToken ct)
    {
        await _rateLimitGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                while (_callTimes.Count > 0 && now - _callTimes.Peek() >= TimeSpan.FromMinutes(1))
                    _callTimes.Dequeue();

                if (_callTimes.Count < MaxCallsPerMinute)
                {
                    _callTimes.Enqueue(now);
                    return;
                }

                var wait = _callTimes.Peek() + TimeSpan.FromMinutes(1) - now;
                await Task.Delay(wait, ct);
            }
        }
        finally
        {
            _rateLimitGate.Release();
        }
    }

    public async Task<(MouserSearchResponse? Response, int StatusCode, string? RateLimitLimit, string? RateLimitRemaining, string? ResponseBody)> SearchRawAsync(
        string keyword, CancellationToken ct = default)
    {
        await WaitForMouserWindowAsync(ct);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/search/keyword?apiKey={Uri.EscapeDataString(_apiKey)}")
        {
            Content = JsonContent.Create(new
            {
                SearchByKeywordRequest = new
                {
                    keyword,
                    records = 10,
                    startingRecord = 0,
                    searchOptions = "",
                    searchWithYourSignUpLanguage = ""
                }
            })
        };

        request.Headers.Accept.ParseAdd("application/json");
        var response = await _http.SendAsync(request, ct);
        _rateTracker.RecordCalls("Mouser", 1);
        var rateLimitLimit = response.Headers.TryGetValues("X-RateLimit-Limit", out var limits)
            ? limits.FirstOrDefault() : null;
        var rateLimitRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            ? remaining.FirstOrDefault() : null;

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return (null, (int)response.StatusCode, rateLimitLimit, rateLimitRemaining, responseBody);

        return (JsonSerializer.Deserialize<MouserSearchResponse>(responseBody, _jsonOptions),
            (int)response.StatusCode, rateLimitLimit, rateLimitRemaining, responseBody);
    }

    public async Task<SupplierSearchResult> SearchAsync(string mpn, CancellationToken ct = default)
    {
        var result = await SearchRawAsync(mpn, ct);
        if (IsRateLimitResponse(result.StatusCode, result.ResponseBody))
        {
            Console.WriteLine("Mouser rate limit reached; waiting 60 seconds before retry.");
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            result = await SearchRawAsync(mpn, ct);
        }

        var products = result.Response?.SearchResults?.Parts
            .Select(MouserMapper.ToDigiKeyProduct)
            .ToList() ?? [];

        return new SupplierSearchResult(
            new DigiKeySearchResponse { Products = products },
            result.StatusCode,
            result.RateLimitLimit,
            result.RateLimitRemaining,
            result.ResponseBody);
    }

    public static bool IsRateLimitResponse(int statusCode, string? responseBody) =>
        statusCode == 429 ||
        (statusCode == 403 && (responseBody?.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) == true ||
                               responseBody?.Contains("MaxCallPerMinute", StringComparison.OrdinalIgnoreCase) == true));

    public void Dispose()
    {
        _rateLimitGate.Dispose();
        _http.Dispose();
    }
}

public static class MouserMapper
{
    public static DigiKeyProduct ToDigiKeyProduct(MouserPart part)
    {
        var moq = ParsePositiveInt(part.Min) ?? 1;
        var standardPackage = FindAttributeInt(part, "Standard Pack Qty") ?? 0;
        var packaging = FindAttribute(part, "Packaging") ?? "";
        var priceBreaks = part.PriceBreaks ?? [];
        var stock = ParsePositiveInt(part.AvailabilityInStock) ?? 0;
        var leadWeeks = ParseLeadWeeks(part.LeadTime);
        var rohs = part.ROHSStatus ?? "";
        var reach = FindCompliance(part.ProductCompliance, "REACH") ?? "Not stated";

        return new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = part.Manufacturer ?? "" },
            ManufacturerProductNumber = part.ManufacturerPartNumber ?? "",
            ManufacturerLeadWeeks = leadWeeks > 0 ? leadWeeks.ToString(CultureInfo.InvariantCulture) : null,
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = moq,
                    StandardPackage = standardPackage,
                    Packaging = new DigiKeyPackaging { Name = packaging },
                    QuantityAvailableforPackageType = stock,
                    UnitPrice = priceBreaks.FirstOrDefault()?.RawPrice ?? 0,
                    StandardPricing = priceBreaks.Select(p => new DigiKeyStandardPricing
                    {
                        BreakQuantity = p.Quantity,
                        UnitPrice = p.RawPrice
                    }).ToList()
                }
            ],
            Classifications = new DigiKeyClassifications
            {
                RohsStatus = rohs,
                ReachStatus = reach
            },
            MouserCountryOfOrigin = FindCompliance(part.TradeCompliance, "Country of Origin")
        };
    }

    private static string? FindAttribute(MouserPart part, string name) =>
        part.ProductAttributes?.FirstOrDefault(a =>
            string.Equals(a.AttributeName, name, StringComparison.OrdinalIgnoreCase))?.AttributeValue;

    private static int? FindAttributeInt(MouserPart part, string name) =>
        ParsePositiveInt(FindAttribute(part, name));

    private static string? FindCompliance(IEnumerable<MouserCompliance>? values, string name) =>
        values?.FirstOrDefault(c => c.ComplianceName?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
            ?.ComplianceValue;

    private static int? ParsePositiveInt(string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0)
            return result;
        return null;
    }

    private static int ParseLeadWeeks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(\d+(?:\.\d+)?)\s*(day|week)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var amount))
            return 0;

        if (match.Groups[2].Value.StartsWith("day", StringComparison.OrdinalIgnoreCase))
            amount /= 7;
        return (int)Math.Ceiling(amount);
    }
}

public sealed record SupplierSearchResult(
    DigiKeySearchResponse? Response,
    int StatusCode,
    string? RateLimitLimit,
    string? RateLimitRemaining,
    string? ResponseBody);

public interface IDistributorClient : IDisposable
{
    Task<SupplierSearchResult> SearchAsync(string mpn, CancellationToken ct = default);
}

public sealed class DigiKeyDistributorClient : IDisposable, IDistributorClient
{
    private readonly DigiKeyClient _client;
    private readonly DistributorRateTracker _rateTracker;

    public DigiKeyDistributorClient(HttpClient http, DistributorRateTracker? rateTracker = null)
    {
        _client = new DigiKeyClient(http);
        _rateTracker = rateTracker ?? DistributorRateTracker.CreateDefault();
    }

    public async Task<SupplierSearchResult> SearchAsync(string mpn, CancellationToken ct = default)
    {
        var result = await _client.SearchAsync(mpn, ct);
        _rateTracker.RecordCalls("DigiKey", 1);
        return new SupplierSearchResult(result.Response, result.StatusCode, result.RateLimitLimit, result.RateLimitRemaining, result.ResponseBody);
    }

    public void Dispose() => _client.Dispose();
}

public static class DistributorFactory
{
    public static (string Name, IDistributorClient Client) Create(string choice, HttpClient http, DistributorRateTracker? rateTracker = null)
    {
        return choice switch
        {
            "1" => ("DigiKey", new DigiKeyDistributorClient(http, rateTracker)),
            "2" => ("Mouser", new MouserClient(http, Credentials.MouserApiKey, rateTracker)),
            "3" => ("Element14", new Element14Client(http, Credentials.Element14ApiKey, Credentials.Element14StoreId, rateTracker)),
            _ => throw new ArgumentException("Choose 1 for DigiKey, 2 for Mouser, or 3 for Element14.", nameof(choice))
        };
    }

    public static string CreateName(string choice) => choice switch
    {
        "1" => "DigiKey",
        "2" => "Mouser",
        "3" => "Element14",
        _ => throw new ArgumentException("Unknown distributor choice.", nameof(choice))
    };
}
