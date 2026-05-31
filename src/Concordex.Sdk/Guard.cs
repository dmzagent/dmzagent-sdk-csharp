using System;

namespace Concordex.Sdk;

/// <summary>
/// Disposable handle returned by <see cref="ConcordexClient.GuardAsync"/>
/// and <see cref="Conversation.GuardAsync"/>. Per sdk-spec.md §5.7 the
/// C# binding exposes a <see cref="Result"/> property reachable from a
/// <c>using</c> block:
///
/// <code>
/// using var g = await client.GuardAsync(subjectId: "user:ws:bot");
/// if (!g.Result.Allow) return Refuse(g.Result.Reason);
/// </code>
///
/// <para>
/// The close path is currently a no-op (Python parity). A future spec
/// revision may use it to emit an end-of-scope ledger entry; treat the
/// scope boundary as load-bearing in your code today so that change
/// lands cleanly.
/// </para>
/// </summary>
public sealed class GuardScope : IDisposable
{
    /// <summary>The <see cref="CheckResult"/> returned by the underlying check.</summary>
    public CheckResult Result { get; }

    internal GuardScope(CheckResult result)
    {
        Result = result;
    }

    private bool _disposed;

    /// <summary>Idempotent. Calling more than once is a no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // future: ledger close-scope event per spec §6.3.
    }
}
