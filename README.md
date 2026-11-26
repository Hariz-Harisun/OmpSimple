# OmpSimple

.NET 10 console app that processes Excel quotation files from `input/`, queries DigiKey, Mouser, or Element14, and writes enriched results to `output/`. It also supports querying all three distributors in parallel and generating one workbook with the lowest valid price for each part.

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

Menu options:

```text
1. DigiKey Remaining
2. Mouser Remaining
3. Element14 Remaining
4. Lowest Price Selection
5. Quit
```

After selecting options 1–4:

1. The menu is cleared while the header remains visible.
2. Processing output is shown below the header.
3. The app displays `Press Enter to continue...` after processing.
4. Press Enter to return to a clean menu screen.

## Distributor Processing

Options 1–3 select the newest `.xlsx` file in `input/`, query the selected distributor, and create two files in `output/`:

- `foo_<timestamp>-<Distributor>.json`
- `foo_<timestamp>-<Distributor>.xlsx`

The Excel output preserves the input workbook and fills columns G–V. The input file is deleted only after both output files are saved successfully and no rate-limit or authentication failure occurred.

## Lowest Price Selection

Option 4:

- Selects the newest `.xlsx` file in `input/`.
- Queries DigiKey, Mouser, and Element14 in parallel.
- Selects the lowest valid unit-price offer for each input row.
- Writes one workbook to `output/`: `foo_lowest_price_<timestamp>.xlsx`.
- Preserves the input workbook format and existing content.
- Writes selected supplier, offer data, and remarks into columns G–V.
- Does not delete the input workbook.

The three distributor progress lines remain visible during processing. A distributor failure is reported, while results from the other distributors can still be used.

## Output

For distributor processing, each input file `foo.xlsx` produces JSON and Excel output. Lowest-price processing produces one Excel workbook containing the lowest valid offer per row.

- Output files are written to `output/`.
- Input workbook formatting and sheets are preserved.
- Input deletion occurs only after successful distributor output generation.
- Lowest-price selection does not delete the input workbook.

## Columns Filled (G–V)

| Col | Field | Description |
|-----|-------|-------------|
| 7 | Supplier | Distributor providing the selected offer |
| 8 | MFG | Manufacturer |
| 9 | MPN | Manufacturer part number |
| 10 | Currency | Offer currency |
| 11 | Unit Price | Selected unit price |
| 12 | SPQ | Standard package quantity when available |
| 13 | MOQ | Minimum order quantity |
| 14 | Lead Time (WKS) | Lead time in weeks when available |
| 15 | Cancellation (WKS) | Cancellation window |
| 16 | Reschedule (WKS) | Reschedule window |
| 17 | CXL & RES DB/PB | Cancellation/reschedule terms |
| 18 | Packaging | Package type |
| 19 | Authorised Distributor | Authorisation status |
| 20 | RoHS/REACH | Compliance status |
| 21 | COO | Country of origin |
| 22 | Remarks | Processing status, alternates, or failure details |

## Tests

```bash
dotnet test
```

## Notes

- `input/` and `output/` are in .gitignore
- `src/Credentials.cs` is in .gitignore; commit only `src/Credentials.example.cs`
- At startup, choose `1` for DigiKey, `2` for Mouser, `3` for Element14, `4` for lowest-price selection, or `5` to quit; each distributor option shows remaining daily quota
- Rate state persists in the system temp file `OmpSimple-rate-limits.json`; it resets daily
- Mouser defaults: 30 calls/minute, 1,000 calls/day, 50 results per batch; the client waits and retries automatically when the minute limit is reached
- Element14 defaults: 2 calls/second, 1,000 calls/day, 10 results per request; store region comes from `Element14StoreId`
- API failure logs: `logs/api-failures-<timestamp>.log` (keys excluded)
- MPN cleaning: first MPN used, alternates in Remarks; brackets removed; Element14 `SMT;...` specifications use keyword search