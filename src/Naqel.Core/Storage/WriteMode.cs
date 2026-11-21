namespace Naqel.Storage;

/// <summary>
/// What a write does when a file is there already.
/// </summary>
public enum WriteMode
{
    /// <summary>Replace it.</summary>
    Overwrite,

    /// <summary>Fail with <see cref="TransferErrorKind.AlreadyExists"/>.</summary>
    Fail,

    /// <summary>Keep both, giving the new file another name, such as <c>a (1).txt</c>.</summary>
    KeepBoth
}
