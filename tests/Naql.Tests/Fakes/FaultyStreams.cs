namespace Naql.Tests.Fakes;

/// <summary>
/// Reads normally, then throws once <paramref name="failAfter"/> bytes were read, like a connection that breaks off.
/// </summary>
public sealed class FailingStream(Stream inner, long failAfter, Func<Exception> error) : ReadOnlyStream
{
    private long _read;

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_read >= failAfter)
        {
            throw error();
        }

        var read = await inner.ReadAsync(buffer.AsMemory(offset, (int)Math.Min(count, failAfter - _read)), cancellationToken);
        _read += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Reads normally, then stops delivering data after <paramref name="stallAfter"/> bytes, like a stalled connection.
/// A stalled read only ends when the stream is disposed, or canceled when <paramref name="honorCancellation"/> is set.
/// </summary>
public sealed class StallingStream(Stream inner, long stallAfter, bool honorCancellation) : ReadOnlyStream
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _read;

    /// <summary>
    /// Completes when a read starts stalling.
    /// </summary>
    public Task Stalled => _stalled.Task;

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_read >= stallAfter)
        {
            _stalled.TrySetResult();
            var canceled = honorCancellation ? Task.Delay(Timeout.Infinite, cancellationToken) : Task.Delay(Timeout.Infinite, CancellationToken.None);
            await Task.WhenAny(_disposed.Task, canceled);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(StallingStream));
        }

        var read = await inner.ReadAsync(buffer.AsMemory(offset, (int)Math.Min(count, stallAfter - _read)), cancellationToken);
        _read += read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed.TrySetResult();
        inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Hides the ability to seek, like a network or pipe stream.
/// </summary>
public sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The members a read-only, forward-only stream doesn't support.
/// </summary>
public abstract class ReadOnlyStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
