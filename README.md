# MusicBee NowPlaying Plugin

A MusicBee plugin that provides SMTC (System Media Transport Control) support for Windows 10/11 and offers a Now Playing API server for external applications to retrieve playback information in real-time.

## Features

### System Media Control (SMTC)
- Media metadata display, including song title, artist, album, and more
- Windows media control support
  - Play/Pause
  - Previous/Next Track
  - Stop
- [ModernFlyout](https://github.com/ModernFlyouts-Community/ModernFlyouts) media control support
  - Shuffle
  - Repeat Mode
  - Timeline information and controls
- Global media key support

### Now Playing API Server
- Listening on `localhost:9863` port
- HTTP endpoint: `GET /api/query` - Get complete player status
- WebSocket endpoint: `ws://localhost:9863/api/ws/lyric` - Receive real-time playback status updates
- Supported event types:
  - `Track` - Song information (title, artist, album, etc.)
  - `Lyric` - LRC format lyrics data
  - `PlayerPauseState` - Play/Pause status
  - `PlayerProgress` - Playback progress (updates every second)

## Installation

Place `mb_NowPlaying.dll` into the `MusicBee/Plugins` folder

## System Requirements

- Windows 10/11
- MusicBee player
- .NET Framework 4.8

## Acknowledgments
* [Windows 10 Media Control Overlay](https://www.getmusicbee.com/addons/plugins/98/windows-10-media-control-overlay/)
* [CharlieJiang version](https://getmusicbee.com/forum/index.php?topic=21240.45)
