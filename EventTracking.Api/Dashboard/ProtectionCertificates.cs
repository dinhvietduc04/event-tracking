using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using EventTracking.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Npgsql;

namespace EventTracking.Api.Dashboard;

public sealed class ProtectionCertificates : IDisposable
{
    private static readonly XNamespace ProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";
    public X509Certificate2? Active { get; private set; }
    public List<X509Certificate2> Previous { get; } = [];

    public static ProtectionCertificates Load(IConfiguration config, bool required)
    {
        var result = new ProtectionCertificates();
        try
        {
            result.Active = Read(config.GetSection("DataProtection:Certificate"), active: true);
            foreach (var previous in config.GetSection("DataProtection:PreviousCertificates").GetChildren())
                result.Previous.Add(Read(previous, active: false) ?? throw new InvalidOperationException("A previous protection certificate is missing."));
            if (required && result.Active is null) throw new InvalidOperationException("Production requires DataProtection:Certificate:Path or Base64 and its Password secret.");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static X509Certificate2? Read(IConfigurationSection section, bool active)
    {
        string? path = section["Path"], encoded = section["Base64"];
        if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(encoded)) return null;
        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(encoded)) throw new InvalidOperationException("Choose either Path or Base64 for each protection certificate.");
        X509Certificate2 certificate;
        try
        {
            byte[] bytes = string.IsNullOrEmpty(path) ? Convert.FromBase64String(encoded!) : File.ReadAllBytes(path);
            try { certificate = X509CertificateLoader.LoadPkcs12(bytes, section["Password"], X509KeyStorageFlags.EphemeralKeySet); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Protection certificate could not be loaded. Check the secret, PFX format, path and password."); }
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null || rsa.KeySize < 2048 || (active && (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)))
        {
            certificate.Dispose();
            throw new InvalidOperationException("Protection certificates require an RSA private key of at least 2048 bits; the active certificate must be valid now.");
        }
        return certificate;
    }

    public void Configure(IDataProtectionBuilder protection)
    {
        if (Active is null) return;
        protection.ProtectKeysWithCertificate(Active).UnprotectKeysWithAnyCertificate([Active, .. Previous]);
    }

    public static async Task VerifyStoredKeys(IServiceProvider services, CancellationToken ct = default)
    {
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        await using var query = DatabaseSql.Command(connection, null, "SELECT xml FROM data_protection_keys");
        HashSet<Guid> expectedKeys = [];
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var xml = XElement.Parse(reader.GetString(0));
                if (xml.DescendantsAndSelf().Any(RequiresEncryption))
                    throw new InvalidOperationException("Unencrypted Data Protection keys remain. Stop old instances and run --protect-keys with the operator connection before starting production.");
                if (xml.Name != "key" && xml.Name != "revocation") throw new InvalidOperationException("Unrecognized Data Protection record.");
                if (xml.Name != "key") continue;
                if (!Guid.TryParse((string?)xml.Attribute("id"), out var id) || !expectedKeys.Add(id))
                    throw new InvalidOperationException("Invalid or duplicate Data Protection key identity.");
                var encrypted = xml.Descendants(ProtectionNamespace + "encryptedSecret").ToArray();
                if (encrypted.Length == 0 || encrypted.Any(e => (string?)e.Attribute("decryptorType") != typeof(EncryptedXmlDecryptor).AssemblyQualifiedName))
                    throw new InvalidOperationException("Every retained Data Protection key must use certificate encryption.");
            }
        // Fail startup if any retained key cannot be recovered with the supplied private certificates.
        var keys = services.GetRequiredService<IKeyManager>().GetAllKeys();
        // Another production instance can add an encrypted key between these reads.
        if (!expectedKeys.IsSubsetOf(keys.Select(k => k.KeyId))) throw new InvalidOperationException("Some Data Protection records could not be loaded. Recover the key ring before starting production.");
        foreach (var key in keys) _ = key.CreateEncryptor();
    }

    public static async Task<int> ProtectStoredKeys(IServiceProvider services, CancellationToken ct = default)
    {
        var certificate = services.GetRequiredService<ProtectionCertificates>().Active
            ?? throw new InvalidOperationException("Configure the active protection certificate before running --protect-keys.");
        var encryptor = new CertificateXmlEncryptor(certificate, services.GetRequiredService<ILoggerFactory>());
        var decryptor = new EncryptedXmlDecryptor(services);
        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Operators must stop old writers. The table lock also serializes this command with key inserts.
        await using (var locked = DatabaseSql.Command(connection, transaction, "LOCK TABLE data_protection_keys IN EXCLUSIVE MODE")) await locked.ExecuteNonQueryAsync(ct);
        List<(int Id, XElement Xml)> records = [];
        await using (var query = DatabaseSql.Command(connection, transaction, "SELECT id,xml FROM data_protection_keys ORDER BY id"))
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) records.Add((reader.GetInt32(0), XElement.Parse(reader.GetString(1))));
        int count = 0;
        foreach (var (id, xml) in records)
        {
            // Use ASP.NET Core's certificate XML encryption format, preserving key IDs and lifetimes.
            foreach (var encrypted in xml.Descendants(ProtectionNamespace + "encryptedSecret").ToArray())
            {
                if ((string?)encrypted.Attribute("decryptorType") != typeof(EncryptedXmlDecryptor).AssemblyQualifiedName)
                    throw new InvalidOperationException("Unsupported key encryption format; retain its original recovery mechanism.");
                encrypted.ReplaceWith(decryptor.Decrypt(encrypted.Elements().Single()));
            }
            bool changed = false;
            while (xml.Descendants().FirstOrDefault(RequiresEncryption) is { } secret)
            {
                var encrypted = encryptor.Encrypt(new XElement(secret));
                secret.ReplaceWith(new XElement(ProtectionNamespace + "encryptedSecret",
                    new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!), encrypted.EncryptedElement));
                changed = true;
            }
            if (!changed) continue;
            await using var update = DatabaseSql.Command(connection, transaction, "UPDATE data_protection_keys SET xml=$1 WHERE id=$2", xml.ToString(SaveOptions.DisableFormatting), id);
            await update.ExecuteNonQueryAsync(ct); count++;
        }
        await AuditLog.Write(connection, transaction, "operator:protect-keys", null, "protection.keys-wrapped", certificate.Thumbprint, new { count }, ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    private static bool RequiresEncryption(XElement element) => (bool?)element.Attribute(ProtectionNamespace + "requiresEncryption") == true;
    public void Dispose() { Active?.Dispose(); foreach (var certificate in Previous) certificate.Dispose(); }
}
