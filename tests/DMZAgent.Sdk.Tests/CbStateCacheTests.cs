using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// The circuit-breaker state cache (sdk-spec.md §4.4).
///
/// <para>
/// <c>CheckAsync()</c> is a network round trip in front of a sensitive
/// action. The cache removes it for repeated checks on the same subject,
/// and the whole reason to be careful is that a cached <c>closed</c> is an
/// allow the server might no longer give.
/// </para>
///
/// <para>The rules these tests hold:</para>
/// <list type="bullet">
///   <item>off unless the caller sets a TTL — a caching safety check
///         nobody asked for is worse than a slow one;</item>
///   <item>a served entry always says it was served, and how old it was,
///         so a caller recording a denial can tell it read stale state;</item>
///   <item>one TTL for every state: holding a deny longer than an allow is
///         a safety policy that belongs to whoever set the TTL;</item>
///   <item><c>subject</c> and <c>interaction</c> with the same id are
///         different keys;</item>
///   <item>the map is bounded, because the key is a subject id;</item>
///   <item>errors are never cached, and <c>LastKnown</c> is opt-in,
///         marked, and unreachable without a TTL to fall back on.</item>
/// </list>
/// </summary>
public sealed class CbStateCacheTests
{
    private const string Key = "ck_test_cache";

    private const string ClosedBody =
        """
        {"state":"closed","allow":true,"warning":false,"reason":"no policies fired",
         "fired_policies":[],"anchor":null,"checked_at":"2026-08-31T00:00:00Z",
         "latency_ms":12.3,"route_latency_ms":18.7}
        """;

    private const string OpenBody =
        """
        {"state":"open","allow":false,"warning":false,"reason":"policy fired",
         "fired_policies":[{"cb_policy_id":"p1","name":"n","action":"block"}],
         "anchor":null,"checked_at":"2026-08-31T00:00:00Z",
         "latency_ms":9.1,"route_latency_ms":11.0}
        """;

    /// <summary>One scripted outcome per call.</summary>
    private sealed record Step(
        string? Body = null,
        Exception? Failure = null,
        HttpStatusCode Status = HttpStatusCode.OK,
        IReadOnlyDictionary<string, string>? Headers = null);

    /// <summary>Counts calls and serves the scripted queue, never touching a socket.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        private readonly IReadOnlyList<Step> _steps;

        public CountingHandler(params Step[] steps)
        {
            _steps = steps.Length > 0 ? steps : new[] { new Step(ClosedBody) };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var step = _steps[Math.Min(Calls - 1, _steps.Count - 1)];
            if (step.Failure is not null) throw step.Failure;

            var response = new HttpResponseMessage(step.Status)
            {
                Content = new StringContent(
                    step.Body ?? """{"detail":"no"}""", Encoding.UTF8, "application/json"),
            };
            if (step.Headers is not null)
            {
                foreach (var (k, v) in step.Headers) response.Headers.TryAddWithoutValidation(k, v);
            }
            return Task.FromResult(response);
        }
    }

    private static DMZAgentClient Client(
        CountingHandler handler,
        TimeSpan? ttl = null,
        int max = 1024,
        CbCacheOnError onError = CbCacheOnError.Raise) =>
        new(Key, handler, cbCacheTtl: ttl, cbCacheMaxEntries: max, cbCacheOnError: onError);

    // =================================================================== //
    // Off by default
    // =================================================================== //

    [Fact]
    public async Task EveryCheckIsARoundTripByDefault()
    {
        var h = new CountingHandler();
        using var cx = Client(h);
        for (var i = 0; i < 3; i++) await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.Equal(3, h.Calls);
    }

    [Fact]
    public async Task ADefaultClientReportsNoCachingOnItsResults()
    {
        using var cx = Client(new CountingHandler());
        var r = await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.False(r.Cached);
        Assert.Equal(TimeSpan.Zero, r.CacheAge);
        Assert.False(r.Stale);
    }

    [Fact]
    public async Task FreshIsHarmlessWithTheCacheOff()
    {
        var h = new CountingHandler();
        using var cx = Client(h);
        await cx.CheckAsync(subjectId: "user:ws:a", fresh: true);
        Assert.Equal(1, h.Calls);
    }

    // =================================================================== //
    // Serving from the cache
    // =================================================================== //

    [Fact]
    public async Task ASecondCheckInsideTheTtlMakesNoRequest()
    {
        var h = new CountingHandler();
        using var cx = Client(h, TimeSpan.FromSeconds(60));
        await cx.CheckAsync(subjectId: "user:ws:a");
        await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task AServedEntrySaysSoAndCarriesItsAge()
    {
        using var cx = Client(new CountingHandler(), TimeSpan.FromSeconds(60));
        await cx.CheckAsync(subjectId: "user:ws:a");
        await Task.Delay(20);
        var second = await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.True(second.Cached);
        Assert.False(second.Stale);
        Assert.True(second.CacheAge.TotalMilliseconds >= 5,
            "the age has to be real, not a placeholder");
    }

    [Fact]
    public async Task TheServersOwnNumbersAreNotRewritten()
    {
        using var cx = Client(new CountingHandler(), TimeSpan.FromSeconds(60));
        var fresh = await cx.CheckAsync(subjectId: "user:ws:a");
        var hit   = await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.Equal(fresh.LatencyMs, hit.LatencyMs);
        Assert.Equal(fresh.RouteLatencyMs, hit.RouteLatencyMs);
        Assert.Equal(fresh.CheckedAt, hit.CheckedAt);
    }

    [Fact]
    public async Task TheDecisionItselfSurvivesTheRoundTrip()
    {
        using var cx = Client(new CountingHandler(new Step(OpenBody)), TimeSpan.FromSeconds(60));
        var first  = await cx.CheckAsync(subjectId: "user:ws:a");
        var second = await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.Equal(first.State, second.State);
        Assert.False(second.Allow);
        Assert.Equal(first.FiredPolicies.Count, second.FiredPolicies.Count);
    }

    [Fact]
    public async Task AnExpiredEntryIsNotServed()
    {
        var h = new CountingHandler();
        using var cx = Client(h, TimeSpan.FromMilliseconds(30));
        await cx.CheckAsync(subjectId: "user:ws:a");
        await Task.Delay(90);
        var again = await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.Equal(2, h.Calls);
        Assert.False(again.Cached);
    }

    [Fact]
    public async Task FreshBypassesTheCacheAndReplacesIt()
    {
        var h = new CountingHandler(new Step(ClosedBody), new Step(OpenBody));
        using var cx = Client(h, TimeSpan.FromSeconds(60));
        Assert.True((await cx.CheckAsync(subjectId: "user:ws:a")).Allow);
        var forced = await cx.CheckAsync(subjectId: "user:ws:a", fresh: true);
        Assert.Equal(2, h.Calls);
        Assert.False(forced.Allow);
        Assert.False(forced.Cached);
        Assert.False((await cx.CheckAsync(subjectId: "user:ws:a")).Allow);
    }

    [Fact]
    public async Task GuardPassesFreshThrough()
    {
        var h = new CountingHandler();
        using var cx = Client(h, TimeSpan.FromSeconds(60));
        (await cx.GuardAsync(subjectId: "user:ws:a")).Dispose();
        (await cx.GuardAsync(subjectId: "user:ws:a", fresh: true)).Dispose();
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task GuardRaisesOnACachedOpenTheSameAsAFreshOne()
    {
        using var cx = Client(new CountingHandler(new Step(OpenBody)), TimeSpan.FromSeconds(60));
        await cx.CheckAsync(subjectId: "user:ws:a");
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(
            () => cx.GuardAsync(subjectId: "user:ws:a", raiseOnOpen: true));
    }

    // =================================================================== //
    // One TTL for every state
    // =================================================================== //

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AllowAndDenyExpireTogether(bool allow)
    {
        // Holding a deny longer than an allow is a safety policy, and it
        // belongs to whoever set the TTL.
        var h = new CountingHandler(new Step(allow ? ClosedBody : OpenBody));
        using var cx = Client(h, TimeSpan.FromMilliseconds(30));
        await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.True((await cx.CheckAsync(subjectId: "user:ws:a")).Cached);
        await Task.Delay(90);
        Assert.False((await cx.CheckAsync(subjectId: "user:ws:a")).Cached);
        Assert.Equal(2, h.Calls);
    }

    // =================================================================== //
    // Keys
    // =================================================================== //

    [Fact]
    public async Task SubjectAndInteractionWithTheSameIdDoNotCollide()
    {
        var h = new CountingHandler();
        using var cx = Client(h, TimeSpan.FromSeconds(60));
        await cx.CheckAsync(subjectId: "x");
        await cx.CheckAsync(interactionId: "x");
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task DifferentSubjectsAreCachedSeparately()
    {
        var h = new CountingHandler(new Step(ClosedBody), new Step(OpenBody));
        using var cx = Client(h, TimeSpan.FromSeconds(60));
        Assert.True((await cx.CheckAsync(subjectId: "a")).Allow);
        Assert.False((await cx.CheckAsync(subjectId: "b")).Allow);
        Assert.True((await cx.CheckAsync(subjectId: "a")).Allow);
    }

    // =================================================================== //
    // Bounded
    // =================================================================== //

    [Fact]
    public async Task AClientsOwnCacheIsBoundedObservedThroughItsRequests()
    {
        // The client's cache is internal, so the bound is asserted by its
        // only visible consequence: an evicted subject costs another round
        // trip.
        var h = new CountingHandler();
        using var cx = Client(h, TimeSpan.FromSeconds(60), max: 2);
        await cx.CheckAsync(subjectId: "a");
        await cx.CheckAsync(subjectId: "b");
        await cx.CheckAsync(subjectId: "c");
        Assert.Equal(3, h.Calls);

        Assert.False((await cx.CheckAsync(subjectId: "a")).Cached);  // evicted by c
        Assert.Equal(4, h.Calls);
        Assert.True((await cx.CheckAsync(subjectId: "c")).Cached);
        Assert.Equal(4, h.Calls);
    }

    [Fact]
    public void AMaxBelowOneIsRefused()
    {
        Assert.Throws<DMZAgentValidationException>(
            () => Client(new CountingHandler(), TimeSpan.FromSeconds(60), max: 0));
    }

    // =================================================================== //
    // Failure
    // =================================================================== //

    [Fact]
    public async Task RaiseIsTheDefaultAndMatchesAClientWithNoCache()
    {
        var h = new CountingHandler(
            new Step(ClosedBody), new Step(Failure: new HttpRequestException("down")));
        using var cx = Client(h, TimeSpan.FromSeconds(60));
        await cx.CheckAsync(subjectId: "user:ws:a");
        await Assert.ThrowsAsync<DMZAgentServerException>(
            () => cx.CheckAsync(subjectId: "user:ws:a", fresh: true));
    }

    [Fact]
    public async Task LastKnownServesThePreviousStateMarkedStale()
    {
        var h = new CountingHandler(
            new Step(OpenBody), new Step(Failure: new HttpRequestException("down")));
        using var cx = Client(h, TimeSpan.FromSeconds(60), onError: CbCacheOnError.LastKnown);
        await cx.CheckAsync(subjectId: "user:ws:a");
        var served = await cx.CheckAsync(subjectId: "user:ws:a", fresh: true);
        Assert.True(served.Cached);
        Assert.True(served.Stale);
        Assert.False(served.Allow);
    }

    [Fact]
    public async Task LastKnownServesAnExpiredEntryToo()
    {
        var h = new CountingHandler(
            new Step(OpenBody), new Step(Failure: new HttpRequestException("down")));
        using var cx = Client(h, TimeSpan.FromMilliseconds(30), onError: CbCacheOnError.LastKnown);
        await cx.CheckAsync(subjectId: "user:ws:a");
        await Task.Delay(90);
        var served = await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.True(served.Stale);
        Assert.False(served.Allow);
    }

    [Fact]
    public async Task LastKnownWithNothingKnownRaises()
    {
        // Never an invented state for a subject this client has never
        // successfully checked.
        var h = new CountingHandler(new Step(Failure: new HttpRequestException("down")));
        using var cx = Client(h, TimeSpan.FromSeconds(60), onError: CbCacheOnError.LastKnown);
        await Assert.ThrowsAsync<DMZAgentServerException>(
            () => cx.CheckAsync(subjectId: "never-seen"));
    }

    [Fact]
    public async Task AFailureIsNeverItselfCached()
    {
        var h = new CountingHandler(
            new Step(ClosedBody),
            new Step(Failure: new HttpRequestException("down")),
            new Step(OpenBody));
        using var cx = Client(h, TimeSpan.FromSeconds(60), onError: CbCacheOnError.LastKnown);
        await cx.CheckAsync(subjectId: "user:ws:a");
        await cx.CheckAsync(subjectId: "user:ws:a", fresh: true);            // fails, serves stale
        var third = await cx.CheckAsync(subjectId: "user:ws:a", fresh: true); // recovers
        Assert.False(third.Cached);
        Assert.False(third.Allow);
    }

    [Fact]
    public async Task ARateLimitIsAnAnswerAndIsNotMasked()
    {
        // 429 carries a RetryAfter the caller can act on. Serving a cached
        // state instead would drop that signal.
        var h = new CountingHandler(
            new Step(ClosedBody),
            new Step(Status: HttpStatusCode.TooManyRequests,
                     Headers: new Dictionary<string, string> { ["Retry-After"] = "30" }));
        using var cx = Client(h, TimeSpan.FromSeconds(60), onError: CbCacheOnError.LastKnown);
        await cx.CheckAsync(subjectId: "user:ws:a");
        await Assert.ThrowsAsync<DMZAgentRateLimitException>(
            () => cx.CheckAsync(subjectId: "user:ws:a", fresh: true));
    }

    // =================================================================== //
    // Configuration
    // =================================================================== //

    [Fact]
    public void LastKnownWithoutATtlIsRefused()
    {
        // There is nothing to fall back TO. Accepting the pair would leave
        // someone believing they had an outage story that can never fire.
        Assert.Throws<DMZAgentValidationException>(
            () => Client(new CountingHandler(), ttl: null, onError: CbCacheOnError.LastKnown));
    }

    [Fact]
    public async Task AZeroTtlDisablesTheCacheRatherThanCachingForever()
    {
        var h = new CountingHandler();
        using var cx = Client(h, TimeSpan.Zero);
        await cx.CheckAsync(subjectId: "user:ws:a");
        await cx.CheckAsync(subjectId: "user:ws:a");
        Assert.Equal(2, h.Calls);
    }
}
