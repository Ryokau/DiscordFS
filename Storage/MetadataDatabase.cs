using Microsoft.Data.Sqlite;
using DiscordFS.Models;

namespace DiscordFS.Storage;

public class MetadataDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public MetadataDatabase(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        CreateSchema();
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                virtual_path TEXT UNIQUE NOT NULL,
                file_name TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                is_directory INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id INTEGER NOT NULL,
                chunk_index INTEGER NOT NULL,
                message_id INTEGER NOT NULL,
                attachment_url TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                crc32 INTEGER NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_files_path ON files(virtual_path);
            CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
        ";
        cmd.ExecuteNonQuery();
    }

    public void SaveFileEntry(FileEntry entry)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                // Inserir ou atualizar arquivo
                using var fileCmd = _connection.CreateCommand();
                fileCmd.CommandText = @"
                    INSERT INTO files (virtual_path, file_name, size_bytes, created_at, modified_at, is_directory)
                    VALUES (@path, @name, @size, @created, @modified, @isDir)
                    ON CONFLICT(virtual_path) DO UPDATE SET
                        file_name = @name,
                        size_bytes = @size,
                        modified_at = @modified
                    RETURNING id;
                ";
                fileCmd.Parameters.AddWithValue("@path", entry.VirtualPath);
                fileCmd.Parameters.AddWithValue("@name", entry.FileName);
                fileCmd.Parameters.AddWithValue("@size", entry.SizeBytes);
                fileCmd.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("O"));
                fileCmd.Parameters.AddWithValue("@modified", entry.ModifiedAt.ToString("O"));
                fileCmd.Parameters.AddWithValue("@isDir", entry.IsDirectory ? 1 : 0);

                var fileId = Convert.ToInt64(fileCmd.ExecuteScalar());
                entry.Id = fileId;

                // Limpar chunks antigos
                using var deleteCmd = _connection.CreateCommand();
                deleteCmd.CommandText = "DELETE FROM chunks WHERE file_id = @fileId";
                deleteCmd.Parameters.AddWithValue("@fileId", fileId);
                deleteCmd.ExecuteNonQuery();

                // Inserir novos chunks
                foreach (var chunk in entry.Chunks)
                {
                    using var chunkCmd = _connection.CreateCommand();
                    chunkCmd.CommandText = @"
                        INSERT INTO chunks (file_id, chunk_index, message_id, attachment_url, size_bytes, crc32)
                        VALUES (@fileId, @index, @msgId, @url, @size, @crc)
                    ";
                    chunkCmd.Parameters.AddWithValue("@fileId", fileId);
                    chunkCmd.Parameters.AddWithValue("@index", chunk.ChunkIndex);
                    chunkCmd.Parameters.AddWithValue("@msgId", (long)chunk.MessageId);
                    chunkCmd.Parameters.AddWithValue("@url", chunk.AttachmentUrl);
                    chunkCmd.Parameters.AddWithValue("@size", chunk.SizeBytes);
                    chunkCmd.Parameters.AddWithValue("@crc", (long)chunk.Crc32);
                    chunkCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    public FileEntry? GetFileEntry(string virtualPath)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, virtual_path, file_name, size_bytes, created_at, modified_at, is_directory
                FROM files WHERE virtual_path = @path
            ";
            cmd.Parameters.AddWithValue("@path", virtualPath);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var entry = new FileEntry
            {
                Id = reader.GetInt64(0),
                VirtualPath = reader.GetString(1),
                FileName = reader.GetString(2),
                SizeBytes = reader.GetInt64(3),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                ModifiedAt = DateTime.Parse(reader.GetString(5)),
                IsDirectory = reader.GetInt32(6) == 1
            };

            // Carregar chunks
            entry.Chunks = GetChunksForFile(entry.Id);
            return entry;
        }
    }

    private List<ChunkReference> GetChunksForFile(long fileId)
    {
        var chunks = new List<ChunkReference>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, chunk_index, message_id, attachment_url, size_bytes, crc32
            FROM chunks WHERE file_id = @fileId ORDER BY chunk_index
        ";
        cmd.Parameters.AddWithValue("@fileId", fileId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new ChunkReference
            {
                Id = reader.GetInt64(0),
                FileId = fileId,
                ChunkIndex = reader.GetInt32(1),
                MessageId = (ulong)reader.GetInt64(2),
                AttachmentUrl = reader.GetString(3),
                SizeBytes = reader.GetInt32(4),
                Crc32 = (uint)reader.GetInt64(5)
            });
        }
        return chunks;
    }

    public void DeleteFileEntry(string virtualPath)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM files WHERE virtual_path = @path";
            cmd.Parameters.AddWithValue("@path", virtualPath);
            cmd.ExecuteNonQuery();
        }
    }

    public IEnumerable<FileEntry> ListDirectory(string directoryPath)
    {
        lock (_lock)
        {
            var entries = new List<FileEntry>();
            var normalizedPath = directoryPath.TrimEnd('\\');
            var pattern = normalizedPath == "" ? "%" : normalizedPath + "\\%";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, virtual_path, file_name, size_bytes, created_at, modified_at, is_directory
                FROM files 
                WHERE virtual_path LIKE @pattern
                AND virtual_path NOT LIKE @deepPattern
            ";
            cmd.Parameters.AddWithValue("@pattern", pattern);
            cmd.Parameters.AddWithValue("@deepPattern", pattern + "\\%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new FileEntry
                {
                    Id = reader.GetInt64(0),
                    VirtualPath = reader.GetString(1),
                    FileName = reader.GetString(2),
                    SizeBytes = reader.GetInt64(3),
                    CreatedAt = DateTime.Parse(reader.GetString(4)),
                    ModifiedAt = DateTime.Parse(reader.GetString(5)),
                    IsDirectory = reader.GetInt32(6) == 1
                });
            }
            return entries;
        }
    }

    public bool Exists(string virtualPath)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE virtual_path = @path";
            cmd.Parameters.AddWithValue("@path", virtualPath);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public void RenameFile(string oldPath, string newPath)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE files 
                SET virtual_path = @newPath, 
                    file_name = @fileName,
                    modified_at = @modified
                WHERE virtual_path = @oldPath
            ";
            cmd.Parameters.AddWithValue("@oldPath", oldPath);
            cmd.Parameters.AddWithValue("@newPath", newPath);
            cmd.Parameters.AddWithValue("@fileName", Path.GetFileName(newPath));
            cmd.Parameters.AddWithValue("@modified", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public List<ChunkReference> GetAllChunksForFile(string virtualPath)
    {
        var entry = GetFileEntry(virtualPath);
        return entry?.Chunks ?? new List<ChunkReference>();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
