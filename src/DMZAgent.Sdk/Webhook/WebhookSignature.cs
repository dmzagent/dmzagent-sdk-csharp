using System;
using System.Security.Cryptography;
using System.Text;

namespace DMZAgent.Sdk.Webhook;

/// <summary>
/// HMAC-SHA256 verifier for DMZAgent outbound webhooks (sdk-spec.md §9).
///
/// <para>
/// DMZAgent signs every outbound webhook with the subscription's
/// secret. The signature header carries:
/// </para>
/// <code>
/// t=&lt;unix_seconds&gt;,v1=&lt;hex_hmac_sha256(secret, "&lt;t&gt;.&lt;payload&gt;")&gt;
/// </code>
///
/// <para>
/// Per sdk-spec.md §9 the verifier MUST: parse the header, reject when
/// <c>t</c> is missing/non-numeric/older than tolerance, compute the
/// expected HMAC, and constant-time compare. This implementation
/// returns <c>false</c> on any failure rather than raising — symmetric
/// with the Python reference.
/// </para>
/// </summary>
public static class WebhookSignature
{
    /// <summary>Verify a signed payload.</summary>
    /// <param name="payload">Raw request body as a UTF-8 string.</param>
    /// <param name="signatureHeader">Value of the <c>DMZAgent-Signature</c> header.</param>
    /// <param name="secret">The subscription secret (whsec_…).</param>
    /// <param name="toleranceSeconds">Maximum age of the <c>t</c> timestamp, in seconds. Default 300.</param>
    /// <param name="nowUnix">Override for the current Unix time, in seconds. Use for tests; default is system time.</param>
    /// <returns><c>true</c> on a successful verification; <c>false</c> on any failure.</returns>
    public static bool Verify(
        string payload,
        string signatureHeader,
        string secret,
        int    toleranceSeconds = 300,
        long?  nowUnix          = null)
    {
        if (payload          is null) return false;
        if (signatureHeader is null) return false;
        if (string.IsNullOrEmpty(secret)) return false;

        if (!TryParseHeader(signatureHeader, out var t, out var v1Hex)) return false;

        var now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - t) > toleranceSeconds) return false;

        // Compute v1 = hex(hmac_sha256(secret, f"{t}.{payload}")).
        byte[] expected;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
        {
            // Build the signed-body bytes without allocating a long
            // intermediate string when payload is large.
            var prefix = Encoding.UTF8.GetBytes(t.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            var body   = Encoding.UTF8.GetBytes(payload);
            var input  = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, input, 0,             prefix.Length);
            Buffer.BlockCopy(body,   0, input, prefix.Length, body.Length);
            expected = hmac.ComputeHash(input);
        }

        // Decode v1Hex. Reject malformed hex up front.
        byte[] provided;
        try
        {
            provided = HexDecode(v1Hex);
        }
        catch (FormatException)
        {
            return false;
        }

        if (provided.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    /// <summary>Parse <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c> tolerantly. Returns
    /// false if either field is missing or <c>t</c> isn't an integer.</summary>
    private static bool TryParseHeader(string header, out long t, out string v1)
    {
        t  = 0;
        v1 = string.Empty;
        var fields = header.Split(',');
        long?   parsedT  = null;
        string? parsedV1 = null;
        foreach (var raw in fields)
        {
            var part = raw.Trim();
            var eq   = part.IndexOf('=');
            if (eq <= 0) continue;
            var key   = part.Substring(0, eq);
            var value = part.Substring(eq + 1);
            if (key == "t")
            {
                if (!long.TryParse(value, System.Globalization.NumberStyles.Integer,
                                   System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    return false;
                }
                parsedT = parsed;
            }
            else if (key == "v1")
            {
                parsedV1 = value;
            }
        }
        if (parsedT is null || parsedV1 is null) return false;
        t  = parsedT.Value;
        v1 = parsedV1;
        return true;
    }

    private static byte[] HexDecode(string hex)
    {
        if (hex.Length % 2 != 0) throw new FormatException("odd-length hex");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            var hi = HexNibble(hex[2 * i]);
            var lo = HexNibble(hex[2 * i + 1]);
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => 10 + (c - 'a'),
        >= 'A' and <= 'F' => 10 + (c - 'A'),
        _                  => throw new FormatException($"invalid hex char '{c}'"),
    };
}
