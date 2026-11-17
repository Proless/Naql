using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Naql.Streams;

/// <summary>
/// Hashes what targets wrote, for checksum verification.
/// </summary>
internal static class Hashing
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Hashes the next <paramref name="length"/> bytes of <paramref name="stream"/>.
    /// </summary>
    /// <exception cref="TransferException">The stream ended before <paramref name="length"/> bytes</exception>
    public static async Task<byte[]> HashAsync(Stream stream, HashAlgorithmName algorithm, long length, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TransferException($"The target holds {length - remaining} of {length} bytes", TransferErrorKind.Integrity)
                    {
                        Side = TransferSide.Target
                    };
                }
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
