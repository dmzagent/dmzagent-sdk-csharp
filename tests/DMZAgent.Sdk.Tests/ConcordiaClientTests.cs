using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DMZAgent.Sdk.Concordia;
using Xunit;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// Wire-shape tests for <see cref="ConcordiaClient"/>. Uses the same
/// <see cref="StubHttpMessageHandler"/> the agent-stream tests use,
/// so the two test suites share the dispatch fake.
///
/// We exercise the JSON-RPC envelope, argument case-conversion
/// (camelCase params → snake_case wire), error-code → exception
/// mapping, and the resource-page decoding path. Full server
/// end-to-end is covered by prothinker-server (#212/#213/#217).
/// </summary>
public class ConcordiaClientTests
{
    private const string TestApiKey = "ck_test_abc";

    private static string WrapToolResult(object data) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id      = 1,
            result  = new
            {
                content = new object[]
                {
                    new { type = "text", text = JsonSerializer.Serialize(data) },
                },
                _data   = data,
                isError = false,
            },
        });

    private static string WrapResourceRead(object payload, string uri) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id      = 1,
            result  = new
            {
                contents = new object[]
                {
                    new { uri, mimeType = "application/json", text = JsonSerializer.Serialize(payload) },
                },
            },
        });

    private static string WrapError(int code, string errorId, string message = "") =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id      = 1,
            error   = new
            {
                code,
                message,
                data = new { error_id = errorId },
            },
        });

    // ----- Construction --------------------------------------------------

    [Fact]
    public void Constructor_RequiresApiKey()
    {
        Environment.SetEnvironmentVariable("DMZAGENT_API_KEY", null);
        var ex = Assert.Throws<ConcordiaException>(() => new ConcordiaClient());
        Assert.Contains("apiKey required", ex.Message);
    }

    [Fact]
    public void Constructor_RejectsNonCkKey()
    {
        var ex = Assert.Throws<ConcordiaException>(() =>
            new ConcordiaClient(apiKey: "sk_oops_wrong"));
        Assert.Contains("ck_", ex.Message);
    }

    [Fact]
    public void Constructor_AcceptsCkKey()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { ok = true } }));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);
        Assert.NotNull(client);
    }

    // ----- Tools ---------------------------------------------------------

    [Fact]
    public async Task EnforceCovenant_HappyPath()
    {
        var responseData = new Dictionary<string, object?>
        {
            ["verdict"]         = "review",
            ["policy_ids"]      = new[] { "pol_a", "pol_b" },
            ["rationale"]       = "policies fired",
            ["cb_state_change"] = new { state = "half_open", warning = true, soul_version = 42 },
            ["ledger_entry_id"] = "lid_abc",
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK, WrapToolResult(responseData));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var r = await client.EnforceCovenantAsync(
            subjectId:     "user:alice",
            actionKind:    "refund.issue",
            actionPayload: new Dictionary<string, object?> { ["amount"] = 9900 });

        Assert.Equal("review", r.Verdict);
        Assert.False(r.Allow);
        Assert.False(r.Blocked);
        Assert.Equal(2, r.PolicyIds.Count);
        Assert.Equal("half_open", r.CbStateChange.State);
        Assert.True(r.CbStateChange.Warning);
        Assert.Equal(42, r.CbStateChange.SoulVersion);
        Assert.Equal("lid_abc", r.LedgerEntryId);

        // Verify wire shape: snake_case fields, JSON-RPC envelope
        Assert.NotNull(stub.LastRequestBody);
        Assert.Contains("\"jsonrpc\":\"2.0\"", stub.LastRequestBody);
        Assert.Contains("\"method\":\"tools/call\"", stub.LastRequestBody);
        Assert.Contains("\"name\":\"enforce_covenant\"", stub.LastRequestBody);
        Assert.Contains("\"subject_id\":\"user:alice\"", stub.LastRequestBody);
        Assert.Contains("\"action_kind\":\"refund.issue\"", stub.LastRequestBody);
    }

    [Fact]
    public async Task EnforceCovenant_BlockedSetsBlockedFlag()
    {
        var responseData = new Dictionary<string, object?>
        {
            ["verdict"]         = "block",
            ["policy_ids"]      = new[] { "pol_x" },
            ["rationale"]       = "blocked",
            ["cb_state_change"] = new { state = "open", warning = false },
            ["ledger_entry_id"] = "lid_2",
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK, WrapToolResult(responseData));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var r = await client.EnforceCovenantAsync(subjectId: "user:b", actionKind: "x");
        Assert.True(r.Blocked);
        Assert.False(r.Allow);
    }

    [Fact]
    public async Task RecordDecision_ReturnsLedgerEntryIdAndChainHead()
    {
        var responseData = new Dictionary<string, object?>
        {
            ["ledger_entry_id"] = "lid_001",
            ["chain_head_hash"] = "0xdeadbeef",
            ["index"]           = 1234,
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK, WrapToolResult(responseData));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var r = await client.RecordDecisionAsync(
            subjectId:    "user:alice",
            decisionKind: "content_generated",
            outcome:      "completed");

        Assert.Equal("lid_001", r.LedgerEntryId);
        Assert.Equal("0xdeadbeef", r.ChainHeadHash);
        Assert.Equal(1234, r.Index);
    }

    [Fact]
    public async Task QueryCorpus_DecodesMatches()
    {
        var responseData = new Dictionary<string, object?>
        {
            ["matches"] = new object[]
            {
                new { canon_id = "cn_a", canon_version = "1.0.0",
                      section = "policies/refund-cap", excerpt = "Block refunds over $500.",
                      relevance = 1.15 },
            },
            ["scope"]       = "installed",
            ["canon_count"] = 1,
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK, WrapToolResult(responseData));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var r = await client.QueryCorpusAsync(query: "refund");
        Assert.Single(r.Matches);
        Assert.Equal("cn_a", r.Matches[0].CanonId);
        Assert.Equal(1.15, r.Matches[0].Relevance, 3);
    }

    [Fact]
    public async Task GetSubjectSoul_DecodesTagsAndTraces()
    {
        var responseData = new Dictionary<string, object?>
        {
            ["subject_id"]    = "user:alice",
            ["snapshot_at"]   = "2026-05-30T00:00:00Z",
            ["soul_version"]  = 42,
            ["retired_count"] = 3,
            ["tags"] = new object[]
            {
                new { tag_id = "risk:moderate", score = 0.6, raw_strength = 0.55,
                      evidence_count = 12,
                      first_observed_at = "2026-01-01T00:00:00Z",
                      last_reinforced_at = "2026-05-30T00:00:00Z" },
            },
            ["recent_traces"] = new object[]
            {
                new { trace_id = "tr_1", frame_id = "fr_1", verdict = "review",
                      outcome = "applied", started_at = "2026-05-30T00:00:00Z",
                      finished_at = "2026-05-30T00:00:01Z" },
            },
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK, WrapToolResult(responseData));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var s = await client.GetSubjectSoulAsync(subjectId: "user:alice");
        Assert.Equal(42, s.SoulVersion);
        Assert.Equal(3, s.RetiredCount);
        Assert.Single(s.Tags);
        Assert.Equal("risk:moderate", s.Tags[0].TagId);
        Assert.Equal(0.6, s.Tags[0].Score, 3);
        Assert.Single(s.RecentTraces);
        Assert.Equal("review", s.RecentTraces[0].Verdict);
    }

    // ----- Resources -----------------------------------------------------

    [Fact]
    public async Task WorkspacePolicies_DecodesContentBlock()
    {
        var payload = new
        {
            workspace_id = "ws_t", count = 2,
            policies = new object[]
            {
                new { cb_policy_id = "pol_a", name = "refund-cap", description = "x",
                      scope = "subject", action = "block", enabled = true,
                      rules = Array.Empty<object>(), updated_at = "2026-05-01T00:00:00Z" },
                new { cb_policy_id = "pol_b", name = "pii-leak", description = "y",
                      scope = "interaction", action = "review", enabled = false,
                      rules = Array.Empty<object>(), updated_at = "2026-05-02T00:00:00Z" },
            },
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapResourceRead(payload, "concordia:/workspace/policies"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var pols = await client.WorkspacePoliciesAsync();
        Assert.Equal(2, pols.Count);
        Assert.Equal("refund-cap", pols[0].Name);
        Assert.True(pols[0].Enabled);
        Assert.False(pols[1].Enabled);
    }

    [Fact]
    public async Task RecentLedger_DecodesPaginationCursor()
    {
        var payload = new
        {
            workspace_id = "ws", since = 0, limit = 3, count = 3,
            next_since   = 3,
            entries = new object[]
            {
                new { event_id = "e0", index = 0, prev_hash = "G",
                      payload_hash = "p", hash = "h0",
                      payload = new { }, recorded_at = "t" },
                new { event_id = "e1", index = 1, prev_hash = "h0",
                      payload_hash = "p", hash = "h1",
                      payload = new { }, recorded_at = "t" },
                new { event_id = "e2", index = 2, prev_hash = "h1",
                      payload_hash = "p", hash = "h2",
                      payload = new { }, recorded_at = "t" },
            },
        };
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapResourceRead(payload, "concordia:/workspace/recent-ledger?since=0&limit=3"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var page = await client.RecentLedgerAsync(since: 0, limit: 3);
        Assert.Equal(3, page.Count);
        Assert.Equal(3, page.NextSince);
        Assert.Equal(0, page.Entries[0].Index);
        Assert.Equal("h2", page.Entries[2].Hash);
    }

    // ----- Error mapping -------------------------------------------------

    [Fact]
    public async Task Error32001MapsToAuthException()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapError(-32001, "auth_expired", "key revoked"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        await Assert.ThrowsAsync<ConcordiaAuthException>(() => client.PingAsync());
    }

    [Fact]
    public async Task Error32004MapsToCanonNotInstalledException()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapError(-32004, "canon_not_installed", "missing canon"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var ex = await Assert.ThrowsAsync<ConcordiaCanonNotInstalledException>(() =>
            client.QueryCorpusAsync(query: "x", canonFilter: new[] { "cn_bogus" }));
        Assert.Equal("canon_not_installed", ex.ErrorId);
        Assert.Equal(-32004, ex.Code);
    }

    [Fact]
    public async Task Error32005MapsToSubjectNotFoundException()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapError(-32005, "subject_not_found", "no such subject"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        await Assert.ThrowsAsync<ConcordiaSubjectNotFoundException>(() =>
            client.GetSubjectSoulAsync(subjectId: "user:ghost"));
    }

    [Fact]
    public async Task Error32007MapsToPermissionDeniedException()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapError(-32007, "permission_denied", "viewer cannot write"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        await Assert.ThrowsAsync<ConcordiaPermissionDeniedException>(() =>
            client.EnforceCovenantAsync(subjectId: "x", actionKind: "y"));
    }

    [Fact]
    public async Task UnknownAppCodeFallsBackToBaseConcordiaException()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.OK,
            WrapError(-32099, "unknown_xyz", "weird"));
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var ex = await Assert.ThrowsAsync<ConcordiaException>(() =>
            client.EnforceCovenantAsync(subjectId: "x", actionKind: "y"));
        // Not one of the typed subclasses
        Assert.False(ex is ConcordiaAuthException);
        Assert.Equal(-32099, ex.Code);
    }

    [Fact]
    public async Task Non200HttpWrapsAsProtocolException()
    {
        using var stub = new StubHttpMessageHandler(HttpStatusCode.BadGateway, "nope");
        using var client = new ConcordiaClient(apiKey: TestApiKey, handler: stub);

        var ex = await Assert.ThrowsAsync<ConcordiaProtocolException>(() => client.PingAsync());
        Assert.Contains("HTTP 502", ex.Message);
    }
}
