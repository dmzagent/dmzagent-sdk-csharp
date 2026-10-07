using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DMZAgent.Sdk;
using FluentAssertions;
using Xunit;

namespace DMZAgent.Sdk.Tests;

/// <summary>
/// Agent mode: a session governed one step at a time, and the conduct it
/// shows (spec §1.9, §2.11–§2.13, §5.22–§5.25).
///
/// <para>What these hold, and why each is here rather than an assertion that
/// merely passes:</para>
/// <list type="bullet">
///   <item>An unanswered step is not a yes. A directive this build does not
///   know reads as <c>block</c>, and a step whose answer cannot be read
///   throws instead of returning something shaped like an answer.</item>
///   <item>A malformed step is refused locally. Each refusal asserts <em>no
///   request was made</em>, because a server-side 400 would also throw and
///   would tell us nothing about where the check lives.</item>
///   <item>The SDK never invents an <c>Idempotency-Key</c>; the header is on
///   the wire exactly when the caller gave one.</item>
///   <item>The session handle holds its two ids and nothing else, so it
///   cannot infer <c>attempt_of</c> from a refusal it remembers.</item>
///   <item>The conduct record is read a page at a time unless the caller
///   names the walk, and nothing in the surface edits it.</item>
/// </list>
/// </summary>
public sealed class AgentModeTests
{
    private const string Key     = "ck_test_agentmode";
    private const string Agent   = "subject:dv_test:agent-a";
    private const string Session = "sess_4b1e";

    //: As in ApprovalsAndLedgerTests: past this many requests beyond the
    //: script the walk is called unbounded, so a runaway cursor is a named
    //: failure and not a hung test.
    private const int RunawayAfter = 5;

    private static string StepJson(
        string directive = "proceed", string? approvalId = null, string scope = "null",
        string settled = "true", string behaviors = "[]")
        => $$"""
        {
          "frame_id": "fr_7c21",
          "interaction_id": "{{Session}}",
          "directive": "{{directive}}",
          "scope": {{scope}},
          "reason": "remote writes are the runner's",
          "approval_id": {{(approvalId is null ? "null" : $"\"{approvalId}\"")}},
          "settled": {{settled}},
          "behaviors": {{behaviors}},
          "anchor": { "ledger_index": 40311, "hash": "c0d9" },
          "livemode": false
        }
        """;

    private static string BehaviorJson(string id = "bhv_19ac", string polarity = "negative")
        => $$"""
        {
          "behavior_id": "{{id}}",
          "subject_id": "{{Agent}}",
          "interaction_id": "{{Session}}",
          "tag": "circumvention",
          "polarity": "{{polarity}}",
          "strength": 0.82,
          "source": "reasoning",
          "evidence": ["fr_7b90", "fr_7c21"],
          "calls": ["call_12", "call_14"],
          "observed_at": "2026-10-07T15:02:11Z",
          "anchor": { "ledger_index": 40312, "hash": "77ab" }
        }
        """;

    private const string ApprovalJson = """
        {
          "approval_id": "apr_9",
          "status": "approved",
          "subject_id": "subject:dv_test:agent-a",
          "interaction_id": "sess_4b1e",
          "frame_id": "fr_7c21",
          "action": { "tool": "Bash", "args": { "command": "git push origin HEAD" } },
          "reason": "needs a person",
          "fired_policies": [],
          "requested_at": "2026-10-07T15:00:00Z",
          "expires_at": "2026-10-07T15:15:00Z",
          "on_expiry": "decline",
          "anchor": null,
          "decision": { "decision": "approve", "actor_id": "acct_4471",
                        "decided_at": "2026-10-07T15:04:00Z" }
        }
        """;

    /// <summary>
    /// Records every request and its body, serves each scripted response in
    /// turn (repeating the last), and refuses a runaway walk.
    /// </summary>
    private sealed class Serving : HttpMessageHandler
    {
        public int Calls => Seen.Count;
        public List<HttpRequestMessage> Seen { get; } = new();
        public List<string?> Bodies { get; } = new();

        private readonly (HttpStatusCode Status, string Body)[] _script;
        private readonly Exception? _throw;

        private Serving((HttpStatusCode, string)[] script, Exception? toThrow = null)
        {
            _script = script.Length == 0 ? new[] { (HttpStatusCode.OK, "{}") } : script;
            _throw  = toThrow;
        }

        public static Serving Ok(params string[] bodies)
            => new(bodies.Select(b => (HttpStatusCode.OK, b)).ToArray());

        public static Serving Status(HttpStatusCode status, string body)
            => new(new[] { (status, body) });

        public static Serving Throwing(Exception e) => new(Array.Empty<(HttpStatusCode, string)>(), e);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            if (Seen.Count >= _script.Length + RunawayAfter)
            {
                throw new InvalidOperationException(
                    $"unbounded pagination: {Seen.Count + 1} requests for {_script.Length} "
                    + "scripted page(s)");
            }
            Seen.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            if (_throw is not null) throw _throw;
            var (status, body) = _script[Math.Min(Seen.Count - 1, _script.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static DMZAgentClient Client(Serving s) => new(Key, handler: s);

    private static Dictionary<string, string> QueryOf(HttpRequestMessage r)
    {
        var nv = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
        return nv.AllKeys.Where(k => k is not null)
                 .ToDictionary(k => k!, k => nv[k]!);
    }

    private static JsonElement BodyOf(Serving s, int i = 0)
        => JsonDocument.Parse(s.Bodies[i]!).RootElement;

    private static Dictionary<string, object?> Args(string command)
        => new() { ["command"] = command };

    // ------------------------------------------------------------------ //
    // The one question a harness asks: may this call run?
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("proceed",    true)]
    [InlineData("warn",       true)]
    [InlineData("hold",       false)]
    [InlineData("block",      false)]
    [InlineData("shutdown",   false)]
    // Appendix B lets the server add a directive. A word this build has
    // never seen is not a yes, whatever it is, however it is cased.
    [InlineData("quarantine", false)]
    [InlineData("PROCEED",    false)]
    [InlineData("",           false)]
    public async Task RunsIsTrueExactlyForProceedAndWarn(string directive, bool runs)
    {
        var stub = Serving.Ok(StepJson(directive));
        using var cx = Client(stub);

        var r = await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash");

        r.Runs.Should().Be(runs);
        r.Directive.Should().Be(directive, "the raw word is exposed, known or not");
    }

    [Fact]
    public async Task RunsIsDerivedSoACopyCannotDisagreeWithItsDirective()
    {
        using var cx = Client(Serving.Ok(StepJson("proceed")));
        var r = await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash");
        r.Runs.Should().BeTrue();

        var blocked = r with { Directive = "block" };
        blocked.Runs.Should().BeFalse();
    }

    [Fact]
    public async Task AHoldNamesItsApprovalAndDoesNotRun()
    {
        var stub = Serving.Ok(StepJson("hold", approvalId: "apr_9", scope: "\"interaction\""));
        using var cx = Client(stub);

        var r = await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash");

        r.Runs.Should().BeFalse();
        r.ApprovalId.Should().Be("apr_9");
        r.Scope.Should().Be("interaction");
    }

    [Fact]
    public async Task TheAnswerIsReadWhole()
    {
        var stub = Serving.Ok(StepJson("block", scope: "\"interaction\"", settled: "false",
            behaviors: """
            [{ "tag": "circumvention", "polarity": "negative", "strength": 0.82,
               "source": "reasoning", "evidence": ["fr_7b90", "fr_7c21"],
               "calls": ["call_12", "call_14"] }]
            """));
        using var cx = Client(stub);

        var r = await cx.AgentStepAsync(Agent, Session, "call", callId: "call_14", tool: "Bash");

        r.FrameId.Should().Be("fr_7c21");
        r.InteractionId.Should().Be(Session);
        r.Reason.Should().Be("remote writes are the runner's");
        r.Settled.Should().BeFalse();
        r.Livemode.Should().BeFalse();
        r.Anchor.Should().NotBeNull();
        r.Behaviors.Should().ContainSingle();
        var b = r.Behaviors[0];
        b.Tag.Should().Be("circumvention");
        b.Polarity.Should().Be("negative");
        b.Strength.Should().Be(0.82);
        b.Source.Should().Be("reasoning");
        b.Evidence.Should().Equal("fr_7b90", "fr_7c21");
        b.Calls.Should().Equal("call_12", "call_14");
        // Not read from the record, so none of the record's own fields.
        b.BehaviorId.Should().BeNull();
        b.ObservedAt.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownPolarityAndAnUnknownTagAreKeptAsSent()
    {
        var stub = Serving.Ok(StepJson(behaviors: """
            [{ "tag": "kept-its-word", "polarity": "neutral", "strength": 0.5,
               "source": "logic", "evidence": [] }]
            """));
        using var cx = Client(stub);

        var r = await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash");

        r.Behaviors[0].Tag.Should().Be("kept-its-word");
        r.Behaviors[0].Polarity.Should().Be("neutral");
        r.Behaviors[0].Calls.Should().BeEmpty("calls MAY be absent and reads as none");
    }

    [Fact]
    public async Task AnAnswerThatDoesNotSaySettledIsNotSettled()
    {
        var stub = Serving.Ok("""{ "frame_id": "fr_1", "interaction_id": "s", "directive": "proceed" }""");
        using var cx = Client(stub);

        var r = await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash");

        r.Settled.Should().BeFalse();
        r.Livemode.Should().BeNull("§7.16: a livemode the server omitted is unknown, not test mode");
        r.Behaviors.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ //
    // An unanswered step is not a yes
    // ------------------------------------------------------------------ //

    [Theory]
    [InlineData("""{ "frame_id": "fr_1", "interaction_id": "s" }""")]
    [InlineData("""{ "frame_id": "fr_1", "directive": null }""")]
    [InlineData("""{ "directive": 1 }""")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("<html>gateway</html>")]
    public async Task ASuccessWithNoReadableDirectiveThrows(string body)
    {
        var stub = Serving.Ok(body);
        using var cx = Client(stub);

        StepResult? returned = null;
        var act = async () => returned = await cx.AgentStepAsync(
            Agent, Session, "call", callId: "call_1", tool: "Bash");

        // ServerError with the response's status (§1.9): the fault is on the
        // server's side of the wire.
        (await act.Should().ThrowAsync<DMZAgentServerException>())
            .Which.StatusCode.Should().Be(200);
        returned.Should().BeNull();
    }

    [Fact]
    public async Task AServerErrorThrowsAndReturnsNothing()
    {
        var stub = Serving.Status(HttpStatusCode.ServiceUnavailable, """{ "detail": "unavailable" }""");
        using var cx = Client(stub);

        var act = async () => await cx.AgentStepAsync(
            Agent, Session, "call", callId: "call_1", tool: "Bash");

        (await act.Should().ThrowAsync<DMZAgentServerException>())
            .Which.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task AStepThatCannotBeSentThrows()
    {
        var stub = Serving.Throwing(new HttpRequestException("connection refused"));
        using var cx = Client(stub);

        var act = async () => await cx.AgentStepAsync(
            Agent, Session, "call", callId: "call_1", tool: "Bash");

        (await act.Should().ThrowAsync<DMZAgentServerException>())
            .Which.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ------------------------------------------------------------------ //
    // A malformed step is refused where the mistake is
    // ------------------------------------------------------------------ //

    public static IEnumerable<object?[]> MalformedSteps()
    {
        // name, message fragment, the step as (agent, interaction, phase,
        // callId, tool, status, refusedBy, hasIntent)
        yield return new object?[] { "unknown phase",       "phase",        Agent, Session, "plan",   null,     null,   null,      null,       false };
        yield return new object?[] { "no phase",            "phase",        Agent, Session, null,     null,     null,   null,      null,       false };
        yield return new object?[] { "phase is cased",      "phase",        Agent, Session, "Call",   "call_1", "Bash", null,      null,       false };
        yield return new object?[] { "no agent",            "agentSubject", "",    Session, "intent", null,     null,   null,      null,       true  };
        yield return new object?[] { "no interaction",      "interaction",  Agent, null,    "intent", null,     null,   null,      null,       true  };
        yield return new object?[] { "call without callId", "callId",       Agent, Session, "call",   null,     "Bash", null,      null,       false };
        yield return new object?[] { "call without tool",   "tool",         Agent, Session, "call",   "call_1", null,   null,      null,       false };
        yield return new object?[] { "result w/o callId",   "callId",       Agent, Session, "result", "",       "Bash", "ok",      null,       false };
        yield return new object?[] { "result w/o tool",     "tool",         Agent, Session, "result", "call_1", "",     "ok",      null,       false };
        yield return new object?[] { "result w/o status",   "status",       Agent, Session, "result", "call_1", "Bash", null,      null,       false };
        yield return new object?[] { "refused w/o refuser", "refused",      Agent, Session, "result", "call_1", "Bash", "refused", null,       false };
        yield return new object?[] { "refuser on ok",       "refused",      Agent, Session, "result", "call_1", "Bash", "ok",      "host",     false };
        yield return new object?[] { "refuser on a call",   "refused",      Agent, Session, "call",   "call_1", "Bash", null,      "governor", false };
        yield return new object?[] { "refuser on intent",   "refused",      Agent, Session, "intent", null,     null,   null,      "harness",  true  };
        yield return new object?[] { "blank interaction",   "interaction",  Agent, "",      "intent", null,     null,   null,      null,       true  };
        yield return new object?[] { "intent w/o intent",   "intent",       Agent, Session, "intent", null,     null,   null,      null,       false };
    }

    [Theory]
    [MemberData(nameof(MalformedSteps))]
    public async Task AMalformedStepIsRefusedBeforeAnyRequest(
        string why, string fragment, string? agent, string? interaction, string? phase,
        string? callId, string? tool, string? status, string? refusedBy, bool hasIntent)
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);

        var act = async () => await cx.AgentStepAsync(
            agent!, interaction!, phase!,
            callId: callId, tool: tool, status: status, refusedBy: refusedBy,
            intent: hasIntent ? new Dictionary<string, object?> { ["text"] = "x" } : null);

        (await act.Should().ThrowAsync<DMZAgentValidationException>(why))
            .WithMessage($"*{fragment}*");
        stub.Calls.Should().Be(0, $"{why}: a malformed step must not reach the wire");
    }

    public static IEnumerable<object?[]> IntentsWithoutText()
    {
        yield return new object?[] { "no text",          new Dictionary<string, object?> { ["paths"] = new[] { "src/" } } };
        yield return new object?[] { "text is null",     new Dictionary<string, object?> { ["text"] = null } };
        yield return new object?[] { "text is a number", new Dictionary<string, object?> { ["text"] = 7L } };
        yield return new object?[] { "text is a list",   new Dictionary<string, object?> { ["text"] = new[] { "x" } } };
    }

    [Theory]
    [MemberData(nameof(IntentsWithoutText))]
    public async Task AnIntentWithoutAStringTextIsRefusedBeforeAnyRequest(
        string why, Dictionary<string, object?> intent)
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);

        var act = async () => await cx.AgentStepAsync(Agent, Session, "intent", intent: intent);

        (await act.Should().ThrowAsync<DMZAgentValidationException>(why)).WithMessage("*text*");
        stub.Calls.Should().Be(0, why);
    }

    [Fact]
    public async Task AnEmptyIntentTextIsStillAString()
    {
        // §5.22 asks for a string text, not a non-empty one.
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);

        await cx.AgentStepAsync(Agent, Session, "intent",
            intent: new Dictionary<string, object?> { ["text"] = "" });

        stub.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("intent", null,     null,   null)]
    [InlineData("call",   "call_1", "Bash", null)]
    [InlineData("result", "call_1", "Bash", "ok")]
    [InlineData("result", "call_1", "Bash", "error")]
    public async Task AWellFormedStepOfEachPhaseIsSent(string phase, string? callId, string? tool, string? status)
    {
        // The other side of every refusal above: a guard that refused
        // everything would pass those and fail here.
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);

        await cx.AgentStepAsync(Agent, Session, phase, callId: callId, tool: tool, status: status,
            intent: phase == "intent" ? new Dictionary<string, object?> { ["text"] = "x" } : null);

        stub.Calls.Should().Be(1);
        stub.Seen[0].Method.Method.Should().Be("POST");
        stub.Seen[0].RequestUri!.AbsolutePath.Should().Be("/v1/agent-stream/step");
    }

    [Fact]
    public async Task AnUnknownStatusOrRefuserIsTheServersToJudge()
    {
        // §5.22 validates presence, not vocabulary: a status or refuser this
        // build does not know may be one the server added (Appendix B).
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);

        await cx.AgentStepAsync(Agent, Session, "result", callId: "c", tool: "t", status: "partial");
        await cx.AgentStepAsync(Agent, Session, "result", callId: "c", tool: "t",
            status: "refused", refusedBy: "sandbox-policy");

        stub.Calls.Should().Be(2);
    }

    // ------------------------------------------------------------------ //
    // Idempotency: the caller's key, or none
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task NoIdempotencyKeyIsSentUnlessOneIsGiven()
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);
        var s = cx.AgentSession(Agent, Session);

        await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash");
        await s.CallAsync("call_2", "Bash");
        await s.ResultAsync("call_2", "Bash", "ok");
        await s.RefusedAsync("call_3", "Bash", "harness");
        await s.IntentAsync("x");

        stub.Seen.Should().HaveCount(5);
        stub.Seen.Should().OnlyContain(r => !r.Headers.Contains("Idempotency-Key"),
            "the SDK never generates a key (§1.8)");
    }

    [Fact]
    public async Task TheCallersIdempotencyKeyIsSentAsGiven()
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);
        var s = cx.AgentSession(Agent, Session);

        await cx.AgentStepAsync(Agent, Session, "call", callId: "call_1", tool: "Bash",
            idempotencyKey: "k-step");
        await s.IntentAsync("x", idempotencyKey: "k-intent");
        await s.CallAsync("call_2", "Bash", idempotencyKey: "k-call");
        await s.ResultAsync("call_2", "Bash", "ok", idempotencyKey: "k-result");
        await s.RefusedAsync("call_3", "Bash", "host", idempotencyKey: "k-refused");

        stub.Seen.Select(r => r.Headers.GetValues("Idempotency-Key").Single())
            .Should().Equal("k-step", "k-intent", "k-call", "k-result", "k-refused");
        // A header, never a body field.
        stub.Bodies.Should().OnlyContain(b => !b!.Contains("idempotency", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ //
    // The wire body
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task EveryFieldGivenTravelsUnderItsWireKey()
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);

        await cx.AgentStepAsync(Agent, Session, "result",
            callId: "call_9", tool: "Bash", status: "refused", refusedBy: "harness",
            reason: "remote writes are the runner's", attemptOf: "call_7",
            occurredAt: "2026-10-07T15:02:11Z",
            metadata: new Dictionary<string, object?> { ["runner"] = "r1" });

        var body = BodyOf(stub);
        body.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            "agent_subject_id", "interaction_id", "phase", "call_id", "tool", "status",
            "refused_by", "reason", "attempt_of", "occurred_at", "metadata",
        });
        body.GetProperty("refused_by").GetString().Should().Be("harness");
        body.GetProperty("attempt_of").GetString().Should().Be("call_7");
        body.TryGetProperty("interaction_kind", out _).Should().BeFalse(
            "a step is an agent_session by definition (§2.11)");
    }

    // ------------------------------------------------------------------ //
    // The session handle
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task EachSessionMethodSendsItsPhase()
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);
        var s = cx.AgentSession(Agent, Session);

        await s.IntentAsync("Add a trace id.", paths: new[] { "src/obs/" }, tools: new[] { "Edit" });
        await s.CallAsync("call_7", "Bash", Args("git push"), attemptOf: "call_5");
        await s.ResultAsync("call_8", "Read", "error", result: "ENOENT", reason: "no such file");
        await s.RefusedAsync("call_9", "Bash", "governor", reason: "blocked", attemptOf: "call_7");

        var intent = BodyOf(stub, 0);
        intent.GetProperty("phase").GetString().Should().Be("intent");
        intent.GetProperty("intent").GetProperty("text").GetString().Should().Be("Add a trace id.");
        intent.GetProperty("intent").GetProperty("paths")[0].GetString().Should().Be("src/obs/");
        intent.GetProperty("intent").GetProperty("tools")[0].GetString().Should().Be("Edit");

        var call = BodyOf(stub, 1);
        call.GetProperty("phase").GetString().Should().Be("call");
        call.GetProperty("args").GetProperty("command").GetString().Should().Be("git push");
        call.GetProperty("attempt_of").GetString().Should().Be("call_5");

        var result = BodyOf(stub, 2);
        result.GetProperty("phase").GetString().Should().Be("result");
        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("result").GetString().Should().Be("ENOENT");
        result.TryGetProperty("refused_by", out _).Should().BeFalse();

        var refused = BodyOf(stub, 3);
        refused.GetProperty("phase").GetString().Should().Be("result");
        refused.GetProperty("status").GetString().Should().Be("refused");
        refused.GetProperty("refused_by").GetString().Should().Be("governor");
        refused.GetProperty("attempt_of").GetString().Should().Be("call_7");

        for (var i = 0; i < 4; i++)
        {
            BodyOf(stub, i).GetProperty("agent_subject_id").GetString().Should().Be(Agent);
            BodyOf(stub, i).GetProperty("interaction_id").GetString().Should().Be(Session);
        }
    }

    [Fact]
    public async Task TheSessionDoesNotInferARetryFromARefusalItSent()
    {
        var stub = Serving.Ok(StepJson("block"));
        using var cx = Client(stub);
        var s = cx.AgentSession(Agent, Session);

        await s.CallAsync("call_7", "Bash", Args("git push"));
        await s.RefusedAsync("call_7", "Bash", "governor");
        await s.CallAsync("call_8", "Bash", Args("git push"));

        BodyOf(stub, 2).TryGetProperty("attempt_of", out _).Should().BeFalse(
            "attempt_of is the caller's to say; the handle remembers nothing");
    }

    [Fact]
    public void TheSessionHoldsItsTwoIdsAndNothingElse()
    {
        // §5.23: no state beyond the two ids — the client it sends through
        // is the only other field. A list of refusals, a counter or a
        // disposed flag would each show up here.
        var fields = typeof(AgentSession)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        fields.Select(f => f.FieldType).Should().BeEquivalentTo(
            new[] { typeof(DMZAgentClient), typeof(string), typeof(string) });

        using var cx = new DMZAgentClient(Key, handler: Serving.Ok());
        var s = cx.AgentSession(Agent, Session);
        s.AgentSubjectId.Should().Be(Agent);
        s.InteractionId.Should().Be(Session);
    }

    [Fact]
    public void TheSessionHasNothingToClose()
    {
        // §5.23: it owns no resource, so unlike Conversation it exposes no
        // close. A Dispose would invite a using that guards nothing.
        typeof(IDisposable).IsAssignableFrom(typeof(AgentSession)).Should().BeFalse();
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(AgentSession)).Should().BeFalse();
        typeof(AgentSession).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(m => m.Name)
            .Should().NotContain(new[] { "Close", "CloseAsync", "Dispose", "DisposeAsync" });
    }

    [Fact]
    public void TheSessionIsNotConstructibleFromUserCode()
    {
        typeof(AgentSession).GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Should().BeEmpty("§6.1: constructed via the client only");
    }

    [Theory]
    [InlineData("",    Session, "agentSubject")]
    [InlineData(Agent, "",      "interaction")]
    [InlineData(Agent, null,    "interaction")]
    public void ASessionWithoutBothIdsIsRefused(string? agent, string? interaction, string fragment)
    {
        using var cx = new DMZAgentClient(Key, handler: Serving.Ok());
        var act = () => cx.AgentSession(agent!, interaction!);
        act.Should().Throw<DMZAgentValidationException>().WithMessage($"*{fragment}*");
    }

    [Fact]
    public async Task ResultRefusesARefusalItCannotName()
    {
        var stub = Serving.Ok(StepJson());
        using var cx = Client(stub);
        var s = cx.AgentSession(Agent, Session);

        var act = async () => await s.ResultAsync("call_1", "Bash", "refused");

        (await act.Should().ThrowAsync<DMZAgentValidationException>())
            .WithMessage("*RefusedAsync*");
        stub.Calls.Should().Be(0);
    }

    // ------------------------------------------------------------------ //
    // The conduct record
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task ListBehaviorsSendsNoFilterItWasNotGivenAndKeepsTheIdReadable()
    {
        var stub = Serving.Ok($$"""{ "behaviors": [{{BehaviorJson()}}], "next_cursor": "c2" }""");
        using var cx = Client(stub);

        var page = await cx.ListBehaviorsAsync(Agent);

        stub.Calls.Should().Be(1, "one page means one request");
        stub.Seen[0].Method.Method.Should().Be("GET");
        stub.Seen[0].RequestUri!.AbsolutePath
            .Should().Be("/v1/subjects/subject:dv_test:agent-a/behaviors");
        QueryOf(stub.Seen[0]).Should().BeEmpty("polarity defaults on the server, not here");
        page.NextCursor.Should().Be("c2");
        page.Count.Should().Be(1);
    }

    [Fact]
    public async Task ListBehaviorsPassesEveryFilterAndTheCursor()
    {
        var stub = Serving.Ok("""{ "behaviors": [], "next_cursor": null }""");
        using var cx = Client(stub);

        await cx.ListBehaviorsAsync(Agent, polarity: "negative", interactionId: Session,
            since: "2026-10-01T00:00:00Z", until: "2026-10-08T00:00:00Z",
            limit: 10, cursor: "eyJpIjo0MH0");

        QueryOf(stub.Seen[0]).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["polarity"]       = "negative",
            ["interaction_id"] = Session,
            ["since"]          = "2026-10-01T00:00:00Z",
            ["until"]          = "2026-10-08T00:00:00Z",
            ["limit"]          = "10",
            ["cursor"]         = "eyJpIjo0MH0",
        });
    }

    [Fact]
    public async Task ASubjectIdIsEscapedExceptForItsColons()
    {
        var stub = Serving.Ok("""{ "behaviors": [] }""");
        using var cx = Client(stub);

        await cx.ListBehaviorsAsync("subject:dv:a b/c?d");

        stub.Seen[0].RequestUri!.AbsolutePath
            .Should().Be("/v1/subjects/subject:dv:a%20b%2Fc%3Fd/behaviors");
    }

    [Fact]
    public async Task ASlashInAnIdIsEncodedAndAnAtSignIsNot()
    {
        var stub = Serving.Ok("""{ "behaviors": [] }""");
        using var cx = Client(stub);

        await cx.ListBehaviorsAsync("subject:dv:team/agent@host");

        stub.Seen[0].RequestUri!.AbsolutePath
            .Should().Be("/v1/subjects/subject:dv:team%2Fagent@host/behaviors",
                "the id is one segment: '/' must not split it");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ADotSegmentIdIsRefusedBeforeAnyRequest(string id)
    {
        // The URI layer resolves these away: the request would reach
        // /v1/subjects/behaviors or /v1/behaviors, not this subject's record.
        var stub = Serving.Ok("""{ "behaviors": [] }""");
        using var cx = Client(stub);

        var list = async () => await cx.ListBehaviorsAsync(id);
        var iter = async () => { await foreach (var _ in cx.IterBehaviorsAsync(id)) { } };

        // The SDK's local-check error, so a caller catching DMZAgentException
        // sees it, naming the parameter as this binding spells it.
        (await list.Should().ThrowAsync<DMZAgentValidationException>()).WithMessage("*subjectId*");
        (await iter.Should().ThrowAsync<DMZAgentValidationException>()).WithMessage("*subjectId*");
        stub.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("..x")]
    [InlineData("...")]
    [InlineData(".hidden")]
    public async Task OnlyTheTwoDotSegmentsAreRefused(string id)
    {
        var stub = Serving.Ok("""{ "behaviors": [] }""");
        using var cx = Client(stub);

        await cx.ListBehaviorsAsync(id);

        stub.Seen[0].RequestUri!.AbsolutePath.Should().Be($"/v1/subjects/{id}/behaviors");
    }

    [Fact]
    public void ABehaviorHasNoRaw()
    {
        // §7.17 lists no raw, and the four SDKs expose the same fields. The
        // step's or the page's own Raw holds the server JSON.
        typeof(Behavior).GetProperty("Raw").Should().BeNull();
        typeof(StepResult).GetProperty("Raw").Should().NotBeNull();
        typeof(BehaviorPage).GetProperty("Raw").Should().NotBeNull();
    }

    [Fact]
    public async Task ARecordedBehaviorCarriesItsRecordFields()
    {
        var stub = Serving.Ok($$"""{ "behaviors": [{{BehaviorJson()}}] }""");
        using var cx = Client(stub);

        var b = (await cx.ListBehaviorsAsync(Agent)).Behaviors[0];

        b.BehaviorId.Should().Be("bhv_19ac");
        b.SubjectId.Should().Be(Agent);
        b.InteractionId.Should().Be(Session);
        b.ObservedAt.Should().Be("2026-10-07T15:02:11Z");
        b.Anchor.Should().NotBeNull();
        b.Strength.Should().Be(0.82);
        b.Calls.Should().Equal("call_12", "call_14");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(500)]
    public async Task ABehaviorPageLimitTheServerWouldRejectIsRefusedBeforeTheRoundTrip(int bad)
    {
        var stub = Serving.Ok("""{ "behaviors": [] }""");
        using var cx = Client(stub);

        var act = async () => await cx.ListBehaviorsAsync(Agent, limit: bad);

        (await act.Should().ThrowAsync<DMZAgentValidationException>()).WithMessage("*limit*");
        stub.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ARecordWithNoSubjectIsRefusedBeforeTheRoundTrip(string? subject)
    {
        var stub = Serving.Ok("""{ "behaviors": [] }""");
        using var cx = Client(stub);

        var act = async () => await cx.ListBehaviorsAsync(subject!);

        (await act.Should().ThrowAsync<DMZAgentValidationException>()).WithMessage("*subjectId*");
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task IterBehaviorsFetchesAPageOnlyWhenAskedPastTheOneItHolds()
    {
        var stub = Serving.Ok(
            $$"""{ "behaviors": [{{BehaviorJson("b1")}}, {{BehaviorJson("b2")}}], "next_cursor": "c2" }""",
            $$"""{ "behaviors": [{{BehaviorJson("b3", "positive")}}], "next_cursor": null }""");
        using var cx = Client(stub);

        await using var it = cx.IterBehaviorsAsync(Agent, polarity: "all", limit: 2).GetAsyncEnumerator();

        (await it.MoveNextAsync()).Should().BeTrue();
        stub.Calls.Should().Be(1, "the first item must not have fetched page two");
        (await it.MoveNextAsync()).Should().BeTrue();
        stub.Calls.Should().Be(1);
        (await it.MoveNextAsync()).Should().BeTrue();
        it.Current.BehaviorId.Should().Be("b3");
        stub.Calls.Should().Be(2);
        (await it.MoveNextAsync()).Should().BeFalse();
        stub.Calls.Should().Be(2, "a null cursor must end the walk");

        QueryOf(stub.Seen[0]).Should().NotContainKey("cursor");
        QueryOf(stub.Seen[1])["cursor"].Should().Be("c2");
        // The filters ride every page, not just the first.
        QueryOf(stub.Seen[1])["polarity"].Should().Be("all");
        QueryOf(stub.Seen[1])["limit"].Should().Be("2");
        stub.Seen[1].RequestUri!.AbsolutePath
            .Should().Be("/v1/subjects/subject:dv_test:agent-a/behaviors");
    }

    [Fact]
    public async Task BreakingOutOfTheBehaviorWalkNeverRequestsTheNextPage()
    {
        var stub = Serving.Ok($$"""{ "behaviors": [{{BehaviorJson()}}], "next_cursor": "c2" }""");
        using var cx = Client(stub);

        await foreach (var _ in cx.IterBehaviorsAsync(Agent)) break;

        stub.Calls.Should().Be(1);
    }

    [Fact]
    public void TheRecordHasNoEditorInTheSurface()
    {
        // §2.12: an SDK MUST NOT offer a method that removes or amends a
        // behavior. The record is corrected by correcting the soul.
        var verbs = new[] { "Delete", "Remove", "Update", "Amend", "Edit", "Withdraw", "Clear" };
        var found = typeof(DMZAgentClient).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Behavior", StringComparison.Ordinal)
                        && verbs.Any(v => n.StartsWith(v, StringComparison.Ordinal)))
            .ToList();
        found.Should().BeEmpty("unexpected conduct-record mutators");
    }

    // ------------------------------------------------------------------ //
    // One approval, by id
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task GetApprovalReadsOneApprovalById()
    {
        var stub = Serving.Ok(ApprovalJson);
        using var cx = Client(stub);

        var a = await cx.GetApprovalAsync("apr_9");

        stub.Seen[0].Method.Method.Should().Be("GET");
        stub.Seen[0].RequestUri!.AbsolutePath.Should().Be("/v1/approvals/apr_9");
        stub.Bodies[0].Should().BeNull();
        a.ApprovalId.Should().Be("apr_9");
        a.Status.Should().Be("approved");
        a.Decision!.ActorId.Should().Be("acct_4471");
        a.OnExpiry.Should().Be("decline");
    }

    [Fact]
    public async Task AnUnknownApprovalIsTheBaseErrorWithItsStatus()
    {
        // §2.13 maps 404 to DMZAgentError and defers a not-found type. A
        // subtype here would be a hierarchy change the spec has not made.
        var stub = Serving.Status(HttpStatusCode.NotFound, """{ "detail": "no such approval" }""");
        using var cx = Client(stub);

        var act = async () => await cx.GetApprovalAsync("apr_missing");

        var exc = (await act.Should().ThrowAsync<DMZAgentException>()).Which;
        exc.GetType().Should().Be(typeof(DMZAgentException));
        exc.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ADotSegmentApprovalIdIsRefusedRatherThanReadingTheList(string id)
    {
        // "/v1/approvals/." resolves to "/v1/approvals/": the list, which
        // would parse as an approval with every field blank.
        var stub = Serving.Ok(ApprovalJson);
        using var cx = Client(stub);

        var act = async () => await cx.GetApprovalAsync(id);

        (await act.Should().ThrowAsync<DMZAgentValidationException>()).WithMessage("*approvalId*");
        stub.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnApprovalWithNoIdIsRefusedRatherThanReadingTheList(string? id)
    {
        var stub = Serving.Ok(ApprovalJson);
        using var cx = Client(stub);

        var act = async () => await cx.GetApprovalAsync(id!);

        (await act.Should().ThrowAsync<DMZAgentValidationException>()).WithMessage("*approvalId*");
        stub.Calls.Should().Be(0);
    }

    // ------------------------------------------------------------------ //
    // Constants (§8.6)
    // ------------------------------------------------------------------ //

    [Fact]
    public void ThePhaseAndDirectiveListsAreTheSpecs()
    {
        StepPhases.All.Should().Equal("intent", "call", "result");
        Directives.All.Should().Equal("proceed", "warn", "hold", "block", "shutdown");
        EventKinds.All.Should().NotContain(StepPhases.All, "a step is not an event kind");
    }
}
