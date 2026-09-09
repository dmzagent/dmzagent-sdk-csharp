using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DMZAgent.Sdk;
using FluentAssertions;
using Xunit;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// The white-label approval control and the readable ledger (spec §2.8–§2.10).
///
/// <para>What these hold, and why each is here rather than an assertion that
/// merely passes:</para>
/// <list type="bullet">
///   <item><c>actorId</c> is refused locally and before any round trip. The
///   point of a human-in-the-loop control is that a person is on the other
///   end, and an approval whose actor is the integration that requested it
///   records nobody. The test asserts <em>no request was made</em>, because a
///   server-side rejection would also throw and would tell us nothing about
///   where the check lives.</item>
///   <item>A settled or expired approval is a conflict, not a retry. The call
///   did not fail, it lost.</item>
///   <item>Expiry declines, and <c>OnExpiry</c> cannot be talked into anything
///   else by a server that sends something else.</item>
///   <item>Neither list method follows a cursor on its own; the async
///   enumerables do, and only when the consumer asks for the next item.</item>
///   <item>There is no method that closes an incident, because the ledger has
///   no endpoint for one.</item>
/// </list>
/// </summary>
public sealed class ApprovalsAndLedgerTests
{
    private const string Key = "ck_test_approvals";

    //: How many requests past the last scripted body the stub tolerates
    //: before it calls the walk unbounded. Only a client that keeps
    //: following a cursor nobody advanced ever reaches it.
    private const int RunawayAfter = 5;

    private static string ApprovalJson(string status = "pending", string? onExpiry = "decline",
                                       string? decision = null)
        => $$"""
        {
          "approval_id": "apr_7f3c9a1b",
          "status": "{{status}}",
          "subject_id": "subject:dv:checkout-bot",
          "interaction_id": "ix_2b8e",
          "frame_id": "fr_91ac",
          "action": { "tool": "refund.issue", "args": { "amount_cents": 9900 } },
          "reason": "refund above the reviewed ceiling",
          "fired_policies": [
            { "cb_policy_id": "cbp_11", "name": "refund ceiling", "action": "require_approval" }
          ],
          "requested_at": "2026-09-09T12:00:00Z",
          "expires_at": "2026-09-09T12:15:00Z",
          "on_expiry": "{{onExpiry}}",
          "anchor": { "ledger_index": 40197, "hash": "b1c4" },
          "decision": {{decision ?? "null"}}
        }
        """;

    private static string IncidentJson(string status = "remediated", string remediations =
        """
        [{ "remediation_id": "rem_88fe", "kind": "approval", "approval_id": "apr_7f3c9a1b",
           "outcome": "approved", "actor_id": "acct_4471", "reason": "verified by phone",
           "occurred_at": "2026-09-09T12:04:31Z",
           "anchor": { "ledger_index": 40202, "hash": "9ee0" } }]
        """)
        => $$"""
        {
          "incident_id": "inc_5d2a70",
          "status": "{{status}}",
          "kind": "cb_open",
          "subject_id": "subject:dv:checkout-bot",
          "frame_id": "fr_91ac",
          "opened_at": "2026-09-09T11:58:02Z",
          "closed_at": {{(status == "open" ? "null" : "\"2026-09-09T12:04:31Z\"")}},
          "reason": "refund above the reviewed ceiling",
          "fired_policies": [],
          "remediations": {{remediations}},
          "anchor": { "ledger_index": 40197, "hash": "b1c4" }
        }
        """;

    /// <summary>
    /// Serves each scripted body in turn, repeating the last — then refuses.
    /// </summary>
    /// <remarks>
    /// The repeat matters: a page carrying <c>next_cursor</c> is served again
    /// and again, which is exactly what a client that auto-paginates keeps
    /// asking for. Left unbounded, the stub would let that client
    /// <em>hang</em>, and a hang is not a failing assertion — it is a test
    /// timeout with no test named. So the stub stops, and the unbounded walk
    /// becomes a named failure in the test that provoked it.
    /// </remarks>
    private sealed class Serving : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<HttpRequestMessage> Seen { get; } = new();

        private readonly string[]     _bodies;
        private readonly HttpStatusCode _status;

        public Serving(HttpStatusCode status, params string[] bodies)
        {
            _status = status;
            _bodies = bodies.Length == 0 ? new[] { "{}" } : bodies;
        }

        public static Serving Ok(params string[] bodies)
            => new(HttpStatusCode.OK, bodies);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            Calls++;
            if (Calls > _bodies.Length + RunawayAfter)
            {
                throw new InvalidOperationException(
                    $"unbounded pagination: {Calls} requests for {_bodies.Length} "
                    + "scripted page(s) — the caller asked for one page and the "
                    + "SDK kept following the cursor");
            }
            Seen.Add(request);
            var body = _bodies[Math.Min(Calls - 1, _bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static DMZAgentClient Client(Serving s) => new(Key, handler: s);

    private static Dictionary<string, string> QueryOf(HttpRequestMessage r)
    {
        var nv = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
        return nv.AllKeys.Where(k => k is not null)
                 .ToDictionary(k => k!, k => nv[k]!);
    }

    // ------------------------------------------------------------------ //
    // The distinction the whole design turns on
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ADenialThatNamesAnApprovalIsAnAsk()
    {
        var stub = Serving.Ok("""
            { "state": "open", "allow": false, "warning": false,
              "reason": "refund above the reviewed ceiling",
              "pending_approval_id": "apr_7f3c9a1b" }
            """);
        using var cx = Client(stub);
        var r = await cx.CheckAsync(subjectId: "subject:dv:bot");
        r.Allow.Should().BeFalse();
        r.AwaitingApproval.Should().BeTrue();
        r.PendingApprovalId.Should().Be("apr_7f3c9a1b");
    }

    [Fact]
    public async Task APlainDenialIsNotAwaitingAnything()
    {
        var stub = Serving.Ok("""{ "state": "open", "allow": false, "warning": false }""");
        using var cx = Client(stub);
        var r = await cx.CheckAsync(subjectId: "subject:dv:bot");
        r.Allow.Should().BeFalse();
        r.AwaitingApproval.Should().BeFalse();
        r.PendingApprovalId.Should().BeNull();
    }

    [Fact]
    public async Task AnOlderResponseWithoutTheFieldStillRefuses()
    {
        // The field is additive on purpose: a client reading Allow alone must
        // not start allowing what it used to deny.
        var stub = Serving.Ok(
            """{ "state": "open", "allow": false, "warning": false, "reason": "policy fired" }""");
        using var cx = Client(stub);
        var r = await cx.CheckAsync(subjectId: "subject:dv:bot");
        r.Allow.Should().BeFalse();
        r.PendingApprovalId.Should().BeNull();
    }

    // ------------------------------------------------------------------ //
    // A decision records a human, or it does not happen
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ADecisionWithNoHumanIsRefusedBeforeAnyRequest(string? actor)
    {
        var stub = Serving.Ok(ApprovalJson("approved"));
        using var cx = Client(stub);

        var act = async () => await cx.DecideApprovalAsync("apr_7f3c9a1b", "approve", actor!);

        (await act.Should().ThrowAsync<DMZAgentValidationException>())
            .WithMessage("*actor*");
        // The assertion that matters. A server-side rejection would throw
        // too, and would not tell us the check is where the mistake is.
        stub.Calls.Should().Be(0, "a decision with no human must not reach the wire");
    }

    [Fact]
    public async Task AnUnknownDecisionIsRefusedBeforeAnyRequest()
    {
        var stub = Serving.Ok(ApprovalJson("approved"));
        using var cx = Client(stub);

        var act = async () => await cx.DecideApprovalAsync("apr_7f3c9a1b", "maybe", "acct_1");

        (await act.Should().ThrowAsync<DMZAgentValidationException>())
            .WithMessage("*decision*");
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SendsTheActorAndReturnsTheDecision()
    {
        var decision = """
            { "decision": "approve", "actor_id": "acct_4471", "actor_label": "Dana R.",
              "reason": "verified the order by phone", "decided_at": "2026-09-09T12:04:31Z" }
            """;
        var stub = Serving.Ok(ApprovalJson("approved", decision: decision));
        using var cx = Client(stub);

        var a = await cx.DecideApprovalAsync(
            "apr_7f3c9a1b", "approve", "acct_4471", "Dana R.", "verified the order by phone");

        stub.Seen.Should().HaveCount(1);
        stub.Seen[0].Method.Method.Should().Be("POST");
        stub.Seen[0].RequestUri!.AbsolutePath
            .Should().Be("/v1/approvals/apr_7f3c9a1b/decision");
        a.Status.Should().Be("approved");
        a.Decision.Should().NotBeNull();
        a.Decision!.ActorId.Should().Be("acct_4471");
        a.Decision!.ActorLabel.Should().Be("Dana R.");
        a.IsPending.Should().BeFalse();
        a.Tool.Should().Be("refund.issue");
    }

    [Fact]
    public async Task TheWrappersStillRequireAHuman()
    {
        var stub = Serving.Ok(ApprovalJson("approved"));
        using var cx = Client(stub);

        var approve = async () => await cx.ApproveApprovalAsync("apr_1", "");
        var decline = async () => await cx.DeclineApprovalAsync("apr_1", "");

        await approve.Should().ThrowAsync<DMZAgentValidationException>();
        await decline.Should().ThrowAsync<DMZAgentValidationException>();
        stub.Calls.Should().Be(0);
    }

    // ------------------------------------------------------------------ //
    // A settled approval is lost, not failed
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("approved")]
    [InlineData("declined")]
    [InlineData("expired")]
    public async Task ASecondDecisionIsAConflictNamingItsState(string settled)
    {
        var stub = new Serving(HttpStatusCode.Conflict,
            $$"""{ "detail": "already settled", "status": "{{settled}}" }""");
        using var cx = Client(stub);

        var act = async () => await cx.DecideApprovalAsync(
            "apr_7f3c9a1b", "approve", "acct_9002");

        (await act.Should().ThrowAsync<DMZAgentConflictException>())
            .WithMessage($"*already {settled}*");
    }

    [Fact]
    public async Task A409WithoutAnApprovalStatusStillReadsAsTheIdempotencyConflict()
    {
        // The other 409. Same type, and the message must not assert the
        // wrong cause.
        var stub = new Serving(HttpStatusCode.Conflict, """{ "detail": "in flight" }""");
        using var cx = Client(stub);

        var act = async () => await cx.DecideApprovalAsync(
            "apr_7f3c9a1b", "approve", "acct_4471");

        (await act.Should().ThrowAsync<DMZAgentConflictException>())
            .WithMessage("*Idempotency-Key*");
    }

    // ------------------------------------------------------------------ //
    // Expiry fails closed
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task OnExpiryIsDeclineEvenIfTheServerSaysOtherwise()
    {
        // An approval that becomes an allow because nobody looked at it is
        // not a human-in-the-loop control. There is no path — server-sent or
        // otherwise — by which this SDK reports one.
        var stub = Serving.Ok(
            $$"""{ "approvals": [{{ApprovalJson("pending", onExpiry: "approve")}}] }""");
        using var cx = Client(stub);
        var a = (await cx.ListApprovalsAsync()).Approvals[0];
        a.OnExpiry.Should().Be("decline");
    }

    [Fact]
    public async Task AnExpiredApprovalCarriesNoDecision()
    {
        var stub = Serving.Ok(
            $$"""{ "approvals": [{{ApprovalJson("expired")}}] }""");
        using var cx = Client(stub);
        var a = (await cx.ListApprovalsAsync("expired")).Approvals[0];
        a.Decision.Should().BeNull();
        a.IsPending.Should().BeFalse();
    }

    // ------------------------------------------------------------------ //
    // Paging: bounded by default, lazy on request
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ListApprovalsDefaultsToPendingAndDoesNotFollowTheCursor()
    {
        var stub = Serving.Ok(
            $$"""{ "approvals": [{{ApprovalJson()}}], "next_cursor": "c2" }""");
        using var cx = Client(stub);

        var page = await cx.ListApprovalsAsync();

        stub.Calls.Should().Be(1, "one page means one request");
        QueryOf(stub.Seen[0])["status"].Should().Be("pending");
        page.NextCursor.Should().Be("c2");
        page.Count.Should().Be(1);
    }

    [Fact]
    public async Task ListApprovalsPassesEveryFilter()
    {
        var stub = Serving.Ok("""{ "approvals": [], "next_cursor": null }""");
        using var cx = Client(stub);

        await cx.ListApprovalsAsync("approved", "subject:dv:bot", 50, "eyJpIjo0MH0");

        QueryOf(stub.Seen[0]).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["status"]     = "approved",
            ["subject_id"] = "subject:dv:bot",
            ["limit"]      = "50",
            ["cursor"]     = "eyJpIjo0MH0",
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(1000)]
    public async Task APageLimitTheServerWouldRejectIsRefusedBeforeTheRoundTrip(int bad)
    {
        var stub = Serving.Ok("""{ "approvals": [] }""");
        using var cx = Client(stub);

        var act = async () => await cx.ListApprovalsAsync(limit: bad);

        (await act.Should().ThrowAsync<DMZAgentValidationException>())
            .WithMessage("*limit*");
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task IterApprovalsFetchesAPageOnlyWhenAskedPastTheOneItHolds()
    {
        var stub = Serving.Ok(
            $$"""{ "approvals": [{{ApprovalJson()}}, {{ApprovalJson()}}], "next_cursor": "c2" }""",
            $$"""{ "approvals": [{{ApprovalJson()}}], "next_cursor": null }""");
        using var cx = Client(stub);

        await using var it = cx.IterApprovalsAsync().GetAsyncEnumerator();

        (await it.MoveNextAsync()).Should().BeTrue();
        stub.Calls.Should().Be(1, "the first item must not have fetched page two");
        (await it.MoveNextAsync()).Should().BeTrue();
        stub.Calls.Should().Be(1);
        (await it.MoveNextAsync()).Should().BeTrue();   // fetches page two
        stub.Calls.Should().Be(2);
        (await it.MoveNextAsync()).Should().BeFalse();
        stub.Calls.Should().Be(2, "a null cursor must end the walk");
    }

    [Fact]
    public async Task BreakingOutNeverRequestsTheNextPage()
    {
        var stub = Serving.Ok(
            $$"""{ "approvals": [{{ApprovalJson()}}], "next_cursor": "c2" }""");
        using var cx = Client(stub);

        await foreach (var _ in cx.IterApprovalsAsync()) break;

        stub.Calls.Should().Be(1);
    }

    // ------------------------------------------------------------------ //
    // The ledger
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task GetIncidentsDefaultsToAllAndParsesRemediations()
    {
        var stub = Serving.Ok(
            $$"""{ "incidents": [{{IncidentJson()}}], "next_cursor": null }""");
        using var cx = Client(stub);

        var page = await cx.GetIncidentsAsync();

        QueryOf(stub.Seen[0])["status"].Should().Be("all");
        var inc = page.Incidents[0];
        inc.Status.Should().Be("remediated");
        inc.IsOpen.Should().BeFalse();
        inc.Remediations.Should().HaveCount(1);
        inc.Remediations[0].Kind.Should().Be("approval");
        inc.Remediations[0].ApprovalId.Should().Be("apr_7f3c9a1b");
        inc.Remediations[0].Anchor.Should().NotBeNull();
    }

    [Fact]
    public async Task GetIncidentsPassesTheWholeWindow()
    {
        var stub = Serving.Ok("""{ "incidents": [] }""");
        using var cx = Client(stub);

        await cx.GetIncidentsAsync("open", "subject:dv:bot",
            "2026-09-01T00:00:00Z", "2026-09-09T00:00:00Z", 100);

        QueryOf(stub.Seen[0]).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["status"]     = "open",
            ["subject_id"] = "subject:dv:bot",
            ["since"]      = "2026-09-01T00:00:00Z",
            ["until"]      = "2026-09-09T00:00:00Z",
            ["limit"]      = "100",
        });
    }

    [Fact]
    public async Task AnUnansweredIncidentIsAnIncidentWithNoRemediations()
    {
        // Not an error, not an empty result, and not collapsed to null.
        var stub = Serving.Ok(
            $$"""{ "incidents": [{{IncidentJson("open", "[]")}}] }""");
        using var cx = Client(stub);

        var inc = (await cx.GetIncidentsAsync("open")).Incidents[0];

        inc.IsOpen.Should().BeTrue();
        inc.Remediations.Should().BeEmpty();
        inc.ClosedAt.Should().BeNull();
    }

    [Fact]
    public async Task TheIncidentAnchorIsTheOneTheCheckHandedBack()
    {
        // The whole point of making the ledger readable: an anchor recorded
        // at check time finds exactly this entry, and the hashes compare.
        var checkStub = Serving.Ok("""
            { "state": "open", "allow": false, "warning": false, "reason": "ceiling",
              "anchor": { "ledger_index": 40197, "hash": "b1c4" } }
            """);
        using var checkCx = Client(checkStub);
        var checkResult = await checkCx.CheckAsync(subjectId: "subject:dv:bot");

        var stub = Serving.Ok($$"""{ "incidents": [{{IncidentJson()}}] }""");
        using var cx = Client(stub);
        var inc = (await cx.GetIncidentsAsync()).Incidents[0];

        inc.Anchor.Should().BeEquivalentTo(checkResult.Anchor);
    }

    [Fact]
    public async Task IterIncidentsWalksPagesLazily()
    {
        var stub = Serving.Ok(
            $$"""{ "incidents": [{{IncidentJson()}}], "next_cursor": "c2" }""",
            $$"""{ "incidents": [{{IncidentJson()}}], "next_cursor": null }""");
        using var cx = Client(stub);

        var got = new List<Incident>();
        await foreach (var i in cx.IterIncidentsAsync()) got.Add(i);

        got.Should().HaveCount(2);
        stub.Calls.Should().Be(2);
    }

    [Fact]
    public void TheLedgerIsAppendOnlyInTheSurfaceToo()
    {
        // A convenience that reads as closing an incident would describe a
        // ledger this is not — there is no endpoint behind one (§5.21).
        var forbidden = new[]
        {
            "CloseIncidentAsync", "ResolveIncidentAsync",
            "DeleteIncidentAsync", "UpdateIncidentAsync",
        };
        var found = typeof(DMZAgentClient).GetMethods()
            .Select(m => m.Name)
            .Where(n => forbidden.Contains(n))
            .ToList();
        found.Should().BeEmpty("unexpected ledger mutators");
    }
}
