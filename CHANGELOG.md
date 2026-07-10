# Changelog

## [0.7.0] — 2026-07-10

### Added
- Error-taxonomy alignment with spec 0.7.0 (sdk-spec.md §3, DX-9):
  - HTTP 422 now maps to `DMZAgentValidationException` (canonical
    `ValidationError`) — well-formed but unprocessable payloads.
  - HTTP 429 now maps to the new `DMZAgentRateLimitException`
    (canonical `RateLimitError`), exposing `RetryAfter` (`int?`) —
    seconds parsed from the response's `Retry-After` header
    (delta-seconds form); `null` when the header is absent or
    unparseable. The SDK never sleeps or retries automatically.
  Both statuses previously fell through to the base
  `DMZAgentException` ("unexpected status") arm.
- Contract-test runner support for the new error-mapping fixture keys:
  `headers` (applied to the stubbed HTTP response) and
  `expected_retry_after` (asserted against `RetryAfter`; JSON `null`
  asserts the property is `null`).
- `RateLimitRetryAfterTests` — unit coverage for `Retry-After`
  parsing: present, absent, and garbage (non-numeric, HTTP-date,
  negative, decimal, overflow) → `null`.

### Changed
- Package version bumped to `0.7.0` (minor — additive, no breaking
  changes to the existing 0.6.0 surface).
- Pinned spec version bumped to `0.7.0`
  (`DMZAgentClient.SpecVersion`, `Directory.Build.props`).

### Fixed
- `Directory.Build.props` defined the spec-version property under the
  retired brand name (`ConcordexSpecVersion`) while
  `DMZAgent.Sdk.csproj`'s NuGet `Description` interpolated
  `$(DMZAgentSpecVersion)` — an undefined property that rendered as an
  empty string in the published package description (and broke the
  `grep` in `spec-conformance.yml` / `publish.yml`, which already
  expect `DMZAgentSpecVersion`). The property is now named
  `DMZAgentSpecVersion`.

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
