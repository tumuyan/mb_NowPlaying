using Gma.System.MouseKeyHook;
﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using static System.IO.WindowsRuntimeStreamExtensions;
using System.Drawing;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private readonly PluginInfo about = new PluginInfo();
        private SystemMediaTransportControls systemMediaControls;
        private SystemMediaTransportControlsDisplayUpdater displayUpdater;
        private MusicDisplayProperties musicProperties;
        private InMemoryRandomAccessStream artworkStream;

        private IKeyboardMouseEvents globalHook;
        // Disable this...
        private int mediaKeysInvalidateBeforeMs = 0; // media control buttons won't trigger for 2000 ms after media button is pressed
        private DateTime lastPlayPauseKeyPress;
        private DateTime lastStopKeyPress;
        private DateTime lastPreviousTrackKeyPress;
        private DateTime lastNextTrackKeyPress;

        private bool trackChangeListenerDisabled = false;
        private System.Threading.Timer timer;

        // Now Playing Server
        private TcpListener listener;
        private CancellationTokenSource cts;
        private ConcurrentBag<WebSocketClient> webSocketClients;
        private JavaScriptSerializer jsonSerializer;

        private string dataPath;
        public bool isSplitTranslation = false;
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            SubscribeGlobalHooks();
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "Media Control with Now Playing";
            about.Description = "Enables MusicBee to interact with the Windows 10/11 Media Control overlay and provides Now Playing API for PV Tool.";
            about.Author = "tumuyan";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 5;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents);
            
            // Initialize Now Playing Server
            InitializeNowPlayingServer();

            about.ConfigurationPanelHeight = 40;   // height in pixels that musicbee should reserve in a panel for config settings. When set, a handle to an empty panel will be passed to the Configure function

            dataPath = mbApiInterface.Setting_GetPersistentStoragePath()+ "NowPlaying_Config.conf";
            if (File.Exists(dataPath))
            {
                var str = File.ReadAllText(dataPath);
                var conf = str.Split('\n');
                foreach(var line in conf)
                {
                    var kv = line.Split(new char[] { ':' }, 2);
                    if (kv.Length == 2)
                    {
                        var key = kv[0].Trim();
                        var value = kv[1].Trim();
                        if (key == "SplitTranslation" && bool.TryParse(value, out var result))
                        {
                            isSplitTranslation = result;
                        }
                    }
                }
            } 
                

            return about;
        }

        private CheckBox cbSplitTranslation;

        public bool Configure(IntPtr panelHandle)
        {
            if (panelHandle != IntPtr.Zero)
            {
                Panel configPanel = (Panel)Panel.FromHandle(panelHandle);
                configPanel.Controls.Clear();
                cbSplitTranslation = new CheckBox
                {
                    Text = "Split lyric translation by '/'",
                    Location = new Point(0, 0),
                    Checked = isSplitTranslation,
                    AutoSize = true
                };
                //cbSplitTranslation.Text = "Split lyric translation by '/'";
                //cbSplitTranslation.Checked = isSplitTranslation;
                //cbSplitTranslation.Location = new System.Drawing.Point(0, 0);
                //cbSplitTranslation.CheckedChanged += new EventHandler(cbSplitTranslation_CheckedChanged);  
                configPanel.Controls.AddRange(new Control[] { cbSplitTranslation });
            }
            return false;
        }



        private void cbSplitTranslation_CheckedChanged(object sender, EventArgs e)
        {
            CheckBox cb = (CheckBox)sender;
            isSplitTranslation = cb.Checked;
            Console.WriteLine($"isSplitTranslation changed -> {isSplitTranslation}");
        }


        // called by MusicBee when the user clicks Apply or Save in the MusicBee Preferences screen.
        // its up to you to figure out whether anything has changed and needs updating
        public void SaveSettings()
        {
            isSplitTranslation = cbSplitTranslation.Checked;

            File.WriteAllText(dataPath, "SplitTranslation:" + isSplitTranslation.ToString());
        }

        // MusicBee is closing the plugin (plugin is being disabled by user or MusicBee is shutting down)
        public void Close(PluginCloseReason reason)
        {
            UnsubscribeGlobalHooks();
            SetArtworkThumbnail(null);
            timer.Dispose();
            
            // Stop Now Playing Server
            cts?.Cancel();
            listener?.Stop();
        }

        // uninstall this plugin - clean up any persisted files
        public void Uninstall()
        {
        }

        private void MediaControl_PlayPauseButtonPress(bool pause)
        {
            trackChangeListenerDisabled = true;
            try
            {
                if (DateTime.Now.Subtract(lastPlayPauseKeyPress).TotalMilliseconds > mediaKeysInvalidateBeforeMs)
                {
                    var state = mbApiInterface.Player_GetPlayState();
                    switch (state)
                    {
                        case PlayState.Playing:
                            if (pause)
                                mbApiInterface.Player_PlayPause();
                            break;
                        case PlayState.Paused:
                            if (!pause)
                                mbApiInterface.Player_PlayPause();
                            break;
                        case PlayState.Stopped:
                        case PlayState.Loading:
                        case PlayState.Undefined:
                        default:
                            break; // Ignored
                    }
                }
                SetPlayerState();
                BroadcastPlayerState();
            }
            finally
            {
                trackChangeListenerDisabled = false; // atomic!
            }
        }

        private void MediaControl_StopButtonPress()
        {
            trackChangeListenerDisabled = true;
            try
            {
                if (DateTime.Now.Subtract(lastStopKeyPress).TotalMilliseconds > mediaKeysInvalidateBeforeMs)
                    mbApiInterface.Player_Stop();
                SetPlayerState();
                BroadcastPlayerState();
            }
            finally
            {
                trackChangeListenerDisabled = false; // atomic!
            }
        }

        private void MediaControl_PreviousTrackButtonPress()
        {
            trackChangeListenerDisabled = true;
            try
            {
                if (DateTime.Now.Subtract(lastPreviousTrackKeyPress).TotalMilliseconds > mediaKeysInvalidateBeforeMs)
                    mbApiInterface.Player_PlayPreviousTrack();
                SetDisplayValues();
                BroadcastTrackInfo();
                BroadcastLyricInfo();
            }
            finally
            {
                trackChangeListenerDisabled = false; // atomic!
            }
        }

        private void MediaControl_NextTrackButtonPress()
        {
            trackChangeListenerDisabled = true;
            try
            {
                if (DateTime.Now.Subtract(lastNextTrackKeyPress).TotalMilliseconds > mediaKeysInvalidateBeforeMs)
                    mbApiInterface.Player_PlayNextTrack();
                SetDisplayValues();
                BroadcastTrackInfo();
                BroadcastLyricInfo();
            }
            finally
            {
                trackChangeListenerDisabled = false; // atomic!
            }
        }

        // receive event notifications from MusicBee
        // you need to set about.ReceiveNotificationFlags = PlayerEvents to receive all notifications, and not just the startup event
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    systemMediaControls = BackgroundMediaPlayer.Current.SystemMediaTransportControls;
                    systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Closed;
                    systemMediaControls.IsEnabled = true;
                    systemMediaControls.IsPlayEnabled = true;
                    systemMediaControls.IsPauseEnabled = true;
                    systemMediaControls.IsStopEnabled = true;
                    systemMediaControls.IsPreviousEnabled = true;
                    systemMediaControls.IsNextEnabled = true;
                    systemMediaControls.IsRewindEnabled = false;
                    systemMediaControls.IsFastForwardEnabled = false;
                    systemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;
                    systemMediaControls.PlaybackPositionChangeRequested += SystemMediaControls_PlaybackPositionChangeRequested;
                    systemMediaControls.PlaybackRateChangeRequested += SystemMediaControls_PlaybackRateChangeRequested;
                    systemMediaControls.ShuffleEnabledChangeRequested += SystemMediaControls_ShuffleEnabledChangeRequested;
                    systemMediaControls.AutoRepeatModeChangeRequested += SystemMediaControls_AutoRepeatModeChangeRequested;
                    displayUpdater = systemMediaControls.DisplayUpdater;
                    displayUpdater.Type = MediaPlaybackType.Music;
                    musicProperties = displayUpdater.MusicProperties;
                    SetDisplayValues();
                    SetShuffleState();
                    SetRepeatState();
                    BroadcastTrackInfo();
                    BroadcastLyricInfo();
                    BroadcastPlayerState();
                    break;
                case NotificationType.PlayStateChanged:
                    if (!trackChangeListenerDisabled)
                    {
                        SetPlayerState();
                        BroadcastPlayerState();
                    }
                    break;
                case NotificationType.TrackChanged:
                    if (!trackChangeListenerDisabled)
                    {
                        SetDisplayValues();
                        BroadcastTrackInfo();
                        BroadcastLyricInfo();
                    }
                    break;
                case NotificationType.NowPlayingLyricsReady:
                    // Lyrics have been downloaded or updated - broadcast to all clients
                    Debug.WriteLine("Lyrics ready - broadcasting to clients");
                    BroadcastLyricInfo();
                    break;
                case NotificationType.PlayerShuffleChanged:
                    if (!trackChangeListenerDisabled)
                        SetShuffleState();
                    break;
                case NotificationType.PlayerRepeatChanged:
                    if (!trackChangeListenerDisabled)
                        SetRepeatState();
                    break;
            }
        }

        private void SystemMediaControls_ButtonPressed(SystemMediaTransportControls smtc, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Stop:
                    MediaControl_StopButtonPress();
                    break;
                case SystemMediaTransportControlsButton.Play:
                    MediaControl_PlayPauseButtonPress(false);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    MediaControl_PlayPauseButtonPress(true);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    MediaControl_NextTrackButtonPress();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    MediaControl_PreviousTrackButtonPress();
                    break;
                case SystemMediaTransportControlsButton.Rewind:
                    break;
                case SystemMediaTransportControlsButton.FastForward:
                    break;
                case SystemMediaTransportControlsButton.ChannelUp:
                    mbApiInterface.Player_SetVolume(mbApiInterface.Player_GetVolume() + 0.05F);
                    break;
                case SystemMediaTransportControlsButton.ChannelDown:
                    mbApiInterface.Player_SetVolume(mbApiInterface.Player_GetVolume() - 0.05F);
                    break;
            }
        }

        private void SystemMediaControls_PlaybackPositionChangeRequested(SystemMediaTransportControls smtc, PlaybackPositionChangeRequestedEventArgs args)
        {
            mbApiInterface.Player_SetPosition((int)args.RequestedPlaybackPosition.TotalMilliseconds);
            BroadcastProgress();
        }

        private void SystemMediaControls_PlaybackRateChangeRequested(SystemMediaTransportControls smtc, PlaybackRateChangeRequestedEventArgs args)
        {
        }

        private void SystemMediaControls_AutoRepeatModeChangeRequested(SystemMediaTransportControls smtc, AutoRepeatModeChangeRequestedEventArgs args)
        {
            switch (args.RequestedAutoRepeatMode)
            {
                case MediaPlaybackAutoRepeatMode.Track:
                    mbApiInterface.Player_SetRepeat(RepeatMode.One);
                    break;
                case MediaPlaybackAutoRepeatMode.List:
                    mbApiInterface.Player_SetRepeat(RepeatMode.All);
                    break;
                case MediaPlaybackAutoRepeatMode.None:
                    mbApiInterface.Player_SetRepeat(RepeatMode.None);
                    break;
            }
        }

        private void SystemMediaControls_ShuffleEnabledChangeRequested(SystemMediaTransportControls smtc, ShuffleEnabledChangeRequestedEventArgs args)
        {
            mbApiInterface.Player_SetShuffle(args.RequestedShuffleEnabled);
        }

        private void SetDisplayValues()
        {
            displayUpdater.ClearAll();
            displayUpdater.Type = MediaPlaybackType.Music;
            SetArtworkThumbnail(null);
            var url = mbApiInterface.NowPlaying_GetFileUrl();
            if (url != null)
            {
                musicProperties.AlbumArtist = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.AlbumArtist);
                musicProperties.AlbumTitle = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album);
                if (uint.TryParse(mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackCount), out var value))
                    musicProperties.AlbumTrackCount = value;
                musicProperties.Artist = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist);
                musicProperties.Title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
                if (string.IsNullOrEmpty(musicProperties.Title))
                    musicProperties.Title = url.Substring(url.LastIndexOfAny(new char[] { '/', '\\' }) + 1);
                if (uint.TryParse(mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackNo), out value))
                    musicProperties.TrackNumber = value;
                mbApiInterface.Library_GetArtworkEx(url, 0, true, out _, out _, out var imageData);
                SetArtworkThumbnail(imageData);
            }
            displayUpdater.Update();
        }

        private void SetPlayerState()
        {
            switch (mbApiInterface.Player_GetPlayState())
            {
                case PlayState.Playing:
                    systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    timer = new System.Threading.Timer(SetPositionState, null, 0, 1000);
                    break;
                case PlayState.Paused:
                    systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                    timer.Dispose();
                    break;
                case PlayState.Stopped:
                    systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    timer.Dispose();
                    break;
            }
        }

        private void SetShuffleState()
        {
            systemMediaControls.ShuffleEnabled = mbApiInterface.Player_GetShuffle();
        }

        private void SetRepeatState()
        {
            switch (mbApiInterface.Player_GetRepeat())
            {
                case RepeatMode.One:
                    systemMediaControls.AutoRepeatMode = MediaPlaybackAutoRepeatMode.Track;
                    break;
                case RepeatMode.All:
                    systemMediaControls.AutoRepeatMode = MediaPlaybackAutoRepeatMode.List;
                    break;
                case RepeatMode.None:
                    systemMediaControls.AutoRepeatMode = MediaPlaybackAutoRepeatMode.None;
                    break;
            }
        }

        private void SetPositionState(object state)
        {
            var timelineProperties = new SystemMediaTransportControlsTimelineProperties();

            timelineProperties.StartTime = TimeSpan.FromSeconds(0);
            timelineProperties.MinSeekTime = TimeSpan.FromSeconds(0);
            timelineProperties.Position = TimeSpan.FromMilliseconds(mbApiInterface.Player_GetPosition());
            timelineProperties.MaxSeekTime = TimeSpan.FromMilliseconds(mbApiInterface.NowPlaying_GetDuration());
            timelineProperties.EndTime = TimeSpan.FromMilliseconds(mbApiInterface.NowPlaying_GetDuration());

            systemMediaControls.UpdateTimelineProperties(timelineProperties);
            BroadcastProgress();
        }

        private async void SetArtworkThumbnail(byte[] data)
        {
            if (artworkStream != null)
                artworkStream.Dispose();
            if (data == null)
            {
                artworkStream = null;
                displayUpdater.Thumbnail = null;
            }
            else
            {
                new MemoryStream(data).AsInputStream();

                artworkStream = new InMemoryRandomAccessStream();
                await artworkStream.WriteAsync(data.AsBuffer());
                displayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(artworkStream);
            }
        }

        private void SubscribeGlobalHooks()
        {
            globalHook = Hook.GlobalEvents();
            globalHook.KeyPress += GlobalHook_KeyPress;
        }

        private void UnsubscribeGlobalHooks()
        {
            globalHook.KeyPress -= GlobalHook_KeyPress;
            globalHook.Dispose();
        }

        private void GlobalHook_KeyPress(object sender, KeyPressEventArgs e)
        {
            switch ((Keys)e.KeyChar)
            {
                case Keys.MediaPlayPause:
                    lastPlayPauseKeyPress = DateTime.Now;
                    break;
                case Keys.MediaStop:
                    lastStopKeyPress = DateTime.Now;
                    break;
                case Keys.MediaPreviousTrack:
                    lastPreviousTrackKeyPress = DateTime.Now;
                    break;
                case Keys.MediaNextTrack:
                    lastNextTrackKeyPress = DateTime.Now;
                    break;
                default:
                    break;
            }
        }

        // Now Playing Server Implementation
        private void InitializeNowPlayingServer()
        {
            cts = new CancellationTokenSource();
            webSocketClients = new ConcurrentBag<WebSocketClient>();
            jsonSerializer = new JavaScriptSerializer();

            // Check if port 9863 is already in use
            bool isPortInUse = false;
            try
            {
                var testListener = new TcpListener(IPAddress.Any, 9863);
                testListener.Start();
                testListener.Stop();
            }
            catch (SocketException)
            {
                isPortInUse = true;
            }

            if (isPortInUse)
            {
                MessageBox.Show(
                    "Port 9863 is already in use by another application.\n\n" +
                    "Please close any other applications using this port (such as another instance of MusicBee or PV Tool) and restart MusicBee.",
                    "Now Playing Server - Port Conflict",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Start server for both HTTP and WebSocket
            listener = new TcpListener(IPAddress.Any, 9863);
            listener.Start();
            Task.Run(() => ServerLoop(listener, cts.Token));

            Debug.WriteLine("Now Playing Server started on port 9863");
        }

        private async Task ServerLoop(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Debug.WriteLine("Waiting for client connection...");
                    var client = await listener.AcceptTcpClientAsync();
                    Debug.WriteLine($"Client accepted: {client.Client.RemoteEndPoint}");
                    Task.Run(() => HandleClient(client));
                }
                catch (ObjectDisposedException ex)
                {
                    Debug.WriteLine($"Listener disposed: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Debug.WriteLine($"Server error: {ex.GetType().Name} - {ex.Message}");
                    }
                }
            }
            Debug.WriteLine("Server loop stopped");
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                Debug.WriteLine($"New client connected from {client.Client.RemoteEndPoint}");

                // Read the request data with timeout
                client.ReceiveTimeout = 5000; // 5 second timeout
                var buffer = new byte[4096];
                int bytesRead = 0;
                NetworkStream stream = null;
                
                try
                {
                    stream = client.GetStream();
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException ex)
                {
                    Debug.WriteLine($"Error reading request: {ex.Message}");
                    client.Close();
                    return;
                }

                if (bytesRead == 0)
                {
                    Debug.WriteLine("Client sent 0 bytes, closing");
                    client.Close();
                    return;
                }

                var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Log the request for debugging
                var firstLine = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                Debug.WriteLine($"Received request: {firstLine}");

                // Parse request line to get path
                string path = "/";
                var lines = request.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length > 0)
                {
                    var requestLine = lines[0];
                    var parts = requestLine.Split(' ');
                    if (parts.Length >= 2)
                    {
                        path = parts[1];
                    }
                }

                // Check if it's a WebSocket upgrade request
                bool isWebSocket = request.IndexOf("Upgrade: websocket", StringComparison.OrdinalIgnoreCase) >= 0;

                Debug.WriteLine($"Path: {path}, IsWebSocket: {isWebSocket}");

                // Check if it's a WebSocket connection request for /api/ws/lyric
                if (isWebSocket && path == "/api/ws/lyric")
                {
                    // Handle WebSocket connection asynchronously
                    Debug.WriteLine($"WebSocket connection request for path: {path}");
                    // Don't dispose client here - WebSocketClient will handle it
                    var webSocketClient = new WebSocketClient(client, webSocketClients, mbApiInterface, jsonSerializer, this);
                    webSocketClients.Add(webSocketClient);
                    Task.Run(() => webSocketClient.Handle(request));
                }
                else if (path == "/api/query")
                {
                    // Handle HTTP request for /api/query
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        // Return complete player and track information
                        var response = GetPlayerInfoResponse();
                        writer.WriteLine("HTTP/1.1 200 OK");
                        writer.WriteLine("Content-Type: application/json");
                        writer.WriteLine("Content-Length: " + Encoding.UTF8.GetByteCount(response));
                        writer.WriteLine("Access-Control-Allow-Origin: *");
                        writer.WriteLine("Access-Control-Allow-Methods: GET, POST, OPTIONS");
                        writer.WriteLine("Access-Control-Allow-Headers: Content-Type");
                        writer.WriteLine("Connection: close");
                        writer.WriteLine();
                        writer.WriteLine(response);
                        writer.Flush();
                    }
                    client.Close();
                }
                else
                {
                    // Handle other paths (404)
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.WriteLine("HTTP/1.1 404 Not Found");
                        writer.WriteLine("Content-Type: text/plain");
                        writer.WriteLine("Connection: close");
                        writer.WriteLine();
                        writer.WriteLine("404 Not Found - Path: " + path);
                        writer.Flush();
                    }
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Client handling error: {ex.GetType().Name} - {ex.Message}");
                try { client.Close(); } catch { }
            }
        }

        private void BroadcastTrackInfo()
        {
            var url = mbApiInterface.NowPlaying_GetFileUrl();
            if (url == null)
                return;

            var trackInfo = new
            {
                @event = "Track",
                data = new
                {
                    title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle) ?? "",
                    author = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist) ?? "",
                    cover = "", // TODO: Implement cover art URL
                    duration = mbApiInterface.NowPlaying_GetDuration(),
                    album = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album) ?? ""
                }
            };

            BroadcastMessage(trackInfo);
        }

        public string GetProcessedLyrics()
        {
            string lrc = null;
            try
            {
                // Try to get lyrics using MusicBee API
                lrc = mbApiInterface.NowPlaying_GetLyrics();

                // If no lyrics found, try downloaded lyrics
                if (string.IsNullOrEmpty(lrc))
                {
                    lrc = mbApiInterface.NowPlaying_GetDownloadedLyrics();
                }

                if (isSplitTranslation)
                    lrc = SplitTranslation(lrc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting lyrics: {ex.Message}");
            }

            return lrc;
        }

        private void BroadcastLyricInfo()
        {
            var url = mbApiInterface.NowPlaying_GetFileUrl();
            if (url == null)
                return;

            string lrc = GetProcessedLyrics();

            var lyricInfo = new
            {
                @event = "Lyric",
                data = new
                {
                    hasLyric = !string.IsNullOrEmpty(lrc),
                    lrc = lrc
                }
            };

            BroadcastMessage(lyricInfo);
        }


        public static string SplitTranslation(string text)
        {
            var lines = text.Split('\n');
            StringBuilder sb = new StringBuilder();
            int addline = 0;
            foreach (var line in lines)
            {
                var v = line.Trim();
                if (v.Contains("/"))
                {
                    var parts = v.Split(new char[] { '/' }, 2);
                    if (parts.Length == 2 && parts[0].Contains(']'))
                    {
                        var part0 = parts[0].Trim();
                        var timeTag = part0.Split(']')[0];
                        sb.AppendLine(part0)
                            .Append(timeTag)
                            .Append(']')
                            .AppendLine(parts[1]);
                        addline += 1;
                    }
                    else
                    {
                        sb.AppendLine(v);
                    }
                }
                else
                {
                    sb.AppendLine(v);
                }
            }

            if (addline * 2 > lines.Length)
            {
                return sb.ToString();
            }
            else
            {
                Debug.WriteLine($"Skip SplitTranslation, addline {addline} * 2 < {lines.Length}");
                return text;
            }

        }

        private void BroadcastPlayerState()
        {
            var isPaused = mbApiInterface.Player_GetPlayState() != PlayState.Playing;
            
            var pauseInfo = new
            {
                @event = "PlayerPauseState",
                data = new
                {
                    isPaused = isPaused
                }
            };

            BroadcastMessage(pauseInfo);
        }

        private void BroadcastProgress()
        {
            var progress = mbApiInterface.Player_GetPosition();
            
            var progressInfo = new
            {
                @event = "PlayerProgress",
                data = new
                {
                    progress = progress
                }
            };

            BroadcastMessage(progressInfo);
        }

        private void BroadcastReplay()
        {
            var replayInfo = new
            {
                @event = "PlayerProgressReplay",
                data = new { }
            };

            BroadcastMessage(replayInfo);
        }

        private void BroadcastMessage(object message)
        {
            var json = jsonSerializer.Serialize(message);
            var data = Encoding.UTF8.GetBytes(json);

            // Create a copy of the clients list to avoid concurrent modification issues
            var clientsCopy = webSocketClients.ToArray();
            foreach (var client in clientsCopy)
            {
                client.Send(data);
            }
        }

        private string GetPlayerInfoResponse()
        {
            var url = mbApiInterface.NowPlaying_GetFileUrl();
            var hasSong = !string.IsNullOrEmpty(url);
            var isPaused = mbApiInterface.Player_GetPlayState() != PlayState.Playing;
            var volumePercent = (int)(mbApiInterface.Player_GetVolume() * 100);
            var currentPosition = mbApiInterface.Player_GetPosition();
            var duration = mbApiInterface.NowPlaying_GetDuration();
            var statePercent = duration > 0 ? (double)currentPosition / duration : 0;

            // Get repeat type
            string repeatType;
            switch (mbApiInterface.Player_GetRepeat())
            {
                case RepeatMode.None:
                    repeatType = "NONE";
                    break;
                case RepeatMode.One:
                    repeatType = "ONE";
                    break;
                case RepeatMode.All:
                    repeatType = "ALL";
                    break;
                default:
                    repeatType = "NONE";
                    break;
            }

            // Format time
            string FormatTime(int milliseconds)
            {
                var totalSeconds = milliseconds / 1000;
                var minutes = totalSeconds / 60;
                var seconds = totalSeconds % 60;
                return $"{minutes}:{seconds:D2}";
            }

            var playerInfo = new
            {
                player = new
                {
                    hasSong = hasSong,
                    isPaused = isPaused,
                    volumePercent = volumePercent,
                    seekbarCurrentPosition = currentPosition / 1000, // Convert to seconds
                    seekbarCurrentPositionHuman = FormatTime(currentPosition),
                    statePercent = statePercent,
                    likeStatus = "INDIFFERENT",
                    repeatType = repeatType
                },
                track = hasSong ? new
                {
                    author = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist) ?? "",
                    title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle) ?? "",
                    album = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album) ?? "",
                    cover = "", // TODO: Implement cover art URL
                    duration = duration / 1000, // Convert to seconds
                    durationHuman = FormatTime(duration),
                    url = "", // TODO: Implement track URL
                    id = "", // TODO: Implement track ID
                    isVideo = false,
                    isAdvertisement = false,
                    inLibrary = true
                } : new
                {
                    author = "",
                    title = "",
                    album = "",
                    cover = "",
                    duration = 0,
                    durationHuman = "0:00",
                    url = "",
                    id = "",
                    isVideo = false,
                    isAdvertisement = false,
                    inLibrary = false
                }
            };

            return jsonSerializer.Serialize(playerInfo);
        }

        private class WebSocketClient
        {
            private TcpClient client;
            private NetworkStream stream;
            private ConcurrentBag<WebSocketClient> clients;
            private MusicBeeApiInterface mbApiInterface;
            private JavaScriptSerializer jsonSerializer;
            private Plugin plugin;

            public WebSocketClient(TcpClient client, ConcurrentBag<WebSocketClient> clients, MusicBeeApiInterface mbApiInterface, JavaScriptSerializer jsonSerializer, Plugin plugin)
            {
                this.client = client;
                this.stream = client.GetStream();
                this.clients = clients;
                this.mbApiInterface = mbApiInterface;
                this.jsonSerializer = jsonSerializer;
                this.plugin = plugin;
            }

            public async Task Handle(string request)
            {
                try
                {
                    // WebSocket handshake
                    await PerformHandshake(request);

                    Debug.WriteLine("WebSocket connection established");

                    // Send initial data to client immediately after connection
                    SendTrackInfo();
                    SendLyricInfo();
                    SendPlayerState();
                    SendProgress();

                    // Handle messages and keep connection alive
                    var buffer = new byte[4096];
                    while (client.Connected)
                    {
                        try
                        {
                            // Check if stream is available
                            if (stream == null)
                            {
                                Debug.WriteLine("Stream is null, closing connection");
                                break;
                            }

                            // Set a timeout for reading to allow periodic checks
                            if (stream.DataAvailable)
                            {
                                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (bytesRead == 0)
                                {
                                    Debug.WriteLine("Client sent 0 bytes, closing");
                                    break;
                                }

                                // Simple WebSocket frame handling
                                // For production use, implement proper WebSocket frame parsing
                            }
                            else
                            {
                                // No data available, wait a bit before checking again
                                await Task.Delay(100);
                            }
                        }
                        catch (ObjectDisposedException ex)
                        {
                            // Stream or client has been disposed
                            Debug.WriteLine($"Object disposed: {ex.Message}");
                            break;
                        }
                        catch (IOException ex)
                        {
                            // Network error, likely client disconnected
                            Debug.WriteLine($"IO error: {ex.Message}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            // Ignore read errors, likely due to client disconnect
                            Debug.WriteLine($"WebSocket read error: {ex.Message}");
                            if (!client.Connected)
                                break;
                        }
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    Debug.WriteLine($"Object disposed in handler: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WebSocket client error: {ex.Message}");
                }
                finally
                {
                    Debug.WriteLine("WebSocket connection closed");
                    // Remove client from the list
                    clients.TryTake(out _);
                    // Clean up resources
                    try
                    {
                        stream?.Close();
                        client?.Close();
                    }
                    catch { }
                }
            }

            private void SendTrackInfo()
            {
                var url = mbApiInterface.NowPlaying_GetFileUrl();
                if (url == null)
                    return;

                var trackInfo = new
                {
                    @event = "Track",
                    data = new
                    {
                        title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle) ?? "",
                        author = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist) ?? "",
                        cover = "", // TODO: Implement cover art URL
                        duration = mbApiInterface.NowPlaying_GetDuration(),
                        album = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album) ?? ""
                    }
                };

                SendMessage(trackInfo);
            }

            private void SendLyricInfo()
            {
                var url = mbApiInterface.NowPlaying_GetFileUrl();
                if (url == null)
                    return;

                string lrc = plugin.GetProcessedLyrics();

                var lyricInfo = new
                {
                    @event = "Lyric",
                    data = new
                    {
                        hasLyric = !string.IsNullOrEmpty(lrc),
                        lrc = lrc
                    }
                };

                SendMessage(lyricInfo);
            }

            private void SendPlayerState()
            {
                var isPaused = mbApiInterface.Player_GetPlayState() != PlayState.Playing;
                
                var pauseInfo = new
                {
                    @event = "PlayerPauseState",
                    data = new
                    {
                        isPaused = isPaused
                    }
                };

                SendMessage(pauseInfo);
            }

            private void SendProgress()
            {
                var progress = mbApiInterface.Player_GetPosition();
                
                var progressInfo = new
                {
                    @event = "PlayerProgress",
                    data = new
                    {
                        progress = progress
                    }
                };

                SendMessage(progressInfo);
            }

            private void SendMessage(object message)
            {
                try
                {
                    if (!client.Connected || stream == null)
                        return;

                    var json = jsonSerializer.Serialize(message);
                    var data = Encoding.UTF8.GetBytes(json);

                    // Create proper WebSocket frame
                    using (var ms = new MemoryStream())
                    {
                        // Byte 0: FIN=1, opcode=1 (text frame)
                        ms.WriteByte(0x81);

                        // Byte 1+: Payload length with masking bit clear (server to client doesn't need masking)
                        if (data.Length < 126)
                        {
                            ms.WriteByte((byte)data.Length);
                        }
                        else if (data.Length < 65536)
                        {
                            ms.WriteByte(126);
                            ms.WriteByte((byte)((data.Length >> 8) & 0xFF));
                            ms.WriteByte((byte)(data.Length & 0xFF));
                        }
                        else
                        {
                            ms.WriteByte(127);
                            // 8 bytes for extended payload length (big-endian)
                            byte[] lengthBytes = BitConverter.GetBytes((long)data.Length);
                            if (BitConverter.IsLittleEndian)
                                Array.Reverse(lengthBytes);
                            ms.Write(lengthBytes, 0, lengthBytes.Length);
                        }

                        // Payload
                        ms.Write(data, 0, data.Length);

                        // Send the frame
                        var frame = ms.ToArray();
                        stream.Write(frame, 0, frame.Length);
                        stream.Flush();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Client has disconnected, ignore
                    Debug.WriteLine("Client disconnected while sending message");
                }
                catch (IOException ex)
                {
                    // Network error, likely client disconnected
                    Debug.WriteLine($"IO error sending message: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Send message error: {ex.Message}");
                }
            }

            private async Task PerformHandshake(string request)
            {
                try
                {
                    // Extract Sec-WebSocket-Key from request
                    string secWebSocketKey = "";
                    var lines = request.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Sec-WebSocket-Key: "))
                        {
                            secWebSocketKey = line.Substring("Sec-WebSocket-Key: ".Length).Trim();
                            break;
                        }
                    }

                    // Calculate Sec-WebSocket-Accept
                    string secWebSocketAccept = "";
                    if (!string.IsNullOrEmpty(secWebSocketKey))
                    {
                        string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                        string combined = secWebSocketKey + magic;
                        byte[] combinedBytes = Encoding.UTF8.GetBytes(combined);
                        byte[] hashBytes;
                        using (SHA1 sha1 = SHA1.Create())
                        {
                            hashBytes = sha1.ComputeHash(combinedBytes);
                        }
                        secWebSocketAccept = Convert.ToBase64String(hashBytes);
                    }

                    Debug.WriteLine($"Sec-WebSocket-Key: {secWebSocketKey}");
                    Debug.WriteLine($"Sec-WebSocket-Accept: {secWebSocketAccept}");

                    // WebSocket handshake response
                    var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Sec-WebSocket-Accept: " + secWebSocketAccept + "\r\n" +
                                  "\r\n";
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    await stream.FlushAsync();

                    Debug.WriteLine("WebSocket handshake response sent");
                }
                catch (ObjectDisposedException ex)
                {
                    Debug.WriteLine($"Stream disposed during handshake: {ex.Message}");
                    throw;
                }
                catch (IOException ex)
                {
                    Debug.WriteLine($"IO error during handshake: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WebSocket handshake error: {ex.Message}");
                    throw;
                }
            }

            public void Send(byte[] data)
            {
                try
                {
                    if (client.Connected && stream != null)
                    {
                        // Simple WebSocket frame wrapper
                        var frame = new byte[data.Length + 2];
                        frame[0] = 0x81; // Text frame
                        frame[1] = (byte)data.Length;
                        Array.Copy(data, 0, frame, 2, data.Length);
                        stream.Write(frame, 0, frame.Length);
                    }
                }
                catch { }
            }
        }

    }
}