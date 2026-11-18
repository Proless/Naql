using System;
using System.IO;

namespace Naql.Files;

/// <summary>
/// Turns file system errors into transfer errors that say whether retrying can help.
/// </summary>
internal static class FileErrors
{
    // Windows: ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL. Unix: ENOSPC, which .NET uses as the HResult
    private const int HandleDiskFull = 0x27;
    private const int DiskFull = 0x70;
    private const int NoSpace = 28;

    /// <summary>
    /// Maps errors that won't go away by retrying, or returns <c>null</c> to leave the error as it is.
    /// </summary>
    public static TransferException? Map(Exception ex, TransferSide side)
    {
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => Create(ex, TransferErrorKind.NotFound, side),
            UnauthorizedAccessException => Create(ex, TransferErrorKind.AccessDenied, side),
            IOException when IsDiskFull(ex.HResult) => Create(ex, TransferErrorKind.InsufficientStorage, side),
            _ => null
        };
    }

    private static bool IsDiskFull(int hresult) => (hresult & 0xFFFF) is HandleDiskFull or DiskFull || hresult == NoSpace;

    private static TransferException Create(Exception ex, TransferErrorKind kind, TransferSide side)
    {
        return new TransferException(ex.Message, kind, isTransient: false, side, innerException: ex);
    }
}
