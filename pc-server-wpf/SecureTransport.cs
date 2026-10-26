using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NexusLink;

/// <summary>Explicit certificate pin + 256-bit pairing secret, sent only inside TLS.</summary>
internal sealed class SecureTransport : IDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly byte[] secret;
    internal string PairingCode => Convert.ToHexString(SHA256.HashData(certificate.RawData)) + ":" + Convert.ToHexString(secret);

    internal SecureTransport(X509Certificate2 certificate, byte[] secret)
    {
        this.certificate = certificate;
        if (secret.Length != 32) throw new ArgumentException("Pairing secret must be 256 bits");
        this.secret = secret.ToArray();
    }

    internal static SecureTransport LoadOrCreate()
    {
        // Private key is persisted by Windows in the current user's certificate store.
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, "CN=NexusLink Local Pairing", false)
            .OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(7));
        if (existing == null)
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=NexusLink Local Pairing", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            existing = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
            store.Add(existing);
        }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusLink");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "pairing-secret.enc");
        byte[] key;
        try
        {
            using var privateKey = existing.GetRSAPrivateKey()!;
            key = privateKey.Decrypt(File.ReadAllBytes(path), RSAEncryptionPadding.OaepSHA256);
            if (key.Length != 32) throw new CryptographicException();
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            key = RandomNumberGenerator.GetBytes(32);
            using var publicKey = existing.GetRSAPublicKey()!;
            File.WriteAllBytes(path, publicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256));
        }
        return new SecureTransport(existing, key);
    }

    internal async Task<SslStream> AcceptAsync(TcpClient client, CancellationToken token = default)
    {
        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = certificate, ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, deadline.Token);
            string auth = await Wire.ReadAsync(tls, deadline.Token);
            if (!auth.StartsWith("AUTH:", StringComparison.Ordinal) || auth.Length != 69)
                throw new AuthenticationException("Pairing required");
            byte[] offered;
            try { offered = Convert.FromHexString(auth.Substring(5)); }
            catch (FormatException) { throw new AuthenticationException("Invalid pairing"); }
            if (!CryptographicOperations.FixedTimeEquals(secret, offered)) throw new AuthenticationException("Pairing rejected");
            await Wire.SendAsync(tls, "AUTH_OK", deadline.Token);
            return tls;
        }
        catch { tls.Dispose(); throw; }
    }
    public void Dispose() { certificate.Dispose(); CryptographicOperations.ZeroMemory(secret); }
}
