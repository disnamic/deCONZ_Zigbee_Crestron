/*******************************************************************************
 * DeConzLightWs.cs  v2.9
 *
 * SIMPL# class – deCONZ light: HTTP commands, WebSocket feedback.
 *
 * Hybrid approach (Option A):
 *   COMMANDS  → HTTP PUT/GET to the deCONZ REST API (same as DeConzLight)
 *   FEEDBACK  → WebSocket events via DeConzBroker (same as DeConzLight)
 *
 * Advantage over DeConzLight (HTTP-only):
 *   - No Device_ID parameter – numeric ID is resolved automatically from
 *     the first incoming WebSocket event ("id" field).
 *   - No Type_Of_Device parameter – resource type ("lights"/"sensors") is
 *     resolved from the same event ("r" field).
 *   → Only Device_UniqueID and API_Key need to be configured.
 *
 * URL is built as soon as both GatewayIP (from broker) and device ID
 * (from first WS event) are known. Commands issued before that point
 * are deferred with a log message.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates (SIMPL+ RegisterDelegate targets) ─────────────────────────
    // Previously defined in DeConzLight.cs (HTTP-only module, removed in v2.11).
    // Now live here as DeConzLightWs is their only consumer.
    public delegate void LightBoolDelegate(ushort value);
    public delegate void LightLevelDelegate(ushort value);
    public delegate void LightStringDelegate(SimplSharpString value);

    public class DeConzLightWs
    {
        private bool _staticInfoSent;

        // ── Delegates wired by SIMPL+ wrapper ────────────────────────────

        public LightBoolDelegate   OnOnline       { get; set; }
        public LightBoolDelegate   OnOnFb         { get; set; }
        public LightBoolDelegate   OnOffFb        { get; set; }
        public LightLevelDelegate  OnBrightnessFb { get; set; }
        public LightLevelDelegate  OnColortempFb  { get; set; }
        public LightStringDelegate OnXFb          { get; set; }
        public LightStringDelegate OnYFb          { get; set; }
        public LightLevelDelegate  OnRFb          { get; set; }
        public LightLevelDelegate  OnGFb          { get; set; }
        public LightLevelDelegate  OnBFb          { get; set; }
        public LightLevelDelegate  OnHueFb        { get; set; }
        public LightLevelDelegate  OnSatFb        { get; set; }
        public LightStringDelegate OnColormodeFb  { get; set; }
        public LightStringDelegate OnEffectFb     { get; set; }
        public LightStringDelegate OnDeviceIdFb   { get; set; }
        public LightStringDelegate OnRawJsonFb    { get; set; }
        public LightStringDelegate OnDebugOut     { get; set; }

        // ── Device info delegates (from HTTP GET response) ────────────────
        public LightBoolDelegate   OnHasColor       { get; set; }   // hascolor
        public LightStringDelegate OnGroupsFb       { get; set; }   // "0,9,17,23"
        public LightStringDelegate OnLastAnnouncedFb{ get; set; }   // lastannounced ISO string
        public LightStringDelegate OnLastSeenFb     { get; set; }   // lastseen ISO string
        public LightStringDelegate OnManufacturerFb { get; set; }   // manufacturername
        public LightStringDelegate OnModelIdFb      { get; set; }   // modelid
        public LightStringDelegate OnNameFb         { get; set; }   // name
        public LightStringDelegate OnSwVersionFb    { get; set; }   // swversion
        public LightStringDelegate OnTypeFb         { get; set; }   // type

        // ── Private state ─────────────────────────────────────────────────
        private string _uniqueId;
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }

        // Resolved from first WS event
        private string _deviceId;
        private string _resource;
        private string _baseUrl;
        private string _stateUrl;

        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;
        private bool   _lastOn;

        // Guards _deviceId / _resource / _baseUrl / _stateUrl:
        // written on broker worker thread, read on SIMPL+ command thread.
        private readonly CCriticalSection _idLock  = new CCriticalSection();

        // Online timer
        private int    _onlineTimeoutMs = 120000;   // default 2 min
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        // Fix: single reusable brightness CTimer instead of spawning a new one on
        // every WS update. Rapid state changes (e.g. scene sweeps) previously
        // created an unbounded number of parallel timers that could not be cancelled.
        private CTimer _brightnessTimer;
        private readonly CCriticalSection _briLock = new CCriticalSection();

        // HTTP client
        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Call from SIMPL+ Main() after all RegisterDelegate calls.
        /// Device ID and resource type are resolved automatically from the first
        /// incoming WebSocket event – no manual configuration required.
        /// </summary>
        public void Initialize(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                CrestronConsole.PrintLine("[LightWS] Initialize: empty uniqueId – ignored");
                return;
            }

            _uniqueId    = uniqueId.Trim().ToLowerInvariant();
            _initialized = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            DeConzBroker.RegisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.RegisterConnectedCallback(_uniqueId, ScheduleGetState);

            DebugLog(
                "[LightWS] Initialized uid=" + _uniqueId +
                " (device ID + resource resolved from first WS event)");

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
            _permRun = true;
            ArmPermTimer();
        }

        /// <summary>Online-timeout in seconds (call before Initialize). Default 300 s.</summary>
        public void SetOnlineTimeout(int seconds)
        {
            _onlineTimeoutMs = Math.Max(5, seconds) * 1000;
        }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }
        public void SetDebug(ushort enable) { _debugEnabled = (enable != 0); }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer.Dispose(); _staleTimer = null; }
            if (!_initialized) return;
            DeConzBroker.UnregisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.UnregisterConnectedCallback(_uniqueId);
            StopOnlineTimer();
            _briLock.Enter();
            try
            {
                if (_brightnessTimer != null)
                {
                    _brightnessTimer.Stop();
                    _brightnessTimer.Dispose();
                    _brightnessTimer = null;
                }
            }
            finally { _briLock.Leave(); }
            _initialized = false;
        }

        // ── Commands (HTTP PUT) ───────────────────────────────────────────

        public void SetOn()  { SendStatePut("{\"on\":true}"); }
        public void SetOff() { SendStatePut("{\"on\":false}"); }

        public void SetBrightness(ushort bri)
        {
            bri = Math.Min((ushort)254, bri);
            SendStatePut("{\"on\":true,\"bri\":" + bri + "}");
        }

        public void SetColortemp(ushort kelvin)
        {
            if (kelvin == 0) return;
            int mired = Math.Max(153, Math.Min(500, 1000000 / kelvin));
            SendStatePut("{\"on\":true,\"ct\":" + mired + "}");
        }

        public void SetXY(string xStr, string yStr)
        {
            if (string.IsNullOrEmpty(xStr) || string.IsNullOrEmpty(yStr)) return;
            SendStatePut("{\"on\":true,\"xy\":[" + xStr.Trim() + "," + yStr.Trim() + "]}");
        }

        public void SetRGB(ushort r, ushort g, ushort b)
        {
            double x, y;
            RgbToXy(r, g, b, out x, out y);
            SendStatePut("{\"on\":true,\"xy\":["
                + x.ToString("F4").Replace(",", ".") + ","
                + y.ToString("F4").Replace(",", ".") + "]}");
        }

        public void SetHue(ushort hue)
        {
            SendStatePut("{\"on\":true,\"hue\":" + hue + "}");
        }

        public void SetSat(ushort sat)
        {
            sat = Math.Min((ushort)254, sat);
            SendStatePut("{\"on\":true,\"sat\":" + sat + "}");
        }

        public void SetHueSat(ushort hue, ushort sat)
        {
            sat = Math.Min((ushort)254, sat);
            SendStatePut("{\"on\":true,\"hue\":" + hue + ",\"sat\":" + sat + "}");
        }

        public void SetEffect(string effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return;
            SendStatePut("{\"effect\":\"" + effectName.Trim().ToLower() + "\"}");
        }

        /// <summary>HTTP GET full device state → fires all feedback delegates.</summary>
        public void GetState()
        {
            if (!EnsureUrls()) return;
            string url = BaseUrl();
            if (string.IsNullOrEmpty(url)) return;
            _staticInfoSent = false;   // re-send static device info on manual refresh
            DebugLog("[LightWS:" + _uniqueId + "] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _http.DispatchAsync(req, OnHttpResponse);
            }
            catch (Exception ex)
            {
                Log("[LightWS:" + _uniqueId + "] GET error: " + ex.Message);
            }
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
                _baseUrl  = string.Format("http://{0}/api/{1}/{2}/{3}",
                                ip, _apiKey, res, id);
                _stateUrl = _baseUrl + "/state";
            }
            finally { _idLock.Leave(); }

            DebugLog("[LightWS:" + _uniqueId + "] URLs built: " + BaseUrl());
            FireStr(OnDeviceIdFb, id);
            ScheduleGetState();   // initial state fetch after first ID resolution
        }

        private bool EnsureUrls()
        {
            string snap;
            _idLock.Enter();
            try { snap = _baseUrl; }
            finally { _idLock.Leave(); }

            if (string.IsNullOrEmpty(snap)) BuildUrls();

            _idLock.Enter();
            try { return !string.IsNullOrEmpty(_baseUrl); }
            finally { _idLock.Leave(); }
        }

        private string BaseUrl()
        {
            _idLock.Enter();
            try { return _baseUrl; }
            finally { _idLock.Leave(); }
        }

        private string StateUrl()
        {
            _idLock.Enter();
            try { return _stateUrl; }
            finally { _idLock.Leave(); }
        }

        // ── HTTP helpers ──────────────────────────────────────────────────

        private void SendStatePut(string body)
        {
            if (!EnsureUrls()) return;
            string url = StateUrl();
            if (string.IsNullOrEmpty(url)) return;

            if (_debugEnabled) DebugLog("[LightWS:" + _uniqueId + "] PUT " + body);
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
            catch (Exception ex) { Log("[LightWS:" + _uniqueId + "] PUT error: " + ex.Message); }
            finally { _cmdLock.Leave(); }
        }

        private void OnHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                Log("[LightWS:" + _uniqueId + "] HTTP error: " + err);
                return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[LightWS:" + _uniqueId + "] HTTP resp: " + body);
            FireRawJson(body);
            ParseAndFire(body);
            ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── WebSocket feedback callback ───────────────────────────────────

        private void OnWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[LightWS:" + _uniqueId + "] WS: " + json);
            // Resolve device ID + resource from first frame (depth-aware, top-level only)
            bool needResolve;
            _idLock.Enter();
            try { needResolve = string.IsNullOrEmpty(_deviceId); }
            finally { _idLock.Leave(); }

            if (needResolve)
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

                    DebugLog("[LightWS:" + _uniqueId + "] Resolved id=" + _deviceId
                        + "  resource=" + _resource);

                    BuildUrls();
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            FireRawJson(json);
            if (DeConzJsonParser.HasStateOrConfig(json)) ParseAndFire(json);
            // deCONZ also pushes "attr" events over WS carrying lastseen,
            // lastannounced, name, swversion etc. Parse them here too so these
            // feedbacks are not limited to the HTTP GET response.
            ParseDeviceInfo(json);
        }

        // ── JSON parser ───────────────────────────────────────────────────

        /// <summary>
        /// Parses device-level fields from the HTTP GET response (top-level and config).
        /// These fields are only present in GET responses, not in WS event frames.
        /// </summary>
        private void ParseDeviceInfo(string json)
        {
            try
            {
                // lastseen / lastannounced are timestamps that genuinely change.
                string vDyn;
                vDyn = DeConzJsonParser.ExtractString(json, "lastannounced", 1);
                if (vDyn != null) FireStr(OnLastAnnouncedFb, vDyn);
                vDyn = DeConzJsonParser.ExtractString(json, "lastseen", 1);
                if (vDyn != null) FireStr(OnLastSeenFb, vDyn);

                // hascolor and groups can change (group membership), keep every call.
                bool? hasColor = DeConzJsonParser.ExtractBool(json, "hascolor", 1);
                if (hasColor.HasValue) Fire(OnHasColor, hasColor.Value ? (ushort)1 : (ushort)0);
                string groups = DeConzJsonParser.ExtractGroupsList(json);
                if (groups != null) FireStr(OnGroupsFb, groups);

                // Static identity fields — parse once.
                if (_staticInfoSent) return;
                string v;
                v = DeConzJsonParser.ExtractString(json, "manufacturername", 1);
                if (v != null) FireStr(OnManufacturerFb, v);
                v = DeConzJsonParser.ExtractString(json, "modelid", 1);
                if (v != null) FireStr(OnModelIdFb, v);
                v = DeConzJsonParser.ExtractString(json, "name", 1);
                if (v != null) FireStr(OnNameFb, v);
                v = DeConzJsonParser.ExtractString(json, "swversion", 1);
                if (v != null) FireStr(OnSwVersionFb, v);
                v = DeConzJsonParser.ExtractString(json, "type", 1);
                if (v != null) FireStr(OnTypeFb, v);
                _staticInfoSent = true;
            }
            catch (Exception ex)
            {
                Log("[LightWS:" + _uniqueId + "] ParseDeviceInfo error: " + ex.Message);
            }
        }

        private void ParseAndFire(string json)
        {
            try
            {
                // State fields live inside the "state" object (depth 2) in both
                // WS event frames and HTTP GET responses. Pinning the depth keeps
                // e.g. a top-level "type" from being read as a state value.

                bool? on = DeConzJsonParser.ExtractBool(json, "on", 2);
                if (on.HasValue)
                {
                    _lastOn = on.Value;
                    Fire(OnOnFb,  (ushort)(on.Value ? 1 : 0));
                    Fire(OnOffFb, (ushort)(on.Value ? 0 : 1));
                }

                bool? reach = DeConzJsonParser.ExtractBool(json, "reachable", 2);
                if (reach.HasValue && reach.Value) { FireOnline(1); RestartOnlineTimer(); }

                int? bri = DeConzJsonParser.ExtractInt(json, "bri", 2);
                if (bri.HasValue)
                {
                    // Use the on-state from THIS event if available; fall back to
                    // _lastOn only when the event does not include an "on" field.
                    // This prevents stale _lastOn from gating the brightness to 0
                    // when a bri-only WS event arrives (e.g. after SetBrightness).
                    bool isOn = on.HasValue ? on.Value : _lastOn;
                    ushort feedback = isOn ? (ushort)Math.Min(254, bri.Value) : (ushort)0;

                    // Replace the timer entirely so the new bv+isOn are always used.
                    // (Timer.Reset() keeps the old lambda with old captured values.)
                    _briLock.Enter();
                    try
                    {
                        if (_brightnessTimer != null) { _brightnessTimer.Stop(); _brightnessTimer.Dispose(); _brightnessTimer = null; }
                        _brightnessTimer = new CTimer(_ => Fire(OnBrightnessFb, feedback),
                                                     null, 600);
                    }
                    finally { _briLock.Leave(); }
                }

                int? ct = DeConzJsonParser.ExtractInt(json, "ct", 2);
                if (ct.HasValue && ct.Value > 0)
                    Fire(OnColortempFb, (ushort)Math.Min(65535, 1000000 / ct.Value));

                double xyX, xyY;
                if (DeConzJsonParser.ExtractXY(json, out xyX, out xyY))
                {
                    FireStr(OnXFb, FormatXY(xyX));
                    FireStr(OnYFb, FormatXY(xyY));
                    ushort rr, gg, bb;
                    XyToRgb(xyX, xyY, out rr, out gg, out bb);
                    Fire(OnRFb, rr); Fire(OnGFb, gg); Fire(OnBFb, bb);
                }

                int? hue = DeConzJsonParser.ExtractInt(json, "hue", 2);
                if (hue.HasValue) Fire(OnHueFb, (ushort)Math.Min(65535, hue.Value));

                int? sat = DeConzJsonParser.ExtractInt(json, "sat", 2);
                if (sat.HasValue) Fire(OnSatFb, (ushort)Math.Min(254, sat.Value));

                string cm = DeConzJsonParser.ExtractString(json, "colormode", 2);
                if (cm != null) FireStr(OnColormodeFb, cm);

                string ef = DeConzJsonParser.ExtractString(json, "effect", 2);
                if (ef != null) FireStr(OnEffectFb, ef);
            }
            catch (Exception ex)
            {
                Log("[LightWS:" + _uniqueId + "] ParseAndFire error: " + ex.Message);
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
            DebugLog("[Light] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnOnFb, 0);
            Fire(OnOffFb, 0);
            Fire(OnBrightnessFb, 0);
            Fire(OnColortempFb, 0);
            FireStr(OnXFb, "");
            FireStr(OnYFb, "");
            Fire(OnRFb, 0);
            Fire(OnGFb, 0);
            Fire(OnBFb, 0);
            Fire(OnHueFb, 0);
            Fire(OnSatFb, 0);
            FireStr(OnColormodeFb, "");
            FireStr(OnEffectFb, "");
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
                    DebugLog("[LightWS:" + _uniqueId + "] Online timeout");
                    FireOnline(0);
                }, null, _onlineTimeoutMs);
        }

        private void ScheduleGetState()
        {
            _staticInfoSent = false;  // re-publish identity after reconnect
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[LightWS:{0}] GetState in {1} s", _uniqueId, delayMs / 1000));
            CTimer t = null;
            t = new CTimer(_ => { GetState(); if (t != null) t.Dispose(); }, null, delayMs);
        }

        private void StopOnlineTimer()
        {
            if (_onlineTimer == null) return;
            _onlineTimer.Stop(); _onlineTimer.Dispose(); _onlineTimer = null;
        }

        private void FireOnline(ushort v) { Fire(OnOnline, v); }

        // ── Color math ────────────────────────────────────────────────────

        private static void RgbToXy(ushort r, ushort g, ushort b,
                                     out double x, out double y)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            rd = rd > 0.04045 ? Math.Pow((rd + 0.055) / 1.055, 2.4) : rd / 12.92;
            gd = gd > 0.04045 ? Math.Pow((gd + 0.055) / 1.055, 2.4) : gd / 12.92;
            bd = bd > 0.04045 ? Math.Pow((bd + 0.055) / 1.055, 2.4) : bd / 12.92;
            double X = rd*0.664511 + gd*0.154324 + bd*0.162028;
            double Y = rd*0.283881 + gd*0.668433 + bd*0.047685;
            double Z = rd*0.000088 + gd*0.072310 + bd*0.986039;
            double s = X + Y + Z;
            if (s == 0.0) { x = 0.0; y = 0.0; return; }
            x = X / s; y = Y / s;
        }

        private static void XyToRgb(double x, double y,
                                     out ushort r, out ushort g, out ushort b)
        {
            if (y == 0.0) { r = g = b = 0; return; }
            double Y = 1.0, X = (Y / y) * x, Z = (Y / y) * (1.0 - x - y);
            double rd =  X*1.656492 - Y*0.354851 - Z*0.255038;
            double gd = -X*0.707196 + Y*1.655397 + Z*0.036152;
            double bd =  X*0.051713 - Y*0.121364 + Z*1.011530;
            rd = Math.Max(0.0, rd); gd = Math.Max(0.0, gd); bd = Math.Max(0.0, bd);
            double mx = Math.Max(rd, Math.Max(gd, bd));
            if (mx > 1.0) { rd /= mx; gd /= mx; bd /= mx; }
            rd = rd <= 0.0031308 ? 12.92*rd : 1.055*Math.Pow(rd, 1.0/2.4) - 0.055;
            gd = gd <= 0.0031308 ? 12.92*gd : 1.055*Math.Pow(gd, 1.0/2.4) - 0.055;
            bd = bd <= 0.0031308 ? 12.92*bd : 1.055*Math.Pow(bd, 1.0/2.4) - 0.055;
            r = (ushort)Math.Round(Math.Max(0, Math.Min(1.0, rd)) * 255);
            g = (ushort)Math.Round(Math.Max(0, Math.Min(1.0, gd)) * 255);
            b = (ushort)Math.Round(Math.Max(0, Math.Min(1.0, bd)) * 255);
        }

        private static string FormatXY(double v)
        {
            return v.ToString("F4").Replace(",", ".");
        }

        // ── Delegate fire helpers ─────────────────────────────────────────

        private static void Fire(LightBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(LightLevelDelegate cb, ushort v)
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
                var cb = snap[i].Key as LightStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
        }

        private void FireStr(LightStringDelegate cb, string s)
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

        private void FireRawJson(string json)
        {
            if (!_rawJsonEnabled) return;
            if (json.Length > 65000) json = json.Substring(0, 65000);
            FireChunked(OnRawJsonFb, json);
        }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(LightStringDelegate cb, string s)
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
            FireStr(OnDebugOut, msg.Length > 250 ? msg.Substring(0, 250) + "…" : msg);
        }
    }
}
