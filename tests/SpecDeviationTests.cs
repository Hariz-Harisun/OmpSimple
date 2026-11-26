using OmpSimple.Models;
using Xunit;

namespace OmpSimple.Tests;

public class SpecDeviationTests
{
    private const string X7R100n = "SMT;SMD0201;100n;+10%;-10%;25V;X7R";

    // ---- thermistors and other look-alikes are not resistors ----

    [Fact]
    public void SelectExact_Resistor_RejectsNtcThermistorWithSameValueSizeAndTolerance()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "MURATA - NCU18XH103E6SRB - NTC Thermistor, 10 kohm, 3380 K, 0603 [1608 Metric], ± 1%, NCU Series" }
        };

        Assert.Null(Element14Query.SelectExact(products, "SMT;SMD0603;10k;1%;100mW;TC100;--"));
    }

    [Fact]
    public void SelectExact_Resistor_SkipsThermistorAndTakesTheRealResistor()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "MURATA - NCU18XH103E6SRB - NTC Thermistor, 10 kohm, 3380 K, 0603 [1608 Metric], ± 1%, NCU Series" },
            new() { DisplayName = "VISHAY - CRCW060310K0FKEA - SMD Chip Resistor, 10 kohm, ± 1%, 100 mW, 0603 [1608 Metric], Thick Film" }
        };

        Assert.Same(products[1], Element14Query.SelectExact(products, "SMT;SMD0603;10k;1%;100mW;TC100;--"));
    }

    [Fact]
    public void SelectExact_Resistor_RejectsDigiKeyStyleThermistorButKeepsResistor()
    {
        var products = new List<DigiKeyProduct>
        {
            new() { ManufacturerProductNumber = "NCP18XH103F03RB", ProductDescription = "THERMISTOR NTC 10KOHM 3380K 0603" },
            new() { ManufacturerProductNumber = "RC0603FR-0710KL", ProductDescription = "RES 10K OHM 1% 1/10W 0603" }
        };

        Assert.Same(products[1], Element14Query.SelectExact(products, "SMT;SMD0603;10k;1%;100mW;TC100;--"));
    }

    [Fact]
    public void SelectExact_Resistor_RejectsResistorKitThatMentionsZeroOhm()
    {
        var products = new List<Element14Product>
        {
            new() { DisplayName = "YAGEO - RC0603FR-SNE96L - RES KIT, 0 OHM TO 10MOHM, 0.1W, 0603" },
            new() { DisplayName = "YAGEO - RC0603FR-070RL - SMD Chip Resistor, 0 ohm, ± 1%, 100 mW, 0603 [1608 Metric], Thick Film" }
        };

        Assert.Same(products[1], Element14Query.SelectExact(products, "SMT;SMD0603;0R;1%;100mW;TC100;--"));
        Assert.Null(Element14Query.SelectExact([products[0]], "SMT;SMD0603;0R;1%;100mW;TC100;--"));
    }

    // ---- capacitor deviations ----

    [Fact]
    public void SpecDeviations_ReportsDielectricThatDiffersFromRequest()
    {
        var name = "MURATA - GRM033C81E104KE14D - SMD Multilayer Ceramic Capacitor, 0.1 µF, 25 V, 0201 [0603 Metric], ± 10%, X6S, GRM Series";

        var deviations = Element14Query.SpecDeviations(name, X7R100n);

        Assert.Equal("dielectric X6S (wanted X7R)", string.Join("; ", deviations));
    }

    [Fact]
    public void SpecDeviations_ReportsToleranceAndVoltageMisses()
    {
        var name = "ACME - TEST1 - SMD Multilayer Ceramic Capacitor, 10 µF, 10 V, 0603 [1608 Metric], ± 20%, X7R";

        var deviations = Element14Query.SpecDeviations(name, "SMT;SMD0603;10u;+10%;-10%;16V;X7R");

        Assert.Equal("voltage 10V (wanted 16V); tolerance 20% (wanted 10%)", string.Join("; ", deviations));
    }

    [Fact]
    public void SpecDeviations_ToleranceCanBeLeftToTheCaller()
    {
        var name = "ACME - TEST1 - SMD Multilayer Ceramic Capacitor, 10 µF, 16 V, 0603 [1608 Metric], ± 20%, X7R";

        Assert.Empty(Element14Query.SpecDeviations(name, "SMT;SMD0603;10u;+10%;-10%;16V;X7R", includeTolerance: false));
    }

    [Fact]
    public void SpecDeviations_SaysWhenDielectricIsNotStated()
    {
        var name = "ACME - TEST2 - SMD Multilayer Ceramic Capacitor, 100 nF, 25 V, 0201 [0603 Metric], ± 10%";

        Assert.Equal("dielectric not stated (wanted X7R)", string.Join("; ", Element14Query.SpecDeviations(name, X7R100n)));
    }

    [Theory]
    [InlineData("TDK - C1005NP01H220J050BA - SMD Multilayer Ceramic Capacitor, 22 pF, 50 V, 0402 [1005 Metric], ± 5%, NP0", "SMT;SMD0402;22p;+5%;-5%;50V;C0G")]
    [InlineData("YAGEO - CC0603KRX7R9BB103 - SMD Multilayer Ceramic Capacitor, 0.01 µF, 50 V, 0603 [1608 Metric], ± 10%, X7R", "SMT;SMD0603;10n;+10%;-10%;50V;X7R")]
    public void SpecDeviations_EmptyWhenPartMeetsTheRequest(string name, string spec)
    {
        Assert.Empty(Element14Query.SpecDeviations(name, spec));
    }

    [Fact]
    public void SpecDeviations_NeverReportsForResistorsOrPlainMpns()
    {
        Assert.Empty(Element14Query.SpecDeviations("VISHAY - X - SMD Chip Resistor, 10 kohm, ± 5%, 0402", "SMT;SMD0402;10k;1%;63mW;--;--"));
        Assert.Empty(Element14Query.SpecDeviations("anything", "GRM155R61E102KA01D"));
    }

    // ---- the remark reaches the output row ----

    [Fact]
    public async Task Element14Search_ThenSelectBest_PutsDeviationInRemarks()
    {
        var body = """
            {"keywordSearchReturn":{"currency":"MYR","products":[{"sku":"1","displayName":"MURATA - GRM033C81E104KE14D - SMD Multilayer Ceramic Capacitor, 0.1 µF, 25 V, 0201 [0603 Metric], ± 10%, X6S, GRM Series","brandName":"MURATA","translatedManufacturerPartNumber":"GRM033C81E104KE14D","translatedMinimumOrderQuality":1,"packSize":1,"unitOfMeasure":"EACH","inv":5000,"prices":[{"from":1,"to":99,"cost":0.05}],"rohsStatusCode":"YES"}]}}
            """;
        using var http = new HttpClient(new FixedHandler(body));
        using var client = new Element14Client(http, "test-key", "my.element14.com");

        var result = await client.SearchAsync(X7R100n);
        var (offer, remarks) = OfferSelector.SelectBest(result.Response!.Products, 10, X7R100n, supplier: "Element14");

        Assert.NotNull(offer);
        Assert.Contains("Spec deviation: dielectric X6S (wanted X7R)", remarks);
    }

    [Fact]
    public void SelectBest_NoDeviationRemarkWhenPartMatches()
    {
        var product = new DigiKeyProduct
        {
            ExactKeywordMatch = true,
            ManufacturerProductNumber = "MC0603X225K100CT",
            ProductDescription = "2.2 µF, 10 V, 0603, ±10%, X5R",
            ProductVariations = [Variation()]
        };

        var (_, remarks) = OfferSelector.SelectBest(product, 1, "SMT;SMD0603;2u2;+10%;-10%;10V;X5R");

        Assert.DoesNotContain("Spec deviation", remarks);
    }

    [Fact]
    public void SelectBest_MouserKeepsItsOwnToleranceRemarkWithoutDuplicatingIt()
    {
        var product = new DigiKeyProduct
        {
            ExactKeywordMatch = true,
            ManufacturerProductNumber = "TEST-470N",
            ProductDescription = "470 nF, 16 V, 0402, ±20%, X5R",
            ProductVariations = [Variation()]
        };

        var (_, remarks) = OfferSelector.SelectBest(product, 1, "SMT;SMD0402;470n;+10%;-10%;16V;X5R", supplier: "Mouser");

        Assert.Contains("Alternate: 20% tolerance", remarks);
        Assert.DoesNotContain("Spec deviation", remarks);
    }

    // ---- wrapped specification cells ----

    [Theory]
    [InlineData("SMT;SMD0603;0R;1%;100mW;TC100\n;--")]
    [InlineData("SMT;SMD0603;0R;1%;100mW;TC100\r\n;--")]
    [InlineData("SMT;SMD0603;0R;1%;100mW;TC100 ;\n--")]
    public void Clean_WrappedSpecificationIsOneValueWithNoAlternates(string raw)
    {
        var (cleaned, alternates) = MpnCleaner.Clean(raw);

        Assert.Equal("SMT;SMD0603;0R;1%;100mW;TC100;--", cleaned);
        Assert.Empty(alternates);
        Assert.Equal(SpecKind.Resistor, Element14Query.Classify(cleaned));
    }

    [Fact]
    public void Clean_PlainMpnWithNewlineStillSplitsIntoAlternates()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("ABC123\nDEF456");

        Assert.Equal("ABC123", cleaned);
        Assert.Equal("DEF456", alternates);
    }

    private static DigiKeyVariation Variation() => new()
    {
        MinimumOrderQuantity = 1,
        QuantityAvailableforPackageType = 1000,
        UnitPrice = 1m,
        StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 1m }]
    };

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
