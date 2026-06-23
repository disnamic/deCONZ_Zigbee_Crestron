/*******************************************************************************
 * DeConzValve.cs  v2.13
 *
 * SIMPL# class – deCONZ Zigbee irrigation valve.
 *
 * One physical device, two deCONZ endpoints:
 *   Valve endpoint  (type "On/Off output", resource "lights")
 *     – On/Off commands via HTTP PUT
 *     – On/Off state feedback via WebSocket
 *   Sensor endpoint (type "ZHAWater", resource "sensors")
 *     – Water detected, battery, flow data via WebSocket
 *     – Read via HTTP GET on connect/reconnect
 *
 * Both UniqueIDs register independently with DeConzBroker.
 * Device IDs and resource types are resolved from the first WS frame
 * of each endpoint – no manual ID parameters needed.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void ValveBoolDelegate(ushort value);
    public delegate void ValveLevelDelegate(ushort value);
    public delegate void ValveStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzValve
    {
        private bool _staticInfoSent;

        // ── Delegates wired by SIMPL+ wrapper ────────────────────────────

        // Valve state
        public ValveBoolDelegate   OnOnline          { get; set; }  // 1 = WS active
        public ValveBoolDelegate   OnValveOnFb        { get; set; }  // 1 = valve open
        public ValveBoolDelegate   OnValveOffFb       { get; set; }  // 1 = valve closed

        // Sensor state
        public ValveBoolDelegate   OnWaterDetected    { get; set; }  // 1 = water present
        public ValveBoolDelegate   OnBatteryLow       { get; set; }  // 1 = low battery
        public ValveLevelDelegate  OnBatteryLevel     { get; set; }  // 0-100 %
        public ValveStringDelegate OnFlowVolume       { get; set; }  // raw string (device-specific)

        // Device info (from HTTP GET, both endpoints)
        public ValveStringDelegate OnValveIdFb        { get; set; }  // resolved valve device ID
        public ValveStringDelegate OnSensorIdFb       { get; set; }  // resolved sensor device ID
        public ValveStringDelegate OnLastSeenFb       { get; set; }  // lastseen (sensor)
        public ValveStringDelegate OnLastAnnouncedFb  { get; set; }  // lastannounced (sensor)
        public ValveStringDelegate OnManufacturerFb   { get; set; }  // manufacturername
        public ValveStringDelegate OnModelIdFb        { get; set; }  // modelid
        public ValveStringDelegate OnNameFb           { get; set; }  // name (valve endpoint)
        public ValveStringDelegate OnTypeFb           { get; set; }  // type (valve endpoint)

        // Raw JSON for both endpoints
        public ValveStringDelegate OnValveRawJson     { get; set; }
        public ValveStringDelegate OnSensorRawJson    { get; set; }
        public ValveStringDelegate OnDebugOut         { get; set; }

        // ── Private state ─────────────────────────────────────────────────

        private string _apiKey;
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        // Valve endpoint (lights)
        private string _valveUid;
        private string _valveId;
        private string _valveResource;
        private string _valveBaseUrl;
        private string _valveStateUrl;

        // Sensor endpoint (sensors)
        private string _sensorUid;
        private string _sensorId;
        private string _sensorResource;
        private string _sensorBaseUrl;

        private string _flowKey = "flow";   // configurable via SetFlowKey()

        private readonly CCriticalSection _valveLock  = new CCriticalSection();
        private readonly CCriticalSection _sensorLock = new CCriticalSection();

        // Online timer (shared – either endpoint activity resets it)
        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        // HTTP
        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Call from SIMPL+ Main() after all RegisterDelegate calls.
        /// valveUniqueId  : uniqueid of the On/Off output endpoint (e.g. "…:11-01")
        /// sensorUniqueId : uniqueid of the ZHAWater sensor endpoint (e.g. "…:11-01-0404")
        /// apiKey         : deCONZ REST API key
        /// </summary>
        public void Initialize(string valveUniqueId, string sensorUniqueId, string apiKey)
        {
            if (string.IsNullOrEmpty(valveUniqueId) || string.IsNullOrEmpty(sensorUniqueId))
            {
                CrestronConsole.PrintLine("[Valve] Initialize: missing uniqueId(s) – ignored");
                return;
            }

            _valveUid    = valveUniqueId.Trim().ToLowerInvariant();
            _sensorUid   = sensorUniqueId.Trim().ToLowerInvariant();
            _apiKey      = apiKey ?? "";
            _initialized = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            // Register both endpoints with the broker
            DeConzBroker.RegisterDevice(_valveUid,  OnValveWsUpdate);
            DeConzBroker.RegisterDevice(_sensorUid, OnSensorWsUpdate);

            // ScheduleGetAll fetches both valve and sensor endpoints on reconnect.
            // No separate callback needed for _sensorUid.
            DeConzBroker.RegisterConnectedCallback(_valveUid, ScheduleGetAll);

            DebugLog(string.Format(
                "[Valve] Initialized  valve-uid={0}  sensor-uid={1}", _valveUid, _sensorUid));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
        }

        public void SetOnlineTimeout(int seconds)
        {
            _onlineTimeoutMs = Math.Max(5, seconds) * 1000;
        }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }
        public void SetDebug(ushort enable) { _debugEnabled = (enable != 0); }

        /// <summary>
        /// Set the JSON key used to read the flow/volume value from the sensor state.
        /// Defaults to "flow". Call from SIMPL+ Main() before Initialize().
        /// </summary>
        public void SetFlowKey(string key)
        {
            if (!string.IsNullOrEmpty(key)) _flowKey = key.Trim();
        }

        public void Dispose()
        {
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            DeConzBroker.UnregisterDevice(_valveUid, OnValveWsUpdate);
            DeConzBroker.UnregisterDevice(_sensorUid, OnSensorWsUpdate);
            DeConzBroker.UnregisterConnectedCallback(_valveUid);
            StopOnlineTimer();
            _initialized = false;
        }

        // ── Commands ──────────────────────────────────────────────────────

        public void SetOn()  { SendValvePut("{\"on\":true}");  }
        public void SetOff() { SendValvePut("{\"on\":false}"); }

        public void GetState()
        {
            GetValveState();
            GetSensorState();
        }

        // ── HTTP helpers ──────────────────────────────────────────────────

        private void SendValvePut(string body)
        {
            if (!EnsureValveUrls()) return;
            string url;
            _valveLock.Enter();
            try { url = _valveStateUrl; }
            finally { _valveLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;

            if (_debugEnabled) DebugLog("[Valve] PUT " + body);
            _cmdLock.Enter();
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType   = RequestType.Put;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _http.DispatchAsync(req, OnValveHttpResponse);
            }
            catch (Exception ex) { Log("[Valve] PUT error: " + ex.Message); }
            finally { _cmdLock.Leave(); }
        }

        private void GetValveState()
        {
            if (!EnsureValveUrls()) return;
            string url;
            _valveLock.Enter();
            try { url = _valveBaseUrl; }
            finally { _valveLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;

            DebugLog("[Valve] GET valve " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnValveHttpResponse);
            }
            catch (Exception ex) { Log("[Valve] GET valve error: " + ex.Message); }
        }

        private void GetSensorState()
        {
            if (!EnsureSensorUrls()) return;
            string url;
            _sensorLock.Enter();
            try { url = _sensorBaseUrl; }
            finally { _sensorLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;

            DebugLog("[Valve] GET sensor " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnSensorHttpResponse);
            }
            catch (Exception ex) { Log("[Valve] GET sensor error: " + ex.Message); }
        }

        private void ScheduleGetAll()
        {
            _staticInfoSent = false;  // re-publish identity after reconnect
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Valve] GetState (valve+sensor) in {0} s", delayMs / 1000));
            CTimer t = null;
            t = new CTimer(_ => { GetState(); if (t != null) t.Dispose(); }, null, delayMs);
        }

        // ── HTTP response handlers ─────────────────────────────────────────

        private void OnValveHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                Log("[Valve] HTTP valve error: " + err); return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Valve] HTTP valve resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnValveRawJson, body);
            ParseValveState(body);
            ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnSensorHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                Log("[Valve] HTTP sensor error: " + err); return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Valve] HTTP sensor resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnSensorRawJson, body);
            ParseSensorState(body);
            ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── URL builders ──────────────────────────────────────────────────

        private void BuildValveUrls()
        {
            var ip = DeConzBroker.GatewayIP;
            string id, res;
            _valveLock.Enter();
            try { id = _valveId; res = _valveResource; }
            finally { _valveLock.Leave(); }
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(id)) return;

            _valveLock.Enter();
            try
            {
                // TOCTOU guard: a concurrent thread may have finished first
                if (!string.IsNullOrEmpty(_valveBaseUrl)) return;
                _valveBaseUrl  = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
                _valveStateUrl = _valveBaseUrl + "/state";
            }
            finally { _valveLock.Leave(); }

            DebugLog("[Valve] Valve URL: " + _valveBaseUrl);
            FireStr(OnValveIdFb, id);
        }

        private void BuildSensorUrls()
        {
            var ip = DeConzBroker.GatewayIP;
            string id, res;
            _sensorLock.Enter();
            try { id = _sensorId; res = _sensorResource; }
            finally { _sensorLock.Leave(); }
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(id)) return;

            _sensorLock.Enter();
            try
            {
                // TOCTOU guard: a concurrent thread may have finished first
                if (!string.IsNullOrEmpty(_sensorBaseUrl)) return;
                _sensorBaseUrl = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
            }
            finally { _sensorLock.Leave(); }

            DebugLog("[Valve] Sensor URL: " + _sensorBaseUrl);
            FireStr(OnSensorIdFb, id);
        }

        private bool EnsureValveUrls()
        {
            _valveLock.Enter();
            string snap; try { snap = _valveBaseUrl; } finally { _valveLock.Leave(); }
            if (string.IsNullOrEmpty(snap)) BuildValveUrls();
            _valveLock.Enter();
            try { return !string.IsNullOrEmpty(_valveBaseUrl); } finally { _valveLock.Leave(); }
        }

        private bool EnsureSensorUrls()
        {
            _sensorLock.Enter();
            string snap; try { snap = _sensorBaseUrl; } finally { _sensorLock.Leave(); }
            if (string.IsNullOrEmpty(snap)) BuildSensorUrls();
            _sensorLock.Enter();
            try { return !string.IsNullOrEmpty(_sensorBaseUrl); } finally { _sensorLock.Leave(); }
        }

        // ── WebSocket callbacks ───────────────────────────────────────────

        private void OnValveWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Valve] WS valve: " + json);
            // Resolve valve device ID on first frame
            bool need;
            _valveLock.Enter();
            try { need = string.IsNullOrEmpty(_valveId); }
            finally { _valveLock.Leave(); }

            if (need)
            {
                string id  = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string res = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    _valveLock.Enter();
                    try
                    {
                        _valveId       = id.Trim();
                        _valveResource = string.IsNullOrEmpty(res) ? "lights" : res.Trim();
                    }
                    finally { _valveLock.Leave(); }
                    DebugLog("[Valve] Valve resolved: id=" + _valveId + " res=" + _valveResource);
                    BuildValveUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnValveRawJson, json);
            ParseValveState(json);
            ParseDeviceInfo(json);
        }

        private void OnSensorWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Valve] WS sensor: " + json);
            // Resolve sensor device ID on first frame
            bool need;
            _sensorLock.Enter();
            try { need = string.IsNullOrEmpty(_sensorId); }
            finally { _sensorLock.Leave(); }

            if (need)
            {
                string id  = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string res = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    _sensorLock.Enter();
                    try
                    {
                        _sensorId       = id.Trim();
                        _sensorResource = string.IsNullOrEmpty(res) ? "sensors" : res.Trim();
                    }
                    finally { _sensorLock.Leave(); }
                    DebugLog("[Valve] Sensor resolved: id=" + _sensorId + " res=" + _sensorResource);
                    BuildSensorUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnSensorRawJson, json);
            ParseSensorState(json);
            ParseDeviceInfo(json);
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        private void ParseValveState(string json)
        {
            try
            {
                bool? on = DeConzJsonParser.ExtractBool(json, "on", 2);
                if (on.HasValue)
                {
                    Fire(OnValveOnFb,  on.Value ? (ushort)1 : (ushort)0);
                    Fire(OnValveOffFb, on.Value ? (ushort)0 : (ushort)1);
                }
            }
            catch (Exception ex)
            {
                Log("[Valve] ParseValveState error: " + ex.Message);
            }
        }

        private void ParseSensorState(string json)
        {
            try
            {
                bool? water = DeConzJsonParser.ExtractBool(json, "water", 2);
                if (water.HasValue)
                    Fire(OnWaterDetected, water.Value ? (ushort)1 : (ushort)0);

                int? batt = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (batt.HasValue)
                    Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, batt.Value)));

                bool? lowBat = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (lowBat.HasValue)
                    Fire(OnBatteryLow, lowBat.Value ? (ushort)1 : (ushort)0);

                // Flow value: try numeric first, fall back to string
                int? flowInt = DeConzJsonParser.ExtractInt(json, _flowKey, 2);
                if (flowInt.HasValue)
                    FireStr(OnFlowVolume, flowInt.Value.ToString());
                else
                {
                    string flowStr = DeConzJsonParser.ExtractString(json, _flowKey, 2);
                    if (flowStr != null) FireStr(OnFlowVolume, flowStr);
                }
            }
            catch (Exception ex)
            {
                Log("[Valve] ParseSensorState error: " + ex.Message);
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
                Log("[Valve] ParseDeviceInfo error: " + ex.Message);
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
            DebugLog("[Valve] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnValveOnFb, 0);
            Fire(OnValveOffFb, 0);
            Fire(OnWaterDetected, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            FireStr(OnFlowVolume, "");
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
                    DebugLog("[Valve] Online timeout");
                    FireOnline(0);
                }, null, _onlineTimeoutMs);
        }

        private void StopOnlineTimer()
        {
            if (_onlineTimer == null) return;
            _onlineTimer.Stop(); _onlineTimer = null;
        }

        private void FireOnline(ushort v) { Fire(OnOnline, v); }

        // ── Delegate fire helpers ─────────────────────────────────────────

        private static void Fire(ValveBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(ValveLevelDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void FireStr(ValveStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 65000);
            try { cb(new SimplSharpString(s)); } catch { }
        }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(ValveStringDelegate cb, string s)
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
