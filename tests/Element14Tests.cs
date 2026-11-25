using System.Net;
using System.Text.Json;
using OmpSimple.Models;
using Xunit;

namespace OmpSimple.Tests;

public class Element14Tests
{
    [Fact]
    public void Mapper_MapsProductToSharedOfferShape()
    {
        const string json = """
        {
          "manufacturerPartNumberSearchReturn": {
            "numberOfResults": 1,
            "products": [{
              "sku": "1360825",
              "displayName": "SCHURTER - 0001.1001 - FUSE",
              "packSize": 1,
              "unitOfMeasure": "EACH",
              "brandName": "SCHURTER",
              "translatedManufacturerPartNumber": "0001.1001",
              "translatedMinimumOrderQuality": 10,
              "prices": [
                { "from": 1, "to": 9, "cost": 0.90 },
                { "from": 10, "to": 99, "cost": 0.78 }
              ],
              "inv": 518,
              "stock": { "level": 0, "leastLeadTime": 14, "status": 1 },
              "countryOfOrigin": "MX",
              "rohsStatusCode": "YES"
            }]
          }
        }
        """;

        var response = JsonSerializer.Deserialize<Element14SearchResponse>(json)!;
        var product = response.ManufacturerPartNumberSearchReturn!.Products[0];
        var mapped = Element14Mapper.ToDigiKeyProduct(product, "MYR");

        Assert.Equal("SCHURTER", mapped.Manufacturer?.Name);
        Assert.Equal("0001.1001", mapped.ManufacturerProductNumber);
        Assert.Equal("MYR", mapped.Currency);
        Assert.Equal(10, mapped.ProductVariations[0].MinimumOrderQuantity);
        Assert.Equal(518, mapped.ProductVariations[0].QuantityAvailableforPackageType);
        Assert.Equal(0.90m, mapped.ProductVariations[0].UnitPrice);
        Assert.Equal(0.78m, mapped.ProductVariations[0].StandardPricing[1].UnitPrice);
        Assert.Equal("2", mapped.ManufacturerLeadWeeks);
    }

    [Fact]
    public void Mapper_UsesMinimumOrderQuantityAndPackSize()
    {
        var product = new Element14Product
        {
            Sku = "SKU-1",
            BrandName = "TestMfg",
            TranslatedManufacturerPartNumber = "TEST-123",
            PackSize = 25,
            UnitOfMeasure = "TAPE",
            TranslatedMinimumOrderQuality = 10,
            Prices = [new Element14Price { From = 1, To = 1000, Cost = 0.25m }],
            Stock = new Element14Stock { Level = 500, LeastLeadTime = 0 }
        };

        var mapped = Element14Mapper.ToDigiKeyProduct(product, "GBP");

        Assert.Equal(10, mapped.ProductVariations[0].MinimumOrderQuantity);
        Assert.Equal(25, mapped.ProductVariations[0].StandardPackage);
        Assert.Equal("TAPE", mapped.ProductVariations[0].Packaging?.Name);
    }

    [Fact]
    public async Task Client_UsesManufacturerPartNumberEnvelopeAndExactQueryContract()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"manufacturerPartNumberSearchReturn":{"products":[{"brandName":"TI","translatedManufacturerPartNumber":"LM358P","inv":7,"prices":[{"from":1,"to":9,"cost":1.2}],"stock":{"leastLeadTime":8}}]}}
            """)
        });
        using var http = new HttpClient(handler);
        using var client = new Element14Client(http, "test-key", "my.element14.com");

        var result = await client.SearchAsync("LM358P");

        Assert.Equal(200, result.StatusCode);
        Assert.Single(result.Response!.Products);
        Assert.Equal(7, result.Response.Products[0].ProductVariations[0].QuantityAvailableforPackageType);
        Assert.Contains("term=manuPartNum%3ALM358P", handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("callInfo.responseDataFormat=JSON", handler.LastRequest.RequestUri.Query);
        Assert.Contains("callInfo.apiKey=test-key", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public void QueryBuilder_ConvertsCapacitorSpecToElement14KeywordSearch()
    {
        var term = Element14Query.Build("SMT;SMD0603;2u2;+10%;-10%;10V;X5R");

        Assert.Equal("any:2.2uF 0603 10V X5R", term);
    }

    [Fact]
    public void ProductSelector_ChoosesExactCapacitorFromKeywordResults()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "OTHER - 1 µF, 10 V, 0603 [1608 Metric], X5R" },
            new() { DisplayName = "MULTICOMP PRO - MC0603X225K100CT - SMD Multilayer Ceramic Capacitor, 2.2 µF, 10 V, 0603 [1608 Metric], ± 10%, X5R" }
        };

        var selected = Element14Query.SelectExact(products, "SMT;SMD0603;2u2;+10%;-10%;10V;X5R");

        Assert.Same(products[1], selected);
    }

    [Fact]
    public async Task Client_UsesKeywordSearchForCapacitorSpecification()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"keywordSearchReturn":{"currency":"MYR","products":[{"sku":"2320817","displayName":"MULTICOMP PRO - MC0603X225K100CT - SMD Multilayer Ceramic Capacitor, 2.2 µF, 10 V, 0603 [1608 Metric], ± 10%, X5R","brandName":"MULTICOMP PRO","translatedManufacturerPartNumber":"MC0603X225K100CT","translatedMinimumOrderQuality":1,"packSize":1,"unitOfMeasure":"EACH (SUPPLIED ON CUT TAPE)","inv":100,"prices":[{"from":1,"to":100,"cost":0.12}],"rohsStatusCode":"YES"}]}}
            """)
        });
        using var http = new HttpClient(handler);
        using var client = new Element14Client(http, "test-key", "my.element14.com");

        var result = await client.SearchAsync("SMT;SMD0603;2u2;+10%;-10%;10V;X5R");

        Assert.Equal(200, result.StatusCode);
        Assert.Single(result.Response!.Products);
        Assert.Equal("MC0603X225K100CT", result.Response.Products[0].ManufacturerProductNumber);
        Assert.Contains("term=any%3A2.2uF%200603%2010V%20X5R", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public void Factory_RegistersElement14WithoutChangingExistingProviders()
    {
        using var http = new HttpClient();
        using var tracker = new DisposableTracker();

        var element14 = DistributorFactory.Create("3", http, tracker.Rates);
        element14.Client.Dispose();

        Assert.Equal("Element14", element14.Name);
        Assert.Equal("DigiKey", DistributorFactory.CreateName("1"));
        Assert.Equal("Mouser", DistributorFactory.CreateName("2"));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_response(request));
        }
    }

    private sealed class DisposableTracker : IDisposable
    {
        public DistributorRateTracker Rates { get; } = DistributorRateTracker.CreateDefault(
            Path.Combine(Path.GetTempPath(), "OmpSimpleElement14_" + Guid.NewGuid(), "rates.json"));

        public void Dispose() { }
    }
}
