namespace Naqel.Plugins;

/// <summary>
/// What a plugin asks the user, which decides how the UIs show it.
/// </summary>
public enum InteractionKind
{
    /// <summary>Some text, such as a name.</summary>
    Text,

    /// <summary>Text the UIs hide, such as a password for a protected link.</summary>
    Secret,

    /// <summary>One of <see cref="InteractionRequest.Choices"/>, such as a video's quality.</summary>
    Choice,

    /// <summary>Yes or no, such as whether to replace a file; the answer is <c>yes</c> or <c>no</c>.</summary>
    Confirm,

    /// <summary>
    /// Open <see cref="InteractionRequest.Url"/> in the browser, such as a service's sign-in page, and answer when done;
    /// the answer is what the page handed back, such as the address it ended on.
    /// </summary>
    OpenUrl,

    /// <summary>The text in <see cref="InteractionRequest.Image"/>, a captcha the user solves themself.</summary>
    Captcha
}
