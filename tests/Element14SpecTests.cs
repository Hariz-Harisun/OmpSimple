using System.Net;
using OmpSimple.Models;
using Xunit;

namespace OmpSimple.Tests;

public class Element14SpecTests
{
    // ---- rows taken from the real quote that came back blank or wrong ----

    [Theory]
    [InlineData("SMT;SMD0402;1M;1%;--;--;--", "any:resistor 1Mohm 0402 1%")]
    [InlineData("SMT;SMD0402;169k;1%;63mW;TC10 0;--", "any:resistor 169kohm 0402 1%")]
    [InlineData("SMT;SMD0603;10k;1%;100mW;TC10 0;--", "any:resistor 10kohm 0603 1%")]
    [InlineData("SMT;SMD0402;3k83;1%;63mW;--;--", "any:resistor 3.83kohm 0402 1%")]
    [InlineData("SMT;SMD0402;10k;0,5%;63mW;TC1 00;--", "any:resistor 10kohm 0402 0.5%")]
    [InlineData("SMT;SMD0402;22R;1%;--;--;--", "any:resistor 22ohm 0402 1%")]
    [InlineData("SMT;SMD0603;8R2;1%;--;--;--", "any:resistor 8.2ohm 0603 1%")]
    [InlineData("SMT;SMD0402;0R;1%;63mW;--;--", "any:resistor 0ohm 0402 1%")]
    public void Build_ResistorSpec_UsesOhmsAndNeverCapacitorUnits(string spec, string expected)
    {
        Assert.Equal(SpecKind.Resistor, Element14Query.Classify(spec));
        Assert.Equal(expected, Element14Query.Build(spec));
    }

    [Fact]
    public void BuildVariants_ResistorHasSecondWordingWithoutTolerance()
    {
        var variants = Element14Query.BuildVariants("SMT;SMD0402;169k;1%;63mW;TC10 0;--");

        Assert.Equal(2, variants.Count);
        Assert.Equal("any:resistor 169kohm 0402 1%", variants[0]);
        Assert.Equal("any:169 kohm resistor 0402", variants[1]);
    }

    [Theory]
    [InlineData("SMT;SMD0603;2u2;+10%;-10%;10V;X5R")]
    [InlineData("SMT;SMD0402;22p;+5%;-5%;50V;C0G")]
    public void Classify_CapacitorSpecIsStillCapacitor(string spec)
    {
        Assert.Equal(SpecKind.Capacitor, Element14Query.Classify(spec));
    }

    [Fact]
    public void SelectExact_Resistor_MatchesValueSizeToleranceAndPower()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "VISHAY - CRCW0402169KFKED - SMD Chip Resistor, 169 kohm, ± 1%, 63 mW, 0402 [1005 Metric], Thick Film" },
            new() { DisplayName = "VISHAY - CRCW0402196KFKED - SMD Chip Resistor, 196 kohm, ± 1%, 63 mW, 0402 [1005 Metric], Thick Film" }
        };

        var selected = Element14Query.SelectExact(products, "SMT;SMD0402;169k;1%;63mW;TC10 0;--");

        Assert.Same(products[0], selected);
    }

    [Fact]
    public void SelectExact_Resistor_RejectsNearbyValuesWrongSizeArraysAndWrongTolerance()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "SMD Chip Resistor, 100 kohm, ± 1%, 62.5 mW, 0402 [1005 Metric]" },
            new() { DisplayName = "SMD Chip Resistor, 10 kohm, ± 1%, 100 mW, 0603 [1608 Metric]" },
            new() { DisplayName = "Resistor Array, 10 kohm, ± 1%, 62.5 mW, 0402 [1005 Metric], 4 Resistors" },
            new() { DisplayName = "SMD Chip Resistor, 10 kohm, ± 5%, 62.5 mW, 0402 [1005 Metric]" }
        };

        Assert.Null(Element14Query.SelectExact(products, "SMT;SMD0402;10k;1%;63mW;--;--"));
    }

    [Fact]
    public void SelectExact_Resistor_ZeroOhmDoesNotMatchOneHundredOhm()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "BOURNS - CR0603-FX-1000ELF - SMD Chip Resistor, 100 ohm, ± 1%, 100 mW, 0603 [1608 Metric]" },
            new() { DisplayName = "PANASONIC - ERJ3GEY0R00V - SMD Chip Resistor, 0 ohm, 0603 [1608 Metric], Thick Film, 100 mW" }
        };

        var selected = Element14Query.SelectExact(products, "SMT;SMD0603;0R;1%;100mW;TC100;--");

        Assert.Same(products[1], selected);
    }

    [Fact]
    public void SelectExact_Resistor_ReadsDigiKeyAndMouserStyleDescriptions()
    {
        var products = new List<DigiKeyProduct>
        {
            new() { ManufacturerProductNumber = "RC0402FR-07100KL", ProductDescription = "RES 100K OHM 1% 1/16W 0402" },
            new() { ManufacturerProductNumber = "RC0402FR-0710KL", ProductDescription = "RES 10K OHM 1% 1/16W 0402" }
        };

        var selected = Element14Query.SelectExact(products, "SMT;SMD0402;10k;1%;63mW;--;--");

        Assert.Same(products[1], selected);
    }

    [Fact]
    public void SelectExact_Resistor_PrefersPowerThatMatchesExactly()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "SMD Chip Resistor, 10 kohm, ± 1%, 125 mW, 0402 [1005 Metric]" },
            new() { DisplayName = "SMD Chip Resistor, 10 kohm, ± 1%, 62.5 mW, 0402 [1005 Metric]" }
        };

        var selected = Element14Query.SelectExact(products, "SMT;SMD0402;10k;1%;63mW;--;--");

        Assert.Same(products[1], selected);
    }

    // ---- capacitor fixes ----

    [Theory]
    [InlineData("SMT;SMD0402;22p;+5%;-5%;50V;C0G", "any:22pF 0402 50V C0G")]
    [InlineData("SMT;SMD0402;470p;+10%;-10%;25V;X7R", "any:470pF 0402 25V X7R")]
    [InlineData("SMT;SMD0402;100p;+10%;-10%;25V;X7R", "any:100pF 0402 25V X7R")]
    [InlineData("SMT;SMD0402;1n;+10%;-10%;25V;X7R", "any:0.001uF 0402 25V X7R")]
    [InlineData("SMT;SMD0603;2u2;+10%;-10%;10V;X5R", "any:2.2uF 0603 10V X5R")]
    public void Build_Capacitor_KeepsPicofaradsInsteadOfRoundingToZero(string spec, string expected)
    {
        Assert.Equal(expected, Element14Query.Build(spec));
    }

    [Fact]
    public void SelectExact_Capacitor_DoesNotAcceptDifferentPicofaradValue()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "KEMET - C0402C330F5GACTU - SMD Multilayer Ceramic Capacitor, 33 pF, 50 V, 0402 [1005 Metric], ± 1%, C0G / NP0" },
            new() { DisplayName = "MURATA - GRM1555C1H220JA01D - SMD Multilayer Ceramic Capacitor, 22 pF, 50 V, 0402 [1005 Metric], ± 5%, C0G / NP0" }
        };

        var selected = Element14Query.SelectExact(products, "SMT;SMD0402;22p;+5%;-5%;50V;C0G");

        Assert.Same(products[1], selected);
    }

    [Fact]
    public void SelectExact_Capacitor_NoMatchWhenOnlyDifferentValueExists()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "KEMET - C0402C104K3RACTU - SMD Multilayer Ceramic Capacitor, 0.1 µF, 25 V, 0402 [1005 Metric], ± 10%, X7R" }
        };

        Assert.Null(Element14Query.SelectExact(products, "SMT;SMD0402;470p;+10%;-10%;25V;X7R"));
    }

    [Fact]
    public void SelectExact_Capacitor_RejectsWrongPackage()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "KEMET - C0603C104K3RACTU - SMD Multilayer Ceramic Capacitor, 0.1 µF, 25 V, 0603 [1608 Metric], ± 10%, X7R" }
        };

        Assert.Null(Element14Query.SelectExact(products, "SMT;SMD0201;100n;+10%;-10%;25V;X7R"));
    }

    [Fact]
    public void SelectExact_Capacitor_AcceptsMetricLabelledPackage()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "MURATA - GRM033R71E104KE14D - SMD Multilayer Ceramic Capacitor, 0.1 µF, 25 V, 0603 Metric, ± 10%, X7R" }
        };

        Assert.Same(products[0], Element14Query.SelectExact(products, "SMT;SMD0201;100n;+10%;-10%;25V;X7R"));
    }

    // ---- client and mapper ----

    [Fact]
    public async Task Client_TriesSecondResistorWordingWhenFirstFindsNoMatch()
    {
        var handler = new QueueHandler(
            """{"keywordSearchReturn":{"currency":"MYR","products":[]}}""",
            """
            {"keywordSearchReturn":{"currency":"MYR","products":[{"sku":"1","displayName":"MULTICOMP PRO - MC0402WGF1693TCE - SMD Chip Resistor, 169 kohm, ± 1%, 62.5 mW, 0402 [1005 Metric], Thick Film","brandName":"MULTICOMP PRO","translatedManufacturerPartNumber":"MC0402WGF1693TCE","translatedMinimumOrderQuality":10,"packSize":1,"unitOfMeasure":"EACH","inv":500,"prices":[{"from":10,"to":99,"cost":0.02}],"rohsStatusCode":"YES"}]}}
            """);
        using var http = new HttpClient(handler);
        using var client = new Element14Client(http, "test-key", "my.element14.com");

        var result = await client.SearchAsync("SMT;SMD0402;169k;1%;63mW;TC10 0;--");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, handler.Queries.Count);
        Assert.Contains("term=any%3Aresistor%20169kohm%200402%201%25", handler.Queries[0]);
        Assert.Contains("term=any%3A169%20kohm%20resistor%200402", handler.Queries[1]);
        Assert.Single(result.Response!.Products);
        Assert.Equal("MC0402WGF1693TCE", result.Response.Products[0].ManufacturerProductNumber);
    }

    [Fact]
    public async Task Client_StopsAfterFirstWordingWhenItMatches()
    {
        var handler = new QueueHandler(
            """
            {"keywordSearchReturn":{"currency":"MYR","products":[{"sku":"1","displayName":"PANASONIC - ERJ2RKF1002X - SMD Chip Resistor, 10 kohm, ± 1%, 100 mW, 0402 [1005 Metric], Thick Film","brandName":"PANASONIC","translatedManufacturerPartNumber":"ERJ2RKF1002X","translatedMinimumOrderQuality":1,"packSize":1,"unitOfMeasure":"EACH","inv":500,"prices":[{"from":1,"to":99,"cost":0.02}],"rohsStatusCode":"YES"}]}}
            """);
        using var http = new HttpClient(handler);
        using var client = new Element14Client(http, "test-key", "my.element14.com");

        var result = await client.SearchAsync("SMT;SMD0402;10k;1%;63mW;--;--");

        Assert.Single(handler.Queries);
        Assert.Single(result.Response!.Products);
    }

    [Fact]
    public async Task Client_ReturnsHttpErrorWithoutRetrying()
    {
        var handler = new QueueHandler(HttpStatusCode.Forbidden, "<h1>Developer Inactive</h1>");
        using var http = new HttpClient(handler);
        using var client = new Element14Client(http, "test-key", "my.element14.com");

        var result = await client.SearchAsync("SMT;SMD0402;10k;1%;63mW;--;--");

        Assert.Equal(403, result.StatusCode);
        Assert.Null(result.Response);
        Assert.Single(handler.Queries);
    }

    [Fact]
    public void Mapper_NoSvhcAttributeMapsToReachUnaffected()
    {
        var product = new Element14Product
        {
            RohsStatusCode = "YES",
            Prices = [new Element14Price { From = 1, To = 9, Cost = 1 }],
            Attributes = [new Element14Attribute { AttributeLabel = "SVHC", AttributeValue = "No SVHC (27-Jun-2018)" }]
        };

        var mapped = Element14Mapper.ToDigiKeyProduct(product, "MYR");

        Assert.Equal("REACH Unaffected", mapped.Classifications!.ReachStatus);
        Assert.Equal("RoHS Compliant", mapped.Classifications.RohsStatus);
    }

    [Fact]
    public void Mapper_MissingSvhcStaysNotStated()
    {
        var mapped = Element14Mapper.ToDigiKeyProduct(new Element14Product { RohsStatusCode = "YES" }, "MYR");

        Assert.Equal("Not stated", mapped.Classifications!.ReachStatus);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<string> Queries { get; } = [];

        public QueueHandler(params string[] bodies)
        {
            foreach (var body in bodies) _responses.Enqueue((HttpStatusCode.OK, body));
        }

        public QueueHandler(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(request.RequestUri!.Query);
            var (status, body) = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
