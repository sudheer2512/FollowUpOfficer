using System.Security.Cryptography;
using FollowUp.Core.Abstractions;
using FollowUp.Core.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace FollowUp.Core.Platform;

/// <summary>Protects integration secrets with ASP.NET Core Data Protection (keys persisted, DPAPI-protected on Windows).</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("FollowUp.IntegrationSecrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

/// <summary>
/// Encrypted local attachment storage (spec §69 "Secure attachment storage", "Encryption at rest").
/// Each file is AES-256-GCM encrypted with a per-file key, itself wrapped by Data Protection.
/// Layout: {root}/{yyyy}/{MM}/{guid}.bin = [keyLen:int32][wrappedKey][nonce:12][tag:16][ciphertext].
/// </summary>
public sealed class EncryptedFileStorage(IOptions<FollowUpOptions> options, IDataProtectionProvider provider, Domain.IClock clock) : IFileStorage
{
    private readonly IDataProtector _keyProtector = provider.CreateProtector("FollowUp.AttachmentKeys.v1");
    private string Root => Path.GetFullPath(options.Value.StoragePath);

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var plain = buffer.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(plain));

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var wrapped = _keyProtector.Protect(key);
        CryptographicOperations.ZeroMemory(key);

        var now = clock.UtcNow;
        var storageKey = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}.bin";
        var path = Resolve(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            await fs.WriteAsync(BitConverter.GetBytes(wrapped.Length), ct);
            await fs.WriteAsync(wrapped, ct);
            await fs.WriteAsync(nonce, ct);
            await fs.WriteAsync(tag, ct);
            await fs.WriteAsync(cipher, ct);
        }
        return new StoredFile(storageKey, plain.LongLength, sha);
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(Resolve(storageKey), ct);
        var keyLen = BitConverter.ToInt32(bytes, 0);
        var offset = 4;
        var key = _keyProtector.Unprotect(bytes[offset..(offset + keyLen)]);
        offset += keyLen;
        var nonce = bytes[offset..(offset + 12)];
        offset += 12;
        var tag = bytes[offset..(offset + 16)];
        offset += 16;
        var cipher = bytes[offset..];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
        CryptographicOperations.ZeroMemory(key);
        return new MemoryStream(plain, writable: false);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var path = Resolve(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string storageKey)
    {
        var full = Path.GetFullPath(Path.Combine(Root, storageKey));
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid storage key.");
        return full;
    }
}
