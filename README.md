# OmpSimple

Simple .NET 10 console app that processes Excel files from `input/`, queries a selected distributor API (DigiKey, Mouser, or Element14) for each MPN, and writes enriched results to `output/` as JSON and Excel.

## Setup

1. Create `C:\Users\hariz\omp-digikey.txt` with your credentials:
   ```
   ClientId=your_client_id
   ClientSecret=your_client_secret
   AccountId=your_account_id
   MouserApiKey=your_mouser_api_key
   Element14ApiKey=your_element14_api_key
   Element14StoreId=my.element14.com
   ```
   (AccountId, MouserApiKey, and Element14ApiKey can be empty when unused.)

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
| 15 | Cancellation (WKS) | DigiKey logic; Mouser `0` |
| 16 | Reschedule (WKS) | DigiKey logic; Mouser `0` |
| 17 | CXL & RES DB/PB | DigiKey `PB`; Mouser `N/A` |
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
- At startup, choose `1` for DigiKey, `2` for Mouser, `3` for Element14, or `4` to quit; each option shows remaining daily quota
- Rate state persists in the system temp file `OmpSimple-rate-limits.json`; it resets daily
- Mouser defaults: 30 calls/minute, 1,000 calls/day, 50 results per batch; the client waits and retries automatically when the minute limit is reached
- Element14 defaults: 2 calls/second, 1,000 calls/day, 10 results per request; store region comes from `Element14StoreId`
- API failure logs: `logs/api-failures-<timestamp>.log` (keys excluded)
- MPN cleaning: first MPN used, alternates in Remarks; brackets removed; Element14 `SMT;...` specifications use keyword search