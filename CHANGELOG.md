# Changelog

## [0.6.0] — 2026-06-02

### Added
- `Concordex.Sdk.Concordia` namespace — typed client for the Concordia
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
  from `ConcordexSpecVersion` (still 0.5.0). The agent-stream surface
  still targets spec 0.5.0; only the SDK package version moved.
  **CI note:** `publish.yml`'s version-check should compare the
  release tag against `SdkVersion` (not `ConcordexSpecVersion`)
  before the next `release/v0.6.0` cut.
- `Concordex.Sdk.csproj` suppresses CS1573 in addition to CS1591 —
  the existing `ConcordexClient.ToolResultAsync` doc-comment block
  is missing some `<param>` tags. Tracked for a tidy-up; not in
  scope for this slice.

## [0.5.0] — 2026-05-30

First lockstep release — aligns with the Python, TypeScript, and
Java SDKs under `concordex-sdk-spec` v0.5.0.
