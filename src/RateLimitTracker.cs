using System.Text.Json;

namespace OmpSimple;

public sealed record DistributorRateInfo(
    int PerMinuteLimit,
    int DailyLimit,
    int BatchSize,
    int CallsUsed);

public sealed class DistributorRateTracker
{
    private const int DisplayBarWidth = 20;
    private readonly string _statePath;
    private readonly Dictionary<string, RateState> _states;

    private DistributorRateTracker(string statePath, Dictionary<string, RateState> states)
    {
        _statePath = statePath;
        _states = states;
    }

    public static DistributorRateTracker CreateDefault(string? statePath = null)
    {
        statePath ??= Path.Combine(Path.GetTempPath(), "OmpSimple-rate-limits.json");
        var states = Load(statePath);

        AddDefaultIfMissing(states, "DigiKey", perMinuteLimit: 30, dailyLimit: 1000, batchSize: 50);
        AddDefaultIfMissing(states, "Mouser", perMinuteLimit: 30, dailyLimit: 1000, batchSize: 50);
        return new DistributorRateTracker(statePath, states);
    }

    public DistributorRateInfo GetInfo(string distributor)
    {
        var state = GetState(distributor);
        ResetIfNewDay(state);
        return new DistributorRateInfo(state.PerMinuteLimit, state.DailyLimit, state.BatchSize, state.CallsUsed);
    }

    public string GetRemainingDisplay(string distributor)
    {
        var state = GetState(distributor);
        ResetIfNewDay(state);
        var remaining = Math.Max(0, state.DailyLimit - state.CallsUsed);
        var percentage = state.DailyLimit == 0 ? 0 : remaining * 100d / state.DailyLimit;
        var filled = (int)Math.Round(percentage / 100d * DisplayBarWidth, MidpointRounding.AwayFromZero);
        var bar = new string('#', filled) + new string('-', DisplayBarWidth - filled);
        return $" [{bar}] {percentage:0.0}% ({remaining} / {state.DailyLimit})";
    }

    public void RecordCalls(string distributor, int calls)
    {
        if (calls < 0)
            throw new ArgumentOutOfRangeException(nameof(calls));

        var state = GetState(distributor);
        ResetIfNewDay(state);
        state.CallsUsed = Math.Min(state.DailyLimit, state.CallsUsed + calls);
        Save();
    }

    private RateState GetState(string distributor)
    {
        if (!_states.TryGetValue(distributor, out var state))
            throw new ArgumentException($"Unknown distributor: {distributor}", nameof(distributor));
        return state;
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(_statePath, JsonSerializer.Serialize(_states, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, RateState> Load(string statePath)
    {
        if (!File.Exists(statePath))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, RateState>>(File.ReadAllText(statePath))
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AddDefaultIfMissing(
        Dictionary<string, RateState> states,
        string distributor,
        int perMinuteLimit,
        int dailyLimit,
        int batchSize)
    {
        if (!states.ContainsKey(distributor))
        {
            states[distributor] = new RateState
            {
                PerMinuteLimit = perMinuteLimit,
                DailyLimit = dailyLimit,
                BatchSize = batchSize
            };
        }
    }

    private static void ResetIfNewDay(RateState state)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (state.DateKey == today)
            return;

        state.DateKey = today;
        state.CallsUsed = 0;
    }

    private sealed class RateState
    {
        public int PerMinuteLimit { get; set; }
        public int DailyLimit { get; set; }
        public int BatchSize { get; set; }
        public int CallsUsed { get; set; }
        public string DateKey { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
    }
}
