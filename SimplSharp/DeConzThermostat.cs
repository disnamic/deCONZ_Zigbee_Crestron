/*******************************************************************************
 * DeConzThermostat.cs  v2.16.4
 *
 * SIMPL# class – deCONZ Zigbee thermostat (ZHAThermostat).
 *
 * COMMAND path  – HTTP PUT to /api/<key>/sensors/<id>/config
 * FEEDBACK path – WebSocket events via DeConzBroker + HTTP GET
 *
 * Key architectural difference from other modules:
 *   Commands target /sensors/<id>/config  (not /lights/<id>/state)
 *   Feedback state comes from both config{} and state{} objects
 *
 * Temperature convention (deCONZ):
 *   All temperature values are integers in 1/100 °C.
 *   Examples: 2150 = 21.50°C   500 = 5.00°C   -50 = -0.50°C
 *
 * Temperature outputs (Option C):
 *   OnTemperatureFb       → raw 1/100°C integer (e.g. 2150)
 *   OnTemperatureStrFb    → formatted string    (e.g. "21.5")
 *   OnHeatSetpointFb      → raw 1/100°C integer
 *   OnHeatSetpointStrFb   → formatted string
 *   (same for cool setpoint)
 *
 * "on" disambiguation:
 *   config.on → thermostat enabled/disabled  (command + feedback)
 *   state.on  → currently heating            → OnHeatingFb
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void ThermostatBoolDelegate(ushort value);
    public delegate void ThermostatLevelDelegate(ushort value);
    public delegate void ThermostatStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzThermostat
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        // Online / battery
        public ThermostatBoolDelegate   OnOnline             { get; set; }
        public ThermostatBoolDelegate   OnLowBattery         { get; set; }
        public ThermostatLevelDelegate  OnBatteryLevel       { get; set; }
        public ThermostatLevelDelegate  OnVoltageFb          { get; set; }   // battery voltage in mV (ZHABattery, if available)

        // Battery endpoint raw JSON
        public ThermostatStringDelegate OnBatteryRawJson     { get; set; }

        // Thermostat on/off  (config.on)
        public ThermostatBoolDelegate   OnOnFb               { get; set; }
        public ThermostatBoolDelegate   OnOffFb              { get; set; }

        // Heating status  (state.on or state.valve > 0)
        public ThermostatBoolDelegate   OnHeatingFb          { get; set; }

        // Valve position  (state.valve, 0-100 %)
        public ThermostatLevelDelegate  OnValveFb            { get; set; }

        // Measured temperature (state.temperature)
        public ThermostatLevelDelegate  OnTemperatureFb      { get; set; }   // raw 1/100°C
        public ThermostatStringDelegate OnTemperatureStrFb   { get; set; }   // "21.5"

        // Heat setpoint (config.heatsetpoint)
        public ThermostatLevelDelegate  OnHeatSetpointFb     { get; set; }   // raw 1/100°C
        public ThermostatStringDelegate OnHeatSetpointStrFb  { get; set; }   // "21.5"

        // Cool setpoint (config.coolsetpoint)
        public ThermostatLevelDelegate  OnCoolSetpointFb     { get; set; }   // raw 1/100°C
        public ThermostatStringDelegate OnCoolSetpointStrFb  { get; set; }   // "21.5"

        // Mode (config.mode)
        public ThermostatStringDelegate OnModeFb             { get; set; }   // "heat"/"cool"/"auto"/"off"

        // Locked (config.locked)
        public ThermostatBoolDelegate   OnLockedFb           { get; set; }

        // Offset (config.offset, signed 1/100°C)
        // Delivered as ushort: offset + 1000 so -1000 → 0, 0 → 1000, +1000 → 2000
        // SIMPL+ can subtract 1000 to recover the signed value.
        public ThermostatLevelDelegate  OnOffsetFb           { get; set; }   // raw offset +1000 bias
        public ThermostatStringDelegate OnOffsetStrFb        { get; set; }   // e.g. "-1.0" or "+0.5"

        // Window open detection (config.windowopen_set)
        public ThermostatBoolDelegate   OnWindowOpenSetFb      { get; set; }
        // Window open currently detected by sensor (state.windowopen)
        public ThermostatBoolDelegate   OnWindowOpenDetectedFb { get; set; }
        // Schedule enabled (config.schedule_on)
        public ThermostatBoolDelegate   OnScheduleOnFb         { get; set; }
        // Valve override state (config.setvalve)
        public ThermostatBoolDelegate   OnSetValveFb           { get; set; }
        // Error code (state.errorcode)
        public ThermostatStringDelegate OnErrorCodeFb          { get; set; }
        // Display flipped 180° (config.displayflipped)
        public ThermostatBoolDelegate   OnDisplayFlippedFb     { get; set; }

        // Device info
        public ThermostatStringDelegate OnDeviceIdFb         { get; set; }
        public ThermostatStringDelegate OnLastSeenFb         { get; set; }
        public ThermostatStringDelegate OnLastAnnouncedFb    { get; set; }
        public ThermostatStringDelegate OnManufacturerFb     { get; set; }
        public ThermostatStringDelegate OnModelIdFb          { get; set; }
        public ThermostatStringDelegate OnNameFb             { get; set; }
        public ThermostatStringDelegate OnTypeFb             { get; set; }

        // Raw / debug
        public ThermostatStringDelegate OnRawJsonFb          { get; set; }
        public ThermostatStringDelegate OnDebugOut           { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey;

        // Thermostat endpoint
        private string _uniqueId;
        private string _deviceId;
        private string _resource;
        private string _baseUrl;
        private string _configUrl;

        // Battery endpoint (ZHABattery, optional second uniqueid)
        private string _batteryUid;
        private string _batteryId;
        private string _batteryResource;
        private string _batteryBaseUrl;
        private readonly CCriticalSection _batteryLock = new CCriticalSection();
        private bool   _hasBatteryEndpoint;   // PUT target – /sensors/<id>/config

        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        private readonly CCriticalSection _idLock  = new CCriticalSection();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        private readonly HttpClient _http = new HttpClient();

        // ── Public API ────────────────────────────────────────────────────

        /// <param name="batteryUniqueId">
        /// Optional uniqueid of the ZHABattery endpoint (e.g. "…:01-0001").
        /// Pass empty string or null if the device has no separate battery endpoint.
        /// </param>
        public void Initialize(string uniqueId, string batteryUniqueId, string apiKey)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                CrestronConsole.PrintLine("[Thermo] Initialize: empty uniqueId – ignored");
                return;
            }

            _uniqueId    = uniqueId.Trim().ToLowerInvariant();
            _apiKey      = apiKey ?? "";
            _initialized = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            // Thermostat endpoint
            DeConzBroker.RegisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.RegisterConnectedCallback(_uniqueId, ScheduleGetState);

            // Battery endpoint (optional)
            if (!string.IsNullOrEmpty(batteryUniqueId))
            {
                _batteryUid          = batteryUniqueId.Trim().ToLowerInvariant();
                _hasBatteryEndpoint  = true;
                DeConzBroker.RegisterDevice(_batteryUid, OnBatteryWsUpdate);
                DeConzBroker.RegisterConnectedCallback(_batteryUid, ScheduleGetBatteryState);
            }

            DebugLog(string.Format(
                "[Thermo] Initialized uid={0}  battery-uid={1}",
                _uniqueId,
                _hasBatteryEndpoint ? _batteryUid : "(none)"));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
        }

        public void SetOnlineTimeout(int seconds)
        {
            _onlineTimeoutMs = Math.Max(5, seconds) * 1000;
        }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }
        public void SetDebug(ushort enable) { _debugEnabled = (enable != 0); }

        public void Dispose()
        {
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
            _initialized = false;
        }

        // ── Commands (all PUT to /config) ─────────────────────────────────

        public void SetOn()     { SendConfigPut("{\"on\":true}");  }
        public void SetOff()    { SendConfigPut("{\"on\":false}"); }

        public void Lock()      { SendConfigPut("{\"locked\":true}");  }
        public void Unlock()    { SendConfigPut("{\"locked\":false}"); }

        public void SetWindowOpenDetection(ushort enable)
        {
            SendConfigPut("{\"windowopen_set\":" + (enable != 0 ? "true" : "false") + "}");
        }

        /// <summary>
        /// Set heat setpoint. value in 1/100°C (e.g. 2150 = 21.5°C).
        /// Typical range 500-3000 (5.0–30.0°C).
        /// </summary>
        public void SetHeatSetpoint(ushort value)
        {
            // deCONZ accepts 500–3500 (5.0°C–35.0°C). Guard against stray
            // SIMPL+ startup signals (AI = 0) or out-of-range values.
            if (value < 500 || value > 3500)
            {
                Log(string.Format("[Thermo:{0}] SetHeatSetpoint out of range ({1}) – ignored", _uniqueId, value));
                return;
            }
            SendConfigPut("{\"heatsetpoint\":" + value + "}");
        }

        /// <summary>Set cool setpoint. value in 1/100°C.</summary>
        public void SetCoolSetpoint(ushort value)
        {
            // deCONZ accepts 500–3500 (5.0°C–35.0°C).
            if (value < 500 || value > 3500)
            {
                Log(string.Format("[Thermo:{0}] SetCoolSetpoint out of range ({1}) – ignored", _uniqueId, value));
                return;
            }
            SendConfigPut("{\"coolsetpoint\":" + value + "}");
        }

        /// <summary>
        /// Set mode: 0=off, 1=auto, 2=heat, 3=cool.
        /// </summary>
        public void SetMode(ushort mode)
        {
            string[] modes = { "off", "auto", "heat", "cool" };
            if (mode >= modes.Length) return;
            SendConfigPut("{\"mode\":\"" + modes[mode] + "\"}");
        }

        /// <summary>
        /// Set temperature calibration offset.
        /// SIMPL+ passes (signed_offset + 1000) as ushort:
        ///   0 → -1000 (−10.0°C)   1000 → 0   1500 → +500 (+5.0°C)
        /// </summary>
        public void SetOffset(ushort biasedValue)
        {
            int signed = (int)biasedValue - 1000;
            SendConfigPut("{\"offset\":" + signed + "}");
        }

        /// <summary>Force-open or force-close the valve (config.setvalve).</summary>
        public void SetValve(ushort open)
        { SendConfigPut("{\"setvalve\":" + (open != 0 ? "true" : "false") + "}"); }

        /// <summary>Enable or disable the built-in schedule (config.schedule_on).</summary>
        public void SetScheduleOn(ushort enable)
        { SendConfigPut("{\"schedule_on\":" + (enable != 0 ? "true" : "false") + "}"); }

        /// <summary>Feed an external temperature in 1/100°C (config.externaltemperature).</summary>
        public void SetExternalTemperature(ushort value)
        { SendConfigPut("{\"externaltemperature\":" + value + "}"); }

        /// <summary>Flip the display 180° (config.displayflipped).</summary>
        public void SetDisplayFlipped(ushort flipped)
        { SendConfigPut("{\"displayflipped\":" + (flipped != 0 ? "true" : "false") + "}"); }

        public void GetState()
        {
            if (!EnsureUrls()) return;
            string url = BaseUrl();
            if (string.IsNullOrEmpty(url)) return;
            DebugLog("[Thermo:" + _uniqueId + "] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnHttpResponse);
            }
            catch (Exception ex) { Log("[Thermo:" + _uniqueId + "] GET error: " + ex.Message); }
        }

        /// <summary>HTTP GET the battery endpoint. Called automatically on connect.</summary>
        public void GetBatteryState()
        {
            if (!_hasBatteryEndpoint) return;
            if (!EnsureBatteryUrls()) return;
            string url;
            _batteryLock.Enter();
            try { url = _batteryBaseUrl; } finally { _batteryLock.Leave(); }
            if (string.IsNullOrEmpty(url)) return;

            DebugLog("[Thermo:" + _uniqueId + "] GET battery " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnBatteryHttpResponse);
            }
            catch (Exception ex)
            {
                Log("[Thermo:" + _uniqueId + "] GET battery error: " + ex.Message);
            }
        }

        // ── HTTP helpers ──────────────────────────────────────────────────

        private void SendConfigPut(string body)
        {
            if (!EnsureUrls()) return;
            string url = ConfigUrl();
            if (string.IsNullOrEmpty(url)) return;

            if (_debugEnabled) DebugLog("[Thermo:" + _uniqueId + "] PUT config " + body);
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
            catch (Exception ex) { Log("[Thermo:" + _uniqueId + "] PUT error: " + ex.Message); }
            finally { _cmdLock.Leave(); }
        }

        private void OnHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                Log("[Thermo:" + _uniqueId + "] HTTP error: " + err); return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Thermo:" + _uniqueId + "] HTTP resp: " + body);
            FireRawJson(body);
            ParseState(body);
            ParseConfig(body);
            ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnBatteryHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                Log("[Thermo:" + _uniqueId + "] HTTP battery error: " + err); return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Thermo:" + _uniqueId + "] HTTP battery resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, body);
            ParseBatteryState(body);
        }
            finally { if (resp != null) resp.Dispose(); }
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
                _baseUrl   = string.Format("http://{0}/api/{1}/{2}/{3}",
                                 ip, _apiKey, res, id);
                _configUrl = _baseUrl + "/config";
            }
            finally { _idLock.Leave(); }

            DebugLog("[Thermo:" + _uniqueId + "] URLs built: " + BaseUrl());
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
            _idLock.Enter(); try { return _baseUrl; } finally { _idLock.Leave(); }
        }

        private string ConfigUrl()
        {
            _idLock.Enter(); try { return _configUrl; } finally { _idLock.Leave(); }
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
            try
            {
                // TOCTOU guard: a concurrent thread may have finished first
                if (!string.IsNullOrEmpty(_batteryBaseUrl)) return;
                _batteryBaseUrl = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
            }
            finally { _batteryLock.Leave(); }

            DebugLog("[Thermo:" + _uniqueId + "] Battery URL: " + _batteryBaseUrl);
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

        private void ScheduleGetState()
        {
            _staticInfoSent = false;  // re-publish identity after reconnect
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Thermo:{0}] GetState in {1} s", _uniqueId, delayMs / 1000));
            CTimer t = null;
            t = new CTimer(_ => { GetState(); if (t != null) t.Dispose(); }, null, delayMs);
        }

        private void ScheduleGetBatteryState()
        {
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Thermo:{0}] GetBatteryState in {1} s", _uniqueId, delayMs / 1000));
            CTimer tb = null;
            tb = new CTimer(_ => { GetBatteryState(); if (tb != null) tb.Dispose(); }, null, delayMs);
        }

        // ── WebSocket callback ────────────────────────────────────────────

        private void OnWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Thermo:" + _uniqueId + "] WS: " + json);
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
                    DebugLog("[Thermo:" + _uniqueId + "] Resolved id=" + _deviceId
                        + " res=" + _resource);
                    BuildUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            FireRawJson(json);
            ParseState(json);
            ParseConfig(json);
            ParseDeviceInfo(json);
        }

        private void OnBatteryWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Thermo:" + _uniqueId + "] WS battery: " + json);
            // Resolve battery endpoint ID from first WS frame
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
                    DebugLog("[Thermo:" + _uniqueId + "] Battery resolved: id=" + _batteryId);
                    BuildBatteryUrls();
                }
            }

            // Battery WS events also keep the online timer alive
            FireOnline(1);
            RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, json);
            ParseBatteryState(json);
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        /// <summary>
        /// Extracts the substring of a named JSON block, e.g. "state":{…}.
        /// Used to isolate "state.on" from "config.on" since both sit at depth 2
        /// in the full document and the scanner would otherwise always hit "state.on"
        /// first (it appears earlier in deCONZ payloads).
        /// </summary>
        private static string ExtractBlock(string json, string blockKey)
        {
            int keyPos = json.IndexOf("\"" + blockKey + "\"",
                                      StringComparison.OrdinalIgnoreCase);
            if (keyPos < 0) return null;
            int brace = json.IndexOf('{', keyPos + blockKey.Length + 2);
            if (brace < 0) return null;

            // String-literal aware brace matching: braces inside JSON string values
            // (e.g. a device name "Room {1}") must not affect the depth counter.
            int depth = 0;
            bool inString = false;
            for (int i = brace; i < json.Length; i++)
            {
                char ch = json[i];

                if (ch == '"')
                {
                    // Count preceding backslashes to detect an escaped quote
                    int bs = 0, k = i - 1;
                    while (k >= 0 && json[k] == '\\') { bs++; k--; }
                    if (bs % 2 == 0) inString = !inString;
                    continue;
                }
                if (inString) continue;

                if      (ch == '{') depth++;
                else if (ch == '}') { if (--depth == 0) return json.Substring(brace, i - brace + 1); }
            }
            return null;
        }

        private static string FormatTemp(int hundredths)
        {
            return (hundredths / 100.0)
                .ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Converts a signed 1/100 °C value to a ushort bit pattern that SIMPL+
        /// can interpret as a signed analog. Negative values are sent as their
        /// 16-bit two's-complement (e.g. -250 → 65286 → read as -250 when the
        /// SIMPL signal is treated as signed). Clamped to -32768..32767.
        /// </summary>
        private static ushort ToSignedAnalog(int value)
        {
            int clamped = Math.Max(-32768, Math.Min(32767, value));
            return (ushort)(short)clamped;
        }

        private static string FormatOffset(int hundredths)
        {
            double val = hundredths / 100.0;
            string s   = val.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return val >= 0 ? "+" + s : s;
        }

        /// <summary>
        /// Parses state{} fields: temperature, valve position, currently-heating flag.
        /// state.on → OnHeatingFb  (currently heating, distinct from config.on)
        /// </summary>
        private void ParseState(string json)
        {
            try
            {
                string block = ExtractBlock(json, "state");
                if (block == null) return;

                // state.on = currently heating (NOT the same as config.on = thermostat enabled)
                bool? heating = DeConzJsonParser.ExtractBool(block, "on", 1);
                if (heating.HasValue) Fire(OnHeatingFb, heating.Value ? (ushort)1 : (ushort)0);

                int? temp = DeConzJsonParser.ExtractInt(block, "temperature", 1);
                if (temp.HasValue)
                {
                    // Measured temperature may be negative (frost sensors, outdoor
                    // probes). Sent as 16-bit two's-complement so SIMPL+ can read it
                    // as a signed analog (e.g. -250 → -2.5 °C). OnTemperatureStrFb
                    // always carries the correct signed string as well.
                    Fire(OnTemperatureFb, ToSignedAnalog(temp.Value));
                    FireStr(OnTemperatureStrFb, FormatTemp(temp.Value));
                }

                int? valve = DeConzJsonParser.ExtractInt(block, "valve", 1);
                if (valve.HasValue)
                {
                    Fire(OnValveFb, (ushort)Math.Max(0, Math.Min(100, valve.Value)));
                    // Valve > 0 also implies heating even if state.on is absent
                    if (!heating.HasValue && valve.Value > 0)
                        Fire(OnHeatingFb, 1);
                }

                // state.windowopen – sensor-detected open window (distinct from config.windowopen_set)
                bool? winDet = DeConzJsonParser.ExtractBool(block, "windowopen", 1);
                if (winDet.HasValue)
                    Fire(OnWindowOpenDetectedFb, winDet.Value ? (ushort)1 : (ushort)0);

                // state.errorcode
                string errCode = DeConzJsonParser.ExtractString(block, "errorcode", 1);
                if (errCode != null) FireStr(OnErrorCodeFb, errCode);
            }
            catch (Exception ex)
            {
                Log("[Thermo:" + _uniqueId + "] ParseState error: " + ex.Message);
            }
        }

        /// <summary>
        /// Parses config{} fields: thermostat on/off, setpoints, mode, locked, offset,
        /// window-open detection, battery (some devices report it in config).
        /// config.on → OnOnFb / OnOffFb  (thermostat enabled, distinct from state.on)
        /// </summary>
        private void ParseConfig(string json)
        {
            try
            {
                string block = ExtractBlock(json, "config");
                if (block == null) return;

                // config.on = thermostat enabled/disabled
                bool? on = DeConzJsonParser.ExtractBool(block, "on", 1);
                if (on.HasValue)
                {
                    Fire(OnOnFb,  on.Value ? (ushort)1 : (ushort)0);
                    Fire(OnOffFb, on.Value ? (ushort)0 : (ushort)1);
                }

                int? heat = DeConzJsonParser.ExtractInt(block, "heatsetpoint", 1);
                if (heat.HasValue)
                {
                    Fire(OnHeatSetpointFb, (ushort)heat.Value);
                    FireStr(OnHeatSetpointStrFb, FormatTemp(heat.Value));
                }

                int? cool = DeConzJsonParser.ExtractInt(block, "coolsetpoint", 1);
                if (cool.HasValue)
                {
                    Fire(OnCoolSetpointFb, (ushort)cool.Value);
                    FireStr(OnCoolSetpointStrFb, FormatTemp(cool.Value));
                }

                string mode = DeConzJsonParser.ExtractString(block, "mode", 1);
                if (mode != null) FireStr(OnModeFb, mode);

                bool? locked = DeConzJsonParser.ExtractBool(block, "locked", 1);
                if (locked.HasValue) Fire(OnLockedFb, locked.Value ? (ushort)1 : (ushort)0);

                int? offset = DeConzJsonParser.ExtractInt(block, "offset", 1);
                if (offset.HasValue)
                {
                    // Bias +1000 so SIMPL+ can pass unsigned: 0→-1000, 1000→0, 1500→+500
                    int biased = Math.Max(0, Math.Min(65535, offset.Value + 1000));
                    Fire(OnOffsetFb, (ushort)biased);
                    FireStr(OnOffsetStrFb, FormatOffset(offset.Value));
                }

                bool? winOpen = DeConzJsonParser.ExtractBool(block, "windowopen_set", 1);
                if (winOpen.HasValue) Fire(OnWindowOpenSetFb, winOpen.Value ? (ushort)1 : (ushort)0);

                // schedule_on (config)
                bool? schedOn = DeConzJsonParser.ExtractBool(block, "schedule_on", 1);
                if (schedOn.HasValue) Fire(OnScheduleOnFb, schedOn.Value ? (ushort)1 : (ushort)0);

                // setvalve (config) – valve override
                bool? setValve = DeConzJsonParser.ExtractBool(block, "setvalve", 1);
                if (setValve.HasValue) Fire(OnSetValveFb, setValve.Value ? (ushort)1 : (ushort)0);

                // displayflipped (config)
                bool? dispFlip = DeConzJsonParser.ExtractBool(block, "displayflipped", 1);
                if (dispFlip.HasValue) Fire(OnDisplayFlippedFb, dispFlip.Value ? (ushort)1 : (ushort)0);

                // Some devices report battery in config
                int? batt = DeConzJsonParser.ExtractInt(block, "battery", 1);
                if (batt.HasValue)
                    Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, batt.Value)));

                bool? lowBat = DeConzJsonParser.ExtractBool(block, "lowbattery", 1);
                if (lowBat.HasValue) Fire(OnLowBattery, lowBat.Value ? (ushort)1 : (ushort)0);
            }
            catch (Exception ex)
            {
                Log("[Thermo:" + _uniqueId + "] ParseConfig error: " + ex.Message);
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
                Log("[Thermo:" + _uniqueId + "] ParseDeviceInfo error: " + ex.Message);
            }
        }

        /// <summary>
        /// Parses state/config from the optional ZHABattery sensor endpoint.
        /// Reports battery percentage, low-battery flag, and voltage (mV).
        /// </summary>
        private void ParseBatteryState(string json)
        {
            try
            {
                int? batt = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (batt.HasValue)
                    Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, batt.Value)));

                bool? lowBat = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (lowBat.HasValue) Fire(OnLowBattery, lowBat.Value ? (ushort)1 : (ushort)0);

                // Voltage in mV (e.g. 3100 = 3.1 V) – present on some ZHABattery sensors
                int? voltage = DeConzJsonParser.ExtractInt(json, "voltage", 2);
                if (voltage.HasValue)
                    Fire(OnVoltageFb, (ushort)Math.Max(0, Math.Min(65535, voltage.Value)));
            }
            catch (Exception ex)
            {
                Log("[Thermo:" + _uniqueId + "] ParseBatteryState error: " + ex.Message);
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
            DebugLog("[Thermo] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnLowBattery, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnVoltageFb, 0);
            Fire(OnOnFb, 0);
            Fire(OnOffFb, 0);
            Fire(OnHeatingFb, 0);
            Fire(OnValveFb, 0);
            Fire(OnTemperatureFb, 0);
            FireStr(OnTemperatureStrFb, "");
            Fire(OnWindowOpenDetectedFb, 0);
            FireStr(OnErrorCodeFb, "");
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
                    DebugLog("[Thermo:" + _uniqueId + "] Online timeout");
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
        private static void FireChunked(ThermostatStringDelegate cb, string s)
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

        private static void Fire(ThermostatBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(ThermostatLevelDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void FireStr(ThermostatStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 250);
            try { cb(new SimplSharpString(s)); } catch { }
        }

        private void FireRawJson(string json)
        {
            if (!_rawJsonEnabled) return;
            var cb = OnRawJsonFb;
            if (cb == null) return;
            if (json.Length > 65000) json = json.Substring(0, 65000);
            try { cb(new SimplSharpString(json)); } catch { }
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
