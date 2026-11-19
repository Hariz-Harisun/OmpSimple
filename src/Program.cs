using System.Text.Json;
using OmpSimple.Models;

namespace OmpSimple;

class Program
{
    static async Task Main()
    {
        Directory.CreateDirectory("input");
        Directory.CreateDirectory("output");

        var inputFiles = Directory.GetFiles("input", "*.xlsx");
        if (inputFiles.Length == 0)
        {
            Console.WriteLine("No .xlsx files in input/");
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var client = new DigiKeyClient(http);

        var mpnCache = new Dictionary<string, DigiKeySearchResponse?>();
        bool rateLimited = false;
        bool authFailed = false;

        foreach (var file in inputFiles)
        {
            Console.WriteLine($"Processing: {file}");
            var inputRows = ExcelProcessor.ReadInput(file);
            Console.WriteLine($"  {inputRows.Count} rows read");

            var outputRows = new List<OutputRow>();
            int found = 0, notFound = 0, skipped = 0, digikeyCalls = 0;

            foreach (var row in inputRows)
            {
                var outputRow = new OutputRow { Input = row };
                string remarks = "";

                var (cleanedMpn, alternates) = MpnCleaner.Clean(row.Mpn);
                if (string.IsNullOrWhiteSpace(cleanedMpn) || cleanedMpn.StartsWith("SMT;", StringComparison.OrdinalIgnoreCase))
                {
                    remarks = "Skipped: not a real MPN";
                    skipped++;
                }
                else if (rateLimited)
                {
                    remarks = "Skipped: rate limit";
                    skipped++;
                }
                else if (authFailed)
                {
                    remarks = "Skipped: auth failed";
                    skipped++;
                }
                else
                {
                    DigiKeySearchResponse? searchResp;
                    int statusCode = 0;
                    if (mpnCache.TryGetValue(cleanedMpn, out var cached))
                    {
                        searchResp = cached;
                    }
                    else
                    {
                        var (resp, sc) = await client.SearchAsync(cleanedMpn);
                        searchResp = resp;
                        statusCode = sc;
                        mpnCache[cleanedMpn] = searchResp;
                        digikeyCalls++;

                        if (statusCode == 429)
                        {
                            rateLimited = true;
                            remarks = "Skipped: rate limit";
                            skipped++;
                        }
                        else if (statusCode == 401 || statusCode == 403)
                        {
                            authFailed = true;
                            remarks = "Skipped: auth failed";
                            skipped++;
                        }

                        await Task.Delay(600);
                    }

                    if (!rateLimited && !authFailed)
                    {
                        if (searchResp?.Products.Count > 0)
                        {
                            int qty = int.TryParse(row.Qty, out var q) ? q : 0;
                            var (offer, offerRemarks) = OfferSelector.SelectBest(searchResp.Products[0], qty, row.Mpn);
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
                                remarks = "No valid offer";
                                notFound++;
                            }
                        }
                        else
                        {
                            remarks = statusCode == 404 ? "Not found" : $"HTTP {statusCode}";
                            notFound++;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(alternates) && !remarks.Contains("Alternates:"))
                    remarks = (remarks + "; Alternates: " + alternates).TrimStart(';', ' ');

                outputRow.Remarks = remarks;
                outputRows.Add(outputRow);
            }

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

            // Verify both exist then delete input
            if (File.Exists(jsonPath) && File.Exists(xlsxPath))
            {
                File.Delete(file);
                Console.WriteLine($"  Input deleted: {file}");
            }
            else
            {
                Console.WriteLine($"  ERROR: Output files missing, input NOT deleted");
            }

            Console.WriteLine($"  Summary: found={found}, notFound={notFound}, skipped={skipped}, digikeyCalls={digikeyCalls}");
        }

        Console.WriteLine("Done.");
    }
}