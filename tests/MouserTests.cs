using System.Net;
using System.Text;
using OmpSimple.Models;
using Xunit;

namespace OmpSimple.Tests;

public class MouserTests
{
    [Fact]
    public void MouserPartMapper_MapsPricingStockAndCompliance()
    {
        var part = new MouserPart
        {
            Manufacturer = "Hirose Connector",
            ManufacturerPartNumber = "CX90B1-24P",
            MouserPartNumber = "798-CX90B1-24P",
            Min = "1",
            Mult = "1",
            AvailabilityInStock = "0",
            LeadTime = "160 Days",
            ROHSStatus = "RoHS Compliant",
            ProductAttributes =
            [
                new MouserProductAttribute { AttributeName = "Packaging", AttributeValue = "Reel" },
                new MouserProductAttribute { AttributeName = "Standard Pack Qty", AttributeValue = "1500" }
            ],
            PriceBreaks =
            [
                new MouserPriceBreak { Quantity = 1, RawPrice = 3.26m, Currency = "USD" },
                new MouserPriceBreak { Quantity = 10, RawPrice = 2.81m, Currency = "USD" }
            ],
            TradeCompliance =
            [new MouserCompliance { ComplianceName = "Country of Origin", ComplianceValue = "Japan" }]
        };

        var product = MouserMapper.ToDigiKeyProduct(part);
        var (offer, _) = OfferSelector.SelectBest(product, 10, "CX90B1-24P", supplier: "Mouser");

        Assert.NotNull(offer);
        Assert.Equal("Mouser", offer.Supplier);
        Assert.Equal("Hirose Connector", offer.Mfg);
        Assert.Equal(2.81m, offer.UnitPrice);
        Assert.Equal(1500, offer.Spq);
        Assert.Equal(1, offer.Moq);
        Assert.Equal(23, offer.LeadTimeWeeks);
        Assert.Equal("Japan", offer.Coo);
        Assert.Equal("No", offer.RoHS_REACH); // Mouser sample does not state REACH status
        Assert.Equal(0, offer.CancellationWeeks);
        Assert.Equal(0, offer.RescheduleWeeks);
        Assert.Equal("N/A", offer.CxlResDbPb);
    }

    [Fact]
    public async Task MouserClient_PreservesFailureResponseBody()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"invalid api key\"}", Encoding.UTF8, "application/json")
        }, _ => { });
        using var http = new HttpClient(handler);
        using var client = new MouserClient(http, "test-api-key");

        var result = await client.SearchRawAsync("TEST-123");

        Assert.Equal(403, result.StatusCode);
        Assert.Contains("invalid api key", result.ResponseBody);
    }

    [Fact]
    public void MouserClient_RecognizesMouserRateLimitErrorReturnedAsForbidden()
    {
        const string body = "{\"Errors\":[{\"Code\":\"TooManyRequests\",\"ResourceKey\":\"MaxCallPerMinute\"}]}";

        Assert.True(MouserClient.IsRateLimitResponse(403, body));
        Assert.True(MouserClient.IsRateLimitResponse(429, ""));
        Assert.False(MouserClient.IsRateLimitResponse(403, "{\"message\":\"invalid api key\"}"));
    }

    [Fact]
    public async Task MouserClient_PostsExpectedRequestAndParsesResponse()
    {
        string? requestBody = null;
        var handler = new CapturingHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {"Errors":[],"SearchResults":{"NumberOfResult":1,"Parts":[{"Manufacturer":"Test Mfg","ManufacturerPartNumber":"TEST-123","Min":"1","PriceBreaks":[{"Quantity":1,"RawPrice":1.25,"Currency":"USD"}],"ProductAttributes":[{"AttributeName":"Packaging","AttributeValue":"Cut Tape"}] }]}}
                """, Encoding.UTF8, "application/json")
            };
        }, body => requestBody = body);
        using var http = new HttpClient(handler);
        using var client = new MouserClient(http, "test-api-key");

        var (response, status, _, _, _) = await client.SearchAsync("TEST-123");

        Assert.Equal(200, status);
        Assert.NotNull(response);
        Assert.Equal("TEST-123", response!.Products[0].ManufacturerProductNumber);
        Assert.Contains("\"keyword\":\"TEST-123\"", requestBody);
        Assert.Contains("apiKey=test-api-key", handler.LastRequest!.RequestUri!.Query);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        private readonly Action<string> _capture;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> response, Action<string> capture)
        {
            _response = response;
            _capture = capture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            _capture(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _response(request);
        }
    }
}
