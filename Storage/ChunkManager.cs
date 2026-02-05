using System.IO.Hashing;
using DiscordFS.Models;
using DiscordFS.Security;

namespace DiscordFS.Storage;

public class ChunkManager
{
    public const int CHUNK_SIZE = 9 * 1024 * 1024; // 9MB - margem para limite de 10MB do Discord

    private readonly FileEncryptor? _encryptor;

    public ChunkManager(FileEncryptor? encryptor = null)
    {
        _encryptor = encryptor;
    }

    /// <summary>
    /// Fragmenta um arquivo, opcionalmente criptografando antes.
    /// </summary>
    public IEnumerable<ChunkData> FragmentFile(Stream fileStream, bool encrypt = true)
    {
        byte[] data;
        using (var ms = new MemoryStream())
        {
            fileStream.CopyTo(ms);
            data = ms.ToArray();
        }

        return FragmentAndEncrypt(data, encrypt);
    }

    /// <summary>
    /// Criptografa (se habilitado) e fragmenta um array de bytes.
    /// </summary>
    public IEnumerable<ChunkData> FragmentAndEncrypt(byte[] data, bool encrypt = true)
    {
        // Criptografar arquivo inteiro antes de fragmentar
        if (encrypt && _encryptor != null)
        {
            Console.WriteLine($"[Crypto] Criptografando {data.Length} bytes...");
            data = _encryptor.Encrypt(data);
            Console.WriteLine($"[Crypto] Resultado: {data.Length} bytes (overhead: IV + Tag)");
        }

        return FragmentBytes(data);
    }

    public IEnumerable<ChunkData> FragmentBytes(byte[] data)
    {
        int offset = 0;
        int chunkIndex = 0;

        while (offset < data.Length)
        {
            int remaining = data.Length - offset;
            int chunkSize = Math.Min(CHUNK_SIZE, remaining);
            
            var chunkBytes = new byte[chunkSize];
            Buffer.BlockCopy(data, offset, chunkBytes, 0, chunkSize);

            yield return new ChunkData
            {
                Index = chunkIndex++,
                Data = chunkBytes,
                Crc32 = CalculateCrc32(chunkBytes)
            };

            offset += chunkSize;
        }
    }

    public byte[] ReassembleChunks(IEnumerable<byte[]> orderedChunks)
    {
        using var output = new MemoryStream();
        foreach (var chunk in orderedChunks)
        {
            output.Write(chunk, 0, chunk.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Recompõe chunks e descriptografa se necessário.
    /// </summary>
    public async Task<byte[]> ReassembleFromReferencesAsync(
        IEnumerable<ChunkReference> chunks,
        Func<string, Task<byte[]>> downloadFunc,
        bool decrypt = true)
    {
        var orderedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
        var downloadedChunks = new byte[orderedChunks.Count][];

        // Download sequencial com pequeno jitter para evitar detecção
        for (int i = 0; i < orderedChunks.Count; i++)
        {
            var chunk = orderedChunks[i];
            var data = await downloadFunc(chunk.AttachmentUrl);
            
            // Verificar integridade
            var actualCrc = CalculateCrc32(data);
            if (actualCrc != chunk.Crc32)
            {
                throw new InvalidDataException(
                    $"CRC mismatch no chunk {chunk.ChunkIndex}: esperado {chunk.Crc32}, obtido {actualCrc}");
            }
            
            downloadedChunks[i] = data;

            // Pequeno delay entre downloads para não parecer automação
            if (i < orderedChunks.Count - 1)
            {
                await Task.Delay(Random.Shared.Next(100, 400));
            }
        }

        var reassembled = ReassembleChunks(downloadedChunks);

        // Tentar descriptografar se encryptor disponível
        if (decrypt && _encryptor != null)
        {
            // Verificar se parece estar criptografado (tem header AES-GCM: IV + Tag >= 28 bytes)
            if (reassembled.Length >= 28)
            {
                Console.WriteLine($"[Crypto] Tentando descriptografar {reassembled.Length} bytes...");
                try
                {
                    var decrypted = _encryptor.Decrypt(reassembled);
                    Console.WriteLine($"[Crypto] Dados recuperados: {decrypted.Length} bytes");
                    return decrypted;
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Arquivo provavelmente não estava criptografado (versão antiga)
                    Console.WriteLine($"[Crypto] Arquivo não criptografado (versão legada), retornando dados brutos");
                    return reassembled;
                }
            }
            else
            {
                Console.WriteLine($"[Crypto] Arquivo muito pequeno para ter header AES-GCM, retornando dados brutos");
                return reassembled;
            }
        }

        return reassembled;
    }

    public uint CalculateCrc32(byte[] data)
    {
        var crc = new Crc32();
        crc.Append(data);
        var hash = crc.GetCurrentHash();
        return BitConverter.ToUInt32(hash);
    }

    public int CalculateChunkCount(long fileSizeBytes)
    {
        // Considerar overhead de criptografia (~28 bytes por arquivo)
        var adjustedSize = fileSizeBytes + 28;
        return (int)Math.Ceiling((double)adjustedSize / CHUNK_SIZE);
    }
}
