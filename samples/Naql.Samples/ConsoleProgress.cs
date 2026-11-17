namespace Naql.Samples;

/// <summary>
/// Shows the progress of a transfer as a single updating console line, or as a line every 25% when the output is
/// redirected. Each new error a part retries after is printed on its own line.
/// </summary>
public sealed class ConsoleProgress : IProgress<TransferProgress>, IDisposable
{
    private const int BarWidth = 30;

    private readonly object _lock = new();
    private Exception? _lastError;
    private int _lastQuarter = -1;
    private bool _rendered;

    public void Report(TransferProgress value)
    {
        lock (_lock)
        {
            if (value.State == TransferState.Retrying && value.LastError is { } error && error != _lastError)
            {
                _lastError = error;
                EndLine();
                Console.WriteLine($"  retrying after: {error.Message}");
            }

            if (Console.IsOutputRedirected)
            {
                var quarter = value.Fraction is double fraction ? (int)(fraction * 4) : -1;
                if (quarter == _lastQuarter && value.State != TransferState.Completed)
                {
                    return;
                }
                _lastQuarter = quarter;
                Console.WriteLine(Line(value));
            }
            else
            {
                Console.Write("\r" + Line(value).PadRight(Console.BufferWidth - 1));
                _rendered = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            EndLine();
        }
    }

    private static string Line(TransferProgress value)
    {
        var parts = value.TotalParts > 1 ? $"  {value.ActiveParts}/{value.TotalParts} parts" : "";
        if (value.Fraction is double fraction && value.Total is long total)
        {
            var filled = (int)(fraction * BarWidth);
            var bar = new string('#', filled) + new string('-', BarWidth - filled);
            var eta = value.Remaining is TimeSpan remaining ? $"  ETA {remaining:mm\\:ss}" : "";
            return $"[{bar}] {fraction * 100,5:0.0}%  {Format.Bytes(value.Transferred)} / {Format.Bytes(total)}  {Format.Bytes(value.CurrentRate)}/s{eta}{parts}";
        }
        return $"{Format.Bytes(value.Transferred)} received  {Format.Bytes(value.CurrentRate)}/s{parts}";
    }

    private void EndLine()
    {
        if (_rendered && !Console.IsOutputRedirected)
        {
            Console.WriteLine();
        }
        _rendered = false;
    }
}
