using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace QueryAnyRest.Services;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string stored);
    bool IsProtected(string value);
}

/// <summary>
/// Encrypts the values that have to survive in a recoverable form: the resource-owner
/// password, the client secret and the live tokens. These cannot be hashed, because the
/// app has to replay them at the token endpoint, so the goal is to make the file useless
/// on its own rather than to make the values irreversible.
///
/// Keys live in a separate folder and are themselves protected with DPAPI, so a copied
/// configurations.json is worthless without the key ring, and the key ring is worthless
/// off this machine.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    /// <summary>Prefix that marks an encrypted value, so plaintext files still load and migrate.</summary>
    public const string Marker = "enc:v1:";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionSecretProtector> _log;

    public DataProtectionSecretProtector(
        IDataProtectionProvider provider,
        ILogger<DataProtectionSecretProtector> log)
    {
        // The purpose string is part of the key derivation: a payload protected here
        // cannot be decrypted by a protector created for any other purpose.
        _protector = provider.CreateProtector("QueryAnyRest.Secrets.v1");
        _log = log;
    }

    public bool IsProtected(string value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Marker, StringComparison.Ordinal);

    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? string.Empty : Marker + _protector.Protect(plaintext);

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        // A value written before encryption was switched on. It gets encrypted on the next save.
        if (!IsProtected(stored)) return stored;

        try
        {
            return _protector.Unprotect(stored[Marker.Length..]);
        }
        catch (CryptographicException ex)
        {
            // Wrong machine, wrong service account, or a lost key ring. Returning empty
            // surfaces as "not set" in the editor, which is the honest state: the value
            // has to be typed in again.
            _log.LogError(ex,
                "A stored secret could not be decrypted. The key ring in the data directory " +
                "does not match the one that encrypted it, so the credential must be re-entered.");
            return string.Empty;
        }
    }
}
