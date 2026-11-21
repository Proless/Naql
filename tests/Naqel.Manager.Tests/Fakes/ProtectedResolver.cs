using Naqel.Plugins;

namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// Resolves <c>protected:name</c> links, which need a password: it asks the user for it through the job's context, like
/// a file host's plugin for a protected link.
/// </summary>
public sealed class ProtectedResolver(string password) : ISourceResolver
{
    private int _asked;

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public int Asked => Volatile.Read(ref _asked);

    public bool CanResolve(Uri reference) => reference.Scheme == "protected";

    public async IAsyncEnumerable<Resolved> ResolveAsync(Uri reference, ResolveContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var job = context.Get<JobContext>() ?? throw new InvalidOperationException("Resolved outside a job");
        Interlocked.Increment(ref _asked);
        var answer = await job.Interaction.AskAsync(new InteractionRequest(InteractionKind.Secret, $"The password for {reference.AbsolutePath}"), cancellationToken);
        if (answer.IsDeclined)
        {
            throw new TransferException("No password, no file", TransferErrorKind.AccessDenied, side: TransferSide.Source);
        }
        if (answer.Value != password)
        {
            throw new TransferException("Wrong password", TransferErrorKind.Unauthorized, side: TransferSide.Source);
        }
        var name = reference.AbsolutePath;
        var source = new SlowSource(Files[name], name, TimeSpan.Zero);
        yield return new ResolvedSource(source, "", await source.GetInfoAsync(cancellationToken));
    }
}
