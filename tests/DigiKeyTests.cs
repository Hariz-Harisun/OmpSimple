using System.Net;
using System.Text;
using OmpSimple.Models;
using Xunit;

namespace OmpSimple.Tests;

public class DigiKeyTests
{
    [Fact]
    public void ProductDescription_AcceptsDigiKeyObjectShape()
    {
        const string json = """
        {
          "Products": [{
            "ManufacturerProductNumber": "MC0603X225K100CT",
            "Description": {
              "ProductDescription": "2.2 µF, 10 V, 0603, ±10%, X5R",
              "DetailedDescription": "Multilayer ceramic capacitor"
            }
          }]
        }
        """;

        var response = System.Text.Json.JsonSerializer.Deserialize<DigiKeySearchResponse>(json)!;

        Assert.Contains("2.2 µF, 10 V, 0603", response.Products[0].ProductDescription);
        Assert.Contains("Multilayer ceramic capacitor", response.Products[0].ProductDescription);
    }

    [Fact]
    public async Task Client_UsesNormalizedKeywordAndSelectsExactSpecification()
    {
        var handler = new CapturingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                return JsonResponse("{\"access_token\":\"test-token\",\"expires_in\":3600}");
            }

            return JsonResponse("""
            {
              "Products": [
                {
                  "ManufacturerProductNumber": "OTHER",
                  "Description": "1 µF, 10 V, 0603, X5R",
                  "ProductVariations": [{"MinimumOrderQuantity":1,"QuantityAvailableforPackageType":10,"StandardPricing":[{"BreakQuantity":1,"UnitPrice":0.10}]}]
                },
                {
                  "ManufacturerProductNumber": "MC0603X225K100CT",
                  "Description": "2.2 µF, 10 V, 0603, ±10%, X5R",
                  "ProductVariations": [{"MinimumOrderQuantity":1,"QuantityAvailableforPackageType":10,"StandardPricing":[{"BreakQuantity":1,"UnitPrice":0.12}]}]
                }
              ]
            }
            """);
        });

        using var http = new HttpClient(handler);
        using var client = new DigiKeyClient(http);

        var result = await client.SearchAsync("SMT;SMD0603;2u2;+10%;-10%;10V;X5R");

        Assert.Equal(200, result.StatusCode);
        Assert.Single(result.Response!.Products);
        Assert.Equal("MC0603X225K100CT", result.Response.Products[0].ManufacturerProductNumber);
        Assert.True(result.Response.Products[0].ExactKeywordMatch);
        Assert.Contains("any:2.2uF 0603 10V X5R", handler.KeywordRequestBody);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        public string KeywordRequestBody { get; private set; } = "";

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/products/v4/search/keyword", StringComparison.Ordinal))
                KeywordRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return _response(request);
        }
    }
}
