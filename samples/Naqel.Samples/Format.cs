using Naqel.Http;
using System.Security.Cryptography;

namespace Naqel.Samples;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Describes a failure: its kind and side, the HTTP status when there was one, and the underlying cause.
    /// </summary>
    public static string Error(TransferException ex)
    {
        var error = ex is TransferFailedException failed ? failed.Error : ex;
        var status = error is HttpTransferException { StatusCode: { } code } ? $", HTTP {(int)code}" : "";
        var cause = error.InnerException is { } inner ? $", cause: {inner.GetType().Name}" : "";
        var transient = ex.IsTransient ? "transient" : "permanent";
        return $"{transient} {ex.Kind} error on the {ex.Side.ToString().ToLowerInvariant()} side{status}{cause}: {error.Message}";
    }

    public static string Bytes(double bytes)
    {
        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return $"{bytes:0.0} {Units[unit]}";
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
