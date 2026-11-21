using System;
using System.Security.Cryptography;

namespace Naqel;

/// <summary>
/// The expected hash of some content, to verify a transfer with.
/// </summary>
public sealed class Checksum : IEquatable<Checksum>
{
    private readonly byte[] _value;

    /// <summary>
    /// Creates a checksum.
    /// </summary>
    /// <param name="algorithm">MD5, SHA1, SHA256, SHA384 or SHA512</param>
    /// <param name="value">The hash</param>
    /// <exception cref="ArgumentException">The algorithm isn't supported, or the hash has the wrong length for it</exception>
    public Checksum(HashAlgorithmName algorithm, ReadOnlySpan<byte> value)
    {
        var length = HashLength(algorithm);
        if (value.Length != length)
        {
            throw new ArgumentException($"A {algorithm.Name} hash has {length} bytes, not {value.Length}", nameof(value));
        }

        Algorithm = algorithm;
        _value = value.ToArray();
    }

    /// <summary>
    /// The hash algorithm.
    /// </summary>
    public HashAlgorithmName Algorithm { get; }

    /// <summary>
    /// The hash.
    /// </summary>
    public ReadOnlyMemory<byte> Value => _value;

    /// <summary>
    /// A SHA-256 checksum from its hexadecimal form, as <c>sha256sum</c> prints it.
    /// </summary>
    public static Checksum Sha256(string hex) => FromHex(HashAlgorithmName.SHA256, hex);

    /// <summary>
    /// A SHA-512 checksum from its hexadecimal form, as <c>sha512sum</c> prints it.
    /// </summary>
    public static Checksum Sha512(string hex) => FromHex(HashAlgorithmName.SHA512, hex);

    /// <summary>
    /// A checksum from its hexadecimal form.
    /// </summary>
    /// <exception cref="FormatException">The text isn't hexadecimal</exception>
    public static Checksum FromHex(HashAlgorithmName algorithm, string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return new Checksum(algorithm, Convert.FromHexString(hex.Trim()));
    }

    /// <summary>
    /// A checksum from its Base64 form, as HTTP headers carry it.
    /// </summary>
    /// <exception cref="FormatException">The text isn't Base64</exception>
    public static Checksum FromBase64(HashAlgorithmName algorithm, string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        return new Checksum(algorithm, Convert.FromBase64String(base64.Trim()));
    }

    /// <summary>
    /// The hash in lowercase hexadecimal.
    /// </summary>
    public string ToHex() => Convert.ToHexString(_value).ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="hash"/> is this checksum's hash.
    /// </summary>
    public bool Matches(ReadOnlySpan<byte> hash) => hash.SequenceEqual(_value);

    /// <inheritdoc />
    public bool Equals(Checksum? other) => other is not null && Algorithm == other.Algorithm && Matches(other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Checksum);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Algorithm, BitConverter.ToInt32(_value, 0));

    /// <summary>
    /// The algorithm and the hash, such as <c>SHA256:9f86d081…</c>.
    /// </summary>
    public override string ToString() => $"{Algorithm.Name}:{ToHex()}";

    private static int HashLength(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5)
        {
            return 16;
        }
        if (algorithm == HashAlgorithmName.SHA1)
        {
            return 20;
        }
        if (algorithm == HashAlgorithmName.SHA256)
        {
            return 32;
        }
        if (algorithm == HashAlgorithmName.SHA384)
        {
            return 48;
        }
        if (algorithm == HashAlgorithmName.SHA512)
        {
            return 64;
        }
        throw new ArgumentException($"The hash algorithm '{algorithm.Name}' isn't supported", nameof(algorithm));
    }
}
