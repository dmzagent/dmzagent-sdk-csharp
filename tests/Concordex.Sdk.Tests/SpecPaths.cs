using System;
using System.IO;

namespace Concordex.Sdk.Tests;

/// <summary>
/// Resolves the contract-test corpus on disk. Per the runner spec the
/// SDK repo and the spec repo sit side-by-side; CI checks the spec out
/// at the pinned tag and exports <c>CONCORDEX_SPEC_PATH</c>.
/// </summary>
internal static class SpecPaths
{
    public static string SpecRoot { get; } = ResolveSpecRoot();
    public static string ContractTestsDir { get; } = Path.Combine(SpecRoot, "contract-tests");

    public static string GoldenEnvelopes   => Path.Combine(ContractTestsDir, "golden-envelopes.json");
    public static string SignatureVectors  => Path.Combine(ContractTestsDir, "signature-vectors.json");
    public static string ErrorMapping      => Path.Combine(ContractTestsDir, "error-mapping.json");
    public static string VersionFile       => Path.Combine(SpecRoot, "VERSION");

    private static string ResolveSpecRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CONCORDEX_SPEC_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

        // Walk up from the test-runner output dir looking for a sibling.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "..", "concordex-sdk-spec");
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Fall back to a plausible relative path so the failure message
        // is informative rather than NullReferenceException.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "concordex-sdk-spec"));
    }
}
