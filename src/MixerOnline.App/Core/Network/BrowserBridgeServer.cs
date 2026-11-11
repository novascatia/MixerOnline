using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MixerOnline.Core.Audio;

namespace MixerOnline.Core.Network
{
    public sealed class BrowserBridgeServer : IDisposable
    {
        private readonly BrowserAudioReceiver _audioReceiver;
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private WebSocket? _activeClient;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public event EventHandler<bool>? ClientConnectionChanged;
        public event EventHandler<TabListMessage>? TabListReceived;
        public event EventHandler<CaptureStatusMessage>? CaptureStatusChanged;
        public event EventHandler<TabNotificationMessage>? TabClosed;
        public event EventHandler<TabNotificationMessage>? TabUpdated;
        public event EventHandler<string>? ExtensionError;

        public bool IsClientConnected => _activeClient != null && _activeClient.State == WebSocketState.Open;
        public int Port => _port;

        public BrowserBridgeServer(BrowserAudioReceiver audioReceiver, int port = 49876)
        {
            _audioReceiver = audioReceiver ?? throw new ArgumentNullException(nameof(audioReceiver));
            _port = port;
        }

        public void Start()
        {
            if (_listener != null && _listener.IsListening) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

            try
            {
                _listener.Start();
                Task.Run(() => AcceptLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                ExtensionError?.Invoke(this, $"Failed to start browser bridge on port {_port}: {ex.Message}");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _activeClient?.Abort();
                _activeClient?.Dispose();
            }
            catch { }
            _activeClient = null;

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }
            _listener = null;

            ClientConnectionChanged?.Invoke(this, false);
        }

        private static bool IsAllowedWebSocketOrigin(string? origin)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                // Native loopback clients and automated tests typically omit the Origin header.
                return true;
            }

            string trimmed = origin.Trim();
            return trimmed.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("extension://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStatusEndpointRequest(HttpListenerRequest request)
        {
            return string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Url?.AbsolutePath, "/status", StringComparison.OrdinalIgnoreCase);
        }

        private async Task WriteStatusResponseAsync(HttpListenerResponse response, CancellationToken token)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                app = "MixerOnline",
                status = "ready",
                port = _port,
                connected = IsClientConnected
            });

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload, token);
            response.Close();
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();

                    if (IsStatusEndpointRequest(context.Request))
                    {
                        await WriteStatusResponseAsync(context.Response, token);
                        continue;
                    }

                    if (context.Request.IsWebSocketRequest)
                    {
                        string? origin = context.Request.Headers["Origin"];
                        if (!IsAllowedWebSocketOrigin(origin))
                        {
                            System.Diagnostics.Debug.WriteLine($"[BrowserBridge] Rejected WebSocket connection from disallowed origin: {origin}");
                            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                            context.Response.Close();
                            continue;
                        }

                        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                        _ = HandleClientAsync(wsContext.WebSocket, token);
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BrowserBridge] Accept loop error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(WebSocket ws, CancellationToken token)
        {
            // Close previous client if any
            if (_activeClient != null)
            {
                try { _activeClient.Abort(); _activeClient.Dispose(); } catch { }
            }

            _activeClient = ws;
            ClientConnectionChanged?.Invoke(this, true);

            // Request initial tab list upon connection
            _ = RequestTabsAsync();

            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            using var ms = new MemoryStream();

            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Audio frame: fast path
                        if (result.EndOfMessage && ms.Length == 0)
                        {
                            _audioReceiver.ProcessAudioFrame(buffer, 0, result.Count);
                        }
                        else
                        {
                            ms.Write(buffer, 0, result.Count);
                            if (result.EndOfMessage)
                            {
                                byte[] frameData = ms.ToArray();
                                ms.SetLength(0);
                                _audioReceiver.ProcessAudioFrame(frameData, 0, frameData.Length);
                            }
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        // JSON control message
                        ms.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            string text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                            ms.SetLength(0);
                            ProcessJsonMessage(text);
                        }
                    }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"[BrowserBridge] Client disconnected: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (ReferenceEquals(_activeClient, ws))
                {
                    _activeClient = null;
                    _audioReceiver.Reset();
                    ClientConnectionChanged?.Invoke(this, false);
                }
                try { ws.Dispose(); } catch { }
            }
        }

        private void ProcessJsonMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;

                string type = typeProp.GetString() ?? string.Empty;

                switch (type.ToUpperInvariant())
                {
                    case "HELLO":
                        var hello = JsonSerializer.Deserialize<HelloMessage>(json);
                        System.Diagnostics.Debug.WriteLine($"[BrowserBridge] Connected client: {hello?.Client} v{hello?.Version}");
                        break;

                    case "TAB_LIST":
                        var listMsg = JsonSerializer.Deserialize<TabListMessage>(json);
                        if (listMsg != null)
                        {
                            TabListReceived?.Invoke(this, listMsg);
                        }
                        break;

                    case "CAPTURE_STARTED":
                    case "CAPTURE_STOPPED":
                        var capMsg = JsonSerializer.Deserialize<CaptureStatusMessage>(json);
                        if (capMsg != null)
                        {
                            if (capMsg.SampleRate > 0)
                            {
                                _audioReceiver.DetectedSampleRate = capMsg.SampleRate;
                            }
                            CaptureStatusChanged?.Invoke(this, capMsg);
                        }
                        break;

                    case "TAB_CLOSED":
                        var closedMsg = JsonSerializer.Deserialize<TabNotificationMessage>(json);
                        if (closedMsg != null)
                        {
                            _audioReceiver.Reset();
                            TabClosed?.Invoke(this, closedMsg);
                        }
                        break;

                    case "TAB_UPDATED":
                        var updateMsg = JsonSerializer.Deserialize<TabNotificationMessage>(json);
                        if (updateMsg != null)
                        {
                            TabUpdated?.Invoke(this, updateMsg);
                        }
                        break;

                    case "ERROR":
                        var errMsg = JsonSerializer.Deserialize<ErrorNotificationMessage>(json);
                        if (errMsg != null)
                        {
                            ExtensionError?.Invoke(this, errMsg.Message);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrowserBridge] JSON parse error: {ex.Message}");
            }
        }

        public async Task SendMessageAsync(object message)
        {
            var ws = _activeClient;
            if (ws == null || ws.State != WebSocketState.Open) return;

            string json = JsonSerializer.Serialize(message);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync();
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BrowserBridge] Send error: {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task RequestTabsAsync()
        {
            return SendMessageAsync(new BaseMessage { Type = "GET_TABS" });
        }

        public Task SelectTabAsync(int tabId)
        {
            return SendMessageAsync(new SelectTabMessage { Type = "SELECT_TAB", TabId = tabId });
        }

        public Task StopCaptureAsync()
        {
            return SendMessageAsync(new BaseMessage { Type = "STOP_CAPTURE" });
        }

        public Task SetBrowserPassthroughAsync(bool enabled)
        {
            return SendMessageAsync(new { type = "SET_BROWSER_PASSTHROUGH", enabled });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _sendLock.Dispose();
        }
    }
}
