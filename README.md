# DiscordFS 📁☁️🔒

A secure virtual file system that uses Discord as an encrypted storage backend. Files are encrypted with AES-256-GCM, chunked, and uploaded with evasion techniques.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/License-MIT-green)
![Encryption](https://img.shields.io/badge/Encryption-AES--256--GCM-red)

## ✨ Features

### Core
- **Virtual Drive**: Mounts a drive (e.g., Z:) visible in Windows Explorer
- **Automatic Upload**: Files are chunked and uploaded to Discord
- **On-Demand Download**: Chunks are downloaded and reassembled when needed

### Security
- **🔐 AES-256-GCM Encryption**: Files are encrypted before chunking with unique IV per file
- **🎭 Name Obfuscation**: Chunk names look like innocent cache files (`img_cache_7721.jpg`)
- **⏱️ Smart Throttling**: Random delays (1.5-4.2s) between uploads with jitter
- **🔄 Rate Limit Handling**: Auto-pause on 429 errors with exponential backoff
- **🕵️ User-Agent Rotation**: Mimics common browsers to avoid detection

## 📋 Prerequisites

1. **Windows 10/11** (x64)
2. **[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**
3. **[Dokan Library 2.x](https://github.com/dokan-dev/dokany/releases)**
4. **Discord Bot** with `Send Messages` and `Attach Files` permissions

## 🚀 Quick Start

```bash
# Clone
git clone https://github.com/Ryokau/DiscordFS.git
cd DiscordFS

# Configure
cp appsettings.example.json appsettings.json
# Edit appsettings.json with your bot token and channel ID

# Run
dotnet run
```

## ⚙️ Configuration

```json
{
  "Discord": {
    "BotToken": "YOUR_BOT_TOKEN",
    "ChannelId": 123456789
  },
  "FileSystem": {
    "DriveLetter": "Z",
    "CacheSizeMB": 256
  },
  "Security": {
    "EnableEncryption": true,
    "MasterKey": ""
  }
}
```

> **Note**: If `MasterKey` is empty, a new key is auto-generated and saved to `.masterkey`. **Back this file up!**

## 🔒 Security Architecture

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Your File   │────▶│ AES-256-GCM  │────▶│   Chunking   │────▶│   Discord    │
│  (plaintext) │     │  Encryption  │     │   (9MB max)  │     │   Storage    │
└──────────────┘     └──────────────┘     └──────────────┘     └──────────────┘
                            │                    │
                            ▼                    ▼
                     Unique IV/Key        Obfuscated Names
                     per file             (img_cache_xxx.jpg)
```

### What Discord Sees
- Random file names like `cache_a1b2c3d4.jpg`
- Encrypted binary blobs (no metadata)
- Minimal messages with just 📎 emoji

### What You Control
- Master key stored locally
- Metadata in local SQLite
- Full file names and structure

## 📂 Project Structure

```
DiscordFS/
├── Discord/
│   └── DiscordStorageClient.cs   # Upload with throttling & retry
├── FileSystem/
│   └── DiscordFileSystem.cs      # Dokan driver
├── Security/
│   ├── FileEncryptor.cs          # AES-256-GCM
│   ├── NameObfuscator.cs         # Random names
│   └── SmartThrottler.cs         # Jitter & backoff
├── Storage/
│   ├── ChunkCache.cs             # LRU cache
│   ├── ChunkManager.cs           # Encrypt + chunk
│   └── MetadataDatabase.cs       # SQLite
└── Program.cs
```

## ⚠️ Limitations

| Item | Limit |
|------|-------|
| Chunk size | 9MB |
| Concurrent uploads | 3 |
| Min delay between uploads | ~1.5s |
| Max file size | Unlimited (theoretically) |

## 📝 License

MIT License - see [LICENSE](LICENSE)

---
*Secure cloud storage, hidden in plain sight* 🔐
