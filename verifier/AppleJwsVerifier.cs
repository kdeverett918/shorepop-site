using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

/// <summary>
/// Verifies an App Store signed JWS (StoreKit 2 Transaction, renewal info, server API responses):
/// header alg ES256 with exactly three x5c certificates; the intermediate must be signed by a configured trusted
/// root (x5c[2] is parsed but never trusted); intermediate and leaf signatures are checked with the issuer's key (manual ASN.1, so the
/// result does not depend on the platform's chain engine); names chain; every certificate is valid at
/// the effective time; the intermediate is a CA carrying Apple's WWDR OID 1.2.840.113635.100.6.2.1 and
/// the leaf is not a CA and carries the App Store receipt-signing OID 1.2.840.113635.100.6.11.1; then
/// the ES256 signature over header.payload is checked with the leaf key.
/// OCSP revocation checking is NOT performed (Apple's own library makes it optional).
/// </summary>
public sealed class AppleJwsVerifier
{
    public const string IntermediateOid = "1.2.840.113635.100.6.2.1";
    public const string LeafOid = "1.2.840.113635.100.6.11.1";
    public const int MaxJwsLength = 16 * 1024;
    private readonly IReadOnlyList<byte[]> trustedRoots;

    public AppleJwsVerifier(IEnumerable<byte[]> trustedRootsDer)
    {
        trustedRoots = trustedRootsDer.Select(r => r.ToArray()).ToArray();
        if (trustedRoots.Count == 0) throw new ArgumentException("At least one trusted root is required.");
        foreach (var root in trustedRoots) using (X509CertificateLoader(root)) { }
    }

    /// <summary>Production verifier: trusts only the embedded Apple Root CA - G3.</summary>
    public static AppleJwsVerifier Production() => new([AppleRootG3()]);

    public static byte[] AppleRootG3()
    {
        using var stream = typeof(AppleJwsVerifier).Assembly.GetManifestResourceStream("AppleRootCA-G3.cer")
            ?? throw new InvalidOperationException("Embedded Apple Root CA - G3 missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Returns the verified payload JSON. Throws <see cref="AppleVerificationException"/> on any failure.</summary>
    public JsonElement VerifyAndDecode(string jws, DateTimeOffset effectiveTime)
    {
        if (string.IsNullOrEmpty(jws) || jws.Length > MaxJwsLength) throw Fail("jws_size");
        string[] parts = jws.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) throw Fail("jws_format");
        JsonElement header;
        try { header = JsonDocument.Parse(Base64Url(parts[0])).RootElement.Clone(); }
        catch (Exception) { throw Fail("jws_header"); }
        if (header.ValueKind != JsonValueKind.Object || !header.TryGetProperty("alg", out var alg) ||
            alg.ValueKind != JsonValueKind.String || alg.GetString() != "ES256") throw Fail("jws_alg");
        if (!header.TryGetProperty("x5c", out var x5c) || x5c.ValueKind != JsonValueKind.Array) throw Fail("jws_x5c_missing");
        var chain = new List<string>();
        foreach (var item in x5c.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Fail("jws_x5c_format");
            chain.Add(item.GetString()!);
        }
        using ECDsa leafKey = VerifyChain(chain, effectiveTime);
        byte[] signature;
        try { signature = Base64Url(parts[2]); }
        catch (Exception) { throw Fail("jws_signature_format"); }
        if (signature.Length != 64) throw Fail("jws_signature_format");
        byte[] signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!leafKey.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw Fail("jws_signature_invalid");
        try
        {
            var payload = JsonDocument.Parse(Base64Url(parts[1])).RootElement.Clone();
            if (payload.ValueKind != JsonValueKind.Object) throw Fail("jws_payload");
            return payload;
        }
        catch (AppleVerificationException) { throw; }
        catch (Exception) { throw Fail("jws_payload"); }
    }

    /// <summary>Validates [leaf, intermediate, root] (standard base64 DER) and returns the leaf's P-256 key.</summary>
    public ECDsa VerifyChain(IReadOnlyList<string> x5c, DateTimeOffset effectiveTime)
    {
        if (x5c.Count != 3) throw Fail("chain_length");
        byte[][] der = new byte[3][];
        for (int i = 0; i < 3; i++)
        {
            try { der[i] = Convert.FromBase64String(x5c[i]); }
            catch (Exception) { throw Fail("chain_certificate_encoding"); }
        }
        X509Certificate2 leaf, intermediate;
        try { leaf = X509CertificateLoader(der[0]); intermediate = X509CertificateLoader(der[1]); using (X509CertificateLoader(der[2])) { } }
        catch (Exception) { throw Fail("chain_certificate_parse"); }
        using (intermediate) using (leaf)
        {
            DateTime at = effectiveTime.UtcDateTime;
            foreach (var cert in new[] { leaf, intermediate })
                if (!ValidAt(cert, at)) throw Fail("chain_not_valid_at_time");
            if (!leaf.IssuerName.RawData.AsSpan().SequenceEqual(intermediate.SubjectName.RawData)) throw Fail("chain_leaf_issuer");
            var interBasic = intermediate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (interBasic == null || !interBasic.CertificateAuthority) throw Fail("chain_intermediate_not_ca");
            var leafBasic = leaf.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (leafBasic != null && leafBasic.CertificateAuthority) throw Fail("chain_leaf_is_ca");
            if (intermediate.Extensions[IntermediateOid] == null) throw Fail("chain_intermediate_oid");
            if (leaf.Extensions[LeafOid] == null) throw Fail("chain_leaf_oid");
            VerifyIssuedBy(der[0], intermediate);
            // Trust anchor = a configured root (name + key), as in Apple's own library: the presented
            // x5c[2] is never a trust input. The intermediate must be signed by a trusted root's key.
            bool anchored = false, nameMatched = false;
            foreach (var rootDer in trustedRoots)
            {
                using var anchor = X509CertificateLoader(rootDer);
                if (!intermediate.IssuerName.RawData.AsSpan().SequenceEqual(anchor.SubjectName.RawData)) continue;
                nameMatched = true;
                if (!ValidAt(anchor, at)) continue;
                try { VerifyIssuedBy(der[1], anchor); anchored = true; break; }
                catch (AppleVerificationException) { }
            }
            if (!anchored) throw Fail(nameMatched ? "chain_untrusted_root" : "chain_intermediate_issuer");
            ECDsa key = leaf.GetECDsaPublicKey() ?? throw Fail("chain_leaf_key");
            if (key.KeySize != 256) { key.Dispose(); throw Fail("chain_leaf_key"); }
            return key;
        }
    }

    private static bool ValidAt(X509Certificate2 cert, DateTime at) => at >= cert.NotBefore.ToUniversalTime() && at <= cert.NotAfter.ToUniversalTime();

    private static void VerifyIssuedBy(byte[] certificateDer, X509Certificate2 issuer)
    {
        ReadOnlyMemory<byte> tbs; string algorithm; byte[] signature;
        try
        {
            var reader = new AsnReader(certificateDer, AsnEncodingRules.DER);
            var certificate = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            tbs = certificate.ReadEncodedValue();
            var algorithmSequence = certificate.ReadSequence();
            algorithm = algorithmSequence.ReadObjectIdentifier();
            signature = certificate.ReadBitString(out int unused);
            certificate.ThrowIfNotEmpty();
            if (unused != 0) throw new CryptographicException();
        }
        catch (Exception) { throw Fail("chain_certificate_structure"); }
        HashAlgorithmName hash = algorithm switch
        {
            "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => throw Fail("chain_signature_algorithm"),
        };
        using ECDsa? key = issuer.GetECDsaPublicKey();
        if (key == null) throw Fail("chain_issuer_key");
        bool valid;
        try { valid = key.VerifyData(tbs.Span, signature, hash, DSASignatureFormat.Rfc3279DerSequence); }
        catch (CryptographicException) { valid = false; }
        if (!valid) throw Fail("chain_signature_invalid");
    }

#pragma warning disable SYSLIB0057 // X509CertificateLoader is .NET 9+; the byte[] constructor is the .NET 8 equivalent.
    private static X509Certificate2 X509CertificateLoader(byte[] der) => new(der);
#pragma warning restore SYSLIB0057

    public static byte[] Base64Url(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static AppleVerificationException Fail(string reason) => new(reason);
}

public sealed class AppleVerificationException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
