using OmpSimple.Models;

namespace OmpSimple;

public static class LowestPriceSelector
{
    public static List<OutputRow> Select(IReadOnlyList<IReadOnlyList<OutputRow>> distributorRows)
    {
        var rowsByNumber = new SortedDictionary<int, List<OutputRow>>();
        foreach (var rows in distributorRows)
        {
            foreach (var row in rows)
            {
                if (!rowsByNumber.TryGetValue(row.Input.RowNumber, out var candidates))
                {
                    candidates = [];
                    rowsByNumber[row.Input.RowNumber] = candidates;
                }

                candidates.Add(row);
            }
        }

        var result = new List<OutputRow>();
        foreach (var candidates in rowsByNumber.Values)
        {
            var first = candidates[0];
            var offers = candidates
                .Where(row => row.ChosenOffer != null)
                .Select(row => row.ChosenOffer!)
                .ToList();

            if (offers.Count == 0)
            {
                result.Add(new OutputRow
                {
                    Input = first.Input,
                    Remarks = "No offer found"
                });
                continue;
            }

            var lowest = offers.MinBy(offer => offer.UnitPrice)!;
            result.Add(new OutputRow
            {
                Input = first.Input,
                ChosenOffer = lowest,
                Remarks = candidates.First(row => ReferenceEquals(row.ChosenOffer, lowest)).Remarks
            });
        }

        return result;
    }
}
