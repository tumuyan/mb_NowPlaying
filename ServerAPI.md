# MusicBee NowPlaying Server API 文档

## 概述

MusicBee NowPlaying 插件提供了一个本地服务器，用于向 PV Tool 客户端实时推送音乐播放信息。服务器监听在 `localhost:9863` 端口，提供 HTTP 和 WebSocket 两种接口。

---

## 1. 服务器端点

### 1.1 HTTP 端点

**端点**: `http://localhost:9863/api/query`

**方法**: GET

**功能**: 获取当前播放器的完整状态信息（实际上 PV tool 只用于测试服务端是否存在，没有使用其中的数据）

**请求示例**:
```http
GET /api/query HTTP/1.1
Host: localhost:9863
```

**响应示例**:
```json
{
  "player": {
    "hasSong": true,
    "isPaused": false,
    "volumePercent": 50,
    "seekbarCurrentPosition": 37,
    "seekbarCurrentPositionHuman": "0:37",
    "statePercent": 0.1989247311827957,
    "likeStatus": "INDIFFERENT",
    "repeatType": "NONE"
  },
  "track": {
    "author": "HOYO-MiX / 张韶涵",
    "title": "Ripples of Past Reverie (Chinese Ver.)",
    "album": "崩坏星穹铁道 - 昔涟 Ripples of Past Reverie",
    "cover": "",
    "duration": 186,
    "durationHuman": "3:06",
    "url": "",
    "id": "",
    "isVideo": false,
    "isAdvertisement": false,
    "inLibrary": true
  }
}
```

**响应字段说明**:

| 字段 | 类型 | 说明 |
|------|------|------|
| **player** | Object | 播放器状态信息 |
| player.hasSong | Boolean | 当前是否有歌曲正在播放 |
| player.isPaused | Boolean | 是否处于暂停状态 |
| player.volumePercent | Integer | 音量百分比 (0-100) |
| player.seekbarCurrentPosition | Integer | 当前播放位置（秒） |
| player.seekbarCurrentPositionHuman | String | 人类可读的播放位置 (如 "0:37") |
| player.statePercent | Float | 播放进度百分比 (0.0-1.0) |
| player.likeStatus | String | 喜欢状态 ("INDIFFERENT", "LIKE", "DISLIKE") |
| player.repeatType | String | 循环类型 ("NONE", "ONE", "ALL") |
| **track** | Object | 歌曲信息 |
| track.author | String | 艺术家/作者 |
| track.title | String | 歌曲标题 |
| track.album | String | 专辑名称 |
| track.cover | String | 封面图片 URL（待实现） |
| track.duration | Integer | 歌曲时长（秒） |
| track.durationHuman | String | 人类可读的时长 (如 "3:06") |
| track.url | String | 歌曲链接（待实现） |
| track.id | String | 歌曲 ID（待实现） |
| track.isVideo | Boolean | 是否为视频 |
| track.isAdvertisement | Boolean | 是否为广告 |
| track.inLibrary | Boolean | 是否在音乐库中 |

---

### 1.2 WebSocket 端点

**端点**: `ws://localhost:9863/api/ws/lyric`

**功能**: 建立持久连接，实时接收播放状态更新

**连接行为**:
- 客户端连接成功后，服务器立即发送 4 条初始消息
- 后续播放状态变化时，服务器主动推送更新
- 连接断开后，客户端应实现自动重连机制（建议重连延迟：2 秒）

---

## 2. WebSocket 消息格式

所有消息均为 JSON 格式，结构为：
```json
{
  "event": "事件名称",
  "data": {
    // 事件数据
  }
}
```

### 2.1 Track 事件（歌曲信息）

**触发时机**:
- ✅ 客户端连接时（初始消息）
- ✅ 插件启动时
- ✅ 歌曲切换时（NotificationType.TrackChanged）
- ✅ 用户点击上一曲按钮时
- ✅ 用户点击下一曲按钮时

**消息格式**:
```json
{
  "event": "Track",
  "data": {
    "title": "歌曲标题",
    "author": "艺术家",
    "cover": "",
    "duration": 186000,
    "album": "专辑名"
  }
}
```

**字段说明**:
| 字段 | 类型 | 说明 |
|------|------|------|
| title | String | 歌曲标题 |
| author | String | 艺术家/作者 |
| cover | String | 封面图片 URL（待实现） |
| duration | Integer | 歌曲时长（毫秒） |
| album | String | 专辑名称 |

---

### 2.2 Lyric 事件（歌词数据）

**触发时机**:
- ✅ 客户端连接时（初始消息）
- ✅ 插件启动时
- ✅ 歌曲切换时（NotificationType.TrackChanged）
- ✅ 用户点击上一曲按钮时
- ✅ 用户点击下一曲按钮时
- ✅ **歌词下载/更新完成时**（NotificationType.NowPlayingLyricsReady）

**消息格式**:
```json
{
  "event": "Lyric",
  "data": {
    "hasLyric": true,
    "lrc": "[00:00.00] 歌曲标题\n[00:05.00] 第一句歌词\n[00:10.00] 第二句歌词"
  }
}
```

**字段说明**:
| 字段 | 类型 | 说明 |
|------|------|------|
| hasLyric | Boolean | 是否有歌词 |
| lrc | String | LRC 格式的歌词内容 |

**LRC 格式示例**:
```
[00:00.00] 歌曲标题 - 歌手
[00:15.50] 第一句歌词
[00:20.80] 第二句歌词
[00:26.10] 第三句歌词
```

**歌词获取逻辑**:
1. 首先尝试使用 `NowPlaying_GetLyrics()` 获取当前歌词
2. 如果未找到，尝试使用 `NowPlaying_GetDownloadedLyrics()` 获取已下载的歌词
3. 如果都没有，返回 `hasLyric: false, lrc: null`

---

### 2.3 PlayerPauseState 事件（播放状态）

**触发时机**:
- ✅ 客户端连接时（初始消息）
- ✅ 插件启动时
- ✅ 播放状态变化时（NotificationType.PlayStateChanged）
- ✅ 用户点击播放/暂停按钮时
- ✅ 用户点击停止按钮时

**消息格式**:
```json
{
  "event": "PlayerPauseState",
  "data": {
    "isPaused": false
  }
}
```

**字段说明**:
| 字段 | 类型 | 说明 |
|------|------|------|
| isPaused | Boolean | 是否暂停（true=暂停，false=播放） |

**状态说明**:
- `isPaused: false` - 正在播放
- `isPaused: true` - 已暂停
- 停止状态也会触发此事件

---

### 2.4 PlayerProgress 事件（播放进度）

**触发时机**:
- ✅ 客户端连接时（初始消息）
- ✅ 播放进度更新时（每秒自动更新）
- ✅ 用户拖动进度条时

**消息格式**:
```json
{
  "event": "PlayerProgress",
  "data": {
    "progress": 37000
  }
}
```

**字段说明**:
| 字段 | 类型 | 说明 |
|------|------|------|
| progress | Integer | 当前播放位置（毫秒） |

**更新频率**:
- 播放状态下：每秒更新一次（通过定时器）
- 用户拖动进度条：立即更新
- 暂停/停止状态：不更新

---

### 2.5 PlayerProgressReplay 事件（重播）

**触发时机**:
- ❌ 尚未实现

**计划触发时机**:
- 当播放进度从接近结束突然变为 0 时
- 当用户手动重播歌曲时

**消息格式**:
```json
{
  "event": "PlayerProgressReplay",
  "data": {}
}
```

**字段说明**:
- 空对象，表示歌曲从头开始播放

---

## 3. 事件触发时机总览

| 事件类型 | 客户端连接 | 插件启动 | 歌曲切换 | 播放/暂停 | 进度更新 | 歌词就绪 | 重播 |
|---------|:---------:|:-------:|:-------:|:--------:|:-------:|:-------:|:---:|
| Track | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Lyric | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ |
| PlayerPauseState | ✅ | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ |
| PlayerProgress | ✅ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| PlayerProgressReplay | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## 4. MusicBee 通知类型

插件监听以下 MusicBee 通知类型：

| 通知类型 | 编号 | 描述 | 触发动作 |
|---------|------|------|---------|
| PluginStartup | 0 | 插件启动 | 发送 Track、Lyric、PlayerPauseState |
| TrackChanged | 1 | 歌曲已切换 | 发送 Track、Lyric |
| PlayStateChanged | 2 | 播放状态变化 | 发送 PlayerPauseState |
| NowPlayingLyricsReady | 9 | 歌词已就绪 | 发送 Lyric |
| PlayerShuffleChanged | 21 | 随机播放状态变化 | 更新内部状态 |
| PlayerRepeatChanged | 20 | 循环模式变化 | 更新内部状态 |

---

## 5. 客户端实现建议

### 5.1 连接流程

```
1. 检查服务可用性
   ├─ 发送 HTTP GET 到 /api/query
   ├─ 超时时间：800ms
   └─ 成功：继续，失败：提示服务未运行

2. 建立 WebSocket 连接
   ├─ 连接到 ws://localhost:9863/api/ws/lyric
   ├─ 等待握手完成
   └─ 接收 4 条初始消息

3. 进入监听状态
   ├─ 持续接收事件
   ├─ 根据事件类型更新 UI
   └─ 连接断开时自动重连（延迟 2 秒）
```

### 5.2 错误处理

- **连接失败**: 提示用户"Now Playing 服务未运行"
- **连接断开**: 自动重连，最多重试 3 次
- **消息解析错误**: 忽略该消息，继续监听
- **超时处理**: HTTP 请求超时 800ms，WebSocket 重连超时 5 秒

### 5.3 状态管理

建议客户端维护以下状态：
- 当前歌曲信息（来自 Track 事件）
- 当前歌词（来自 Lyric 事件）
- 播放状态（来自 PlayerPauseState 事件）
- 播放进度（来自 PlayerProgress 事件）

---

## 6. 技术实现细节

### 6.1 WebSocket 帧格式

服务器使用简单的 WebSocket 帧格式：
```
Byte 0: 0x81 (文本帧)
Byte 1: 数据长度
Byte 2-N: UTF-8 编码的 JSON 数据
```

### 6.2 端口占用检测

- 插件启动时检测端口 9863 是否被占用
- 如果端口被占用，弹出警告窗口
- 用户需要关闭占用端口的程序并重启 MusicBee

### 6.3 日志输出

服务器会在控制台输出以下日志：
- `Now Playing Server started on port 9863` - 服务器启动成功
- `Received request: GET /api/ws/lyric HTTP/1.1` - 收到 WebSocket 请求
- `Path: /api/ws/lyric, IsWebSocket: True` - 请求路径和类型
- `Sec-WebSocket-Key: xxxxx` - WebSocket 密钥
- `Sec-WebSocket-Accept: xxxxx` - WebSocket 接受密钥
- `WebSocket handshake response sent` - 握手响应已发送
- `WebSocket connection established` - 连接已建立
- `Lyrics ready - broadcasting to clients` - 歌词已就绪并广播
- `WebSocket connection closed` - 连接已关闭

---

## 7. 待实现功能

以下功能已规划但尚未实现：

| 功能 | 描述 | 优先级 |
|------|------|--------|
| 封面图片 URL | 提供专辑封面的网络访问地址 | 中 |
| 歌曲 URL | 提供歌曲的网络访问地址 | 低 |
| 歌曲 ID | 提供歌曲的唯一标识符 | 低 |
| PlayerProgressReplay | 重播事件检测 | 低 |
| 歌词同步高亮 | 根据进度高亮歌词 | 高 |

---

