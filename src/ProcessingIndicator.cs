using System.Diagnostics;

namespace OmpSimple;

internal sealed class ProcessingIndicator : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _worker;
    private readonly bool _enabled;
    private int _lastWidth;
    private int _frame;
    private bool _stopped;

    private ProcessingIndicator()
    {
        _enabled = !Console.IsOutputRedirected;
        if (!_enabled)
        {
            _worker = Task.CompletedTask;
            return;
        }

        WriteStatus();
        _worker = RunAsync();
    }

    public static ProcessingIndicator Start() => new();

    public TimeSpan Elapsed => _clock.Elapsed;

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, _cancellation.Token);
                _frame++;
                WriteStatus();
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    private void WriteStatus()
    {
        var dots = new string('.', _frame % 4).PadRight(3);
        var status = $"Processing{dots} (time running {_clock.Elapsed.TotalSeconds:0.00} seconds)";
        var padding = new string(' ', Math.Max(0, _lastWidth - status.Length));
        Console.Write($"\r{status}{padding}");
        _lastWidth = status.Length;
    }

    public void Dispose()
    {
        if (_stopped)
            return;

        _stopped = true;
        _cancellation.Cancel();
        _worker.GetAwaiter().GetResult();

        if (_enabled)
            Console.Write($"\r{new string(' ', _lastWidth)}\r");

        _cancellation.Dispose();
    }
}
