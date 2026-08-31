using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// Spec 0.8.1: <c>AwaitOutcomeAsync</c> against the division-scoped story
/// endpoint.
/// </summary>
/// <remarks>
/// Mirrors <c>contract-tests/outcome-vectors.json</c>. Kept here as well
/// because the request-shape assertion — that no <c>workspace_id</c> is
/// sent, on every request and not merely the first — is the whole point of
/// the fix.
/// <para>
/// What was wrong at 0.8.0, specifically in this SDK: the loop returned the
/// first response that parsed. The story endpoint answers 200 throughout the
/// fan-out, handing back traces as each workspace finishes, so that is a
/// half-finished story. (It also sent no <c>workspace_id</c>, which the
/// endpoint then required, so in practice the first request 422'd.)
/// </para>
/// </remarks>
public sealed class AwaitOutcomeTests
{
    private const string TestApiKey = "ck_test_xxxxxxxxxxxxxxxxxxxxx";

    private static string Trace(string id, string ws, string outcome) =>
        $$"""{"trace_id":"{{id}}","workspace_id":"{{ws}}","outcome":"{{outcome}}"}""";

    private static readonly string BothTraces =
        Trace("trace_1", "ws_1", "applied") + "," + Trace("trace_2", "ws_2", "no_change");

    /// <summary>A story page. <paramref name="outcome"/> null omits the key.</summary>
    private static string Story(bool complete, string? outcome, string traces, int traceCount)
    {
        string outcomeField = outcome is null ? "" : $"\"outcome\":\"{outcome}\",";
        return $$"""
        {
          "frame_id": "frame_abc",
          "subject_id": "subject:dv_test:acme-bot",
          "division_id": "dv_test",
          "workspace_id": null,
          "workspace_ids": ["ws_1", "ws_2"],
          {{outcomeField}}
          "reasoning": [{{traces}}],
          "summary": { "trace_count": {{traceCount}}, "workspace_count": 2, "complete": {{(complete ? "true" : "false")}} }
        }
        """;
    }

    /// <summary>Serves pages in order — the last repeats — recording each URI.</summary>
    private sealed class Serve
    {
        public readonly List<string> Uris = new();
        private readonly string[] _pages;
        private int _i;

        public Serve(params string[] pages) => _pages = pages;

        public StubHttpMessageHandler Handler() => new((req, _) =>
        {
            Uris.Add(req.RequestUri?.ToString() ?? "");
            var body = _pages[Math.Min(_i, _pages.Length - 1)];
            _i++;
            return StubHttpMessageHandler.MakeResponse(HttpStatusCode.OK, body);
        });
    }

    // ------------------------------------------------------------------ //
    // request shape
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task SendsNoWorkspaceId()
    {
        // The regression. The endpoint is division-scoped; the SDK holds no
        // workspace to name, and naming one narrows to 1 of N perspectives.
        var serve = new Serve(Story(true, "applied", BothTraces, 2));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());
        await cx.AwaitOutcomeAsync("frame_abc", 5.0);

        Assert.NotEmpty(serve.Uris);
        Assert.All(serve.Uris, u => Assert.DoesNotContain("workspace_id", u));
        Assert.Contains("/v1/frames/frame_abc/story", serve.Uris[0]);
    }

    // ------------------------------------------------------------------ //
    // termination
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ReturnsOnceComplete()
    {
        var serve = new Serve(Story(true, "applied", BothTraces, 2));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());
        var result = await cx.AwaitOutcomeAsync("frame_abc", 5.0);

        Assert.True(result.Complete);
        Assert.Single(serve.Uris);
    }

    [Fact]
    public async Task KeepsPollingWhileIncomplete()
    {
        // The regression: this used to return the first page.
        var serve = new Serve(
            Story(false, "applied", Trace("trace_1", "ws_1", "applied"), 1),
            Story(true, "applied", BothTraces, 2));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());
        var result = await cx.AwaitOutcomeAsync("frame_abc", 5.0);

        Assert.Equal(2, serve.Uris.Count);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Reasoning!.Value.GetArrayLength());
    }

    [Fact]
    public async Task TimesOutRatherThanReturningPartial()
    {
        var serve = new Serve(Story(false, "applied", Trace("trace_1", "ws_1", "applied"), 1));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());

        var ex = await Assert.ThrowsAsync<DMZAgentServerException>(
            () => cx.AwaitOutcomeAsync("frame_abc", 0.5));
        Assert.Contains("timed out", ex.Message);
    }

    // ------------------------------------------------------------------ //
    // result shape
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ReportsServerFoldVerbatim()
    {
        // An SDK recomputing Outcome from Reasoning — say by taking the
        // first trace — would report "applied" here instead of "failed".
        var traces = Trace("trace_1", "ws_1", "applied") + "," + Trace("trace_2", "ws_2", "failed");
        var serve = new Serve(Story(true, "failed", traces, 2));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());

        Assert.Equal("failed", (await cx.AwaitOutcomeAsync("frame_abc", 5.0)).Outcome);
    }

    [Fact]
    public async Task CarriesWorkspaceAttribution()
    {
        var serve = new Serve(Story(true, "applied", BothTraces, 2));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());
        var result = await cx.AwaitOutcomeAsync("frame_abc", 5.0);

        Assert.Equal("dv_test", result.DivisionId);
        Assert.Equal(
            new[] { "ws_1", "ws_2" },
            result.WorkspaceIds!.Value.EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new[] { "ws_1", "ws_2" },
            result.Reasoning!.Value.EnumerateArray()
                .Select(t => t.GetProperty("workspace_id").GetString()).ToArray());
    }

    [Fact]
    public async Task HeldIsCarriedThrough()
    {
        // `held` joined the enum in 0.8.1; the server has emitted it since
        // ST-8 while the spec listed four of the five values.
        var serve = new Serve(Story(true, "held", Trace("trace_1", "ws_1", "held"), 1));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());

        Assert.Equal("held", (await cx.AwaitOutcomeAsync("frame_abc", 5.0)).Outcome);
    }

    [Fact]
    public async Task MissingOutcomeIsNullNotEmptyString()
    {
        // It was string.Empty when the key was absent — neither a real
        // outcome nor distinguishable from one.
        var serve = new Serve(Story(true, null, BothTraces, 2));
        using var cx = new DMZAgentClient(TestApiKey, handler: serve.Handler());

        Assert.Null((await cx.AwaitOutcomeAsync("frame_abc", 5.0)).Outcome);
    }
}
