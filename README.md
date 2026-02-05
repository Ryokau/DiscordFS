# DiscordFS 📁☁️

A virtual file system that uses Discord as a storage backend. Drag files to a Windows Explorer drive and they're automatically chunked, uploaded, and managed on Discord.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/License-MIT-green)

## ✨ Features

- **Virtual Drive**: Mounts a drive (e.g., Z:) visible in Windows Explorer
- **Automatic Upload**: Files are split into 9MB chunks and uploaded to Discord
- **On-Demand Download**: When opening a file, chunks are downloaded and reassembled automatically
- **Integrity Check**: CRC32 verification ensures data isn't corrupted
- **LRU Cache**: In-memory cache speeds up repeated reads
- **Persistence**: Metadata saved in local SQLite database

## 📋 Prerequisites

1. **Windows 10/11** (x64)
2. **[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**
3. **[Dokan Library 2.x](https://github.com/dokan-dev/dokany/releases)** - Virtual filesystem driver
4. **Discord Bot** with permissions to send messages and attachments

## 🚀 Installation

### 1. Clone the repository
```bash
git clone https://github.com/your-username/DiscordFS.git
cd DiscordFS
```

### 2. Set up the Discord Bot

1. Go to [Discord Developer Portal](https://discord.com/developers/applications)
2. Create a new application and add a Bot
3. Copy the **Bot Token**
4. Enable required intents (Message Content Intent)
5. Invite the bot to your server with `Send Messages` and `Attach Files` permissions

### 3. Configure the project

Copy the example file and edit with your credentials:
```bash
cp appsettings.example.json appsettings.json
```

Edit `appsettings.json`:
```json
{
  "Discord": {
    "BotToken": "YOUR_BOT_TOKEN_HERE",
    "ChannelId": 123456789012345678
  },
  "FileSystem": {
    "DriveLetter": "Z",
    "CacheSizeMB": 256
  }
}
```

> **Tip**: To get the Channel ID, enable Developer Mode in Discord (Settings > Advanced), right-click the channel and select "Copy ID".

### 4. Run
```bash
dotnet run
```

## 📂 Project Structure

```
DiscordFS/
├── Discord/
│   └── DiscordStorageClient.cs    # Discord upload/download client
├── FileSystem/
│   └── DiscordFileSystem.cs       # DokanNet driver implementation
├── Models/
│   └── FileEntry.cs               # Data models
├── Storage/
│   ├── ChunkCache.cs              # In-memory LRU cache
│   ├── ChunkManager.cs            # Chunking and reassembly
│   └── MetadataDatabase.cs        # SQLite persistence
├── Program.cs                     # Entry point
├── appsettings.json               # Config (not versioned)
└── appsettings.example.json       # Config template
```

## ⚠️ Limitations

| Limitation | Description |
|------------|-------------|
| **Chunk size** | 9MB (Discord limit is 10MB for bots) |
| **Rate Limit** | ~5 simultaneous uploads to avoid throttling |
| **Latency** | Upload/download depends on Discord CDN connection |
| **Large files** | Works, but can be slow for files >100MB |

## 🔧 How It Works

```
┌─────────────────┐     ┌──────────────┐     ┌─────────────┐
│ Windows Explorer│────▶│  DokanNet    │────▶│ DiscordFS   │
│   (Drive Z:)    │     │   Driver     │     │  FileSystem │
└─────────────────┘     └──────────────┘     └──────┬──────┘
                                                    │
                    ┌───────────────────────────────┼───────────────────────────────┐
                    │                               │                               │
                    ▼                               ▼                               ▼
            ┌──────────────┐               ┌──────────────┐               ┌──────────────┐
            │ ChunkManager │               │   SQLite     │               │   Discord    │
            │ (Fragment)   │               │  (Metadata)  │               │   (Storage)  │
            └──────────────┘               └──────────────┘               └──────────────┘
```

## 📝 License

MIT License - see [LICENSE](LICENSE) for details.

## 🤝 Contributing

Pull requests are welcome! For major changes, please open an issue first to discuss.

---
*Made with ☕ and an obsession for free storage*
