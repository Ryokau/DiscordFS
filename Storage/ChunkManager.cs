using System.IO.Hashing;
using DiscordFS.Models;

namespace DiscordFS.Storage;

public class ChunkManager
{
    public const int CHUNK_SIZE = 9 * 1024 * 1024; // 9MB - margem de segurança para limite de 10MB do Discord

    public IEnumerable<ChunkData> FragmentFile(Stream fileStream)
    {
        var buffer = new byte[CHUNK_SIZE];
        int bytesRead;
        int chunkIndex = 0;

        while ((bytesRead = fileStream.Read(buffer, 0, CHUNK_SIZE)) > 0)
        {
            var chunkBytes = new byte[bytesRead];
            Array.Copy(buffer, chunkBytes, bytesRead);

            yield return new ChunkData
            {
                Index = chunkIndex++,
                Data = chunkBytes,
                Crc32 = CalculateCrc32(chunkBytes)
            };
        }
    }

    public IEnumerable<ChunkData> FragmentBytes(byte[] data)
    {
        using var stream = new MemoryStream(data);
        foreach (var chunk in FragmentFile(stream))
        {
            yield return chunk;
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

    public async Task<byte[]> ReassembleFromReferencesAsync(
        IEnumerable<ChunkReference> chunks,
        Func<string, Task<byte[]>> downloadFunc)
    {
        var orderedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
        var downloadedChunks = new byte[orderedChunks.Count][];

        // Download paralelo para performance
        var tasks = orderedChunks.Select(async (chunk, idx) =>
        {
            var data = await downloadFunc(chunk.AttachmentUrl);
            
            // Verificar integridade
            var actualCrc = CalculateCrc32(data);
            if (actualCrc != chunk.Crc32)
            {
                throw new InvalidDataException(
                    $"CRC mismatch no chunk {chunk.ChunkIndex}: esperado {chunk.Crc32}, obtido {actualCrc}");
            }
            
            downloadedChunks[idx] = data;
        });

        await Task.WhenAll(tasks);
        return ReassembleChunks(downloadedChunks);
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
        return (int)Math.Ceiling((double)fileSizeBytes / CHUNK_SIZE);
    }
}
