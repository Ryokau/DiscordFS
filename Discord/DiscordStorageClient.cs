using Discord;
using Discord.WebSocket;
using DiscordFS.Models;

namespace DiscordFS.Discord;

public class DiscordStorageClient : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly string _token;
    private readonly ulong _channelId;
    private ITextChannel? _channel;
    private readonly SemaphoreSlim _rateLimiter = new(5, 5); // Max 5 concurrent uploads
    private readonly HttpClient _httpClient = new();
    private bool _isReady = false;

    public DiscordStorageClient(string botToken, ulong channelId)
    {
        _token = botToken;
        _channelId = channelId;

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages,
            LogLevel = LogSeverity.Warning
        };

        _client = new DiscordSocketClient(config);
        _client.Ready += OnReady;
        _client.Log += OnLog;
    }

    public async Task ConnectAsync()
    {
        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();

        // Aguardar conexão
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (!_isReady && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100);
        }

        if (!_isReady)
        {
            throw new TimeoutException("Falha ao conectar ao Discord dentro do tempo limite");
        }

        _channel = await _client.GetChannelAsync(_channelId) as ITextChannel;
        if (_channel == null)
        {
            throw new InvalidOperationException($"Canal {_channelId} não encontrado ou não é um canal de texto");
        }

        Console.WriteLine($"[Discord] Conectado ao canal: {_channel.Name}");
    }

    private Task OnReady()
    {
        _isReady = true;
        Console.WriteLine("[Discord] Bot pronto!");
        return Task.CompletedTask;
    }

    private Task OnLog(LogMessage log)
    {
        if (log.Severity <= LogSeverity.Warning)
        {
            Console.WriteLine($"[Discord] {log.Severity}: {log.Message}");
        }
        return Task.CompletedTask;
    }

    public async Task<ChunkReference> UploadChunkAsync(byte[] data, string fileName, int chunkIndex, uint crc32)
    {
        if (_channel == null)
            throw new InvalidOperationException("Cliente não conectado");

        await _rateLimiter.WaitAsync();
        try
        {
            var chunkFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.chunk{chunkIndex:D4}{Path.GetExtension(fileName)}";
            
            using var stream = new MemoryStream(data);
            var attachment = new FileAttachment(stream, chunkFileName);

            var message = await _channel.SendFileAsync(
                attachment,
                text: $"📦 `{fileName}` | Chunk {chunkIndex} | {data.Length / 1024.0:F1} KB"
            );

            var uploadedAttachment = message.Attachments.First();

            Console.WriteLine($"[Upload] {chunkFileName} -> Msg {message.Id}");

            return new ChunkReference
            {
                ChunkIndex = chunkIndex,
                MessageId = message.Id,
                AttachmentUrl = uploadedAttachment.Url,
                SizeBytes = data.Length,
                Crc32 = crc32
            };
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    public async Task<byte[]> DownloadChunkAsync(string attachmentUrl)
    {
        var maxRetries = 3;
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await _httpClient.GetByteArrayAsync(attachmentUrl);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries - 1)
            {
                Console.WriteLine($"[Download] Retry {attempt + 1}/{maxRetries}: {ex.Message}");
                await Task.Delay(delay);
                delay *= 2; // Exponential backoff
            }
        }

        throw new IOException($"Falha ao baixar chunk após {maxRetries} tentativas");
    }

    public async Task DeleteChunkAsync(ulong messageId)
    {
        if (_channel == null)
            throw new InvalidOperationException("Cliente não conectado");

        try
        {
            var message = await _channel.GetMessageAsync(messageId);
            if (message != null)
            {
                await _channel.DeleteMessageAsync(message);
                Console.WriteLine($"[Delete] Mensagem {messageId} removida");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Delete] Erro ao deletar mensagem {messageId}: {ex.Message}");
        }
    }

    public async Task DeleteChunksAsync(IEnumerable<ulong> messageIds)
    {
        foreach (var msgId in messageIds)
        {
            await DeleteChunkAsync(msgId);
            await Task.Delay(100); // Respeitar rate limit
        }
    }

    public bool IsConnected => _isReady && _channel != null;

    public async ValueTask DisposeAsync()
    {
        await _client.LogoutAsync();
        await _client.StopAsync();
        _client.Dispose();
        _httpClient.Dispose();
        _rateLimiter.Dispose();
    }
}
