using System.Security.Cryptography;
using System.Text;

namespace WhatsAppBridge.API.Services;

public class EncryptionService
{
    private readonly bool _encryptionEnabled;
    private readonly byte[] _key;
    private readonly byte[] _iv;

    public EncryptionService(IConfiguration configuration)
    {
        _encryptionEnabled = configuration.GetValue<bool>("Encryption:Enabled", false);

        if (_encryptionEnabled)
        {
            var keyString = configuration["Encryption:Key"]
                ?? throw new InvalidOperationException("Encryption:Key is required when encryption is enabled");
            var ivString = configuration["Encryption:IV"]
                ?? throw new InvalidOperationException("Encryption:IV is required when encryption is enabled");

            _key = Convert.FromBase64String(keyString);
            _iv = Convert.FromBase64String(ivString);

            if (_key.Length != 32)
                throw new InvalidOperationException("Encryption key must be 32 bytes (256 bits)");
            if (_iv.Length != 16)
                throw new InvalidOperationException("Encryption IV must be 16 bytes (128 bits)");
        }
        else
        {
            _key = Array.Empty<byte>();
            _iv = Array.Empty<byte>();
        }
    }

    public string Encrypt(string plainText)
    {
        if (!_encryptionEnabled || string.IsNullOrEmpty(plainText))
            return plainText;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;

        var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var msEncrypt = new MemoryStream();
        using var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write);
        using (var swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(plainText);
        }

        return Convert.ToBase64String(msEncrypt.ToArray());
    }

    public string Decrypt(string cipherText)
    {
        if (!_encryptionEnabled || string.IsNullOrEmpty(cipherText))
            return cipherText;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;

        var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var msDecrypt = new MemoryStream(Convert.FromBase64String(cipherText));
        using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using var srDecrypt = new StreamReader(csDecrypt);

        return srDecrypt.ReadToEnd();
    }

    public bool IsEncryptionEnabled => _encryptionEnabled;

    // Helper to generate encryption keys
    public static (string key, string iv) GenerateKeys()
    {
        using var aes = Aes.Create();
        aes.GenerateKey();
        aes.GenerateIV();

        return (Convert.ToBase64String(aes.Key), Convert.ToBase64String(aes.IV));
    }
}
