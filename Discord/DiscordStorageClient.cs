using Discord;
using Discord.WebSocket;
using DiscordFS.Models;
using DiscordFS.Security;
using DiscordRateLimit = Discord.Net.RateLimitedException;

namespace DiscordFS.Discord;

public class DiscordStorageClient : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly string _token;
    private readonly ulong _channelId;
    private ITextChannel? _channel;
    private readonly SemaphoreSlim _rateLimiter = new(3, 3); // Reduzido para 3 concurrent
    private readonly HttpClient _httpClient;
    private readonly SmartThrottler _throttler;
    private bool _isReady = false;

    // User-Agent pool para rotação
    private static readonly string[] UserAgents = 
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36"
    };

    public DiscordStorageClient(string botToken, ulong channelId)
    {
        _token = botToken;
        _channelId = channelId;
        _throttler = new SmartThrottler(1.5, 4.2);

        // Configurar HttpClient com headers de navegador
        _httpClient = new HttpClient();
        ConfigureHttpClient();

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages,
            LogLevel = LogSeverity.Warning
        };

        _client = new DiscordSocketClient(config);
        _client.Ready += OnReady;
        _client.Log += OnLog;
    }

    private void ConfigureHttpClient()
    {
        var userAgent = UserAgents[Random.Shared.Next(UserAgents.Length)];
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
        _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        _httpClient.DefaultRequestHeaders.Add("DNT", "1");
        _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
    }

    private void RotateUserAgent()
    {
        var userAgent = UserAgents[Random.Shared.Next(UserAgents.Length)];
        _httpClient.DefaultRequestHeaders.Remove("User-Agent");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
    }

    public async Task ConnectAsync()
    {
        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();

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

    /// <summary>
    /// Upload com throttling inteligente, retry automático e nomes ofuscados.
    /// </summary>
    public async Task<ChunkReference> UploadChunkAsync(
        byte[] data, 
        string originalFileName, 
        int chunkIndex, 
        uint crc32,
        CancellationToken cancellationToken = default)
    {
        if (_channel == null)
            throw new InvalidOperationException("Cliente não conectado");

        // Gerar nome ofuscado
        var obfuscatedName = NameObfuscator.GenerateObfuscatedName(originalFileName, chunkIndex);

        var maxRetries = 5;
        var attempt = 0;

        while (attempt < maxRetries)
        {
            await _rateLimiter.WaitAsync(cancellationToken);
            try
            {
                // Aguardar jitter antes do upload
                await _throttler.WaitAsync(cancellationToken);

                using var stream = new MemoryStream(data);
                var attachment = new FileAttachment(stream, obfuscatedName);

                // Mensagem sem metadados óbvios (apenas emoji genérico)
                var message = await _channel.SendFileAsync(
                    attachment,
                    text: "📎" // Mensagem mínima, sem informações identificáveis
                );

                var uploadedAttachment = message.Attachments.First();

                _throttler.RegisterSuccess();
                
                // Log ofuscado (não mostra nome real)
                var hashedName = NameObfuscator.HashFileName(originalFileName);
                Console.WriteLine($"[Upload] {hashedName}:{chunkIndex} -> {obfuscatedName}");

                return new ChunkReference
                {
                    ChunkIndex = chunkIndex,
                    MessageId = message.Id,
                    AttachmentUrl = uploadedAttachment.Url,
                    SizeBytes = data.Length,
                    Crc32 = crc32
                };
            }
            catch (DiscordRateLimit)
            {
                _throttler.RegisterError(429);
                await _throttler.RateLimitPauseAsync(60, cancellationToken);
                attempt++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _throttler.RegisterError(429);
                await _throttler.RateLimitPauseAsync(60, cancellationToken);
                attempt++;
            }
            catch (Exception ex) when (attempt < maxRetries - 1)
            {
                _throttler.RegisterError();
                Console.WriteLine($"[Upload] Erro (tentativa {attempt + 1}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), cancellationToken);
                attempt++;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        throw new IOException($"Falha no upload após {maxRetries} tentativas");
    }

    /// <summary>
    /// Download com retry e rotação de User-Agent.
    /// </summary>
    public async Task<byte[]> DownloadChunkAsync(string attachmentUrl, CancellationToken cancellationToken = default)
    {
        var maxRetries = 5;
        var delay = TimeSpan.FromSeconds(2);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Rotacionar User-Agent a cada tentativa
                RotateUserAgent();
                
                // Pequeno jitter antes do download também
                await Task.Delay(Random.Shared.Next(200, 800), cancellationToken);
                
                return await _httpClient.GetByteArrayAsync(attachmentUrl, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                Console.WriteLine($"[Download] Rate limit! Aguardando 60s...");
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries - 1)
            {
                Console.WriteLine($"[Download] Retry {attempt + 1}/{maxRetries}: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }

        throw new IOException($"Falha ao baixar chunk após {maxRetries} tentativas");
    }

    public async Task DeleteChunkAsync(ulong messageId, CancellationToken cancellationToken = default)
    {
        if (_channel == null)
            throw new InvalidOperationException("Cliente não conectado");

        try
        {
            await _throttler.WaitAsync(cancellationToken);
            
            var message = await _channel.GetMessageAsync(messageId);
            if (message != null)
            {
                await _channel.DeleteMessageAsync(message);
                _throttler.RegisterSuccess();
            }
        }
        catch (Exception ex)
        {
            _throttler.RegisterError();
            Console.WriteLine($"[Delete] Erro: {ex.Message}");
        }
    }

    public async Task DeleteChunksAsync(IEnumerable<ulong> messageIds, CancellationToken cancellationToken = default)
    {
        foreach (var msgId in messageIds)
        {
            await DeleteChunkAsync(msgId, cancellationToken);
        }
    }

    public bool IsConnected => _isReady && _channel != null;
    public double CurrentThrottleMultiplier => _throttler.CurrentMultiplier;

    public async ValueTask DisposeAsync()
    {
        await _client.LogoutAsync();
        await _client.StopAsync();
        _client.Dispose();
        _httpClient.Dispose();
        _rateLimiter.Dispose();
    }
}
