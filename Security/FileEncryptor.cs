using System.Security.Cryptography;

namespace DiscordFS.Security;

/// <summary>
/// Criptografia AES-256-GCM com geração automática de IV único por arquivo.
/// O IV e o Authentication Tag são prefixados ao ciphertext.
/// </summary>
public class FileEncryptor : IDisposable
{
    private readonly byte[] _masterKey;
    private const int KEY_SIZE = 32;  // 256 bits
    private const int IV_SIZE = 12;   // 96 bits (recomendado para GCM)
    private const int TAG_SIZE = 16;  // 128 bits

    public FileEncryptor(byte[] masterKey)
    {
        if (masterKey.Length != KEY_SIZE)
            throw new ArgumentException($"Master key must be {KEY_SIZE} bytes (256 bits)");
        
        _masterKey = masterKey;
    }

    public FileEncryptor(string base64Key)
        : this(Convert.FromBase64String(base64Key))
    {
    }

    /// <summary>
    /// Gera uma nova chave mestra aleatória.
    /// </summary>
    public static byte[] GenerateMasterKey()
    {
        var key = new byte[KEY_SIZE];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// Deriva uma chave única por arquivo usando HKDF.
    /// Isso garante que cada arquivo use uma chave diferente.
    /// </summary>
    private byte[] DeriveFileKey(byte[] salt)
    {
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            _masterKey,
            KEY_SIZE,
            salt,
            info: System.Text.Encoding.UTF8.GetBytes("DiscordFS-FileKey-v1")
        );
    }

    /// <summary>
    /// Criptografa dados com AES-256-GCM.
    /// Formato de saída: [IV (12 bytes)] [Tag (16 bytes)] [Ciphertext]
    /// </summary>
    public byte[] Encrypt(byte[] plaintext)
    {
        // Gerar IV único
        var iv = new byte[IV_SIZE];
        RandomNumberGenerator.Fill(iv);

        // Derivar chave única para este arquivo
        var fileKey = DeriveFileKey(iv);

        using var aes = new AesGcm(fileKey, TAG_SIZE);
        
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TAG_SIZE];

        aes.Encrypt(iv, plaintext, ciphertext, tag);

        // Combinar: IV + Tag + Ciphertext
        var result = new byte[IV_SIZE + TAG_SIZE + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, result, 0, IV_SIZE);
        Buffer.BlockCopy(tag, 0, result, IV_SIZE, TAG_SIZE);
        Buffer.BlockCopy(ciphertext, 0, result, IV_SIZE + TAG_SIZE, ciphertext.Length);

        // Limpar chave derivada da memória
        CryptographicOperations.ZeroMemory(fileKey);

        return result;
    }

    /// <summary>
    /// Descriptografa dados criptografados com AES-256-GCM.
    /// </summary>
    public byte[] Decrypt(byte[] encryptedData)
    {
        if (encryptedData.Length < IV_SIZE + TAG_SIZE)
            throw new ArgumentException("Encrypted data is too short");

        // Extrair componentes
        var iv = new byte[IV_SIZE];
        var tag = new byte[TAG_SIZE];
        var ciphertext = new byte[encryptedData.Length - IV_SIZE - TAG_SIZE];

        Buffer.BlockCopy(encryptedData, 0, iv, 0, IV_SIZE);
        Buffer.BlockCopy(encryptedData, IV_SIZE, tag, 0, TAG_SIZE);
        Buffer.BlockCopy(encryptedData, IV_SIZE + TAG_SIZE, ciphertext, 0, ciphertext.Length);

        // Derivar mesma chave usando o IV extraído
        var fileKey = DeriveFileKey(iv);

        using var aes = new AesGcm(fileKey, TAG_SIZE);
        
        var plaintext = new byte[ciphertext.Length];
        
        try
        {
            aes.Decrypt(iv, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(fileKey);
            throw new CryptographicException("Data integrity check failed - file may be corrupted or tampered");
        }

        CryptographicOperations.ZeroMemory(fileKey);
        return plaintext;
    }

    /// <summary>
    /// Criptografa um stream grande em chunks para evitar carregar tudo na memória.
    /// </summary>
    public async Task<byte[]> EncryptStreamAsync(Stream input)
    {
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms);
        return Encrypt(ms.ToArray());
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_masterKey);
    }
}
