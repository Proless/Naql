using Naqel.Plugins;

namespace Naqel.Http.Plugin;

/// <summary>
/// HTTP and HTTPS for the Naqel app: <c>http:</c> and <c>https:</c> links resolve into downloads, and folder URLs into
/// uploads, as generic protocol resolvers that site plugins come before.
/// </summary>
public sealed class HttpPlugin : INaqelPlugin
{
    /// <summary>
    /// The setting for the User-Agent sent with every request.
    /// </summary>
    public const string UserAgentSetting = "userAgent";

    /// <inheritdoc />
    public void Register(IPluginRegistry registry, IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        registry.AddSettings(
        [
            new SettingDefinition(UserAgentSetting, "User-Agent", SettingKind.Text)
            {
                Description = "What Naqel calls itself in every request; some servers treat browsers and other programs differently",
                Default = HttpTransport.DefaultUserAgent
            }
        ]);

        var transport = new HttpTransport(HttpTransport.Default.Client) { UserAgent = context.Settings.GetValue(UserAgentSetting) };
        // Describing every link costs a request each; the engine describes a single file itself when it needs its name
        registry.AddSourceResolver(new HttpSourceResolver(transport, describe: false), ResolverRole.Protocol);
        registry.AddTargetResolver(new HttpTargetResolver(transport), ResolverRole.Protocol);
    }
}
