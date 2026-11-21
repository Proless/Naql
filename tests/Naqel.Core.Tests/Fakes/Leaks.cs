namespace Naqel.Core.Tests.Fakes;

/// <summary>
/// Counts the source streams a test opened and disposed, and collects task exceptions nobody observed, so a test can
/// check that a transfer cleaned up after itself however it ended.
/// </summary>
public sealed class Leaks : IDisposable
{
    private readonly List<Exception> _unobserved = [];
    private int _opened;
    private int _disposed;

    public Leaks()
    {
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    public int OpenStreams => Volatile.Read(ref _opened) - Volatile.Read(ref _disposed);

    /// <summary>
    /// Wraps a source stream so its disposal is counted.
    /// </summary>
    public Stream Track(Stream stream)
    {
        Interlocked.Increment(ref _opened);
        return new TrackedStream(stream, () => Interlocked.Increment(ref _disposed));
    }

    /// <summary>
    /// Collects garbage so that faulted tasks nobody awaited report themselves, and returns their exceptions.
    /// </summary>
    public IReadOnlyList<Exception> Unobserved()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        lock (_unobserved)
        {
            return _unobserved.ToArray();
        }
    }

    public void Dispose() => TaskScheduler.UnobservedTaskException -= OnUnobserved;

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Only this library's tasks count; other tests in the run may leave their own
        if (e.Exception.InnerExceptions.Any(ex => ex.StackTrace?.Contains("Naqel.") == true && ex.StackTrace?.Contains("Naqel.Core.Tests.") != true))
        {
            lock (_unobserved)
            {
                _unobserved.Add(e.Exception);
            }
        }
    }

    private sealed class TrackedStream(Stream inner, Action onDispose) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                onDispose();
            }
            base.Dispose(disposing);
        }
    }
}
