## **NowPlaying API 通讯详情**

### **1. 连接测试（HTTP）**

属性

值

**协议**

HTTP

**地址**

`localhost`

**端口**

`9863`

**API 路径**

`/api/query`

**完整 URL**

`http://localhost:9863/api/query`

**请求方法**

GET

**发送内容**

无请求体（空 GET 请求）

**期望响应**

HTTP 状态码 200（表示服务可用）

**超时时间**

800ms

### **2. 实时数据通讯（WebSocket）**

属性

值

**协议**

WebSocket

**地址**

`localhost`

**端口**

`9863`

**API 路径**

`/api/ws/lyric`

**完整 URL**

`ws://localhost:9863/api/ws/lyric`

**发送内容**

无（WebSocket 客户端连接后被动接收消息）

### **3. WebSocket 接收的消息类型**

客户端连接后会收到 JSON 格式的消息，结构为 `{ "event": "事件名", "data": {...} }`：

#### **Track 事件（歌曲信息）**

```
{
  "event": "Track",
  "data": {
    "title": "歌曲标题",
    "author": "艺术家",
    "cover": "封面图片URL",
    "duration": 240000,
    "album": "专辑名"
  }
}

```

#### **Lyric 事件（歌词数据）**

```
{
  "event": "Lyric",
  "data": {
    "hasLyric": true,
    "lrc": "[00:00.00]歌词内容\n[00:05.00]第二行歌词"
  }
}

```

#### **PlayerPauseState 事件（播放状态）**

```
{
  "event": "PlayerPauseState",
  "data": { "isPaused": false }
}

```

#### **PlayerProgress 事件（播放进度）**

```
{
  "event": "PlayerProgress",
  "data": { "progress": 123456 }
}

```

进度单位为毫秒。

#### **PlayerProgressReplay 事件（重播）**

```
{
  "event": "PlayerProgressReplay",
  "data": {}
}

```

表示歌曲从头开始播放。

### **4. 连接行为**

- **重连延迟**: 2秒（连接断开后自动重连）
- **测试超时**: 800ms（检测服务是否可用）

***

**总结**: NowPlaying 服务监听在本地 `9863` 端口，提供 HTTP 测试接口和 WebSocket 实时数据推送。客户端无需主动发送数据，只需建立 WebSocket 连接即可持续接收歌曲信息、歌词、播放状态等更新。
