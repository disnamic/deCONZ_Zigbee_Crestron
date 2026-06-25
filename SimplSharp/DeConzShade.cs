/*******************************************************************************
 * DeConzShade.cs  v2.15
 *
 * SIMPL# class – deCONZ Zigbee window covering (shade / blind / drape).
 *
 * COMMAND path  – HTTP PUT to deCONZ REST API
 * FEEDBACK path – WebSocket events via DeConzBroker
 *
 * deCONZ window covering model:
 *   {"open": true}   → fully open  (move up)
 *   {"open": false}  → fully closed (move down)
 *   {"stop": true}   → stop motion
 *   {"lift": 0-100}  → lift position  (0 = open, 100 = closed)
 *   {"tilt": 0-100}  → tilt/lamella angle (venetian blinds)
 *
 * Feedback (state):
 *   lift, tilt, open, lowbattery, reachable
 * Feedback (config):
 *   battery
 *
 * Lift convention (deCONZ): 0 = fully open, 100 = fully closed.
 * Optional Invert flag mirrors this for SIMPL (0 = closed, 100 = open).
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void ShadeBoolDelegate(ushort value);
    public delegate void ShadeLevelDelegate(ushort value);
    public delegate void ShadeStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzShade
    {
        private bool _staticInfoSent;

        // ── Delegates wired by SIMPL+ wrapper ────────────────────────────

        // Online / battery
        public ShadeBoolDelegate   OnOnline          { get; set; }   // 1 = WS active
        public ShadeBoolDelegate   OnLowBattery      { get; set; }   // 1 = low battery
        public ShadeLevelDelegate  OnBatteryLevel    { get; set; }   // 0-100 %
        public ShadeLevelDelegate  OnVoltageFb       { get; set; }   // mV (ZHABattery)

        // Battery endpoint raw JSON
        public ShadeStringDelegate OnBatteryRawJson  { get; set; }

        // Position
        public ShadeLevelDelegate  OnLiftFb          { get; set; }   // 0-100 (per Invert flag)
        public ShadeLevelDelegate  OnTiltFb          { get; set; }   // 0-100 (per Invert flag)

        // Derived status (Group B)
        public ShadeBoolDelegate   OnOpenFb          { get; set; }   // 1 = fully open
        public ShadeBoolDelegate   OnClosedFb        { get; set; }   // 1 = fully closed
        public ShadeBoolDelegate   OnMovingFb        { get; set; }   // 1 = moving (target != current)

        // Device info (from HTTP GET / WS attr events)
        public ShadeStringDelegate OnDeviceIdFb      { get; set; }
        public ShadeStringDelegate OnLastSeenFb      { get; set; }
        public ShadeStringDelegate OnLastAnnouncedFb { get; set; }
        public ShadeStringDelegate OnManufacturerFb  { get; set; }
        public ShadeStringDelegate OnModelIdFb       { get; set; }
        public ShadeStringDelegate OnNameFb          { get; set; }
        public ShadeStringDelegate OnTypeFb          { get; set; }

        // Raw / debug
        public ShadeStringDelegate OnRawJsonFb       { get; set; }
        public ShadeStringDelegate OnDebugOut        { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }

        // Shade endpoint
        private string _uniqueId;
        private string _deviceId;
        private string _resource;
        private string _baseUrl;
        private string _stateUrl;

        // Battery endpoint (ZHABattery, optional)
        private string _batteryUid;
        private string _batteryId;
        private string _batteryResource;
        private string _batteryBaseUrl;
        private readonly CCriticalSection _batteryLock = new CCriticalSection();
        private bool   _hasBatteryEndpoint;

        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;
        private bool   _invertLift;

        private int    _lastLift = -1;       // last reported raw lift (0-100), -1 = unknown
        private int    _targetLift = -1;     // last commanded lift, -1 = none
        private int    _openThreshold  = 1;  // lift <= this → open_fb = 1  (default 1)
        private int    _closedThreshold = 99; // lift >= this → closed_fb = 1 (default 99)
        private CTimer _moveTimer;           // clears "moving" after a short window

        private readonly CCriticalSection _idLock = new CCriticalSection();

        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        private const int MoveClearMs = 3000;   // assume motion done this long after a command

        // ── Public API ────────────────────────────────────────────────────

        /// <param name="batteryUniqueId">
        /// Optional uniqueid of a separate ZHABattery sensor endpoint.
        /// Pass empty string or null when battery is reported on the shade endpoint itself.
        /// </param>
        public void Initialize(string uniqueId, string batteryUniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                CrestronConsole.PrintLine("[Shade] Initialize: empty uniqueId – ignored");
                return;
            }

            _uniqueId    = uniqueId.Trim().ToLowerInvariant();
            _initialized = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            // Shade endpoint
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

            DebugLog(string.Format(
                "[Shade] Initialized uid={0}  battery-uid={1}",
                _uniqueId, _hasBatteryEndpoint ? _batteryUid : "(none)"));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
            _permRun = true;
            ArmPermTimer();
        }

        public void SetOnlineTimeout(int seconds)
        {
            _onlineTimeoutMs = Math.Max(5, seconds) * 1000;
        }

        /// <summary>0 = deCONZ native (0 open, 100 closed); 1 = inverted (0 closed, 100 open).</summary>
        public void SetInvertLift(ushort invert) { _invertLift = (invert != 0); }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }
        public void SetOpenThreshold(ushort value)
        { _openThreshold  = Math.Max(0, Math.Min(100, (int)value)); }

        public void SetClosedThreshold(ushort value)
        { _closedThreshold = Math.Max(0, Math.Min(100, (int)value)); }

        public void SetDebug(ushort enable) { _debugEnabled = (enable != 0); }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            DeConzBroker.UnregisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.UnregisterConnectedCallback(_uniqueId);
            if (_hasBatteryEndpoint)
            {
                DeConzBroker.UnregisterDevice(_batteryUid, OnBatteryWsUpdate);
                DeConzBroker.UnregisterConnectedCallback(_batteryUid);
            }
            StopOnlineTimer();
            if (_moveTimer != null) { _moveTimer.Stop(); _moveTimer = null; }
            _initialized = false;
        }

        // ── Commands ──────────────────────────────────────────────────────

        public void MoveUp()   { SetMoving(); SendStatePut("{\"open\":true}");  }
        public void MoveDown() { SetMoving(); SendStatePut("{\"open\":false}"); }
        public void Stop()     { SendStatePut("{\"stop\":true}"); ClearMovingSoon(); }

        /// <param name="percent">0-100 in the SIMPL convention (respects Invert flag).</param>
        public void SetLift(ushort percent)
        {
            int raw = NormaliseToDeconz(Math.Min((ushort)100, percent));
            _targetLift = raw;
            SetMoving();
            SendStatePut("{\"lift\":" + raw + "}");
        }

        /// <param name="percent">0-100 in the SIMPL convention (respects Invert flag).</param>
        public void SetTilt(ushort percent)
        {
            int raw = NormaliseToDeconz(Math.Min((ushort)100, percent));
            SendStatePut("{\"tilt\":" + raw + "}");
        }

        public void GetState()
        {
            if (!EnsureUrls()) return;
            string url = BaseUrl();
            if (string.IsNullOrEmpty(url)) return;
            _staticInfoSent = false;   // re-send static device info on manual refresh
            DebugLog("[Shade:" + _uniqueId + "] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnHttpResponse);
            }
            catch (Exception ex) { Log("[Shade:" + _uniqueId + "] GET error: " + ex.Message); }
        }

        // ── Invert helper ─────────────────────────────────────────────────

        private int NormaliseToDeconz(int simplPercent)
        {
            return _invertLift ? (100 - simplPercent) : simplPercent;
        }

        private ushort NormaliseFromDeconz(int rawPercent)
        {
            int v = _invertLift ? (100 - rawPercent) : rawPercent;
            return (ushort)Math.Max(0, Math.Min(100, v));
        }

        // ── Moving status (Group B) ───────────────────────────────────────

        private void SetMoving()
        {
            Fire(OnMovingFb, 1);
            ClearMovingSoon();
        }

        private void ClearMovingSoon()
        {
            if (_moveTimer != null) _moveTimer.Reset(MoveClearMs);
            else _moveTimer = new CTimer(_ => Fire(OnMovingFb, 0), null, MoveClearMs);
        }

        // ── URL builder ───────────────────────────────────────────────────

        private void BuildUrls()
        {
            var ip = DeConzBroker.GatewayIP;
            string id, res;
            _idLock.Enter();
            try { id = _deviceId; res = _resource; }
            finally { _idLock.Leave(); }

            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(id)) return;

            _idLock.Enter();
            try
            {
                // TOCTOU guard: a concurrent thread may have finished first
                if (!string.IsNullOrEmpty(_baseUrl)) return;
                _baseUrl  = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
                _stateUrl = _baseUrl + "/state";
            }
            finally { _idLock.Leave(); }

            DebugLog("[Shade:" + _uniqueId + "] URLs built: " + BaseUrl());
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

        private string BaseUrl()
        {
            _idLock.Enter();
            try { return _baseUrl; } finally { _idLock.Leave(); }
        }

        private string StateUrl()
        {
            _idLock.Enter();
            try { return _stateUrl; } finally { _idLock.Leave(); }
        }

        public void GetBatteryState()
        {
            if (!_hasBatteryEndpoint) return;
            if (!EnsureBatteryUrls()) return;
            string url;
            _batteryLock.Enter();
            try { url = _batteryBaseUrl; } finally { _batteryLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;
            DebugLog("[Shade:" + _uniqueId + "] GET battery " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnBatteryHttpResponse);
            }
            catch (Exception ex) { Log("[Shade:" + _uniqueId + "] GET battery error: " + ex.Message); }
        }

        private void OnBatteryHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Shade:" + _uniqueId + "] HTTP battery error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Shade:" + _uniqueId + "] HTTP battery resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, body);
            ParseBatteryState(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── HTTP ──────────────────────────────────────────────────────────

        private void SendStatePut(string body)
        {
            if (!EnsureUrls()) return;
            string url = StateUrl();
            if (string.IsNullOrEmpty(url)) return;

            if (_debugEnabled) DebugLog("[Shade:" + _uniqueId + "] PUT " + body);
            _cmdLock.Enter();
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType   = RequestType.Put;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _http.DispatchAsync(req, OnHttpResponse);
            }
            catch (Exception ex) { Log("[Shade:" + _uniqueId + "] PUT error: " + ex.Message); }
            finally { _cmdLock.Leave(); }
        }

        private void OnHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                Log("[Shade:" + _uniqueId + "] HTTP error: " + err);
                return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Shade:" + _uniqueId + "] HTTP resp: " + body);
            FireRawJson(body);
            ParseState(body);
            ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
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
            DebugLog("[Shade:" + _uniqueId + "] Battery URL: " + _batteryBaseUrl);
            ScheduleGetBatteryState();
        }

        private bool EnsureBatteryUrls()
        {
            _batteryLock.Enter();
            string snap; try { snap = _batteryBaseUrl; } finally { _batteryLock.Leave(); }
            if (string.IsNullOrEmpty(snap)) BuildBatteryUrls();
            _batteryLock.Enter();
            try { return !string.IsNullOrEmpty(_batteryBaseUrl); } finally { _batteryLock.Leave(); }
        }

        private void ScheduleGetBatteryState()
        {
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Shade:{0}] GetBatteryState in {1} s", _uniqueId, delayMs / 1000));
            CTimer tb = null;
            tb = new CTimer(_ => { GetBatteryState(); if (tb != null) tb.Dispose(); }, null, delayMs);
        }

        // ── WebSocket callback ────────────────────────────────────────────

        private void OnWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Shade:" + _uniqueId + "] WS: " + json);
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
                        _resource = string.IsNullOrEmpty(res) ? "lights" : res.Trim();
                    }
                    finally { _idLock.Leave(); }
                    DebugLog("[Shade:" + _uniqueId + "] Resolved id=" + _deviceId + " res=" + _resource);
                    BuildUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            FireRawJson(json);
            ParseState(json);
            ParseDeviceInfo(json);
        }

        private void OnBatteryWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Shade:" + _uniqueId + "] WS battery: " + json);
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
                    DebugLog("[Shade:" + _uniqueId + "] Battery resolved: id=" + _batteryId);
                    BuildBatteryUrls();
                }
            }
            FireOnline(1);
            RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, json);
            ParseBatteryState(json);
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        private void ParseState(string json)
        {
            try
            {
                bool? reach = DeConzJsonParser.ExtractBool(json, "reachable", 2);
                if (reach.HasValue && reach.Value) { FireOnline(1); RestartOnlineTimer(); }

                int? lift = DeConzJsonParser.ExtractInt(json, "lift", 2);
                if (lift.HasValue)
                {
                    _lastLift = lift.Value;
                    Fire(OnLiftFb, NormaliseFromDeconz(lift.Value));
                    Fire(OnOpenFb,   (ushort)(lift.Value <= _openThreshold   ? 1 : 0));
                    Fire(OnClosedFb, (ushort)(lift.Value >= _closedThreshold ? 1 : 0));

                    // Clear moving indicator once the reported position matches the target
                    if (_targetLift >= 0 && _lastLift == _targetLift)
                    {
                        _targetLift = -1;
                        Fire(OnMovingFb, 0);
                        if (_moveTimer != null) { _moveTimer.Stop(); _moveTimer = null; }
                    }
                }

                int? tilt = DeConzJsonParser.ExtractInt(json, "tilt", 2);
                if (tilt.HasValue)
                    Fire(OnTiltFb, NormaliseFromDeconz(tilt.Value));

                // open bool – only fire when lift is absent to avoid contradiction
                bool? openVal = DeConzJsonParser.ExtractBool(json, "open", 2);
                if (openVal.HasValue && !lift.HasValue)
                {
                    Fire(OnOpenFb,   (ushort)(openVal.Value ? 1 : 0));
                    Fire(OnClosedFb, (ushort)(openVal.Value ? 0 : 1));
                }

                bool? lowBat = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (lowBat.HasValue) Fire(OnLowBattery, lowBat.Value ? (ushort)1 : (ushort)0);

                int? batt = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (batt.HasValue)
                    Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, batt.Value)));
            }
            catch (Exception ex)
            {
                Log("[Shade:" + _uniqueId + "] ParseState error: " + ex.Message);
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
                Log("[Shade:" + _uniqueId + "] ParseDeviceInfo error: " + ex.Message);
            }
        }

        // ── Online timer ──────────────────────────────────────────────────

        private void ParseBatteryState(string json)
        {
            try
            {
                int? batt = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (batt.HasValue)
                    Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, batt.Value)));
                bool? lowBat = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (lowBat.HasValue)
                    Fire(OnLowBattery, lowBat.Value ? (ushort)1 : (ushort)0);
                int? voltage = DeConzJsonParser.ExtractInt(json, "voltage", 2);
                if (voltage.HasValue)
                    Fire(OnVoltageFb, (ushort)Math.Max(0, Math.Min(65535, voltage.Value)));
            }
            catch (Exception ex)
            { Log("[Shade:" + _uniqueId + "] ParseBatteryState error: " + ex.Message); }
        }

        private void ScheduleGetState()
        {
            _staticInfoSent = false;  // re-publish identity after reconnect
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Shade:{0}] GetState in {1} s", _uniqueId, delayMs / 1000));
            CTimer t = null;
            t = new CTimer(_ => { GetState(); if (t != null) t.Dispose(); }, null, delayMs);
        }

        // Reset value/status outputs when no update received for over an hour.
        // Static device info (manufacturer/model/name/type/swversion/id),
        // lastseen/lastannounced, capabilities and raw JSON are preserved.
        private void CheckStale()
        {
            if (_staleResetDone) return;
            if ((DateTime.UtcNow - _lastActivityUtc).TotalMilliseconds < 3600000) return;
            _staleResetDone = true;
            DebugLog("[Shade] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnLowBattery, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnVoltageFb, 0);
            Fire(OnLiftFb, 0);
            Fire(OnTiltFb, 0);
            Fire(OnOpenFb, 0);
            Fire(OnClosedFb, 0);
            Fire(OnMovingFb, 0);
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
                    DebugLog("[Shade:" + _uniqueId + "] Online timeout");
                    FireOnline(0);
                }, null, _onlineTimeoutMs);
        }

        private void StopOnlineTimer()
        {
            if (_onlineTimer == null) return;
            _onlineTimer.Stop(); _onlineTimer = null;
        }

        private void FireOnline(ushort v) { Fire(OnOnline, v); }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(ShadeStringDelegate cb, string s)
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

        private static void Fire(ShadeBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(ShadeLevelDelegate cb, ushort v)
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
                var cb = snap[i].Key as ShadeStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
        }

        private void FireStr(ShadeStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 65000);
            if (cb != OnDebugOut)
            {
                _strLock.Enter();
                try { _lastStr[cb] = s; }
                finally { _strLock.Leave(); }
            }
            try { cb(new SimplSharpString(s)); } catch { }
        }

        private void FireRawJson(string json)
        { if (_rawJsonEnabled) FireChunked(OnRawJsonFb, json); }

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
