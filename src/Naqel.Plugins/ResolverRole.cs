namespace Naqel.Plugins;

/// <summary>
/// What a resolver knows about, which decides who gets a reference when several resolvers could take it: the most
/// specific first, so a site's own plugin sees its links before a generic protocol does.
/// </summary>
public enum ResolverRole
{
    /// <summary>One site or service, such as a cloud drive or a video site; asked first.</summary>
    Site,

    /// <summary>A standard format on top of a protocol, such as an HLS playlist; asked after sites.</summary>
    Format,

    /// <summary>A standard protocol, such as HTTP or FTP; asked last.</summary>
    Protocol
}
