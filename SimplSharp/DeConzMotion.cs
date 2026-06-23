/*******************************************************************************
 * DeConzMotion.cs
 *
 * SIMPL# class – deCONZ Zigbee motion / presence + light level sensor.
 *
 * Supports two sensor endpoints:
 *   ZHAPresence   – state.presence, state.tampered
 *   ZHALightLevel – state.lux, state.lightlevel, state.dark, state.daylight
 *
 * Both UniqueIDs are optional. Presence is the primary endpoint.
 * State is fetched via HTTP GET on WS connect/reconnect (random 1-15 s).
 * No background poll – WS events are reliable for presence sensors.
 *
 * Configurable via HTTP PUT /sensors/<id>/config:
 *   Presence  : duration    (0-65535 s, time until presence resets to false)
 *   LightLevel: tholddark   (0-65534, lightlevel at which dark becomes true)
 *               tholdoffset (1-65534, offset above tholddark for daylight)
 *
 * Optional Battery_UniqueID for a dedicated ZHABattery endpoint.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    public delegate void MotionBoolDelegate(ushort value);
    public delegate void MotionLevelDelegate(ushort value);
    public delegate void MotionStringDelegate(SimplSharpString value);

    public class DeConzMotion
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        // Shared
        public MotionBoolDelegate   OnOnline          { get; set; }
        public MotionBoolDelegate   OnBatteryLow      { get; set; }
        public MotionLevelDelegate  OnBatteryLevel    { get; set; }
        public MotionLevelDelegate  OnVoltageFb       { get; set; }

        // Presence (ZHAPresence)
        public MotionBoolDelegate   OnPresenceFb      { get; set; }
        public MotionBoolDelegate   OnTamperedFb      { get; set; }
        public MotionBoolDelegate   OnSensorOnFb      { get; set; }   // config.on
        public MotionLevelDelegate  OnSensitivityFb   { get; set; }   // config.sensitivity
        public MotionLevelDelegate  OnSensitivityMaxFb { get; set; }  // config.sensitivitymax (readonly)
        public MotionLevelDelegate  OnDelayFb         { get; set; }   // config.delay (ms)
        public MotionBoolDelegate   OnLedIndicationFb { get; set; }   // config.ledindication
        public MotionBoolDelegate   OnUserTestFb      { get; set; }   // config.usertest

        // Light level (ZHALightLevel)
        public MotionLevelDelegate  OnLuxFb           { get; set; }
        public MotionLevelDelegate  OnLightlevelFb    { get; set; }
        public MotionBoolDelegate   OnDarkFb          { get; set; }
        public MotionBoolDelegate   OnDaylightFb      { get; set; }

        // Device info
        public MotionStringDelegate OnLastSeenFb      { get; set; }
        public MotionStringDelegate OnManufacturerFb  { get; set; }
        public MotionStringDelegate OnModelIdFb       { get; set; }
        public MotionStringDelegate OnNameFb          { get; set; }

        // Raw JSON
        public MotionStringDelegate OnPresenceRawJson { get; set; }
        public MotionStringDelegate OnLightRawJson    { get; set; }
        public MotionStringDelegate OnBatteryRawJson  { get; set; }
        public MotionStringDelegate OnDebugOut        { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey;
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        // Presence endpoint
        private string _presenceUid;
        private string _presenceId;
        private string _presenceRes;
        private string _presenceUrl;
        private readonly CCriticalSection _presLock = new CCriticalSection();

        // LightLevel endpoint
        private string _lightUid;
        private string _lightId;
        private string _lightRes;
        private string _lightUrl;
        private readonly CCriticalSection _lightLock = new CCriticalSection();

        // Battery endpoint
        private string _batteryUid;
        private string _batteryId;
        private string _batteryRes;
        private string _batteryUrl;
        private readonly CCriticalSection _battLock = new CCriticalSection();
        private bool   _hasBattery;

        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize(string presenceUid, string lightUid,
                               string batteryUid, string apiKey)
        {
            _apiKey      = apiKey ?? "";
            _initialized = true;
            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            if (!string.IsNullOrEmpty(presenceUid))
            {
                _presenceUid = presenceUid.Trim().ToLowerInvariant();
                DeConzBroker.RegisterDevice(_presenceUid, OnPresenceWs);
                DeConzBroker.RegisterConnectedCallback(_presenceUid, ScheduleGetPresence);
            }
            if (!string.IsNullOrEmpty(lightUid))
            {
                _lightUid = lightUid.Trim().ToLowerInvariant();
                DeConzBroker.RegisterDevice(_lightUid, OnLightWs);
                DeConzBroker.RegisterConnectedCallback(_lightUid, ScheduleGetLight);
            }
            if (!string.IsNullOrEmpty(batteryUid))
            {
                _batteryUid = batteryUid.Trim().ToLowerInvariant();
                _hasBattery = true;
                DeConzBroker.RegisterDevice(_batteryUid, OnBatteryWs);
                DeConzBroker.RegisterConnectedCallback(_batteryUid, ScheduleGetBattery);
            }
            DebugLog(string.Format("[Motion] Initialized pres={0} light={1} batt={2}",
                _presenceUid ?? "(none)", _lightUid ?? "(none)",
                _hasBattery ? _batteryUid : "(none)"));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
        }

        public void SetOnlineTimeout(int seconds) { _onlineTimeoutMs = Math.Max(5, seconds) * 1000; }
        public void SetDebug(ushort e)            { _debugEnabled   = (e != 0); }
        public void SetRawJsonEnabled(ushort e)   { _rawJsonEnabled = (e != 0); }

        /// <summary>Set presence duration (seconds until presence resets to false).</summary>
        public void SetDuration(ushort seconds)
        {
            SendConfig(_presenceUid, _presLock, ref _presenceId, ref _presenceRes,
                       "{\"duration\":" + seconds + "}");
        }

        /// <summary>Set lightlevel threshold for dark flag.</summary>
        public void SetTholdDark(ushort value)
        {
            SendConfig(_lightUid, _lightLock, ref _lightId, ref _lightRes,
                       "{\"tholddark\":" + value + "}");
        }

        /// <summary>Set lightlevel offset above tholddark for daylight flag.</summary>
        public void SetTholdOffset(ushort value)
        {
            SendConfig(_lightUid, _lightLock, ref _lightId, ref _lightRes,
                       "{\"tholdoffset\":" + value + "}");
        }

        /// <summary>Enable or disable the presence sensor (config.on).</summary>
        public void SetSensorOn(ushort enable)
        { SendConfig(_presenceUid, _presLock, ref _presenceId, ref _presenceRes,
                     "{\"on\":" + (enable != 0 ? "true" : "false") + "}"); }

        /// <summary>Set sensitivity 0..sensitivitymax (config.sensitivity).</summary>
        public void SetSensitivity(ushort value)
        { SendConfig(_presenceUid, _presLock, ref _presenceId, ref _presenceRes,
                     "{\"sensitivity\":" + value + "}"); }

        /// <summary>Set delay in ms before presence resets to false (config.delay, Philips).</summary>
        public void SetDelay(ushort ms)
        { SendConfig(_presenceUid, _presLock, ref _presenceId, ref _presenceRes,
                     "{\"delay\":" + ms + "}"); }

        /// <summary>Enable or disable LED on sensor (config.ledindication).</summary>
        public void SetLedIndication(ushort enable)
        { SendConfig(_presenceUid, _presLock, ref _presenceId, ref _presenceRes,
                     "{\"ledindication\":" + (enable != 0 ? "true" : "false") + "}"); }

        /// <summary>Enable usertest mode – increased sensitivity (config.usertest).</summary>
        public void SetUserTest(ushort enable)
        { SendConfig(_presenceUid, _presLock, ref _presenceId, ref _presenceRes,
                     "{\"usertest\":" + (enable != 0 ? "true" : "false") + "}"); }

        public void GetState()
        {
            if (!string.IsNullOrEmpty(_presenceUid)) FetchHttp(_presenceUid, _presLock,
                ref _presenceId, ref _presenceRes, ref _presenceUrl, OnPresenceHttpResp);
            if (!string.IsNullOrEmpty(_lightUid)) FetchHttp(_lightUid, _lightLock,
                ref _lightId, ref _lightRes, ref _lightUrl, OnLightHttpResp);
            if (_hasBattery) FetchHttp(_batteryUid, _battLock,
                ref _batteryId, ref _batteryRes, ref _batteryUrl, OnBatteryHttpResp);
        }

        public void Dispose()
        {
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            if (!string.IsNullOrEmpty(_presenceUid)) { DeConzBroker.UnregisterDevice(_presenceUid, OnPresenceWs); DeConzBroker.UnregisterConnectedCallback(_presenceUid); }
            if (!string.IsNullOrEmpty(_lightUid))    { DeConzBroker.UnregisterDevice(_lightUid, OnLightWs);    DeConzBroker.UnregisterConnectedCallback(_lightUid); }
            if (_hasBattery)                          { DeConzBroker.UnregisterDevice(_batteryUid, OnBatteryWs); DeConzBroker.UnregisterConnectedCallback(_batteryUid); }
            StopOnlineTimer();
            _initialized = false;
        }

        // ── Scheduling ────────────────────────────────────────────────────

        private void ScheduleGetPresence() { ScheduleFetch(() => FetchHttp(_presenceUid, _presLock, ref _presenceId, ref _presenceRes, ref _presenceUrl, OnPresenceHttpResp), "presence"); }
        private void ScheduleGetLight()    { ScheduleFetch(() => FetchHttp(_lightUid, _lightLock, ref _lightId, ref _lightRes, ref _lightUrl, OnLightHttpResp), "light"); }
        private void ScheduleGetBattery()  { ScheduleFetch(() => FetchHttp(_batteryUid, _battLock, ref _batteryId, ref _batteryRes, ref _batteryUrl, OnBatteryHttpResp), "battery"); }

        private void ScheduleFetch(Action action, string label)
        {
            int d = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Motion] GetState/{0} in {1} s", label, d / 1000));
            CTimer t = null;
            t = new CTimer(_ => { action(); if (t != null) t.Dispose(); }, null, d);
        }

        // ── HTTP ──────────────────────────────────────────────────────────

        private void FetchHttp(string uid, CCriticalSection lk,
                               ref string id, ref string res, ref string url,
                               HTTPClientResponseCallback cb)
        {
            string u = BuildOrGetUrl(uid, lk, ref id, ref res, ref url);
            if (string.IsNullOrEmpty(u)) return;
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(u);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, cb); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Motion] GET error: " + ex.Message); }
        }

        private void SendConfig(string uid, CCriticalSection lk,
                                ref string id, ref string res, string body)
        {
            if (string.IsNullOrEmpty(uid)) return;
            // Need id to build config URL; try to resolve from stored id
            lk.Enter(); string devId = id; string devRes = res; lk.Leave();
            if (string.IsNullOrEmpty(devId)) { Log("[Motion] Config: id not yet resolved"); return; }
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip)) return;
            string u = string.Format("http://{0}/api/{1}/{2}/{3}/config", ip, _apiKey, devRes, devId);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(u);
                req.RequestType   = RequestType.Put;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnConfigResp); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Motion] Config error: " + ex.Message); }
        }

        private void OnConfigResp(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) Log("[Motion] Config error: " + err);
            else DebugLog("[Motion] Config resp: " + resp.ContentString);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnPresenceHttpResp(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Motion] GET presence error: " + err); return; }
            var body = resp.ContentString; if (string.IsNullOrEmpty(body)) return;
            if (_rawJsonEnabled) FireChunked(OnPresenceRawJson, body);
            ParsePresence(body); ParseBattery(body); ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnLightHttpResp(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Motion] GET light error: " + err); return; }
            var body = resp.ContentString; if (string.IsNullOrEmpty(body)) return;
            if (_rawJsonEnabled) FireChunked(OnLightRawJson, body);
            ParseLight(body); ParseBattery(body); ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnBatteryHttpResp(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Motion] GET battery error: " + err); return; }
            var body = resp.ContentString; if (string.IsNullOrEmpty(body)) return;
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, body);
            ParseBattery(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── URL helpers ───────────────────────────────────────────────────

        private string BuildOrGetUrl(string uid, CCriticalSection lk,
                                     ref string id, ref string res, ref string url)
        {
            lk.Enter(); string u = url; lk.Leave();
            return string.IsNullOrEmpty(u) ? null : u;
        }

        private void ResolveAndBuild(string json, CCriticalSection lk,
                                     ref string id, ref string res, ref string url,
                                     Action scheduleGet)
        {
            bool need; lk.Enter(); need = string.IsNullOrEmpty(id); lk.Leave();
            if (!need) return;
            string newId  = DeConzJsonParser.ExtractTopLevelString(json, "id");
            string newRes = DeConzJsonParser.ExtractTopLevelString(json, "r");
            if (string.IsNullOrEmpty(newId)) return;
            var ip = DeConzBroker.GatewayIP;
            lk.Enter();
            id  = newId.Trim();
            res = string.IsNullOrEmpty(newRes) ? "sensors" : newRes.Trim();
            url = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
            lk.Leave();
            scheduleGet();
        }

        // ── WS callbacks ──────────────────────────────────────────────────

        private void OnPresenceWs(string json)
        {
            if (_debugEnabled) DebugLog("[Motion] WS presence: " + json);
            ResolveAndBuild(json, _presLock, ref _presenceId, ref _presenceRes, ref _presenceUrl, ScheduleGetPresence);
            FireOnline(1); RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnPresenceRawJson, json);
            ParsePresence(json); ParseBattery(json); ParseDeviceInfo(json);
        }

        private void OnLightWs(string json)
        {
            if (_debugEnabled) DebugLog("[Motion] WS light: " + json);
            ResolveAndBuild(json, _lightLock, ref _lightId, ref _lightRes, ref _lightUrl, ScheduleGetLight);
            FireOnline(1); RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnLightRawJson, json);
            ParseLight(json); ParseBattery(json); ParseDeviceInfo(json);
        }

        private void OnBatteryWs(string json)
        {
            if (_debugEnabled) DebugLog("[Motion] WS battery: " + json);
            ResolveAndBuild(json, _battLock, ref _batteryId, ref _batteryRes, ref _batteryUrl, ScheduleGetBattery);
            FireOnline(1); RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, json);
            ParseBattery(json);
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        private void ParsePresence(string json)
        {
            try
            {
                bool? pres = DeConzJsonParser.ExtractBool(json, "presence", 2);
                if (pres.HasValue) Fire(OnPresenceFb, pres.Value ? (ushort)1 : (ushort)0);
                bool? tamp = DeConzJsonParser.ExtractBool(json, "tampered", 2);
                if (tamp.HasValue) Fire(OnTamperedFb, tamp.Value ? (ushort)1 : (ushort)0);

                // config.on – sensor enabled
                bool? on = DeConzJsonParser.ExtractBool(json, "on", 2);
                if (on.HasValue) Fire(OnSensorOnFb, on.Value ? (ushort)1 : (ushort)0);

                // config.sensitivity / sensitivitymax
                int? sens = DeConzJsonParser.ExtractInt(json, "sensitivity", 2);
                if (sens.HasValue) Fire(OnSensitivityFb, (ushort)Math.Max(0, Math.Min(255, sens.Value)));
                int? sensMax = DeConzJsonParser.ExtractInt(json, "sensitivitymax", 2);
                if (sensMax.HasValue) Fire(OnSensitivityMaxFb, (ushort)Math.Max(0, Math.Min(255, sensMax.Value)));

                // config.delay (Philips: ms until presence resets)
                int? delay = DeConzJsonParser.ExtractInt(json, "delay", 2);
                if (delay.HasValue) Fire(OnDelayFb, (ushort)Math.Max(0, Math.Min(65535, delay.Value)));

                // config.ledindication
                bool? led = DeConzJsonParser.ExtractBool(json, "ledindication", 2);
                if (led.HasValue) Fire(OnLedIndicationFb, led.Value ? (ushort)1 : (ushort)0);

                // config.usertest
                bool? ut = DeConzJsonParser.ExtractBool(json, "usertest", 2);
                if (ut.HasValue) Fire(OnUserTestFb, ut.Value ? (ushort)1 : (ushort)0);
            }
            catch (Exception ex) { Log("[Motion] ParsePresence error: " + ex.Message); }
        }

        private void ParseLight(string json)
        {
            try
            {
                int? lux = DeConzJsonParser.ExtractInt(json, "lux", 2);
                if (lux.HasValue) Fire(OnLuxFb, (ushort)Math.Max(0, Math.Min(65535, lux.Value)));
                int? ll = DeConzJsonParser.ExtractInt(json, "lightlevel", 2);
                if (ll.HasValue) Fire(OnLightlevelFb, (ushort)Math.Max(0, Math.Min(65535, ll.Value)));
                bool? dark = DeConzJsonParser.ExtractBool(json, "dark", 2);
                if (dark.HasValue) Fire(OnDarkFb, dark.Value ? (ushort)1 : (ushort)0);
                bool? day = DeConzJsonParser.ExtractBool(json, "daylight", 2);
                if (day.HasValue) Fire(OnDaylightFb, day.Value ? (ushort)1 : (ushort)0);
            }
            catch (Exception ex) { Log("[Motion] ParseLight error: " + ex.Message); }
        }

        private void ParseBattery(string json)
        {
            try
            {
                int? b = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (b.HasValue) Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, b.Value)));
                bool? low = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (low.HasValue) Fire(OnBatteryLow, low.Value ? (ushort)1 : (ushort)0);
                int? v = DeConzJsonParser.ExtractInt(json, "voltage", 2);
                if (v.HasValue) Fire(OnVoltageFb, (ushort)Math.Max(0, Math.Min(65535, v.Value)));
            }
            catch (Exception ex) { Log("[Motion] ParseBattery error: " + ex.Message); }
        }

        private void ParseDeviceInfo(string json)
        {
            try
            {
                string vDyn = DeConzJsonParser.ExtractTopLevelString(json, "lastseen");
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
                _staticInfoSent = true;
            }
            catch { }
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
            DebugLog("[Motion] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnVoltageFb, 0);
            Fire(OnPresenceFb, 0);
            Fire(OnTamperedFb, 0);
            Fire(OnLuxFb, 0);
            Fire(OnLightlevelFb, 0);
            Fire(OnDarkFb, 0);
            Fire(OnDaylightFb, 0);
        }

        private void RestartOnlineTimer()
        {
            _lastActivityUtc = DateTime.UtcNow;
            _staleResetDone  = false;
            if (_onlineTimer != null) _onlineTimer.Reset(_onlineTimeoutMs);
            else _onlineTimer = new CTimer(_ => { DebugLog("[Motion] Online timeout"); FireOnline(0); _staticInfoSent = false; }, null, _onlineTimeoutMs);
        }
        private void StopOnlineTimer() { if (_onlineTimer == null) return; _onlineTimer.Stop(); _onlineTimer = null; }
        private void FireOnline(ushort v) { Fire(OnOnline, v); }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(MotionStringDelegate cb, string s)
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

        private static void Fire(MotionBoolDelegate cb, ushort v)  { if (cb != null) try { cb(v); } catch { } }
        private static void Fire(MotionLevelDelegate cb, ushort v) { if (cb != null) try { cb(v); } catch { } }
        private static void FireStr(MotionStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 65000);
            try { cb(new SimplSharpString(s)); } catch { }
        }

        private void Log(string m) { CrestronConsole.PrintLine(m); }
        private void DebugLog(string m)
        {
            if (!_debugEnabled) return;
            Log(m);
            FireStr(OnDebugOut, m.Length > 250 ? m.Substring(0, 250) + "…" : m);
        }
    }
}
