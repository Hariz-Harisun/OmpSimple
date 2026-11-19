using ClosedXML.Excel;
using OmpSimple.Models;

namespace OmpSimple;

public static class ExcelProcessor
{
    public static List<InputRow> ReadInput(string filePath)
    {
        var rows = new List<InputRow>();
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name == "LT Quote") ?? wb.Worksheets.First();
        var firstRow = ws.FirstRowUsed();
        if (firstRow == null) return rows;

        int headerRow = firstRow.RowNumber();
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow;

        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            var row = ws.Row(r);
            if (row.IsEmpty()) continue;

            var input = new InputRow
            {
                RowNumber = r,
                PnNumber = row.Cell(1).GetString(),
                Description = row.Cell(2).GetString(),
                Mfg = row.Cell(3).GetString(),
                Mpn = row.Cell(4).GetString(),
                Qty = row.Cell(5).GetString(),
                Eau = row.Cell(6).GetString()
            };
            rows.Add(input);
        }
        return rows;
    }

    public static void WriteOutput(string inputFilePath, string outputFilePath, List<OutputRow> outputRows)
    {
        File.Copy(inputFilePath, outputFilePath, overwrite: true);
        using var wb = new XLWorkbook(outputFilePath);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name == "LT Quote") ?? wb.Worksheets.First();

        var dropdownCols = new[] { 19, 20, 21 }; // S, T, U (1-indexed)
        var dropdownsBefore = new HashSet<int>();
        foreach (int col in dropdownCols)
        {
            var dv = ws.DataValidations.FirstOrDefault(d => d.Ranges.Any(rng => rng.RangeAddress.FirstAddress.ColumnNumber == col));
            if (dv != null)
                dropdownsBefore.Add(col);
        }

        foreach (var row in outputRows)
        {
            int r = row.Input.RowNumber;
            var offer = row.ChosenOffer;
            if (offer == null) continue;

            ws.Cell(r, 7).Value = offer.Supplier;           // G
            ws.Cell(r, 8).Value = offer.Mfg;                // H
            ws.Cell(r, 9).Value = offer.Mpn;                // I
            ws.Cell(r, 10).Value = offer.Currency;          // J
            ws.Cell(r, 11).Value = offer.UnitPrice;         // K
            if (offer.Spq.HasValue) ws.Cell(r, 12).Value = offer.Spq.Value; // L
            ws.Cell(r, 13).Value = offer.Moq;               // M
            if (offer.LeadTimeWeeks.HasValue) ws.Cell(r, 14).Value = offer.LeadTimeWeeks.Value; // N
            ws.Cell(r, 15).Value = offer.CancellationWeeks; // O
            ws.Cell(r, 16).Value = offer.RescheduleWeeks;   // P
            ws.Cell(r, 17).Value = offer.CxlResDbPb;        // Q
            ws.Cell(r, 18).Value = offer.Packaging;         // R
            ws.Cell(r, 19).Value = offer.AuthorisedDistributor; // S
            ws.Cell(r, 20).Value = offer.RoHS_REACH;        // T
            ws.Cell(r, 21).Value = offer.Coo;               // U
            ws.Cell(r, 22).Value = row.Remarks;             // V
        }

        wb.Save();

        // Verify dropdowns still exist
        using var wbCheck = new XLWorkbook(outputFilePath);
        var wsCheck = wbCheck.Worksheets.FirstOrDefault(w => w.Name == "LT Quote") ?? wbCheck.Worksheets.First();
        foreach (int col in dropdownCols)
        {
            var dv = wsCheck.DataValidations.FirstOrDefault(d => d.Ranges.Any(rng => rng.RangeAddress.FirstAddress.ColumnNumber == col));
            if (!dropdownsBefore.Contains(col) || dv == null)
                Console.WriteLine($"WARNING: Dropdown lost on column {col} in output file");
        }
    }
}