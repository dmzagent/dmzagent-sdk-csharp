using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DMZAgent.Sdk;

/// <summary>
/// What <see cref="DMZAgentClient.CheckAsync"/> does when the request
/// itself fails — network error, timeout, or 5xx. sdk-spec.md §4.4.
/// </summary>
/// <remarks>
/// An enum rather than a string, because the point of validating this
/// value in the other bindings is that a typo must not silently become a
/// different safety posture. Here the compiler does it.
/// </remarks>
public enum CbCacheOnError
{
    /// <summary>
    /// Propagate the error. This is what a client with no cache does, and
    /// it is the default.
    /// </summary>
    Raise = 0,

    /// <summary>
    /// Serve the last cached state for that subject even if it has
    /// expired, marked <c>Cached</c> and <c>Stale</c>. When nothing is
    /// known for that subject, propagate the error instead — never an
    /// invented state.
    /// <para>
    /// Cannot be set without a cache TTL above zero: there is nothing to
    /// fall back to until the caller has opted into the cache.
    /// </para>
    /// </summary>
    LastKnown = 1,
}

/// <summary>
/// In-process circuit-breaker state cache (sdk-spec.md §4.4).
/// </summary>
/// <remarks>
/// <para>
/// <c>CheckAsync()</c> is a network round trip on a path callers put in
/// front of a sensitive action, and it is often the only synchronous
/// DMZAgent call in a request. This removes that round trip for repeated
/// checks on the same subject.
/// </para>
/// <para>
/// <b>What the caller is choosing.</b> A cached <c>closed</c> is an allow
/// the server might no longer give. <b>The TTL is the maximum time a
/// newly-opened breaker can go unobserved by this client.</b> Nothing here
/// softens that, and every served entry carries its age so the caller can
/// see it.
/// </para>
/// <para>
/// One TTL applies to every state. Holding a deny longer than an allow is
/// a safety policy and it belongs to whoever set the TTL, not to this
/// class.
/// </para>
/// <para>
/// The clock is <see cref="Stopwatch.GetTimestamp"/>: a TTL measured
/// against the wall clock would expire early or late whenever the host's
/// time is adjusted, and the adjustment is invisible to the caller.
/// </para>
/// <para>
/// Locked on every operation, because the client it belongs to is
/// documented shareable across threads (spec §4.2).
/// </para>
/// </remarks>
internal sealed class CbStateCache
{
    internal const int DefaultMaxEntries = 1024;

    /// <summary>A cached result and the age at which it was read.</summary>
    internal readonly record struct Hit(CheckResult Result, TimeSpan Age);

    private sealed record Stored(CheckResult Result, long StoredAtTicks);

    private readonly TimeSpan _ttl;
    private readonly int      _max;
    private readonly object   _gate = new();

    /// <summary>
    /// Insertion-ordered by <see cref="_order"/>. Bounded because the key
    /// is a subject id — an agent that sees a hundred thousand subjects
    /// would otherwise hold a hundred thousand entries for the life of the
    /// process. Least-recently-used goes first.
    /// </summary>
    private readonly Dictionary<string, Stored>       _entries = new();
    private readonly LinkedList<string>               _order   = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();

    internal CbStateCache(TimeSpan? ttl, int maxEntries)
    {
        if (maxEntries < 1)
        {
            throw new DMZAgentValidationException("cbCacheMaxEntries must be at least 1");
        }
        _ttl = (ttl is { } t && t > TimeSpan.Zero) ? t : TimeSpan.Zero;
        _max = maxEntries;
    }

    /// <summary>
    /// NUL cannot occur inside a scope name or a subject id, so
    /// <c>subject</c> + <c>"a"</c> can never collide with <c>subjecta</c> +
    /// <c>""</c>.
    /// </summary>
    internal static string Key(string scope, string scopeRef) => scope + '\0' + scopeRef;

    internal bool Enabled => _ttl > TimeSpan.Zero;

    internal int Count
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    /// <summary>
    /// A live entry, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// An expired entry is left in place rather than dropped — the
    /// <see cref="CbCacheOnError.LastKnown"/> policy is the reason it is
    /// still worth something after the TTL. Eviction is by size, never by
    /// age.
    /// </remarks>
    internal Hit? Get(string key)
    {
        var hit = Lookup(key);
        if (hit is not { } h) return null;
        return h.Age <= _ttl ? h : null;
    }

    /// <summary>
    /// A live OR expired entry. Only <see cref="CbCacheOnError.LastKnown"/>
    /// may use this, and only after a failed check.
    /// </summary>
    internal Hit? GetAny(string key) => Lookup(key);

    /// <summary>
    /// Store a SUCCESSFUL check. Errors are never cached: a failure is not
    /// a state, and serving one back would turn one bad round trip into a
    /// TTL's worth of them.
    /// </summary>
    internal void Put(string key, CheckResult result)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            _entries[key] = new Stored(result, Stopwatch.GetTimestamp());
            Touch(key);
            while (_entries.Count > _max)
            {
                var oldest = _order.First;
                if (oldest is null) break;
                _order.RemoveFirst();
                _nodes.Remove(oldest.Value);
                _entries.Remove(oldest.Value);
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _nodes.Clear();
        }
    }

    private Hit? Lookup(string key)
    {
        if (!Enabled) return null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var stored)) return null;
            Touch(key);
            var elapsed = Stopwatch.GetTimestamp() - stored.StoredAtTicks;
            var seconds = elapsed <= 0 ? 0d : (double)elapsed / Stopwatch.Frequency;
            return new Hit(stored.Result, TimeSpan.FromSeconds(seconds));
        }
    }

    /// <summary>Move a key to the most-recently-used end. Caller holds the lock.</summary>
    private void Touch(string key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _order.Remove(node);
        }
        _nodes[key] = _order.AddLast(key);
    }
}
