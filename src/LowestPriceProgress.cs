using System.Diagnostics;

namespace OmpSimple;

internal sealed class LowestPriceProgress : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly string[] _distributors;
    private readonly Stopwatch[] _clocks;
    private readonly string[] _lines;
    private readonly bool _enabled;
    private readonly int _lineWidth;
    private bool _stopped;

    private LowestPriceProgress(IReadOnlyList<string> distributors)
    {
        _distributors = distributors.ToArray();
        _clocks = _distributors.Select(_ => Stopwatch.StartNew()).ToArray();
        _lines = _distributors.Select((name, index) => FormatProcessing(name, _clocks[index].Elapsed)).ToArray();
        _enabled = !Console.IsOutputRedirected;
        _lineWidth = _lines.Max(line => line.Length);

        if (_enabled)
        {
            foreach (var line in _lines)
                Console.WriteLine(line);
            Render();
        }

        _worker = _enabled ? RunAsync() : Task.CompletedTask;
    }

    public static LowestPriceProgress Start(IReadOnlyList<string> distributors) => new(distributors);

    public void Finished(string distributor, int rowCount)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_distributors, distributor);
            if (index < 0)
                return;

            _clocks[index].Stop();
            _lines[index] = FormatFinished(distributor, rowCount, _clocks[index].Elapsed);
            if (_enabled)
                Render();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, _cancellation.Token);
                lock (_gate)
                {
                    for (var index = 0; index < _distributors.Length; index++)
                    {
                        if (_clocks[index].IsRunning)
                            _lines[index] = FormatProcessing(_distributors[index], _clocks[index].Elapsed);
                    }

                    Render();
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private void Render()
    {
        if (!_enabled)
            return;

        try
        {
            var width = Math.Max(_lineWidth, Console.WindowWidth - 1);
            Console.Write($"\u001b[{_lines.Length}A\r");
            foreach (var line in _lines)
                Console.Write($"\u001b[2K{line.PadRight(width)}\u001b[1B\r");
        }
        catch (IOException)
        {
            // The console can disappear while the API work continues.
        }
        catch (ArgumentOutOfRangeException)
        {
            // The console can resize between reading its dimensions and writing.
        }
    }

    private static string FormatProcessing(string distributor, TimeSpan elapsed) =>
        $"{distributor} Processing... (time running {elapsed.TotalSeconds:0.00} seconds)";

    private static string FormatFinished(string distributor, int rowCount, TimeSpan elapsed) =>
        $"{distributor} finished: {rowCount} rows ({elapsed.TotalSeconds:0.00} seconds)";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _cancellation.Cancel();
        }

        _worker.GetAwaiter().GetResult();
        if (_enabled)
        {
            lock (_gate)
                Render();
        }
        _cancellation.Dispose();
    }
}
