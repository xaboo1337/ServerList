<div align="center">

# ⚡ ServerList

**High-Performance CS2 Player Connection Announcer with GeoIP for SwiftlyS2**

[![SwiftlyS2](https://img.shields.io/badge/SwiftlyS2-Compatible-blue.svg)](https://swiftlys2.net)
[![Release](https://img.shields.io/github/v/release/xaboo1337/CS2-ConnectAnnouncer?color=green)](https://github.com/xaboo1337/CS2-ConnectAnnouncer/releases)
[![License](https://img.shields.io/badge/license-MIT-orange.svg)]()

</div>

A lightweight CS2 plugin for **SwiftlyS2** that queries server statuses in real time using Steam A2S (`A2S_INFO`) and displays live player counts, current maps, and IP addresses via chat command.

**Author:** `- xaboo.`

## Features
- Real-time A2S server querying (`players / maxplayers`, current map, online/offline status)
- Excludes bots from active player counts
- Highlights the server the player is currently connected to
- Configurable background refresh interval and per-player command cooldown
- Multi-language support via JSONC translation files

## Commands
- `!serv` / `/serv` — Displays the configured server list in chat.

## Installation
1. Download the latest release archive and extract it into `addons/swiftlys2/plugins/`.
2. Edit `addons/swiftlys2/configs/plugins/ServerList/config.json` with your server IPs and ports.
3. Restart the server or reload the `ServerList` plugin.

## Configuration (`config.json`)
```json
{
  "ServerList": {
    "CurrentServerAddress": "185.143.177.14:5555",
    "RefreshIntervalSeconds": 15,
    "CommandCooldownSeconds": 3,
    "Servers": [
      {
        "Name": "AutoMix #1",
        "Address": "185.143.177.14:1121"
      },
      {
        "Name": "Public",
        "Address": "185.143.177.14:5555"
      }
    ]
  }
}
