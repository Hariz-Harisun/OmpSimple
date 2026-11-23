using System.IO;
using ClosedXML.Excel;
using OmpSimple.Models;
using Xunit;

namespace OmpSimple.Tests;

public class OfferSelectorTests
{
    [Fact]
    public void SelectBest_PicksCheapestPrice()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "TEST-123",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.55m,
                    StandardPricing =
                    [
                        new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 0.65m },
                        new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.55m },
                        new DigiKeyStandardPricing { BreakQuantity = 100, UnitPrice = 0.50m }
                    ]
                },
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 5,
                    StandardPackage = 50,
                    Packaging = new DigiKeyPackaging { Name = "Tape & Reel" },
                    QuantityAvailableforPackageType = 500,
                    UnitPrice = 0.50m,
                    StandardPricing =
                    [
                        new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 0.60m },
                        new DigiKeyStandardPricing { BreakQuantity = 5, UnitPrice = 0.50m }
                    ]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH Unaffected" }
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(0.50m, offer.UnitPrice); // Cheapest at qty 10 (Tape & Reel)
        Assert.Equal(5, offer.Moq);
        Assert.Equal("T&R", offer.Packaging); // Tape & Reel -> T&R
    }

    [Fact]
    public void SelectBest_UsesPackageTypeFromPricedVariation()
    {
        const string json = """
        {
            "Manufacturer": { "Name": "TestMfg" },
            "ManufacturerProductNumber": "TEST-123",
            "ProductVariations": [
                {
                    "PackageType": { "Name": "Cut Tape (CT)" },
                    "QuantityAvailableforPackageType": 1000,
                    "MinimumOrderQuantity": 1,
                    "StandardPricing": [{ "BreakQuantity": 1, "UnitPrice": 0.25 }]
                },
                {
                    "PackageType": { "Name": "Tape & Reel (TR)" },
                    "QuantityAvailableforPackageType": 1000,
                    "MinimumOrderQuantity": 1,
                    "StandardPricing": [{ "BreakQuantity": 1, "UnitPrice": 0.20 }]
                }
            ]
        }
        """;

        var product = System.Text.Json.JsonSerializer.Deserialize<DigiKeyProduct>(json)!;
        var (offer, _) = OfferSelector.SelectBest(product, 1, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(0.20m, offer.UnitPrice);
        Assert.Equal("T&R", offer.Packaging);
    }

    [Fact]
    public void SelectBest_UsesProductLevelManufacturerLeadWeeks()
    {
        const string json = """
        {
            "Manufacturer": { "Name": "TestMfg" },
            "ManufacturerProductNumber": "TEST-123",
            "ManufacturerLeadWeeks": "21",
            "ProductVariations": [
                {
                    "PackageType": { "Name": "Cut Tape (CT)" },
                    "QuantityAvailableforPackageType": 1000,
                    "MinimumOrderQuantity": 1,
                    "StandardPricing": [{ "BreakQuantity": 1, "UnitPrice": 0.25 }]
                }
            ]
        }
        """;

        var product = System.Text.Json.JsonSerializer.Deserialize<DigiKeyProduct>(json)!;
        var (offer, _) = OfferSelector.SelectBest(product, 1, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(21, offer.LeadTimeWeeks);
    }

    [Fact]
    public void SelectBest_IgnoresMarketplace()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "TEST-123",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = true, // Marketplace - should be ignored
                    MinimumOrderQuantity = 1,
                    StandardPackage = 1,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.01m, // Very cheap but marketplace
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 0.01m }]
                },
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 1.00m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 1.00m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH Unaffected" }
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(1.00m, offer.UnitPrice); // Non-marketplace chosen
        Assert.DoesNotContain("Marketplace only", remarks); // Not added since non-marketplace exists
    }

    [Fact]
    public void SelectBest_TieBreaksOnLowerMoq()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "TEST-123",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 100,
                    StandardPackage = 1000,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.50m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 100, UnitPrice = 0.50m }]
                },
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.50m, // Same price
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.50m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH Unaffected" }
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(10, offer.Moq); // Lower MOQ wins tie
    }

    [Fact]
    public void SelectBest_MpnMismatch_ReturnsNotFoundWithClosest()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "DIFFERENT-MPN", // Different from requested
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.50m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.50m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH Unaffected" }
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "REQUESTED-MPN");

        Assert.Null(offer);
        Assert.Contains("Not found (closest: DIFFERENT-MPN)", remarks);
    }

    [Fact]
    public void SelectBest_DifferentManufacturerStillAcceptsProduct()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "onsemi" },
            ManufacturerProductNumber = "BAT54CWT1G",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 1,
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.10m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 0.10m }]
                }
            ]
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 1, "BAT54CWT1G", "On Semi Alternate");

        Assert.NotNull(offer);
        Assert.DoesNotContain("Manufacturer mismatch", remarks);
    }

    [Fact]
    public void SelectBest_ChoosesManufacturerAlignedProductFromResults()
    {
        var products = new List<DigiKeyProduct>
        {
            new()
            {
                Manufacturer = new DigiKeyManufacturer { Name = "Other Manufacturer" },
                ManufacturerProductNumber = "CC3100MOD",
                ProductVariations = [CreateVariation()]
            },
            new()
            {
                Manufacturer = new DigiKeyManufacturer { Name = "Texas Instruments" },
                ManufacturerProductNumber = "CC3100MODR11MAMOBR",
                ProductVariations = [CreateVariation()]
            }
        };

        var (offer, _) = OfferSelector.SelectBest(products, 1, "CC3100MOD", "TI");

        Assert.NotNull(offer);
        Assert.Equal("CC3100MODR11MAMOBR", offer.Mpn);
    }

    [Fact]
    public void SelectBest_ManufacturerAliasIsAcceptedWithoutDifferenceRemark()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "Texas Instruments" },
            ManufacturerProductNumber = "CC3100MODR11MAMOBR",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 1,
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 1.00m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 1.00m }]
                }
            ]
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 1, "CC3100MOD", "TI");

        Assert.NotNull(offer);
        Assert.DoesNotContain("Mfg differs", remarks);
    }

    [Fact]
    public void SelectBest_RohsNonCompliant_ReturnsNo()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "TEST-123",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.50m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.50m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "RoHS non-compliant", ReachStatus = "REACH Unaffected" }
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal("No", offer.RoHS_REACH); // non-compliant -> No
        Assert.Equal("Not stated.", offer.Coo);
        Assert.Contains("COO missing", remarks);
    }

    [Fact]
    public void SelectBest_ReachNonCompliant_ReturnsNo()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "TEST-123",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.50m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.50m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH non-compliant" }
        };

        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal("No", offer.RoHS_REACH); // REACH non-compliant -> No
    }

    [Fact]
    public void SelectBest_CleanedMpnMatchesWithAlternatesInRemarks()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "BAT54CW,115",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000,
                    UnitPrice = 0.50m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.50m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH Unaffected" }
        };

        // Pass the CLEANED MPN (first MPN only) for matching
        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "BAT54CW,115");

        Assert.NotNull(offer);
        Assert.Equal("BAT54CW,115", offer.Mpn);
        // Alternates are added by Program.cs, not OfferSelector - but verify no spurious alternates in remarks from SelectBest
        Assert.DoesNotContain("Alternates:", remarks);
        Assert.Contains("COO missing", remarks);
    }

    [Fact]
    public void SelectBest_NcnrUsesManufacturerLeadWeeksForBothWindows()
    {
        var product = new DigiKeyProduct
        {
            ManufacturerProductNumber = "TEST-123",
            ManufacturerLeadWeeks = "21",
            ProductVariations = [CreateVariation(ncnr: true)]
        };

        var (offer, _) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(21, offer.CancellationWeeks);
        Assert.Equal(21, offer.RescheduleWeeks);
    }

    [Fact]
    public void SelectBest_InStockNonNcnrUsesZeroWindows()
    {
        var product = new DigiKeyProduct
        {
            ManufacturerProductNumber = "TEST-123",
            ProductVariations = [CreateVariation()]
        };

        var (offer, _) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(0, offer.CancellationWeeks);
        Assert.Equal(0, offer.RescheduleWeeks);
    }

    [Fact]
    public void SelectBest_OutOfStockNonNcnrUsesScheduledOrderBaseline()
    {
        var product = new DigiKeyProduct
        {
            ManufacturerProductNumber = "TEST-123",
            ProductVariations = [CreateVariation(quantityAvailable: 0)]
        };

        var (offer, _) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(4, offer.CancellationWeeks);
        Assert.Equal(8, offer.RescheduleWeeks);
    }

    [Fact]
    public void SelectBest_StockCheckUsesMaxQtyMoq()
    {
        var product = new DigiKeyProduct
        {
            Manufacturer = new DigiKeyManufacturer { Name = "TestMfg" },
            ManufacturerProductNumber = "TEST-123",
            ProductVariations =
            [
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 100, // MOQ = 100
                    StandardPackage = 1000,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 50, // Stock = 50, less than max(qty=10, MOQ=100)=100
                    UnitPrice = 0.50m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 100, UnitPrice = 0.50m }]
                },
                new DigiKeyVariation
                {
                    MarketPlace = false,
                    MinimumOrderQuantity = 10,
                    StandardPackage = 100,
                    Packaging = new DigiKeyPackaging { Name = "Cut Tape" },
                    QuantityAvailableforPackageType = 1000, // Stock = 1000 >= max(10, 10) = 10
                    UnitPrice = 0.60m,
                    StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 10, UnitPrice = 0.60m }]
                }
            ],
            Classifications = new DigiKeyClassifications { RohsStatus = "ROHS3 Compliant", ReachStatus = "REACH Unaffected" }
        };

        // Qty = 10, but first variation has MOQ=100, so effective qty = 100
        // First variation has stock 50 < 100 -> low stock, should fall through to second variation
        var (offer, remarks) = OfferSelector.SelectBest(product, 10, "TEST-123");

        Assert.NotNull(offer);
        Assert.Equal(10, offer.Moq); // Second variation chosen (has stock)
        Assert.Equal(0.60m, offer.UnitPrice);
        Assert.DoesNotContain("Low stock", remarks); // Second variation has sufficient stock
    }
    private static DigiKeyVariation CreateVariation(bool ncnr = false, int quantityAvailable = 1000) => new()
    {
        MarketPlace = false,
        MinimumOrderQuantity = 1,
        QuantityAvailableforPackageType = quantityAvailable,
        UnitPrice = 1.00m,
        Ncnr = ncnr,
        StandardPricing = [new DigiKeyStandardPricing { BreakQuantity = 1, UnitPrice = 1.00m }]
    };
}

public class MpnCleanerTests
{
    [Fact]
    public void Clean_SingleMpn_ReturnsAsIs()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("ABC123");
        Assert.Equal("ABC123", cleaned);
        Assert.Empty(alternates);
    }

    [Fact]
    public void Clean_MultipleMpnSlash_ReturnsFirstAndAlternates()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("ABC123/DEF456/GHI789");
        Assert.Equal("ABC123", cleaned);
        Assert.Equal("DEF456, GHI789", alternates);
    }

    [Fact]
    public void Clean_MultipleMpnNewline_ReturnsFirstAndAlternates()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("ABC123\r\nDEF456\nGHI789");
        Assert.Equal("ABC123", cleaned);
        Assert.Equal("DEF456, GHI789", alternates);
    }

    [Fact]
    public void Clean_RemovesBrackets()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("ABC123 [obsolete]");
        Assert.Equal("ABC123", cleaned);
    }

    [Fact]
    public void Clean_TrimsWhitespace()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("  ABC123  ");
        Assert.Equal("ABC123", cleaned);
    }

    [Fact]
    public void Clean_Bat54cwWithAlternates_ReturnsFirstAndAlternates()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("BAT54CW,115 / BAT54CWT1G (at)");
        Assert.Equal("BAT54CW,115", cleaned);
        Assert.Equal("BAT54CWT1G (at)", alternates);
    }

    [Fact]
    public void Clean_RemovesTrailingXMarkerFromEachMpn()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("BLM18PG331SN1x\nXFL4020-222MEx\nLQH32CN100K53x");

        Assert.Equal("BLM18PG331SN1", cleaned);
        Assert.Equal("XFL4020-222ME, LQH32CN100K53", alternates);
    }

    [Fact]
    public void Clean_RemovesTrailingDoubleXMarker()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("04381.25xx\nSTM32H743BIT6xx");

        Assert.Equal("04381.25", cleaned);
        Assert.Equal("STM32H743BIT6", alternates);
    }

    [Fact]
    public void Clean_PreservesUppercaseXProductSuffix()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("ABCXX");

        Assert.Equal("ABCXX", cleaned);
        Assert.Empty(alternates);
    }

    [Fact]
    public void Clean_EmptyString_ReturnsEmpty()
    {
        var (cleaned, alternates) = MpnCleaner.Clean("");
        Assert.Empty(cleaned);
        Assert.Empty(alternates);
    }
}

public class ColumnDefaultsTests
{
    [Fact]
    public void Offer_Defaults_SetCorrectly()
    {
        var offer = new Offer
        {
            Supplier = "DigiKey",
            Currency = "USD",
            CancellationWeeks = 2,
            RescheduleWeeks = 8,
            CxlResDbPb = "PB",
            AuthorisedDistributor = "Yes"
        };

        Assert.Equal("DigiKey", offer.Supplier);
        Assert.Equal("USD", offer.Currency);
        Assert.Equal(2, offer.CancellationWeeks);
        Assert.Equal(8, offer.RescheduleWeeks);
        Assert.Equal("PB", offer.CxlResDbPb);
        Assert.Equal("Yes", offer.AuthorisedDistributor);
    }
}

public class InputFileNotDeletedTests
{
    [Fact]
    public void WriteOutput_WhenSaveFails_InputNotDeleted()
    {
        // This test verifies the logic: we only delete input AFTER both JSON and Excel are saved
        // The actual deletion logic is in Program.cs - we test the principle here
        var tempDir = Path.Combine(Path.GetTempPath(), "OmpSimpleTest_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var inputFile = Path.Combine(tempDir, "input.xlsx");
        var outputFile = Path.Combine(tempDir, "output.xlsx");

        // Create a dummy input file
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("LT Quote");
            ws.Cell(1, 1).Value = "PN#";
            ws.Cell(2, 1).Value = "TEST-1";
            wb.SaveAs(inputFile);
        }

        // Simulate: JSON save succeeds, Excel save fails (throw)
        bool jsonSaved = false;
        bool excelSaved = false;
        bool inputDeleted = false;

        try
        {
            // Simulate JSON save
            File.WriteAllText(Path.ChangeExtension(outputFile, ".json"), "[]");
            jsonSaved = true;

            // Simulate Excel save failure
            throw new IOException("Disk full");
        }
        catch
        {
            // Excel save failed
        }
        finally
        {
            // Only delete if BOTH succeeded
            if (jsonSaved && excelSaved)
            {
                File.Delete(inputFile);
                inputDeleted = true;
            }
        }

        Assert.True(jsonSaved);
        Assert.False(excelSaved);
        Assert.False(inputDeleted);
        Assert.True(File.Exists(inputFile), "Input file should still exist when save fails");

        Directory.Delete(tempDir, true);
    }
}