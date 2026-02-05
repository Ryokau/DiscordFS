using Microsoft.Extensions.Configuration;
using DokanNet;
using DiscordFS.Discord;
using DiscordFS.FileSystem;
using DiscordFS.Storage;
using DiscordFS.Security;

namespace DiscordFS;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("╔═══════════════════════════════════════════╗");
        Console.WriteLine("║       DiscordFS - Secure Virtual Drive    ║");
        Console.WriteLine("║   Armazenamento criptografado via Discord ║");
        Console.WriteLine("╚═══════════════════════════════════════════╝");
        Console.WriteLine();

        // Carregar configuração
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var botToken = config["Discord:BotToken"];
        var channelIdStr = config["Discord:ChannelId"];
        var driveLetter = config["FileSystem:DriveLetter"] ?? "Z";
        var cacheSizeMB = int.Parse(config["FileSystem:CacheSizeMB"] ?? "256");
        
        // Configurações de segurança
        var enableEncryption = bool.Parse(config["Security:EnableEncryption"] ?? "true");
        var masterKeyBase64 = config["Security:MasterKey"];

        if (string.IsNullOrEmpty(botToken) || botToken == "YOUR_BOT_TOKEN_HERE")
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERRO] Configure o BotToken em appsettings.json");
            Console.ResetColor();
            return;
        }

        if (!ulong.TryParse(channelIdStr, out var channelId) || channelId == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[AVISO] ChannelId não configurado!");
            Console.WriteLine("Para obter o Channel ID:");
            Console.WriteLine("  1. Ative o Modo Desenvolvedor no Discord (Configurações > Avançado)");
            Console.WriteLine("  2. Clique direito no canal desejado");
            Console.WriteLine("  3. Clique em 'Copiar ID'");
            Console.WriteLine("  4. Cole o ID em appsettings.json");
            Console.ResetColor();
            return;
        }

        // Configurar criptografia
        FileEncryptor? encryptor = null;
        var keyFilePath = Path.Combine(AppContext.BaseDirectory, ".masterkey");

        if (enableEncryption)
        {
            Console.WriteLine("[Security] Criptografia habilitada (AES-256-GCM)");
            
            byte[] masterKey;
            
            if (!string.IsNullOrEmpty(masterKeyBase64))
            {
                // Usar chave do config
                masterKey = Convert.FromBase64String(masterKeyBase64);
                Console.WriteLine("[Security] Usando chave do appsettings.json");
            }
            else if (File.Exists(keyFilePath))
            {
                // Carregar chave existente
                masterKey = Convert.FromBase64String(File.ReadAllText(keyFilePath));
                Console.WriteLine("[Security] Chave carregada de .masterkey");
            }
            else
            {
                // Gerar nova chave
                masterKey = FileEncryptor.GenerateMasterKey();
                File.WriteAllText(keyFilePath, Convert.ToBase64String(masterKey));
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[Security] NOVA chave gerada e salva em .masterkey");
                Console.WriteLine("           GUARDE ESTE ARQUIVO EM LOCAL SEGURO!");
                Console.WriteLine($"           Key: {Convert.ToBase64String(masterKey)}");
                Console.ResetColor();
            }

            encryptor = new FileEncryptor(masterKey);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[Security] AVISO: Criptografia DESABILITADA!");
            Console.ResetColor();
        }

        var mountPoint = $"{driveLetter}:\\";
        var dbPath = Path.Combine(AppContext.BaseDirectory, "discordfs.db");

        Console.WriteLine($"[Config] Token: ...{botToken[^10..]}");
        Console.WriteLine($"[Config] Canal: {channelId}");
        Console.WriteLine($"[Config] Drive: {mountPoint}");
        Console.WriteLine($"[Config] Cache: {cacheSizeMB} MB");
        Console.WriteLine($"[Config] DB: {dbPath}");
        Console.WriteLine();

        // Inicializar componentes
        Console.WriteLine("[Init] Conectando ao Discord...");
        var discordClient = new DiscordStorageClient(botToken, channelId);
        
        try
        {
            await discordClient.ConnectAsync();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERRO] Falha ao conectar: {ex.Message}");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("[Init] Inicializando banco de dados...");
        var database = new MetadataDatabase(dbPath);

        Console.WriteLine("[Init] Configurando cache...");
        var cache = new ChunkCache(cacheSizeMB);

        Console.WriteLine("[Init] Criando chunk manager com criptografia...");
        var chunkManager = new ChunkManager(encryptor);

        Console.WriteLine("[Init] Criando sistema de arquivos...");
        var fileSystem = new DiscordFileSystem(
            discordClient,
            database,
            cache,
            chunkManager
        );

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[OK] Montando drive {mountPoint}");
        Console.WriteLine("     Pressione Ctrl+C para desmontar e sair.");
        Console.ResetColor();
        Console.WriteLine();

        // Configurar handler para Ctrl+C
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[Exit] Desmontando drive...");
            cts.Cancel();
        };

        // Montar drive em thread separada
        var dokanInstance = default(DokanInstance);
        var mountTask = Task.Run(() =>
        {
            try
            {
                var dokanBuilder = new DokanInstanceBuilder(new Dokan(new DokanNet.Logging.NullLogger()))
                    .ConfigureOptions(options =>
                    {
                        options.MountPoint = mountPoint;
                        options.Options = DokanOptions.FixedDrive | DokanOptions.StderrOutput;
                    });
                
                dokanInstance = dokanBuilder.Build(fileSystem);
            }
            catch (DokanException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERRO] Falha ao montar: {ex.Message}");
                Console.WriteLine();
                Console.WriteLine("Verifique se o Dokan está instalado:");
                Console.WriteLine("  https://github.com/dokan-dev/dokany/releases");
                Console.ResetColor();
                cts.Cancel();
            }
        });

        // Aguardar cancelamento
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Normal - usuário pressionou Ctrl+C
        }

        // Cleanup
        Console.WriteLine("[Cleanup] Limpando recursos...");
        dokanInstance?.Dispose();
        database.Dispose();
        encryptor?.Dispose();
        await discordClient.DisposeAsync();

        Console.WriteLine("[Exit] DiscordFS encerrado.");
    }
}
