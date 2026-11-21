using Naqel.Plugins;

namespace Naqel.Manager.Tests.Fakes;

/// <summary>
/// A completion step that records its runs, sets data, and can wait at a gate or fail.
/// </summary>
public sealed class RecordingStep(string name, List<string>? log = null) : ICompletionStep
{
    private int _runs;

    public string Name { get; } = name;

    public int Runs => Volatile.Read(ref _runs);

    /// <summary>The step waits for this before it finishes, if set.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Signaled when the step starts.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The step fails with this, if set.</summary>
    public Exception? Failure { get; set; }

    public JobInfo? Saw { get; private set; }

    public async Task RunAsync(ICompletionContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        Saw = context.Job;
        if (log != null)
        {
            lock (log)
            {
                log.Add(Name);
            }
        }
        context.SetData($"test.{Name}", "ran");
        Started.TrySetResult();
        if (Gate != null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }
        if (Failure != null)
        {
            throw Failure;
        }
    }
}
