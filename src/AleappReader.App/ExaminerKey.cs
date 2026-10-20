using System.Security.Cryptography;
using AleappReader.Core;

namespace AleappReader.App;

/// <summary>The examiner's signing key for portable cases: an ECDSA P-256 key created on first use in the user's
/// Windows key store (protected by DPAPI, not exportable), so the private half never leaves this Windows account.</summary>
static class ExaminerKey
{
    const string Name = "LEXIS examiner signing key";

    /// <summary>The key, created when <paramref name="create"/> and there is none yet; null otherwise.</summary>
    public static ECDsa? Open(bool create)
    {
        if (CngKey.Exists(Name)) return new ECDsaCng(CngKey.Open(Name));
        if (!create) return null;
        return new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, Name, new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            KeyCreationOptions = CngKeyCreationOptions.None,
        }));
    }

    /// <summary>This machine's examiner fingerprint, or null when no key was made yet.</summary>
    public static string? Fingerprint()
    {
        try { using var k = Open(false); return k is null ? null : PortableCase.Fingerprint(k); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>A package check in words, saying when the signing key is this examiner's own.</summary>
    public static string Describe(PackageCheck c) =>
        c.Describe() + (c.SignatureValid == true && c.Fingerprint == Fingerprint() ? "\nThe key is your own examiner key on this machine." : "");
}
