using System.Security.Cryptography;
using System.Text;

namespace DiscordFS.Security;

/// <summary>
/// Gera nomes ofuscados para chunks, evitando padrões detectáveis.
/// O mapeamento real fica apenas no SQLite local.
/// </summary>
public static class NameObfuscator
{
    private static readonly string[] InnocentExtensions = 
    {
        ".jpg", ".png", ".gif", ".webp", ".bmp",
        ".mp3", ".wav", ".ogg",
        ".txt", ".log", ".tmp", ".cache"
    };

    private static readonly string[] InnocentPrefixes =
    {
        "img_", "cache_", "tmp_", "data_", "asset_",
        "thumb_", "preview_", "backup_", "sync_", "media_"
    };

    /// <summary>
    /// Gera um nome de arquivo que parece inocente/comum.
    /// Formato: {prefix}{hash}.{extension}
    /// </summary>
    public static string GenerateObfuscatedName(string originalPath, int chunkIndex)
    {
        // Criar hash único baseado no path + chunk index + timestamp
        var input = $"{originalPath}:{chunkIndex}:{DateTime.UtcNow.Ticks}:{Random.Shared.NextInt64()}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        
        // Usar parte do hash como identificador
        var hashPart = Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
        
        // Selecionar prefixo e extensão aleatórios
        var prefix = InnocentPrefixes[Random.Shared.Next(InnocentPrefixes.Length)];
        var extension = InnocentExtensions[Random.Shared.Next(InnocentExtensions.Length)];
        
        return $"{prefix}{hashPart}{extension}";
    }

    /// <summary>
    /// Gera um nome completamente aleatório (mais seguro, menos "natural").
    /// </summary>
    public static string GenerateRandomName(int length = 16)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var result = new char[length];
        
        for (int i = 0; i < length; i++)
        {
            result[i] = chars[Random.Shared.Next(chars.Length)];
        }
        
        var extension = InnocentExtensions[Random.Shared.Next(InnocentExtensions.Length)];
        return new string(result) + extension;
    }

    /// <summary>
    /// Gera um hash irreversível do nome original para logs.
    /// </summary>
    public static string HashFileName(string fileName)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(fileName + "salt_discordfs"));
        return Convert.ToHexString(hashBytes[..6]).ToLowerInvariant();
    }
}
