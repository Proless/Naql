#if NETSTANDARD2_1
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace System;

/// <summary>
/// The newer .NET APIs the library uses, for netstandard2.1, written as extension members so the code calls them the
/// same way on every target. Each behaves like the API it stands in for, as far as the library relies on it.
/// </summary>
internal static class Polyfills
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }

    extension(ArgumentOutOfRangeException)
    {
        public static void ThrowIfNegative(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} ('{value}') must be a non-negative value.");
            }
        }

        public static void ThrowIfNegativeOrZero(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} ('{value}') must be a non-negative and non-zero value.");
            }
        }

        public static void ThrowIfLessThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
        {
            if (value.CompareTo(other) < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} ('{value}') must be greater than or equal to '{other}'.");
            }
        }

        public static void ThrowIfLessThanOrEqual<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
        {
            if (value.CompareTo(other) <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} ('{value}') must be greater than '{other}'.");
            }
        }
    }

    extension(Convert)
    {
        public static string ToHexString(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[2 * i] = HexDigit(bytes[i] >> 4);
                chars[(2 * i) + 1] = HexDigit(bytes[i] & 0xF);
            }
            return new string(chars);
        }

        public static byte[] FromHexString(string hex)
        {
            if (hex.Length % 2 != 0)
            {
                throw new FormatException("The input is not a valid hex string as its length is not a multiple of 2.");
            }
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((HexValue(hex[2 * i]) << 4) | HexValue(hex[(2 * i) + 1]));
            }
            return bytes;
        }
    }

    extension(SHA256)
    {
        public static byte[] HashData(byte[] source)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(source);
        }
    }

    extension(Random)
    {
        /// <summary>A random number generator for the current thread, since <see cref="Random"/> isn't thread-safe.</summary>
        public static Random Shared => s_random ??= new Random();
    }

    extension(OperatingSystem)
    {
        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    extension(Path)
    {
        public static string TrimEndingDirectorySeparator(string path)
        {
            var root = Path.GetPathRoot(path);
            return path.Length > (root?.Length ?? 0) && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
                ? path.Substring(0, path.Length - 1)
                : path;
        }
    }

    extension(File)
    {
        /// <summary>
        /// Moves a file, replacing <paramref name="destination"/> when <paramref name="overwrite"/> is set. Replacing
        /// uses <see cref="File.Replace(string, string, string?)"/>, which swaps the file in one step like the newer
        /// <c>File.Move</c> does.
        /// </summary>
        public static void Move(string source, string destination, bool overwrite)
        {
            if (overwrite && File.Exists(destination))
            {
                File.Replace(source, destination, null);
            }
            else
            {
                File.Move(source, destination);
            }
        }
    }

    extension<TKey, TValue>(ReadOnlyDictionary<TKey, TValue>) where TKey : notnull
    {
        public static ReadOnlyDictionary<TKey, TValue> Empty => EmptyDictionary<TKey, TValue>.Instance;
    }

    extension<T>(Task<T> task)
    {
        /// <summary>
        /// Waits for the task until <paramref name="cancellationToken"/> is canceled, without canceling the task itself.
        /// </summary>
        public async Task<T> WaitAsync(CancellationToken cancellationToken)
        {
            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                return await task.ConfigureAwait(false);
            }

            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            return await task.ConfigureAwait(false);
        }
    }

    extension(Stream)
    {
        public static void ValidateBufferArguments(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Non-negative number required.");
            }
            if ((uint)count > buffer.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Offset and length were out of bounds for the array.");
            }
        }
    }

    [ThreadStatic]
    private static Random? s_random;

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => throw new FormatException($"The input is not a valid hex string as it contains a non-hex character.")
    };

    private static class EmptyDictionary<TKey, TValue> where TKey : notnull
    {
        public static readonly ReadOnlyDictionary<TKey, TValue> Instance = new(new Dictionary<TKey, TValue>());
    }
}
#endif
