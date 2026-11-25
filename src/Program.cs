using System.Diagnostics;
using System.Text.Json;
using OmpSimple.Models;

namespace OmpSimple;

class Program
{
    private const string Banner = """
      
      
        ███████    ██████   ██████ ███████████      █████████  █████ █████  █████████  ███████████ ██████████ ██████   ██████
      ███░░░░░███ ░░██████ ██████ ░░███░░░░░███    ███░░░░░███░░███ ░░███  ███░░░░░███░█░░░███░░░█░░███░░░░░█░░██████ ██████ 
     ███     ░░███ ░███░█████░███  ░███    ░███   ░███    ░░░  ░░███ ███  ░███    ░░░ ░   ░███  ░  ░███  █ ░  ░███░█████░███ 
    ░███      ░███ ░███░░███ ░███  ░██████████    ░░█████████   ░░█████   ░░█████████     ░███     ░██████    ░███░░███ ░███ 
    ░███      ░███ ░███ ░░░  ░███  ░███░░░░░░      ░░░░░░░░███   ░░███     ░░░░░░░░███    ░███     ░███░░█    ░███ ░░░  ░███ 
    ░░███     ███  ░███      ░███  ░███            ███    ░███    ░███     ███    ░███    ░███     ░███ ░   █ ░███      ░███ 
     ░░░███████░   █████     █████ █████          ░░█████████     █████   ░░█████████     █████    ██████████ █████     █████
       ░░░░░░░    ░░░░░     ░░░░░ ░░░░░            ░░░░░░░░░     ░░░░░     ░░░░░░░░░     ░░░░░    ░░░░░░░░░░ ░░░░░     ░░░░
    BY BIZSMART TECHNOLOGY SDN. BHD.
      
    
      
    """;

    static async Task Main()
    {
        Console.WriteLine(Banner);
        Directory.CreateDirectory("input");
        Directory.CreateDirectory("output");
        Directory.CreateDirectory("logs");

        var inputFiles = Directory.GetFiles("input", "*.xlsx");
        if (inputFiles.Length == 0)
        {
            Console.WriteLine("No .xlsx files in input/");
            return;
        }

        var rateTracker = DistributorRateTracker.CreateDefault();
        var distributor = PromptForDistributor(rateTracker);
        if (distributor == null) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var distributorClient = DistributorFactory.Create(distributor, http, rateTracker);
        var supplierName = distributorClient.Name;
        using var client = distributorClient.Client;

        var mpnCache = new Dictionary<string, SupplierSearchResult>();
        bool rateLimited = false;
        bool authFailed = false;
        bool anyRateLimitOrAuthFailure = false;
        string? lastRateLimitLimit = null;
        string? lastRateLimitRemaining = null;
        var failureLogPath = Path.Combine("logs", $"api-failures-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        async Task LogApiFailureAsync(string mpn, SupplierSearchResult result)
        {
            var entry = $"[{DateTime.Now:O}] supplier={supplierName}, mpn={mpn}, status={result.StatusCode}, " +
                        $"rateLimit={result.RateLimitLimit ?? "N/A"}, remaining={result.RateLimitRemaining ?? "N/A"}" +
                        Environment.NewLine + $"response={result.ResponseBody ?? "<empty>"}" + Environment.NewLine + Environment.NewLine;
            await File.AppendAllTextAsync(failureLogPath, entry);
        }

        foreach (var file in inputFiles)
        {
            Console.WriteLine($"Processing: {file}");
            var inputRows = ExcelProcessor.ReadInput(file);
            Console.WriteLine($"  {inputRows.Count} rows read");

            var outputRows = new List<OutputRow>();
            int found = 0, notFound = 0, skipped = 0, apiCalls = 0;
            var processingClock = Stopwatch.StartNew();
            var processing = ProcessingIndicator.Start();

            try
            {
                foreach (var row in inputRows)
                {
                var outputRow = new OutputRow { Input = row };
                string remarks = "";

                var (cleanedMpn, alternates) = MpnCleaner.Clean(row.Mpn);
                if (string.IsNullOrWhiteSpace(cleanedMpn) ||
                    (supplierName is not ("DigiKey" or "Mouser" or "Element14") && cleanedMpn.StartsWith("SMT;", StringComparison.OrdinalIgnoreCase)))
                {
                    remarks = "Skipped: not a real MPN";
                    skipped++;
                }
                else if (rateLimited)
                {
                    remarks = "Skipped: rate limit";
                    skipped++;
                    anyRateLimitOrAuthFailure = true;
                }
                else if (authFailed)
                {
                    remarks = "Skipped: auth failed";
                    skipped++;
                    anyRateLimitOrAuthFailure = true;
                }
                else
                {
                    SupplierSearchResult result;
                    if (mpnCache.TryGetValue(cleanedMpn, out var cached))
                    {
                        result = cached;
                    }
                    else
                    {
                        result = await client.SearchAsync(cleanedMpn);
                        mpnCache[cleanedMpn] = result;
                        apiCalls++;

                        lastRateLimitLimit = result.RateLimitLimit;
                        lastRateLimitRemaining = result.RateLimitRemaining;

                        var isRateLimit = result.StatusCode == 429 ||
                            (supplierName == "Mouser" && MouserClient.IsRateLimitResponse(result.StatusCode, result.ResponseBody));
                        if (isRateLimit)
                        {
                            await LogApiFailureAsync(cleanedMpn, result);
                            rateLimited = true;
                            remarks = "Skipped: rate limit";
                            skipped++;
                            anyRateLimitOrAuthFailure = true;
                        }
                        else if (result.StatusCode == 401 || result.StatusCode == 403)
                        {
                            await LogApiFailureAsync(cleanedMpn, result);
                            authFailed = true;
                            remarks = "Skipped: auth failed";
                            skipped++;
                            anyRateLimitOrAuthFailure = true;
                        }

                        await Task.Delay(600);
                    }

                    if (!rateLimited && !authFailed)
                    {
                        if (result.Response?.Products.Count > 0)
                        {
                            int qty = int.TryParse(row.Qty, out var q) ? q : 0;
                            var (offer, offerRemarks) = OfferSelector.SelectBest(result.Response!.Products, qty, cleanedMpn, row.Mfg, supplierName);
                            if (offer != null)
                            {
                                outputRow.ChosenOffer = offer;
                                remarks = offerRemarks;
                                if (!string.IsNullOrEmpty(alternates))
                                    remarks = (remarks + "; " + "Alternates: " + alternates).TrimStart(';', ' ');
                                found++;
                            }
                            else
                            {
                                remarks = offerRemarks; // Contains "Not found (closest: ...)" or "No valid offer"
                                notFound++;
                            }
                        }
                        else
                        {
                            remarks = result.StatusCode is 200 or 404 ? "Not found" : $"HTTP {result.StatusCode}";
                            notFound++;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(alternates) && !remarks.Contains("Alternates:"))
                    remarks = (remarks + "; Alternates: " + alternates).TrimStart(';', ' ');

                outputRow.Remarks = remarks;
                outputRows.Add(outputRow);
                }

            processing.Dispose();

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var baseName = Path.GetFileNameWithoutExtension(file);
            var jsonPath = Path.Combine("output", $"{baseName}_{timestamp}.json");
            var xlsxPath = Path.Combine("output", $"{baseName}_{timestamp}.xlsx");

            // Save JSON first
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(outputRows, jsonOptions));
            Console.WriteLine($"  JSON saved: {jsonPath}");

            // Save Excel
            ExcelProcessor.WriteOutput(file, xlsxPath, outputRows);
            Console.WriteLine($"  Excel saved: {xlsxPath}");

            // Verify both exist then delete input (only if no rate limit/auth failure)
            if (File.Exists(jsonPath) && File.Exists(xlsxPath))
            {
                if (anyRateLimitOrAuthFailure)
                {
                    Console.WriteLine($"  Input NOT deleted (rate limit or auth failure occurred)");
                    Console.WriteLine($"  API failure details: {failureLogPath}");
                }
                else
                {
                    File.Delete(file);
                    Console.WriteLine($"  Input deleted: {file}");
                }
            }
            else
            {
                Console.WriteLine($"  ERROR: Output files missing, input NOT deleted");
            }

            Console.WriteLine($"  Summary: supplier={supplierName}, found={found}, notFound={notFound}, skipped={skipped}, apiCalls={apiCalls}");
            if (supplierName != "DigiKey" &&
                (!string.IsNullOrEmpty(lastRateLimitLimit) || !string.IsNullOrEmpty(lastRateLimitRemaining)))
            {
                Console.WriteLine($"  X-RateLimit-Limit: {lastRateLimitLimit ?? "N/A"}");
                Console.WriteLine($"  X-RateLimit-Remaining: {lastRateLimitRemaining ?? "N/A"}");
            }

            var elapsed = processingClock.Elapsed;
            Console.WriteLine($"  Time processed: {elapsed.TotalSeconds:0.00} seconds");
            }
            finally
            {
                processing.Dispose();
            }
        }

        Console.WriteLine("Done.");
    }

    private static string? PromptForDistributor(DistributorRateTracker rateTracker)
    {
        Console.WriteLine("Select distributor API:");
        Console.WriteLine($"  1. DigiKey {rateTracker.GetRemainingDisplay("DigiKey")}");
        Console.WriteLine($"  2. Mouser {rateTracker.GetRemainingDisplay("Mouser")}");
        Console.WriteLine($"  3. Element14 {rateTracker.GetRemainingDisplay("Element14")}");
        Console.WriteLine("  4. Quit");
        Console.Write("Choice [1/2/3/4]: ");
        var choice = Console.ReadLine()?.Trim();
        if (choice == "4")
        {
            Console.WriteLine("Run stopped.");
            return null;
        }

        if (choice is not ("1" or "2" or "3"))
        {
            Console.WriteLine("Invalid choice. Exiting.");
            return null;
        }

        if (choice == "2" && string.IsNullOrWhiteSpace(Credentials.MouserApiKey))
        {
            Console.WriteLine("MouserApiKey missing in C:\\Users\\hariz\\omp-digikey.txt");
            return null;
        }

        if (choice == "3" && string.IsNullOrWhiteSpace(Credentials.Element14ApiKey))
        {
            Console.WriteLine("Element14ApiKey missing in C:\\Users\\hariz\\omp-digikey.txt");
            return null;
        }

        return choice;
    }
}