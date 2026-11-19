# OmpSimple

Simple .NET 10 console app that processes Excel files from `input/`, queries DigiKey API for each MPN, and writes enriched results to `output/` as JSON and Excel.

## Setup

1. Create `C:\Users\hariz\omp-digikey.txt` with your credentials:
   ```
   ClientId=your_client_id
   ClientSecret=your_client_secret
   AccountId=your_account_id
   ```
   (AccountId can be empty)

2. Copy `src/Credentials.example.cs` to `src/Credentials.cs` (already in .gitignore — never commit real keys).

3. Place `.xlsx` files in `input/` folder.

## Run

```bash
dotnet run
```

## Output

For each input file `foo.xlsx`:
- `output/foo_<timestamp>.json` — all rows with input, chosen offer, remarks
- `output/foo_<timestamp>.xlsx` — original workbook with columns G–V filled in
- Input file deleted **only after both outputs saved successfully**

## Columns Filled (G–V)

| Col | Field | Source |
|-----|-------|--------|
| 7 | Supplier | "DigiKey" |
| 8 | MFG | `Products[0].Manufacturer.Name` |
| 9 | MPN | `Products[0].ManufacturerProductNumber` |
| 10 | Currency | "USD" |
| 11 | Unit Price | Cheapest variation's effective price |
| 12 | SPQ | `StandardPackage` (or MOQ if Tape & Reel) |
| 13 | MOQ | Variation's `MinimumOrderQuantity` |
| 14 | Lead Time (WKS) | Parsed from `ManufacturerLeadWeeks` |
| 15 | Cancellation (WKS) | 2 (default) |
| 16 | Reschedule (WKS) | 8 (default) |
| 17 | CXL & RES DB/PB | "PB" (default) |
| 18 | Packaging | Mapped from DigiKey name |
| 19 | Authorised Distributor | "Yes" |
| 20 | RoHS/REACH | "Yes" if both compliant |
| 21 | COO | From `CountryOfOrigin` if present |
| 22 | Remarks | Aggregated flags |

## Tests

```bash
dotnet test
```

## Notes

- `input/` and `output/` are in .gitignore
- `src/Credentials.cs` is in .gitignore; commit only `src/Credentials.example.cs`
- Rate limit: ~600ms between DigiKey calls; on HTTP 429 remaining rows marked "Skipped: rate limit"
- MPN cleaning: first MPN used, alternates in Remarks; brackets removed; "SMT;" rows skipped