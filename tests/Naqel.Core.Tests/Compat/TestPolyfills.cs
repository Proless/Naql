#if NETFRAMEWORK
using System.Security.Cryptography;

namespace Naqel.Core.Tests;

/// <summary>
/// Newer .NET APIs the tests use, for the net48 run that tests the libraries' netstandard2.0 build. The libraries'
/// own polyfills come from Naqel, whose internals the tests can see.
/// </summary>
internal static class TestPolyfills
{
    extension<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
    {
        public void Deconstruct(out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    extension(Task task)
    {
        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, delayCts.Token);
            if (await Task.WhenAny(task, delay).ConfigureAwait(false) == delay)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            delayCts.Cancel();
            await task.ConfigureAwait(false);
        }
    }

    extension<T>(Task<T> task)
    {
        public async Task<T> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await ((Task)task).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }

    extension(HttpContent content)
    {
        public Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken) => content.ReadAsByteArrayAsync();

        public Task<string> ReadAsStringAsync(CancellationToken cancellationToken) => content.ReadAsStringAsync();
    }

    extension(Stream stream)
    {
        public Task CopyToAsync(Stream destination, CancellationToken cancellationToken) => stream.CopyToAsync(destination, 81920, cancellationToken);
    }

    extension(SHA512)
    {
        public static byte[] HashData(byte[] source)
        {
            using var sha = SHA512.Create();
            return sha.ComputeHash(source);
        }
    }
}
#endif
