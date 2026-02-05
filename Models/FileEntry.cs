namespace DiscordFS.Models;

public class FileEntry
{
    public long Id { get; set; }
    public required string VirtualPath { get; set; }
    public required string FileName { get; set; }
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsDirectory { get; set; }
    public List<ChunkReference> Chunks { get; set; } = new();
}

public class ChunkReference
{
    public long Id { get; set; }
    public long FileId { get; set; }
    public int ChunkIndex { get; set; }
    public ulong MessageId { get; set; }
    public required string AttachmentUrl { get; set; }
    public int SizeBytes { get; set; }
    public uint Crc32 { get; set; }
}

public class ChunkData
{
    public int Index { get; set; }
    public required byte[] Data { get; set; }
    public uint Crc32 { get; set; }
}
