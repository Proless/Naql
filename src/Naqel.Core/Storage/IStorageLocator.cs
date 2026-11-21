using System;
using System.Diagnostics.CodeAnalysis;

namespace Naqel.Storage;

/// <summary>
/// A resolver that knows which storage, and which path in it, a reference stands for.
/// </summary>
public interface IStorageLocator
{
    /// <summary>
    /// The storage and the path in it that <paramref name="reference"/> stands for.
    /// </summary>
    /// <returns><c>false</c> when <paramref name="reference"/> isn't in a storage this resolver knows</returns>
    bool TryLocate(Uri reference, [NotNullWhen(true)] out IStorage? storage, [NotNullWhen(true)] out string? path);
}
