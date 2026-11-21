namespace Naqel.Plugins;

/// <summary>
/// Something a plugin asks the user, described as data so both UIs can show it without code of the plugin's own.
/// </summary>
/// <param name="Kind">What is asked</param>
/// <param name="Title">The question, to show</param>
public sealed record InteractionRequest(InteractionKind Kind, string Title)
{
    /// <summary>More about it, to show.</summary>
    public string? Message { get; init; }

    /// <summary>The answers to choose from, for <see cref="InteractionKind.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The page to open, for <see cref="InteractionKind.OpenUrl"/>.</summary>
    public Uri? Url { get; init; }

    /// <summary>The image to show, for <see cref="InteractionKind.Captcha"/>.</summary>
    public ReadOnlyMemory<byte> Image { get; init; }

    /// <summary>The image's media type, such as <c>image/png</c>.</summary>
    public string? ImageType { get; init; }

    /// <summary>The job it's about, if any; that job needs attention until the user answers.</summary>
    public string? JobId { get; init; }
}
