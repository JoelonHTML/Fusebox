using System.Security.Cryptography;

namespace Fusebox.Core.Update;

/// <summary>
/// Update signatures: ECDSA P-256 / SHA-256 over the zip bytes, signature in IEEE P1363 form (r||s, 64 bytes), base64.
/// Public key = base64 SubjectPublicKeyInfo, private key = base64 PKCS#8.
/// </summary>
public static class Signing
{
    /// <summary>Public half of the release key. DEVELOPMENT KEY: replace with the output of `Fusebox.Sign keygen`.</summary>
    public const string ReleaseKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZBL4yr+Nm/8+eu3IEl6KEZOiG2lxxJV9FTb5OJG58Iqxo1kGZyp+12JQo2xnz7pFcVXIrD0MIikMUhqtDmOqfg==";

    const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    public static (string Private, string Public) NewKeyPair()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(k.ExportPkcs8PrivateKey()), Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    }

    public static string Sign(Stream data, string privateKey)
    {
        using var k = ECDsa.Create();
        k.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey.Trim()), out _);
        return Convert.ToBase64String(k.SignData(data, HashAlgorithmName.SHA256, Format));
    }

    public static bool Verify(Stream data, string signature, string publicKey = ReleaseKey)
    {
        try
        {
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return k.VerifyData(data, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, Format);
        }
        catch { return false; }
    }
}
