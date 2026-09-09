# DMZAgent.Sdk

Official .NET client for the [DMZAgent](https://spec.dmzagent.com)
agent-stream and circuit-breaker APIs.

This package implements **spec version 0.9.0** — same constructor
shape, same methods, same return types, same error hierarchy, same wire
protocol as the Python, TypeScript, and Java SDKs. The naming follows
the C# convention map in §8 of the spec.

## Install

```bash
dotnet add package DMZAgent.Sdk
```

Target framework: **net8.0+**.

## Quick start

```csharp
using DMZAgent.Sdk;

using var cx = new DMZAgentClient(apiKey: "ck_live_...");

await cx.SubjectSaysAsync(
    agentSubjectId: "user:ws:bot",
    subjectId:      "user:ws:cust",
    text:           "I want a refund.");

var g = await cx.CheckAsync(subjectId: "user:ws:bot");
if (!g.Allow) return Refuse(g.Reason);
```

The API key must start with `ck_` — the constructor rejects anything
else at startup. Get one from your tenant admin.

## The four event helpers

Every wire kind has a typed wrapper. The lower-level `EmitEventAsync`
is available when you need fields outside the wrappers' surfaces.

```csharp
// A subject in the conversation spoke.
await cx.SubjectSaysAsync(
    subjectId:      "user:ws:cust",
    text:           "Cancel my subscription.",
    agentSubjectId: "user:ws:bot");

// An agent invoked a tool. Use BEFORE running the tool.
await cx.ToolCallAsync(
    subjectId:     "user:ws:bot",
    tool:          "subscription.cancel",
    args:          new Dictionary<string, object?> { ["plan_id"] = "pro_monthly" });

// The tool returned a result. Pair with the prior call.
await cx.ToolResultAsync(
    subjectId: "user:ws:bot",
    tool:      "subscription.cancel",
    result:    new { canceled_at = DateTime.UtcNow });

// A structured non-utterance observation (sensor reading, IoT, video).
await cx.ObservationAsync(
    agentSubjectId: "user:ws:sensor",
    subjects:       new[] { new Dictionary<string, object?> { ["subject_id"] = "user:ws:sensor", ["role"] = "service", ["kind"] = "sensor" } },
    payload:        new Dictionary<string, object?> { ["kind"] = "video_keyframe", ["frame_index"] = 42 });
```

## Circuit-breaker guard

The `using var g = await client.GuardAsync(...)` pattern is the
idiomatic seam for gating sensitive actions:

```csharp
using var g = await cx.GuardAsync(subjectId: "user:ws:bot");
if (!g.Result.Allow)
{
    return BlockAction(g.Result.Reason);
}
DoSensitiveThing();
```

Or if you'd rather have an exception escape on a blocked subject:

```csharp
try
{
    using var g = await cx.GuardAsync(subjectId: "user:ws:bot", raiseOnOpen: true);
    DoSensitiveThing();
}
catch (CircuitBreakerOpenException ex)
{
    LogBlocked(ex.Reason, ex.FiredPolicies);
}
```

## Multi-party conversations

For longer interactions, use the `Conversation` handle — it caches the
server-assigned `interaction_id` from the first emit and re-sends it,
so the server stitches the events together.

```csharp
using var conv = cx.Conversation(participants: new[]
{
    new Dictionary<string, object?> { ["subject_id"] = "user:ws:bot",  ["role"] = "agent",    ["kind"] = "agent" },
    new Dictionary<string, object?> { ["subject_id"] = "user:ws:cust", ["role"] = "customer", ["kind"] = "human" },
});

await conv.SaysAsync("user:ws:cust", "I want a refund.");
await conv.SaysAsync("user:ws:bot",  "I can help.");
using (var g = await conv.GuardAsync("user:ws:bot", raiseOnOpen: true))
{
    await conv.ToolCallAsync("user:ws:bot", "refund.issue",
        args: new Dictionary<string, object?> { ["amount"] = 99 });
}
```

The roster is generic — agent, customer, observer, sensor, anything
the conversation includes. `AddSubject(...)` extends it mid-flight.

## Webhook signature verification

DMZAgent outbound webhooks are signed with HMAC-SHA256. Verify them
with the static helper:

```csharp
using DMZAgent.Sdk.Webhook;

var ok = WebhookSignature.Verify(
    payload:          rawRequestBody,
    signatureHeader:  Request.Headers["DMZAgent-Signature"]!,
    secret:           "whsec_...",
    toleranceSeconds: 300);
if (!ok) return Unauthorized();
```

The verifier rejects (returns `false`) on a missing `t` field, a
malformed timestamp, a stale timestamp outside the tolerance window,
or a signature mismatch. It runs in constant time.

## Human-in-the-loop approvals

A circuit-breaker policy can fire with action `require_approval`, which
**holds** the action instead of refusing it. `CheckAsync` then hands back a
denial that names what it is waiting on:

```csharp
var g = await cx.CheckAsync(subjectId: "subject:dv:checkout-bot");

if (g.AwaitingApproval)
    ShowMyOwnApprovalScreen(g.PendingApprovalId);   // asked
else if (!g.Allow)
    return Refuse(g.Reason);                        // refused
```

That is the whole difference between a breaker and a human-in-the-loop
control, and it is one field because you have to branch on it.

### You render it. All of it.

```csharp
await foreach (var a in cx.IterApprovalsAsync(status: "pending"))
{
    Console.WriteLine(a.Tool);        // the held call, verbatim
    Console.WriteLine(a.Reason);      // your operator's policy words
    Console.WriteLine(a.ExpiresAt);   // decide before this
}
```

Nothing in an `Approval` is display text we wrote. `Reason` and each
`FiredPolicies` entry's name are the words your operator typed when they
wrote the policy, and `Action` is the call your agent was about to make.
There is no message for your end user, no copy of ours, and no branding —
because a sentence we wrote would read identically in every customer's
product, which is the thing this is designed to avoid.

### A decision records which human made it

```csharp
await cx.ApproveApprovalAsync(
    approvalId: "apr_7f3c9a1b",
    actorId:    "acct_4471",           // your identifier, not ours
    actorLabel: "Dana R.",
    reason:     "verified the order by phone");
```

`actorId` is required, never defaulted, and never derived from the API
key — the key identifies your integration, and an approval whose actor is
the integration that requested it has recorded nobody. We resolve it
against no directory, so your users never need an account here. A blank
one throws `DMZAgentValidationException` before any request goes out.

Two operators who click at the same moment produce one decision and one
`DMZAgentConflictException`; the body carries the status the approval had
already reached. That is not a retry — the call did not fail, it lost.

**An approval that nobody answers declines.** `OnExpiry` is always
`"decline"` and there is no setting that changes it: an approval that
becomes an allow because nobody looked at it is not a human-in-the-loop
control, it is a delay with extra steps.

## The incident and remediation ledger

`Anchor` has been on `CheckResult` for several releases, pointing into a
ledger nothing could open. Now it opens:

```csharp
var g = await cx.CheckAsync(subjectId: "subject:dv:checkout-bot");
var recorded = g.Anchor;      // { ledger_index: 40197, hash: "b1c4…" }

await foreach (var inc in cx.IterIncidentsAsync(
    status: "open", since: "2026-09-01T00:00:00Z"))
{
    // inc.Anchor equal to `recorded` is the entry your check was told about
}
```

Every breaker that opened, every approval decided, every remediation that
ran — newest ledger entry first, in the order the ledger recorded them
rather than by timestamp, because two entries written in the same second
still have an order.

The ledger is **append-only**. There is no `CloseIncidentAsync` and no
method that edits an entry: an incident reaches `remediated` because a
remediation was appended to it, and `Status` is a fold over what has been
appended. An incident with no remediations is the normal shape of
something nobody has answered yet.

### Paging

`ListApprovalsAsync` and `GetIncidentsAsync` return one page and do not
follow `NextCursor`. You asked for 25 and you get 25 — a method that
quietly walked every page would turn one bounded request into an unbounded
one against a record that only grows. `IterApprovalsAsync` and
`IterIncidentsAsync` are `IAsyncEnumerable<T>`: breaking out of the
`await foreach` means the next page is never requested.

## Exception hierarchy

| Status / situation                                  | Type                              |
|-----------------------------------------------------|-----------------------------------|
| Base — all SDK errors                               | `DMZAgentException`              |
| 400 / client-side argument validation               | `DMZAgentValidationException`    |
| 401                                                 | `DMZAgentAuthException`          |
| 403                                                 | `DMZAgentPermissionException`    |
| 5xx, timeout, network failure                       | `DMZAgentServerException`        |
| `Guard(..., raiseOnOpen: true)` → blocked subject   | `CircuitBreakerOpenException`     |

Every exception exposes `StatusCode` and `Body` (the parsed JSON
response or raw text). `CircuitBreakerOpenException` adds `Reason`,
`FiredPolicies`, `Anchor`, and `ScopeRef`.

## Resource lifecycle

`DMZAgentClient` and `Conversation` both implement `IDisposable`.
Always wrap them in `using` — or call `.Close()` explicitly. Closing
the client releases the underlying HTTP transport; calling close
multiple times is a no-op.

## Configuration

| Parameter   | Default                          |
|-------------|----------------------------------|
| `apiKey`    | required, must start with `ck_`  |
| `baseUrl`   | `https://api.dmzagent.com`      |
| `timeout`   | 10 seconds                        |
| `userAgent` | `dmzagent-csharp/<spec version>` |
| `cbCacheTtl` | `null` (state cache off)        |
| `cbCacheMaxEntries` | `1024`                   |
| `cbCacheOnError` | `CbCacheOnError.Raise`      |

For testing or self-hosted environments, pass a custom
`HttpMessageHandler`:

```csharp
using var cx = new DMZAgentClient(
    apiKey: "ck_test_xxxxx",
    handler: new MyStubHandler(),
    baseUrl: "http://localhost:8080");
```

### Circuit-breaker state cache

`CheckAsync()` is a network round trip, and it usually sits in front of
the sensitive action. A per-client cache removes it for repeated checks
on the same subject. It is off unless you pass a TTL:

```csharp
using var cx = new DMZAgentClient(
    apiKey:            "ck_...",
    cbCacheTtl:        TimeSpan.FromSeconds(5),   // null or Zero is off
    cbCacheMaxEntries: 1024,                      // bounded, LRU evicted
    cbCacheOnError:    CbCacheOnError.LastKnown); // or Raise (default)

var r = await cx.CheckAsync(subjectId: "user:ws:bot");
r.Cached;    // served from memory?
r.CacheAge;  // how old it was
r.Stale;     // served because the check itself failed

await cx.CheckAsync(subjectId: "user:ws:bot", fresh: true); // skip and refresh
```

Read the TTL as **the longest a newly-opened breaker can go unnoticed by
this client**. A cached `closed` is an allow the server might no longer
give, which is why the cache is opt-in and why every result says whether
it came from memory and how old it was.

One TTL covers every state. Holding a deny longer than an allow is a
safety policy, and it is yours to make with the value you pass.

`CbCacheOnError.LastKnown` serves the last state for that subject —
marked `Stale` — when the check cannot reach the server. With no entry
for that subject it throws, and it needs a TTL above zero to be set at
all. A `429` is not covered: that is the server answering, and it carries
a `RetryAfter` worth acting on.

## Spec conformance

This SDK passes the contract-test corpus pinned at
`dmzagent-sdk-spec@v0.9.0` — the value of `<DMZAgentSpecVersion>` in
`Directory.Build.props`, which also generates the `SpecVersion` constant
and the default User-Agent, so the pin and the constant cannot drift.
The conformance run lives in `.github/workflows/spec-conformance.yml`.

## License

[Apache-2.0](./LICENSE)
