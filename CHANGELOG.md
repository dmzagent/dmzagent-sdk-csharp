# Changelog

## [Unreleased]

## [0.7.0] — 2026-09-09 (spec 0.10.0)

### Added
- **Human-in-the-loop approvals you render yourself.**
  `ListApprovalsAsync`, `IterApprovalsAsync`, `DecideApprovalAsync`, and
  the `ApproveApprovalAsync` / `DeclineApprovalAsync` wrappers.

  An `Approval` holds nothing we wrote for your users: `Reason` and each
  `FiredPolicies` entry's name are your operator's own policy words, and
  `Action` is the call your agent was about to make, verbatim.

  `actorId` is required on every decision and is never defaulted or
  derived from the API key. A decision without one throws
  `DMZAgentValidationException` before any request is made.

- **`CheckResult.PendingApprovalId` and `.AwaitingApproval`.** The
  difference between being refused and being asked. `Allow` is still
  `false` in both cases, deliberately: code that reads `Allow` alone keeps
  refusing, so nothing written before this release starts allowing what it
  used to deny.

- **The incident and remediation ledger is readable** —
  `GetIncidentsAsync` and `IterIncidentsAsync`, returning `Incident` with
  its `Remediations`.

  `Anchor` has been on `CheckResult` for several releases and pointed into
  a ledger nothing could open. Record it at check time, find that
  `ledger_index` here, compare hashes.

### Changed
- **`DMZAgentConflictException` also means a settled approval.** A second
  decision on an approval someone already decided, or one past its
  deadline, is a 409. Same type as the idempotency conflict for the same
  reason: the call did not fail, it lost. The body's `status` says which
  of the two it was, and the message no longer asserts "Idempotency-Key"
  on paths where that is not the cause.

### Notes
- **Neither list method follows a cursor on its own.** The `Iter*` methods
  are `IAsyncEnumerable<T>`; breaking out of the `await foreach` means the
  next page is never requested.
- **There is no `CloseIncidentAsync`.** The ledger is append-only and has
  no endpoint for one.
- **Expiry declines, and cannot be configured otherwise.** `OnExpiry`
  reads `"decline"` even if a server sends something else.
- The contract runner now dispatches the three new corpus methods and
  pins **verb and query string** on read vectors: asserting only the body
  would let a GET that dropped every filter pass, since a GET has no body
  to be wrong about.

### Added
- **Circuit-breaker state cache** (spec §4.4). `CheckAsync()` is a
  network round trip in front of a sensitive action; `cbCacheTtl` lets a
  repeat check on the same subject come from memory instead. Off when
  null or `TimeSpan.Zero`, which is the default.

  `cbCacheMaxEntries` bounds it — the key is a subject id, so an agent
  seeing many subjects would otherwise hold an entry for each for the
  life of the process — and evicts least-recently-used first.
  `CheckAsync(fresh: true)` skips the cache and refreshes it;
  `GuardAsync` passes `fresh` through. All three constructor overloads
  accept the settings.
- `CheckResult.Cached`, `.CacheAge`, `.Stale`. A caller recording a
  denial has to be able to tell it read four-second-old state. The
  server's own `LatencyMs`, `RouteLatencyMs`, `CheckedAt` and `Raw` are
  left alone on a cached result — they describe the check that happened.
- `CbCacheOnError.LastKnown` serves the last known state for a subject,
  marked `Stale`, when the check cannot reach the server. It throws when
  nothing is known for that subject, and cannot be set without a TTL to
  fall back on. A `429` stays a `DMZAgentRateLimitException`: the server
  answered, and the `RetryAfter` is worth acting on. An enum rather than
  a string, so a typo cannot become a different safety posture.

### Changed
- Spec pin `<DMZAgentSpecVersion>` moves to `0.9.0`. The `SpecVersion`
  constant and the default User-Agent are generated from it, so they
  follow automatically — this repo already had the single-sourcing the
  other three SDKs needed adding.
- README's Configuration table and Spec-conformance section still cited
  `0.5.0`; both now derive from the pin rather than restating a literal.

## [0.6.0] — 2026-06-02

### Added
- `DMZAgent.Sdk.Concordia` namespace — typed client for the Concordia
  MCP 1.0 governance server at `/mcp/v1`. New `ConcordiaClient` class
  wraps the four MCP tools (`EnforceCovenantAsync`, `RecordDecisionAsync`,
  `QueryCorpusAsync`, `GetSubjectSoulAsync`) and the three resources
  (`WorkspacePoliciesAsync`, `WorkspaceCanonsAsync`, `RecentLedgerAsync`)
  so callers don't write JSON-RPC envelopes by hand.
- Typed result records: `EnforceCovenantResult`, `CbStateChange`,
  `RecordDecisionResult`, `CorpusMatch`, `QueryCorpusResult`, `SoulTag`,
  `SoulTrace`, `SubjectSoul`, `PolicySummary`, `InstalledCanon`,
  `LedgerEntry`, `LedgerPage`.
- Typed exception hierarchy with 1:1 mapping to MCP spec §8 error
  codes: `ConcordiaAuthException`, `ConcordiaQuotaExceededException`,
  `ConcordiaPolicyEngineUnavailableException`,
  `ConcordiaCanonNotInstalledException`,
  `ConcordiaSubjectNotFoundException`, `ConcordiaCircuitOpenException`,
  `ConcordiaPermissionDeniedException`. Plus base
  `ConcordiaException` and transport-level
  `ConcordiaProtocolException`.
- `ConcordiaClient.IterLedgerAsync()` async iterator for streaming
  through paginated ledger pages.

### Changed
- Package version bumped to `0.6.0` (minor — additive, no breaking
  changes to the existing 0.5.0 surface).
- `Directory.Build.props` introduces `SdkVersion` (0.6.0) separate
  from `DMZAgentSpecVersion` (still 0.5.0). The agent-stream surface
  still targets spec 0.5.0; only the SDK package version moved.
  **CI note:** `publish.yml`'s version-check should compare the
  release tag against `SdkVersion` (not `DMZAgentSpecVersion`)
  before the next `release/v0.6.0` cut.
- `DMZAgent.Sdk.csproj` suppresses CS1573 in addition to CS1591 —
  the existing `DMZAgentClient.ToolResultAsync` doc-comment block
  is missing some `<param>` tags. Tracked for a tidy-up; not in
  scope for this slice.

## [0.5.0] — 2026-05-30

First lockstep release — aligns with the Python, TypeScript, and
Java SDKs under `dmzagent-sdk-spec` v0.5.0.
