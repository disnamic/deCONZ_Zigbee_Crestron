/*******************************************************************************
 * DeConzWebSocketClient.cs
 *
 * SIMPL# class – WebSocket gateway to deCONZ Zigbee coordinator.
 *
 * Changelog v2.1:
 *   - AliveTimeoutMs now configurable via SetAliveTimeout(seconds) → SIMPL+ parameter
 *   - TLS/WSS support via SecureTCPClient, selectable via SetTls(enable) → SIMPL+ parameter
 *   - Origin header switches http/https automatically based on TLS flag
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;

namespace DeConzZigbee
{
    // ── Delegates (SIMPL+ RegisterDelegate targets) ─────────────────────────
    public delegate void ConnectionStatusDelegate(ushort tcpStatus);
    public delegate void DebugOutputDelegate(SimplSharpString message);
    public delegate void TrafficDelegate(ushort active);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzWebSocketClient
    {
        // ── Delegates wired up by the SIMPL+ wrapper ─────────────────────
        public ConnectionStatusDelegate OnConnectionStatus { get; set; }
        public DebugOutputDelegate      OnDebugOutput      { get; set; }
        public TrafficDelegate          OnTrafficPresent   { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string  _ip;
        private int     _port;
        private bool    _debugEnabled;
        private bool    _handshakeDone;
        private bool    _intentionalDisconnect;
        private string  _wsKey;
        private bool    _useTls;

        // Plain TCP client (ws://)
        private TCPClient       _tcp;

        // Secure TCP client (wss://)
        private SecureTCPClient _stcp;

        // ── Reconnect back-off: 5 → 10 → 20 → 30 s (capped) ─────────────
        private int          _reconnectMs = 5000;
        private const int    ReconnectMax = 30000;

        // ── Alive timer – default 15 s, overridable via SIMPL+ parameter ─
        private int          _aliveTimeoutMs = 15000;
        private CTimer       _aliveTimer;
        private CTimer       _reconnectTimer;

        // ── Frame accumulation buffer ─────────────────────────────────────
        private byte[]  _frameBuffer = new byte[0];

        private readonly CCriticalSection _sendLock = new CCriticalSection();

        // ── Single shared RNG for masking keys / WS key ────────────────────
        // Seeded once at construction. Re-seeding per call (e.g. with
        // DateTime.Now.Ticks) can yield identical seeds for frames sent within
        // the same system tick, producing repeated masking keys.
        private static readonly Random _rng = new Random();

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Must be called once from SIMPL+ Main() before Connect().
        /// </summary>
        public void Initialize(string ipAddress, int port)
        {
            _ip   = ipAddress;
            _port = port;

            DeConzBroker.OnBrokerLog = msg => DebugLog(msg);

            // Publish IP so device modules can build HTTP URLs without signal wiring
            DeConzBroker.GatewayIP = ipAddress;

            Log(string.Format("[GW] Initialized – {0}:{1}  TLS={2}  AliveTimeout={3}s",
                _ip, _port, _useTls, _aliveTimeoutMs / 1000));
        }

        /// <summary>
        /// Set the alive-timeout in seconds (called from SIMPL+ before Initialize).
        /// Valid range: 5-3600 s. Values outside are clamped.
        /// </summary>
        public void SetAliveTimeout(int seconds)
        {
            seconds         = Math.Max(5, Math.Min(3600, seconds));
            _aliveTimeoutMs = seconds * 1000;
            Log(string.Format("[GW] AliveTimeout set to {0} s", seconds));
        }

        /// <summary>
        /// Enable (1) or disable (0) TLS/WSS (called from SIMPL+ before Initialize).
        /// When enabled, SecureTCPClient is used and the WS path changes to wss://.
        /// </summary>
        public void SetTls(ushort enable)
        {
            _useTls = (enable != 0);
            Log("[GW] TLS/WSS " + (_useTls ? "enabled" : "disabled"));
        }

        public void SetDebug(ushort enable)
        {
            _debugEnabled = (enable != 0);
        }

        /// <summary>Open (or re-open) the WebSocket connection.</summary>
        public void Connect()
        {
            _intentionalDisconnect = false;
            _reconnectMs           = 5000;
            StartConnection();
        }

        /// <summary>Cleanly close the connection and stop reconnect attempts.</summary>
        public void Disconnect()
        {
            _intentionalDisconnect = true;
            StopTimers();
            CloseSocket();
            Log("[GW] Disconnected by user");
        }

        // ── Connection internals ──────────────────────────────────────────

        private void StartConnection()
        {
            try
            {
                CloseSocket();

                _handshakeDone = false;
                _frameBuffer   = new byte[0];
                _wsKey         = GenerateWebSocketKey();

                Log(string.Format("[GW] Connecting ({0})…", _useTls ? "WSS/TLS" : "WS/plain"));

                if (_useTls)
                {
                    _stcp = new SecureTCPClient(_ip, _port, 65536);
                    _stcp.SocketStatusChange += OnSecureStatusChange;
                    _stcp.ConnectToServerAsync(OnSecureConnectComplete);
                }
                else
                {
                    _tcp = new TCPClient(_ip, _port, 65536);
                    _tcp.SocketStatusChange += OnPlainStatusChange;
                    _tcp.ConnectToServerAsync(OnPlainConnectComplete);
                }
            }
            catch (Exception ex)
            {
                Log("[GW] StartConnection error: " + ex.Message);
                ScheduleReconnect();
            }
        }

        // ── Plain TCP callbacks ───────────────────────────────────────────

        private void OnPlainConnectComplete(TCPClient tcp)
        {
            if (tcp.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                Log("[GW] TCP connected – sending WS handshake");
                SendHandshake();
                tcp.ReceiveDataAsync(OnPlainDataReceived);
            }
            else
            {
                Log("[GW] TCP connect failed – status: " + tcp.ClientStatus);
                ScheduleReconnect();
            }
        }

        private void OnPlainStatusChange(TCPClient tcp, SocketStatus status)
        {
            FireStatus((ushort)status);
            Log("[GW] Socket status: " + status);
            if (IsDropped(status) && !_intentionalDisconnect)
                ScheduleReconnect();
        }

        private void OnPlainDataReceived(TCPClient tcp, int bytesReceived)
        {
            if (bytesReceived <= 0)
            {
                Log("[GW] Receive returned 0 – peer closed connection");
                if (!_intentionalDisconnect) ScheduleReconnect();
                return;
            }

            var chunk = new byte[bytesReceived];
            Array.Copy(tcp.IncomingDataBuffer, chunk, bytesReceived);
            HandleChunk(chunk);

            if (!_intentionalDisconnect && tcp.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                tcp.ReceiveDataAsync(OnPlainDataReceived);
        }

        // ── Secure TCP callbacks ──────────────────────────────────────────

        private void OnSecureConnectComplete(SecureTCPClient stcp)
        {
            if (stcp.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
            {
                Log("[GW] SecureTCP (TLS) connected – sending WS handshake");
                SendHandshake();
                stcp.ReceiveDataAsync(OnSecureDataReceived);
            }
            else
            {
                Log("[GW] SecureTCP connect failed – status: " + stcp.ClientStatus);
                ScheduleReconnect();
            }
        }

        private void OnSecureStatusChange(SecureTCPClient stcp, SocketStatus status)
        {
            FireStatus((ushort)status);
            Log("[GW] SecureSocket status: " + status);
            if (IsDropped(status) && !_intentionalDisconnect)
                ScheduleReconnect();
        }

        private void OnSecureDataReceived(SecureTCPClient stcp, int bytesReceived)
        {
            if (bytesReceived <= 0)
            {
                Log("[GW] Secure receive returned 0 – peer closed connection");
                if (!_intentionalDisconnect) ScheduleReconnect();
                return;
            }

            var chunk = new byte[bytesReceived];
            Array.Copy(stcp.IncomingDataBuffer, chunk, bytesReceived);
            HandleChunk(chunk);

            if (!_intentionalDisconnect && stcp.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                stcp.ReceiveDataAsync(OnSecureDataReceived);
        }

        // ── Shared chunk handler ──────────────────────────────────────────

        private void HandleChunk(byte[] chunk)
        {
            if (!_handshakeDone)
            {
                ProcessHandshakeResponse(chunk);
            }
            else
            {
                var combined = new byte[_frameBuffer.Length + chunk.Length];
                Array.Copy(_frameBuffer, combined, _frameBuffer.Length);
                Array.Copy(chunk, 0, combined, _frameBuffer.Length, chunk.Length);
                _frameBuffer = combined;
                ProcessFrameBuffer();
            }
        }

        // ── WebSocket handshake ───────────────────────────────────────────

        private void SendHandshake()
        {
            var scheme = _useTls ? "https" : "http";
            var sb = new StringBuilder();
            sb.Append("GET / HTTP/1.1\r\n");
            sb.AppendFormat("Host: {0}:{1}\r\n", _ip, _port);
            sb.Append("Upgrade: websocket\r\n");
            sb.Append("Connection: Upgrade\r\n");
            sb.Append("Pragma: no-cache\r\n");
            sb.Append("Cache-Control: no-cache\r\n");
            sb.Append("User-Agent: CrestronSIMPL/DeConzGW\r\n");
            sb.AppendFormat("Origin: {0}://{1}\r\n", scheme, _ip);
            sb.Append("Sec-WebSocket-Version: 13\r\n");
            sb.AppendFormat("Sec-WebSocket-Key: {0}\r\n", _wsKey);
            // permessage-deflate intentionally omitted: the frame decoder does not
            // handle compressed payloads. Advertising the extension risks a server
            // or proxy accepting it, after which all incoming frames would be
            // deflate-compressed and decoded as garbage without any error signal.
            sb.Append("\r\n");

            SendRaw(Encoding.ASCII.GetBytes(sb.ToString()));
        }

        private void ProcessHandshakeResponse(byte[] data)
        {
            var text = Encoding.ASCII.GetString(data);

            if (text.Contains("101 Switching Protocols"))
            {
                _handshakeDone = true;
                Log("[GW] WebSocket handshake OK – listening for events");
                ResetAliveTimer();
                // Expose WS send capability to all device modules via broker
                DeConzBroker.SendWsFrame = SendTextFrame;
                // Notify all light modules to schedule a delayed GetState()
                DeConzBroker.NotifyWsConnected();
            }
            else if (text.Contains("HTTP/1.1 4") || text.Contains("HTTP/1.1 5"))
            {
                Log("[GW] Handshake rejected: " + text.Substring(0, Math.Min(80, text.Length)));
                ScheduleReconnect();
            }
        }

        // ── WebSocket frame decoder (RFC 6455) ────────────────────────────

        private void ProcessFrameBuffer()
        {
            while (_frameBuffer.Length >= 2)
            {
                bool fin        = (_frameBuffer[0] & 0x80) != 0;
                int  opcode     = (_frameBuffer[0] & 0x0F);
                bool masked     = (_frameBuffer[1] & 0x80) != 0;
                long payloadLen = (_frameBuffer[1] & 0x7F);
                int  headerLen  = 2;

                if (payloadLen == 126)
                {
                    if (_frameBuffer.Length < 4) break;
                    payloadLen = (_frameBuffer[2] << 8) | _frameBuffer[3];
                    headerLen  = 4;
                }
                else if (payloadLen == 127)
                {
                    if (_frameBuffer.Length < 10) break;
                    payloadLen = 0;
                    for (int i = 0; i < 8; i++)
                        payloadLen = (payloadLen << 8) | _frameBuffer[2 + i];
                    headerLen = 10;
                }

                if (masked) headerLen += 4;

                long totalLen = headerLen + payloadLen;
                if (_frameBuffer.Length < totalLen) break;

                var payload = new byte[payloadLen];
                Array.Copy(_frameBuffer, headerLen, payload, 0, (int)payloadLen);

                if (masked)
                {
                    int maskOffset = headerLen - 4;
                    for (long i = 0; i < payloadLen; i++)
                        payload[i] ^= _frameBuffer[maskOffset + (i % 4)];
                }

                var remaining = new byte[_frameBuffer.Length - totalLen];
                Array.Copy(_frameBuffer, totalLen, remaining, 0, remaining.Length);
                _frameBuffer = remaining;

                switch (opcode)
                {
                    case 0x00: // Continuation
                    case 0x01: // Text frame
                        if (fin)
                        {
                            ResetAliveTimer();
                            HandleTextFrame(Encoding.UTF8.GetString(payload));
                        }
                        break;

                    case 0x02: // Binary – deCONZ does not use this
                        break;

                    case 0x08: // Close
                        Log("[GW] Server sent Close frame");
                        if (!_intentionalDisconnect) ScheduleReconnect();
                        return;

                    case 0x09: // Ping → Pong
                        SendPong(payload);
                        break;

                    case 0x0A: // Pong – nothing to do
                        break;
                }
            }
        }

        // ── JSON routing ──────────────────────────────────────────────────

        private void HandleTextFrame(string json)
        {
            if (_debugEnabled) DebugLog("[RX] " + json);
            if (!json.Contains("uniqueid")) return;

            var uid = ExtractJsonStringValue(json, "uniqueid");
            if (string.IsNullOrEmpty(uid))
            {
                Log("[GW] Could not extract uniqueid from: " +
                    json.Substring(0, Math.Min(80, json.Length)));
                return;
            }

            DeConzBroker.DispatchUpdate(uid, json);
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            try
            {
                var search    = "\"" + key + "\"";
                int keyPos    = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
                if (keyPos < 0) return null;
                int colonPos  = json.IndexOf(':', keyPos + search.Length);
                if (colonPos < 0) return null;
                int quoteStart = json.IndexOf('"', colonPos + 1);
                if (quoteStart < 0) return null;
                int quoteEnd   = json.IndexOf('"', quoteStart + 1);
                if (quoteEnd < 0) return null;
                return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
            }
            catch { return null; }
        }

        // ── Alive timer ───────────────────────────────────────────────────

        private void ResetAliveTimer()
        {
            FireTraffic(1);

            if (_aliveTimer != null)
                _aliveTimer.Reset(_aliveTimeoutMs);
            else
                _aliveTimer = new CTimer(AliveExpired, null, _aliveTimeoutMs);
        }

        private void AliveExpired(object _)
        {
            Log(string.Format("[GW] No WebSocket traffic for {0} s", _aliveTimeoutMs / 1000));
            FireTraffic(0);
        }

        // ── Reconnect back-off ────────────────────────────────────────────

        private void ScheduleReconnect()
        {
            if (_intentionalDisconnect) return;

            Log(string.Format("[GW] Reconnecting in {0} s…", _reconnectMs / 1000));
            if (_reconnectTimer != null) _reconnectTimer.Stop();
            _reconnectTimer = new CTimer(_ =>
            {
                _reconnectMs = Math.Min(_reconnectMs * 2, ReconnectMax);
                StartConnection();
            }, null, _reconnectMs);
        }

        private void StopTimers()
        {
            if (_aliveTimer    != null) { _aliveTimer.Stop();    _aliveTimer    = null; }
            if (_reconnectTimer != null) { _reconnectTimer.Stop(); _reconnectTimer = null; }
        }

        // ── Socket helpers ────────────────────────────────────────────────

        private void CloseSocket()
        {
            // Remove send capability so device modules see no active connection
            DeConzBroker.SendWsFrame = null;

            if (_tcp != null)
            {
                _tcp.SocketStatusChange -= OnPlainStatusChange;
                _tcp.DisconnectFromServer();
                _tcp = null;
            }
            if (_stcp != null)
            {
                _stcp.SocketStatusChange -= OnSecureStatusChange;
                _stcp.DisconnectFromServer();
                _stcp = null;
            }
        }

        private bool IsDropped(SocketStatus s)
        {
            return s == SocketStatus.SOCKET_STATUS_NO_CONNECT
                || s == SocketStatus.SOCKET_STATUS_CONNECT_FAILED
                || s == SocketStatus.SOCKET_STATUS_BROKEN_REMOTELY;
        }

        private void SendPong(byte[] pingPayload)
        {
            var frame = new byte[2 + pingPayload.Length];
            frame[0] = 0x8A;
            frame[1] = (byte)pingPayload.Length;
            Array.Copy(pingPayload, 0, frame, 2, pingPayload.Length);
            SendRaw(frame);
        }

        /// <summary>
        /// Builds a masked RFC 6455 text frame and sends it.
        /// Client→server frames MUST be masked (RFC 6455 §5.3).
        /// Called by DeConzBroker.SendCommand from device modules.
        /// </summary>
        private void SendTextFrame(string text)
        {
            if (!_handshakeDone) return;

            var payload = Encoding.UTF8.GetBytes(text);
            int plen    = payload.Length;

            // Generate 4-byte masking key from the shared RNG (locked)
            var mask = new byte[4];
            lock (_rng) { _rng.NextBytes(mask); }

            // Calculate header size
            int headerLen;
            if      (plen <= 125) headerLen = 2 + 4;
            else if (plen <= 65535) headerLen = 4 + 4;
            else                    headerLen = 10 + 4;

            var frame = new byte[headerLen + plen];

            // byte 0: FIN=1, opcode=0x01 (text)
            frame[0] = 0x81;

            // byte 1+: payload length with MASK bit set
            int maskOffset;
            if (plen <= 125)
            {
                frame[1]   = (byte)(0x80 | plen);
                maskOffset = 2;
            }
            else if (plen <= 65535)
            {
                frame[1]   = 0x80 | 126;
                frame[2]   = (byte)(plen >> 8);
                frame[3]   = (byte)(plen & 0xFF);
                maskOffset = 4;
            }
            else
            {
                frame[1] = 0x80 | 127;
                for (int i = 0; i < 8; i++)
                    frame[2 + i] = (byte)((plen >> (56 - i * 8)) & 0xFF);
                maskOffset = 10;
            }

            // Write masking key
            Array.Copy(mask, 0, frame, maskOffset, 4);

            // Mask and write payload
            for (int i = 0; i < plen; i++)
                frame[maskOffset + 4 + i] = (byte)(payload[i] ^ mask[i % 4]);

            SendRaw(frame);
        }

        private void SendRaw(byte[] data)
        {
            _sendLock.Enter();
            try
            {
                if (_useTls && _stcp != null &&
                    _stcp.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                {
                    _stcp.SendData(data, data.Length);
                }
                else if (!_useTls && _tcp != null &&
                    _tcp.ClientStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                {
                    _tcp.SendData(data, data.Length);
                }
            }
            finally { _sendLock.Leave(); }
        }

        // ── Delegate fire helpers ─────────────────────────────────────────

        private void FireStatus(ushort s)
        {
            var cb = OnConnectionStatus;
            if (cb != null) cb(s);
        }

        private void FireTraffic(ushort v)
        {
            var cb = OnTrafficPresent;
            if (cb != null) cb(v);
        }

        // ── Utilities ─────────────────────────────────────────────────────

        private static string GenerateWebSocketKey()
        {
            var bytes = new byte[16];
            lock (_rng) { _rng.NextBytes(bytes); }
            return Convert.ToBase64String(bytes);
        }

        private void Log(string msg)
        {
            CrestronConsole.PrintLine(msg);
        }

        private void DebugLog(string msg)
        {
            if (!_debugEnabled) return;
            Log(msg);
            var cb = OnDebugOutput;
            if (cb != null)
            {
                if (msg.Length > 250) msg = msg.Substring(0, 250) + "…";
                cb(new SimplSharpString(msg));
            }
        }
    }
}
