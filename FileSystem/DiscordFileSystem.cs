using System.Collections.Concurrent;
using System.Security.AccessControl;
using DokanNet;
using DiscordFS.Discord;
using DiscordFS.Models;
using DiscordFS.Storage;
using FileAccess = DokanNet.FileAccess;

namespace DiscordFS.FileSystem;

public class DiscordFileSystem : IDokanOperations
{
    private readonly DiscordStorageClient _discordClient;
    private readonly MetadataDatabase _database;
    private readonly ChunkCache _cache;
    private readonly ChunkManager _chunkManager;
    
    // Buffer para arquivos sendo escritos
    private readonly ConcurrentDictionary<string, MemoryStream> _writeBuffers = new();
    
    // Cache de dados lidos
    private readonly ConcurrentDictionary<string, byte[]> _readCache = new();

    public DiscordFileSystem(
        DiscordStorageClient discordClient,
        MetadataDatabase database,
        ChunkCache cache,
        ChunkManager chunkManager)
    {
        _discordClient = discordClient;
        _database = database;
        _cache = cache;
        _chunkManager = chunkManager;
    }

    private string NormalizePath(string path)
    {
        return path.TrimStart('\\').Replace('/', '\\');
    }

    #region CreateFile / OpenFile

    public NtStatus CreateFile(
        string fileName,
        FileAccess access,
        FileShare share,
        FileMode mode,
        FileOptions options,
        FileAttributes attributes,
        IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);
        
        // Root directory
        if (string.IsNullOrEmpty(normalizedPath))
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        var existingEntry = _database.GetFileEntry(normalizedPath);
        var exists = existingEntry != null;

        // Verificar se é diretório
        if (exists && existingEntry!.IsDirectory)
        {
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        // Criar diretório
        if (info.IsDirectory)
        {
            switch (mode)
            {
                case FileMode.CreateNew:
                    if (exists) return DokanResult.FileExists;
                    CreateDirectory(normalizedPath);
                    return DokanResult.Success;

                case FileMode.Open:
                    if (!exists) return DokanResult.PathNotFound;
                    return DokanResult.Success;

                default:
                    return DokanResult.Success;
            }
        }

        // Operações de arquivo
        switch (mode)
        {
            case FileMode.CreateNew:
                if (exists) return DokanResult.FileExists;
                _writeBuffers[normalizedPath] = new MemoryStream();
                return DokanResult.Success;

            case FileMode.Create:
                _writeBuffers[normalizedPath] = new MemoryStream();
                return DokanResult.Success;

            case FileMode.Open:
                if (!exists) return DokanResult.FileNotFound;
                return DokanResult.Success;

            case FileMode.OpenOrCreate:
                if (!exists)
                {
                    _writeBuffers[normalizedPath] = new MemoryStream();
                }
                return DokanResult.Success;

            case FileMode.Truncate:
                if (!exists) return DokanResult.FileNotFound;
                _writeBuffers[normalizedPath] = new MemoryStream();
                return DokanResult.Success;

            case FileMode.Append:
                if (exists)
                {
                    // Carregar arquivo existente no buffer
                    var data = LoadFileData(normalizedPath);
                    if (data != null)
                    {
                        var stream = new MemoryStream();
                        stream.Write(data, 0, data.Length);
                        _writeBuffers[normalizedPath] = stream;
                    }
                }
                else
                {
                    _writeBuffers[normalizedPath] = new MemoryStream();
                }
                return DokanResult.Success;

            default:
                return DokanResult.Success;
        }
    }

    private void CreateDirectory(string path)
    {
        var entry = new FileEntry
        {
            VirtualPath = path,
            FileName = Path.GetFileName(path),
            SizeBytes = 0,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            IsDirectory = true
        };
        _database.SaveFileEntry(entry);
    }

    #endregion

    #region ReadFile

    public NtStatus ReadFile(
        string fileName,
        byte[] buffer,
        out int bytesRead,
        long offset,
        IDokanFileInfo info)
    {
        bytesRead = 0;
        var normalizedPath = NormalizePath(fileName);

        if (info.IsDirectory)
        {
            return DokanResult.AccessDenied;
        }

        try
        {
            byte[]? fileData;

            // Verificar se está no cache de leitura
            if (!_readCache.TryGetValue(normalizedPath, out fileData))
            {
                fileData = LoadFileData(normalizedPath);
                if (fileData == null)
                {
                    return DokanResult.FileNotFound;
                }
                _readCache[normalizedPath] = fileData;
            }

            if (offset >= fileData.Length)
            {
                bytesRead = 0;
                return DokanResult.Success;
            }

            var toRead = Math.Min(buffer.Length, (int)(fileData.Length - offset));
            Array.Copy(fileData, offset, buffer, 0, toRead);
            bytesRead = toRead;

            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ReadFile] Erro: {ex.Message}");
            return DokanResult.InternalError;
        }
    }

    private byte[]? LoadFileData(string path)
    {
        var entry = _database.GetFileEntry(path);
        if (entry == null || entry.Chunks.Count == 0)
        {
            return null;
        }

        // Verificar cache primeiro
        var cacheKey = $"file:{path}";
        var cached = _cache.Get(cacheKey);
        if (cached != null) return cached;

        // Download e recomposição
        try
        {
            var data = _chunkManager.ReassembleFromReferencesAsync(
                entry.Chunks,
                url => _discordClient.DownloadChunkAsync(url)
            ).GetAwaiter().GetResult();

            _cache.Add(cacheKey, data);
            return data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoadFileData] Erro ao carregar {path}: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region WriteFile

    public NtStatus WriteFile(
        string fileName,
        byte[] buffer,
        out int bytesWritten,
        long offset,
        IDokanFileInfo info)
    {
        bytesWritten = 0;
        var normalizedPath = NormalizePath(fileName);

        if (info.IsDirectory)
        {
            return DokanResult.AccessDenied;
        }

        try
        {
            if (!_writeBuffers.TryGetValue(normalizedPath, out var stream))
            {
                stream = new MemoryStream();
                _writeBuffers[normalizedPath] = stream;
            }

            lock (stream)
            {
                stream.Position = offset;
                stream.Write(buffer, 0, buffer.Length);
                bytesWritten = buffer.Length;
            }

            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WriteFile] Erro: {ex.Message}");
            return DokanResult.InternalError;
        }
    }

    #endregion

    #region Cleanup / CloseFile

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);

        // Se há dados pendentes para upload
        if (_writeBuffers.TryRemove(normalizedPath, out var stream))
        {
            Task.Run(async () =>
            {
                try
                {
                    await UploadFileAsync(normalizedPath, stream);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Cleanup] Erro no upload de {normalizedPath}: {ex.Message}");
                }
                finally
                {
                    stream.Dispose();
                }
            });
        }

        // Limpar cache de leitura
        _readCache.TryRemove(normalizedPath, out _);
    }

    private async Task UploadFileAsync(string path, MemoryStream stream)
    {
        var data = stream.ToArray();
        if (data.Length == 0) return;

        Console.WriteLine($"[Upload] Iniciando upload de {path} ({data.Length / 1024.0:F1} KB)");

        var chunks = _chunkManager.FragmentBytes(data).ToList();
        var uploadedChunks = new List<ChunkReference>();


        foreach (var chunk in chunks)
        {
            var reference = await _discordClient.UploadChunkAsync(
                chunk.Data,
                Path.GetFileName(path),
                chunk.Index,
                chunk.Crc32
            );
            reference.FileId = 0; // Será definido pelo database
            uploadedChunks.Add(reference);
        }

        var entry = new FileEntry
        {
            VirtualPath = path,
            FileName = Path.GetFileName(path),
            SizeBytes = data.Length,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            IsDirectory = false,
            Chunks = uploadedChunks
        };

        _database.SaveFileEntry(entry);
        Console.WriteLine($"[Upload] Concluído: {path} ({uploadedChunks.Count} chunks)");
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        // Nada a fazer aqui, cleanup já foi feito em Cleanup()
    }

    #endregion

    #region DeleteFile / DeleteDirectory

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);
        
        var entry = _database.GetFileEntry(normalizedPath);
        if (entry == null)
        {
            return DokanResult.FileNotFound;
        }

        // Deletar chunks do Discord em background
        if (entry.Chunks.Count > 0)
        {
            var messageIds = entry.Chunks.Select(c => c.MessageId).ToList();
            Task.Run(async () => await _discordClient.DeleteChunksAsync(messageIds));
        }

        // Deletar do banco de dados
        _database.DeleteFileEntry(normalizedPath);
        _cache.EvictByPrefix($"file:{normalizedPath}");
        
        return DokanResult.Success;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);

        var entry = _database.GetFileEntry(normalizedPath);
        if (entry == null)
        {
            return DokanResult.PathNotFound;
        }

        // Verificar se está vazio
        var children = _database.ListDirectory(normalizedPath);
        if (children.Any())
        {
            return DokanResult.DirectoryNotEmpty;
        }

        // Deletar do banco de dados
        _database.DeleteFileEntry(normalizedPath);
        return DokanResult.Success;
    }

    #endregion

    #region FindFiles / GetFileInformation

    public NtStatus FindFiles(
        string fileName,
        out IList<FileInformation> files,
        IDokanFileInfo info)
    {
        return FindFilesWithPattern(fileName, "*", out files, info);
    }

    public NtStatus FindFilesWithPattern(
        string fileName,
        string searchPattern,
        out IList<FileInformation> files,
        IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        var normalizedPath = NormalizePath(fileName);

        try
        {
            var entries = _database.ListDirectory(normalizedPath);

            foreach (var entry in entries)
            {
                // Filtrar por pattern se necessário
                if (searchPattern != "*" && !MatchPattern(entry.FileName, searchPattern))
                {
                    continue;
                }

                files.Add(new FileInformation
                {
                    FileName = entry.FileName,
                    Attributes = entry.IsDirectory 
                        ? FileAttributes.Directory 
                        : FileAttributes.Normal,
                    CreationTime = entry.CreatedAt,
                    LastAccessTime = entry.ModifiedAt,
                    LastWriteTime = entry.ModifiedAt,
                    Length = entry.SizeBytes
                });
            }

            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FindFiles] Erro: {ex.Message}");
            return DokanResult.InternalError;
        }
    }

    private bool MatchPattern(string fileName, string pattern)
    {
        if (pattern == "*" || pattern == "*.*") return true;
        
        // Simples wildcard matching
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        
        return System.Text.RegularExpressions.Regex.IsMatch(
            fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public NtStatus GetFileInformation(
        string fileName,
        out FileInformation fileInfo,
        IDokanFileInfo info)
    {
        var normalizedPath = NormalizePath(fileName);

        // Root
        if (string.IsNullOrEmpty(normalizedPath))
        {
            fileInfo = new FileInformation
            {
                FileName = "",
                Attributes = FileAttributes.Directory,
                CreationTime = DateTime.Now,
                LastAccessTime = DateTime.Now,
                LastWriteTime = DateTime.Now,
                Length = 0
            };
            return DokanResult.Success;
        }

        var entry = _database.GetFileEntry(normalizedPath);
        if (entry == null)
        {
            // Verificar se está no buffer de escrita
            if (_writeBuffers.TryGetValue(normalizedPath, out var stream))
            {
                fileInfo = new FileInformation
                {
                    FileName = Path.GetFileName(normalizedPath),
                    Attributes = FileAttributes.Normal,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = stream.Length
                };
                return DokanResult.Success;
            }

            fileInfo = default;
            return DokanResult.FileNotFound;
        }

        fileInfo = new FileInformation
        {
            FileName = entry.FileName,
            Attributes = entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal,
            CreationTime = entry.CreatedAt,
            LastAccessTime = entry.ModifiedAt,
            LastWriteTime = entry.ModifiedAt,
            Length = entry.SizeBytes
        };

        return DokanResult.Success;
    }

    #endregion

    #region MoveFile

    public NtStatus MoveFile(
        string oldName,
        string newName,
        bool replace,
        IDokanFileInfo info)
    {
        var oldPath = NormalizePath(oldName);
        var newPath = NormalizePath(newName);

        var oldEntry = _database.GetFileEntry(oldPath);
        if (oldEntry == null)
        {
            return DokanResult.FileNotFound;
        }

        if (!replace && _database.Exists(newPath))
        {
            return DokanResult.FileExists;
        }

        _database.RenameFile(oldPath, newPath);
        _cache.EvictByPrefix($"file:{oldPath}");

        return DokanResult.Success;
    }

    #endregion

    #region Volume Info

    public NtStatus GetDiskFreeSpace(
        out long freeBytesAvailable,
        out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes,
        IDokanFileInfo info)
    {
        // Discord = "ilimitado" para fins práticos
        totalNumberOfBytes = 1L * 1024 * 1024 * 1024 * 1024; // 1 TB virtual
        freeBytesAvailable = 999L * 1024 * 1024 * 1024; // 999 GB livres
        totalNumberOfFreeBytes = freeBytesAvailable;
        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(
        out string volumeLabel,
        out FileSystemFeatures features,
        out string fileSystemName,
        out uint maximumComponentLength,
        IDokanFileInfo info)
    {
        volumeLabel = "DiscordFS";
        fileSystemName = "NTFS";
        maximumComponentLength = 256;
        features = FileSystemFeatures.CasePreservedNames |
                   FileSystemFeatures.CaseSensitiveSearch |
                   FileSystemFeatures.UnicodeOnDisk;
        return DokanResult.Success;
    }

    #endregion

    #region Stubs (não implementados)

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        => DokanResult.Success;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.NotImplemented;
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        Console.WriteLine($"[DiscordFS] Montado em {mountPoint}");
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        Console.WriteLine("[DiscordFS] Desmontado");
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        => DokanResult.NotImplemented;

    #endregion
}
