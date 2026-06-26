/*******************************************************************************
 * DeConzContact.cs
 *
 * SIMPL# class – deCONZ Zigbee door/window contact sensor (ZHAOpenClose).
 *
 * FEEDBACK path – WebSocket events via DeConzBroker + HTTP GET
 * No commands – read-only sensor.
 *
 * State is fetched via HTTP GET on:
 *   - WS connect / reconnect (random 1-15 s stagger via NextStaggerMs)
 *   - Every 30 minutes (background poll timer)
 *   - SIMPL+ pulse on Get_State input
 *
 * Optional Battery_UniqueID registers a dedicated ZHABattery endpoint
 * (same pattern as Thermostat / Shade).
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void ContactBoolDelegate(ushort value);
    public delegate void ContactLevelDelegate(ushort value);
    public delegate void ContactStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzContact
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        // State
        public ContactBoolDelegate   OnOnline           { get; set; }
        public ContactBoolDelegate   OnOpenFb           { get; set; }   // 1 = open
        public ContactBoolDelegate   OnClosedFb         { get; set; }   // 1 = closed
        public ContactBoolDelegate   OnBatteryLow       { get; set; }
        public ContactLevelDelegate  OnBatteryLevel     { get; set; }   // 0-100%
        public ContactLevelDelegate  OnVoltageFb        { get; set; }   // mV

        // Device info
        public ContactStringDelegate OnDeviceIdFb       { get; set; }
        public ContactStringDelegate OnLastSeenFb       { get; set; }
        public ContactStringDelegate OnLastAnnouncedFb  { get; set; }
        public ContactStringDelegate OnManufacturerFb   { get; set; }
        public ContactStringDelegate OnModelIdFb        { get; set; }
        public ContactStringDelegate OnNameFb           { get; set; }
        public ContactStringDelegate OnTypeFb           { get; set; }

        // Raw JSON
        public ContactStringDelegate OnRawJsonFb        { get; set; }
        public ContactStringDelegate OnBatteryRawJson   { get; set; }
        public ContactStringDelegate OnDebugOut         { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        // Contact endpoint
        private string _uniqueId;
        private string _deviceId;
        private string _resource;
        private string _baseUrl;
        private readonly CCriticalSection _idLock = new CCriticalSection();

        // Battery endpoint (optional)
        private string _batteryUid;
        private string _batteryId;
        private string _batteryResource;
        private string _batteryBaseUrl;
        private readonly CCriticalSection _batteryLock = new CCriticalSection();
        private bool   _hasBatteryEndpoint;

        // Online timer
        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        // 30-minute poll timer
        private CTimer _pollTimer;
        private const int PollIntervalMs = 1800000;

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize(string uniqueId, string batteryUniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                CrestronConsole.PrintLine("[Contact] Initialize: empty uniqueId – ignored");
                return;
            }

            _uniqueId    = uniqueId.Trim().ToLowerInvariant();
            _initialized = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            // Contact endpoint
            DeConzBroker.RegisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.RegisterConnectedCallback(_uniqueId, ScheduleGetState);

            // Battery endpoint (optional)
            if (!string.IsNullOrEmpty(batteryUniqueId))
            {
                _batteryUid         = batteryUniqueId.Trim().ToLowerInvariant();
                _hasBatteryEndpoint = true;
                DeConzBroker.RegisterDevice(_batteryUid, OnBatteryWsUpdate);
                DeConzBroker.RegisterConnectedCallback(_batteryUid, ScheduleGetBatteryState);
            }

            // 30-minute poll
            _pollTimer = new CTimer(_ => GetState(), null,
                                    PollIntervalMs, PollIntervalMs);

            DebugLog(string.Format(
                "[Contact] Initialized uid={0}  battery={1}",
                _uniqueId, _hasBatteryEndpoint ? _batteryUid : "(none)"));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
            _permRun = true;
            ArmPermTimer();
        }

        public void SetOnlineTimeout(int seconds)
        {
            _onlineTimeoutMs = Math.Max(5, seconds) * 1000;
        }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }
        public void SetDebug(ushort enable) { _debugEnabled = (enable != 0); }

        public void GetState()
        {
            _staticInfoSent = false;   // re-send static device info on manual refresh
            FetchContactHttp();
            if (_hasBatteryEndpoint) GetBatteryState();
        }

        public void GetBatteryState()
        {
            if (!_hasBatteryEndpoint) return;
            if (!EnsureBatteryUrls()) return;
            string url;
            _batteryLock.Enter();
            try { url = _batteryBaseUrl; } finally { _batteryLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;
            DebugLog("[Contact] GET battery " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnBatteryHttpResponse);
            }
            catch (Exception ex) { Log("[Contact] GET battery error: " + ex.Message); }
        }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer.Dispose(); _staleTimer = null; }
            if (!_initialized) return;
            DeConzBroker.UnregisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.UnregisterConnectedCallback(_uniqueId);
            if (_hasBatteryEndpoint)
            {
                DeConzBroker.UnregisterDevice(_batteryUid, OnBatteryWsUpdate);
                DeConzBroker.UnregisterConnectedCallback(_batteryUid);
            }
            StopOnlineTimer();
            if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); _pollTimer = null; }
            _initialized = false;
        }

        // ── HTTP GET (contact) ────────────────────────────────────────────

        private void FetchContactHttp()
        {
            if (!EnsureUrls()) return;
            string url;
            _idLock.Enter();
            try { url = _baseUrl; } finally { _idLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;
            DebugLog("[Contact] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnHttpResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Contact] GET error: " + ex.Message); }
        }

        private void ScheduleGetState()
        {
            _staticInfoSent = false;  // re-publish identity after reconnect
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Contact:{0}] GetState in {1} s",
                _uniqueId, delayMs / 1000));
            CTimer t = null;
            t = new CTimer(_ => { GetState(); if (t != null) t.Dispose(); }, null, delayMs);
        }

        // ── HTTP responses ────────────────────────────────────────────────

        private void OnHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Contact] HTTP error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Contact] HTTP resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnRawJsonFb, body);
            ParseContactState(body);
            ParseBattery(body);
            ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnBatteryHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Contact] HTTP battery error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Contact] HTTP battery resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, body);
            ParseBattery(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── URL builders ──────────────────────────────────────────────────

        private void BuildUrls()
        {
            var ip = DeConzBroker.GatewayIP;
            string id, res;
            _idLock.Enter();
            try { id = _deviceId; res = _resource; }
            finally { _idLock.Leave(); }
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(id)) return;

            _idLock.Enter();
            try { _baseUrl = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id); }
            finally { _idLock.Leave(); }
            DebugLog("[Contact] URL: " + _baseUrl);
            FireStr(OnDeviceIdFb, id);
            ScheduleGetState();
        }

        private bool EnsureUrls()
        {
            _idLock.Enter();
            string snap; try { snap = _baseUrl; } finally { _idLock.Leave(); }
            if (string.IsNullOrEmpty(snap)) BuildUrls();
            _idLock.Enter();
            try { return !string.IsNullOrEmpty(_baseUrl); } finally { _idLock.Leave(); }
        }

        private void BuildBatteryUrls()
        {
            var ip = DeConzBroker.GatewayIP;
            string id, res;
            _batteryLock.Enter();
            try { id = _batteryId; res = _batteryResource; }
            finally { _batteryLock.Leave(); }
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(id)) return;

            _batteryLock.Enter();
            try { _batteryBaseUrl = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id); }
            finally { _batteryLock.Leave(); }
            DebugLog("[Contact] Battery URL: " + _batteryBaseUrl);
            ScheduleGetBatteryState();
        }

        private bool EnsureBatteryUrls()
        {
            _batteryLock.Enter();
            string snap; try { snap = _batteryBaseUrl; } finally { _batteryLock.Leave(); }
            if (string.IsNullOrEmpty(snap)) BuildBatteryUrls();
            _batteryLock.Enter();
            try { return !string.IsNullOrEmpty(_batteryBaseUrl); }
            finally { _batteryLock.Leave(); }
        }

        private void ScheduleGetBatteryState()
        {
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Contact:{0}] GetBatteryState in {1} s",
                _uniqueId, delayMs / 1000));
            CTimer tb = null;
            tb = new CTimer(_ => { GetBatteryState(); if (tb != null) tb.Dispose(); }, null, delayMs);
        }

        // ── WS callbacks ──────────────────────────────────────────────────

        private void OnWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Contact] WS: " + json);
            bool need;
            _idLock.Enter();
            try { need = string.IsNullOrEmpty(_deviceId); }
            finally { _idLock.Leave(); }

            if (need)
            {
                string id  = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string res = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    _idLock.Enter();
                    try
                    {
                        _deviceId = id.Trim();
                        _resource = string.IsNullOrEmpty(res) ? "sensors" : res.Trim();
                    }
                    finally { _idLock.Leave(); }
                    DebugLog("[Contact] Resolved id=" + _deviceId);
                    BuildUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnRawJsonFb, json);
            if (DeConzJsonParser.HasStateOrConfig(json))
            {
                ParseContactState(json);
                ParseBattery(json);
            }
            ParseDeviceInfo(json);
        }

        private void OnBatteryWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Contact] WS battery: " + json);
            bool need;
            _batteryLock.Enter();
            try { need = string.IsNullOrEmpty(_batteryId); }
            finally { _batteryLock.Leave(); }

            if (need)
            {
                string id  = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string res = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    _batteryLock.Enter();
                    try
                    {
                        _batteryId       = id.Trim();
                        _batteryResource = string.IsNullOrEmpty(res) ? "sensors" : res.Trim();
                    }
                    finally { _batteryLock.Leave(); }
                    DebugLog("[Contact] Battery resolved id=" + _batteryId);
                    BuildBatteryUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, json);
            ParseBattery(json);
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        private void ParseContactState(string json)
        {
            try
            {
                bool? open = DeConzJsonParser.ExtractBool(json, "open", 2);
                if (open.HasValue)
                {
                    Fire(OnOpenFb,   open.Value ? (ushort)1 : (ushort)0);
                    Fire(OnClosedFb, open.Value ? (ushort)0 : (ushort)1);
                }

                bool? reach = DeConzJsonParser.ExtractBool(json, "reachable", 2);
                if (reach.HasValue && reach.Value) { FireOnline(1); RestartOnlineTimer(); }
            }
            catch (Exception ex)
            {
                Log("[Contact] ParseContactState error: " + ex.Message);
            }
        }

        private void ParseBattery(string json)
        {
            try
            {
                int? batt = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (batt.HasValue)
                    Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, batt.Value)));

                bool? low = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (low.HasValue)
                    Fire(OnBatteryLow, low.Value ? (ushort)1 : (ushort)0);

                int? voltage = DeConzJsonParser.ExtractInt(json, "voltage", 2);
                if (voltage.HasValue)
                    Fire(OnVoltageFb, (ushort)Math.Max(0, Math.Min(65535, voltage.Value)));
            }
            catch (Exception ex)
            {
                Log("[Contact] ParseBattery error: " + ex.Message);
            }
        }

        private void ParseDeviceInfo(string json)
        {
            try
            {
                string vDyn;
                vDyn = DeConzJsonParser.ExtractTopLevelString(json, "lastannounced");
                if (vDyn != null) FireStr(OnLastAnnouncedFb, vDyn);
                vDyn = DeConzJsonParser.ExtractTopLevelString(json, "lastseen");
                if (vDyn != null) FireStr(OnLastSeenFb, vDyn);

                // Static device info changes practically never — parse once.
                if (_staticInfoSent) return;
                string v;
                v = DeConzJsonParser.ExtractTopLevelString(json, "manufacturername");
                if (v != null) FireStr(OnManufacturerFb, v);
                v = DeConzJsonParser.ExtractTopLevelString(json, "modelid");
                if (v != null) FireStr(OnModelIdFb, v);
                v = DeConzJsonParser.ExtractTopLevelString(json, "name");
                if (v != null) FireStr(OnNameFb, v);
                v = DeConzJsonParser.ExtractTopLevelString(json, "type");
                if (v != null) FireStr(OnTypeFb, v);
                _staticInfoSent = true;
            }
            catch (Exception ex)
            {
                Log("[Contact] ParseDeviceInfo error: " + ex.Message);
            }
        }

        // ── Online timer ──────────────────────────────────────────────────

        // Reset value/status outputs when no update received for over an hour.
        // Static device info (manufacturer/model/name/type/swversion/id),
        // lastseen/lastannounced, capabilities and raw JSON are preserved.
        private void CheckStale()
        {
            if (_staleResetDone) return;
            if ((DateTime.UtcNow - _lastActivityUtc).TotalMilliseconds < 3600000) return;
            _staleResetDone = true;
            DebugLog("[Contact] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnOpenFb, 0);
            Fire(OnClosedFb, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnVoltageFb, 0);
        }

        private void RestartOnlineTimer()
        {
            _lastActivityUtc = DateTime.UtcNow;
            _staleResetDone  = false;
            if (_onlineTimer != null)
                _onlineTimer.Reset(_onlineTimeoutMs);
            else
                _onlineTimer = new CTimer(_ =>
                {
                    DebugLog("[Contact] Online timeout");
                    FireOnline(0);
                }, null, _onlineTimeoutMs);
        }

        private void StopOnlineTimer() 
        { 
            if (_onlineTimer == null) return;
            _onlineTimer.Stop(); _onlineTimer.Dispose(); _onlineTimer = null; 
        }

        private void FireOnline(ushort v) { Fire(OnOnline, v); }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(ContactStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            const int ChunkSize = 250;
            int pos = 0;
            while (pos < s.Length)
            {
                int len = Math.Min(ChunkSize, s.Length - pos);
                try { cb(new SimplSharpString(s.Substring(pos, len))); } catch { }
                pos += len;
            }
        }
        // ── Fire helpers ──────────────────────────────────────────────────

        private static void Fire(ContactBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(ContactLevelDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        // ── Permanent string re-assert (Make-String-Permanent equivalent) ──
        // Periodically re-fire the cached (non-raw, non-debug) string outputs
        // while the global or this module's local enable is high, so late
        // joining sinks always see the current values.
        private readonly System.Collections.Generic.Dictionary<object, string> _lastStr
            = new System.Collections.Generic.Dictionary<object, string>();
        private readonly CCriticalSection _strLock = new CCriticalSection();
        private bool _permLocal;
        private bool _permRun;

        public void SetPermanentResend(ushort e) { _permLocal = (e != 0); }

        private void ArmPermTimer()
        {
            int ms = DeConzBroker.PermanentResendMs;
            if (ms < 1000) ms = 30000;
            CTimer t = null;
            t = new CTimer(_ =>
            {
                if (DeConzBroker.GlobalPermanentResend || _permLocal) ReassertStrings();
                if (t != null) t.Dispose();
                if (_permRun) ArmPermTimer();
            }, null, ms);
        }

        private void ReassertStrings()
        {
            System.Collections.Generic.KeyValuePair<object, string>[] snap;
            _strLock.Enter();
            try
            {
                snap = new System.Collections.Generic.KeyValuePair<object, string>[_lastStr.Count];
                ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<object, string>>)_lastStr).CopyTo(snap, 0);
            }
            finally { _strLock.Leave(); }
            for (int i = 0; i < snap.Length; i++)
            {
                var cb = snap[i].Key as ContactStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
        }

        private void FireStr(ContactStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 250);
            if (cb != OnDebugOut)
            {
                _strLock.Enter();
                try { _lastStr[cb] = s; }
                finally { _strLock.Leave(); }
            }
            try { cb(new SimplSharpString(s)); } catch { }
        }

        // ── Logging ───────────────────────────────────────────────────────

        private void Log(string msg) { CrestronConsole.PrintLine(msg); }

        private void DebugLog(string msg)
        {
            if (!_debugEnabled) return;
            Log(msg);
            if (msg.Length > 250) msg = msg.Substring(0, 250) + "…";
            FireStr(OnDebugOut, msg);
        }
    }
}
