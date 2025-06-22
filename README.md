# OwnDownloaderBot

A simple open Telegram bot for downloading media from TikTok, Instagram, and YouTube.

## Features

- Supports videos & posts downloading from TikTok
- Instagram Reels and posts support
- Download YouTube videos, shorts and community posts
- Modular architecture with Docker

## Getting Started

### Prerequisites
- Docker and Docker Compose
- Telegram API token

> **Note:** For the bot to read links in a group chat without admin privileges, you need to use the `/setprivacy` setting to 'Disable' in BotFather.

### Sample Configuration

Create a docker-compose.yml file with the following content:

```yaml
version: '3.8'
services:
  tgbot:
    image: hehehol33/owndownloaderbot:tgbot
    environment:
      - TELEGRAM_BOT_TOKEN=your_token # API Token
    # - PORT=your_port # If you want to use port other than 8098 
    ports:
      - "8098:8098"  # or (your_port:8098) if you use custom one
    networks:
      - owndownloader

  tiktok-linker:
    image: hehehol33/owndownloaderbot:tiktok-linker
    environment:
    # - PORT=9120  # custom port variable
    # - LOG_LEVEL=DEBUG # logging verbose option (DEBUG/INFO/WARN/ERROR/NONE) 
    depends_on:
      - tgbot
    networks:
      - owndownloader

  instagram-linker: 
    image: hehehol33/owndownloaderbot:instagram-linker
    environment:
    #  - IG_USERNAME=${IG_USERNAME} # If you need to use credentials instead of anonymous account 
    #  - IG_PASSWORD=${IG_PASSWORD} 
    # - PORT=9120  # custom port variable
    # - LOG_LEVEL=DEBUG  # logging verbose option (DEBUG/INFO/WARN/ERROR/NONE) 
    depends_on:
      - tgbot
    networks:
      - owndownloader

  youtube-linker:  
    image: hehehol33/owndownloaderbot:youtube-linker
    environment:
    # - PORT=9120 # custom port set
    # - LOG_LEVEL=DEBUG # logging verbose option (DEBUG/INFO/WARN/ERROR/NONE) 
    depends_on:
      - tgbot
    networks:
      - owndownloader

networks:
  owndownloader:
    driver: bridge
```

## Configuration Options

> **Note:** It's recommended to use docker secrets for sensitive data, like your credentials and API keys.

| Variable | Description | Required |
|----------|-------------|----------|
| `TELEGRAM_BOT_TOKEN=your_apikey` / `TELEGRAM_BOT_TOKEN=${TELEGRAM_BOT_TOKEN}` | Your Telegram bot token | Yes |
| `PORT=your_port` | Sets services to listen to your set port (default is 8098). **Important**: Change the "8098:8098" parameter for tgbot container to your desired port like "your_port:8098" | No |
| `LOG_LEVEL=level` | Sets logging level (default is INFO). Options: DEBUG/INFO/WARN/ERROR/NONE | No |
| `IG_USERNAME=username` / `IG_USERNAME=${IG_USERNAME}` | Instagram username for IGlinker, if you want to download stories or access private accounts | No |
| `IG_PASSWORD=password` / `IG_PASSWORD=${IG_PASSWORD}` | Password for IG account that you set in IG_USERNAME | No |

If you don't need all modules (for example, if you won't be downloading any YouTube content), you can remove that container from the stack.

## Deployment

After you've prepared the compose file, deploy the stack with:

```bash
docker compose up -d
```

After successful deployment, you can check tgbot logs to verify your configuration and the linker-containers connected:

```
OwnDownloader tgbot v. A5
2025-06-21 23:22:06 - Bot started!
2025-06-21 23:22:09 - WebSocket server active on port 9120
2025-06-21 23:22:27 - Received registration: platform:tiktok
2025-06-21 23:22:27 - Client registered for platform: tiktok
2025-06-21 23:22:29 - Received registration: platform:instagram
2025-06-21 23:22:29 - Client registered for platform: instagram
2025-06-21 23:22:30 - Received registration: platform:youtube
2025-06-21 23:22:30 - Client registered for platform: youtube
```

## Bot Commands

- `/changeview` - Toggle signature status (adding username of the person who sent the media link)
