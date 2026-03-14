using System.Security.Cryptography;
using System.Text;
using Dawa.Signal;

namespace Dawa.Crypto;

/// <summary>
/// WhatsApp media encryption — derives keys from a random media key using HKDF,
/// then encrypts with AES-CBC and appends a 10-byte HMAC-SHA256 MAC.
/// </summary>
public sealed class MediaEncryptResult
{
    public byte[] EncryptedBytes  { get; init; } = []; // ciphertext || 10-byte mac
    public byte[] MediaKey        { get; init; } = []; // 32-byte random key (sent in proto)
    public byte[] FileSha256      { get; init; } = []; // SHA-256 of original plaintext
    public byte[] FileEncSha256   { get; init; } = []; // SHA-256 of EncryptedBytes
    public long   FileLength      { get; init; }       // original byte count
    public string DirectPath      { get; init; } = ""; // set after CDN upload
    public string Url             { get; init; } = ""; // set after CDN upload
    public long   MediaKeyTimestamp { get; init; }
}

public static class MediaCrypto
{
    private static string HkdfInfo(string mediaType) => mediaType switch
    {
        "image"    => "WhatsApp Image Keys",
        "video"    => "WhatsApp Video Keys",
        "audio"    => "WhatsApp Audio Keys",
        "document" => "WhatsApp Document Keys",
        _          => "WhatsApp Image Keys",
    };

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for WhatsApp media upload.
    /// <paramref name="mediaType"/> is one of: "image", "video", "audio", "document".
    /// </summary>
    public static MediaEncryptResult Encrypt(byte[] plaintext, string mediaType)
    {
        // 1. Random 32-byte media key
        var mediaKey = RandomNumberGenerator.GetBytes(32);

        // 2. HKDF-expand: 112 bytes (IV=16, AES key=32, MAC key=32, ref=32)
        var info    = Encoding.UTF8.GetBytes(HkdfInfo(mediaType));
        var derived = DawaHKDF.DeriveKey(mediaKey, new byte[32], info, 112);
        var iv         = derived[0..16];
        var cipherKey  = derived[16..48];
        var macKey     = derived[48..80];

        // 3. AES-CBC encrypt
        var enc = MessageCipher.AesCbcEncrypt(cipherKey, iv, plaintext);

        // 4. HMAC-SHA256(macKey, IV || enc) — first 10 bytes only
        var macInput = new byte[iv.Length + enc.Length];
        iv.CopyTo(macInput, 0);
        enc.CopyTo(macInput, iv.Length);
        var mac = HMACSHA256.HashData(macKey, macInput)[..10];

        // 5. Final encrypted blob = enc || mac (Baileys format — NO IV).
        // The IV is NOT uploaded to the CDN; receivers derive it from mediaKey via HKDF.
        // The CDN stores only enc+mac and verifies SHA256(enc+mac) == token in URL.
        var encFile = new byte[enc.Length + mac.Length];
        enc.CopyTo(encFile, 0);
        mac.CopyTo(encFile, enc.Length);

        return new MediaEncryptResult
        {
            EncryptedBytes     = encFile,
            MediaKey           = mediaKey,
            FileSha256         = SHA256.HashData(plaintext),
            FileEncSha256      = SHA256.HashData(encFile),
            FileLength         = plaintext.Length,
            MediaKeyTimestamp  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }
}
