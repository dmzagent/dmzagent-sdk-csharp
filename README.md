# Concordex.Sdk

Official .NET client for the [Concordex](https://spec.concordex.dev)
agent-stream and circuit-breaker APIs.

This package implements **spec version 0.5.0** — same constructor
shape, same methods, same return types, same error hierarchy, same wire
protocol as the Python, TypeScript, and Java SDKs. The naming follows
the C# convention map in §8 of the spec.

## Install

```bash
dotnet add package Concordex.Sdk
```

Target framework: **net8.0+**.

## Quick start

```csharp
using Concordex.Sdk;

using var cx = new ConcordexClient(apiKey: "ck_live_...");

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

Concordex outbound webhooks are signed with HMAC-SHA256. Verify them
with the static helper:

```csharp
using Concordex.Sdk.Webhook;

var ok = WebhookSignature.Verify(
    payload:          rawRequestBody,
    signatureHeader:  Request.Headers["Concordex-Signature"]!,
    secret:           "whsec_...",
    toleranceSeconds: 300);
if (!ok) return Unauthorized();
```

The verifier rejects (returns `false`) on a missing `t` field, a
malformed timestamp, a stale timestamp outside the tolerance window,
or a signature mismatch. It runs in constant time.

## Exception hierarchy

| Status / situation                                  | Type                              |
|-----------------------------------------------------|-----------------------------------|
| Base — all SDK errors                               | `ConcordexException`              |
| 400 / client-side argument validation               | `ConcordexValidationException`    |
| 401                                                 | `ConcordexAuthException`          |
| 403                                                 | `ConcordexPermissionException`    |
| 5xx, timeout, network failure                       | `ConcordexServerException`        |
| `Guard(..., raiseOnOpen: true)` → blocked subject   | `CircuitBreakerOpenException`     |

Every exception exposes `StatusCode` and `Body` (the parsed JSON
response or raw text). `CircuitBreakerOpenException` adds `Reason`,
`FiredPolicies`, `Anchor`, and `ScopeRef`.

## Resource lifecycle

`ConcordexClient` and `Conversation` both implement `IDisposable`.
Always wrap them in `using` — or call `.Close()` explicitly. Closing
the client releases the underlying HTTP transport; calling close
multiple times is a no-op.

## Configuration

| Parameter   | Default                          |
|-------------|----------------------------------|
| `apiKey`    | required, must start with `ck_`  |
| `baseUrl`   | `https://api.concordex.dev`      |
| `timeout`   | 10 seconds                        |
| `userAgent` | `concordex-csharp/0.5.0`         |

For testing or self-hosted environments, pass a custom
`HttpMessageHandler`:

```csharp
using var cx = new ConcordexClient(
    apiKey: "ck_test_xxxxx",
    handler: new MyStubHandler(),
    baseUrl: "http://localhost:8080");
```

## Spec conformance

This SDK passes the contract-test corpus pinned at
`concordex-sdk-spec@v0.5.0`. The conformance run lives in
`.github/workflows/spec-conformance.yml`; it reports a status check of
`spec-conformance/0.5.0` to the spec-coordination workflow.

## License

[Apache-2.0](./LICENSE)
