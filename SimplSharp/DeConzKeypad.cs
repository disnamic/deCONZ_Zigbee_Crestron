/*******************************************************************************
 * DeConzKeypad.cs
 *
 * SIMPL# class – deCONZ Zigbee keypad / remote control (1-10 buttons).
 *
 * Registers with DeConzBroker for WebSocket events.
 * Also polls GetState() via HTTP on:
 *   - First WS connect / reconnect (random 1-15 s delay, via broker callback)
 *   - Every 30 minutes (background polling timer)
 *   - SIMPL+ pulse on Get_State input
 *
 * Button events, battery, online detection unchanged from previous version.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    public static class ButtonEvent
    {
        public const int InitialPress = 0;
        public const int Hold         = 1;
        public const int ShortRelease = 2;
        public const int LongRelease  = 3;
        public const int DoublePress  = 4;
        public const int TreblePress  = 5;
    }

    public delegate void KeypadBoolDelegate(ushort value);
    public delegate void KeypadLevelDelegate(ushort value);
    public delegate void KeypadButtonDelegate(ushort buttonNumber);
    public delegate void KeypadRawJsonDelegate(SimplSharpString json);
    public delegate void KeypadDebugDelegate(SimplSharpString msg);
    public delegate void KeypadStringDelegate(SimplSharpString value);

    public class DeConzKeypad
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        public KeypadBoolDelegate   OnOnline       { get; set; }
        public KeypadBoolDelegate   OnBatteryLow   { get; set; }
        public KeypadLevelDelegate  OnBatteryLevel { get; set; }
        public KeypadButtonDelegate OnShortRelease { get; set; }
        public KeypadButtonDelegate OnHold         { get; set; }
        public KeypadButtonDelegate OnLongRelease  { get; set; }
        public KeypadButtonDelegate OnDoublePress  { get; set; }
        public KeypadButtonDelegate OnTreblePress  { get; set; }

        // Rotary (ZHARelativeRotary) — optional second endpoint
        public KeypadLevelDelegate  OnRotationFb         { get; set; }   // expectedrotation (signed)
        public KeypadBoolDelegate   OnRotateCw           { get; set; }   // expectedrotation > 0
        public KeypadBoolDelegate   OnRotateCcw          { get; set; }   // expectedrotation < 0
        public KeypadLevelDelegate  OnRotaryEventFb      { get; set; }   // rotaryevent
        public KeypadLevelDelegate  OnRotationDurationFb { get; set; }   // expectedeventduration (ms)
        public KeypadLevelDelegate  OnLevelFb            { get; set; }   // accumulated level 0-65535 (0-100%)
        public KeypadRawJsonDelegate OnRawJson     { get; set; }
        public KeypadDebugDelegate  OnDebugOut     { get; set; }

        // ── Device info delegates (from HTTP GET response and WS attr events) ──
        public KeypadStringDelegate OnLastAnnouncedFb { get; set; }   // lastannounced ISO
        public KeypadStringDelegate OnLastSeenFb      { get; set; }   // lastseen ISO
        public KeypadStringDelegate OnManufacturerFb  { get; set; }   // manufacturername
        public KeypadStringDelegate OnModelIdFb       { get; set; }   // modelid
        public KeypadStringDelegate OnNameFb          { get; set; }   // name
        public KeypadStringDelegate OnSwVersionFb     { get; set; }   // swversion
        public KeypadStringDelegate OnTypeFb          { get; set; }   // type

        // ── Private state ─────────────────────────────────────────────────
        private string _uniqueId;
        private string _rotaryUid;
        private int    _fullScaleDeg = 720;   // rotations × 360 = full-scale rotation for 0-100%
        private double _accumDeg;             // accumulated rotation, clamped [0, _fullScaleDeg]
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }
        private string _deviceId;    // resolved from first WS frame
        private string _resource;    // resolved from first WS frame
        private string _baseUrl;
        private int    _numberOfButtons;
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        private readonly CCriticalSection _idLock = new CCriticalSection();

        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        private const int MaxButtons        = 10;
        private const int PollIntervalMs    = 1800000;  // 30 minutes

        private CTimer _pollTimer;

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Call from SIMPL+ Main() after all RegisterDelegate calls.
        /// </summary>
        public void Initialize(string uniqueId, string rotaryUniqueId,
                               int numberOfButtons, int onlineTimeoutSeconds,
                               int fullScaleRotations)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                CrestronConsole.PrintLine("[Keypad] Initialize: empty uniqueId – ignored");
                return;
            }

            _uniqueId        = uniqueId.Trim().ToLowerInvariant();
            _numberOfButtons = Math.Max(1, Math.Min(MaxButtons, numberOfButtons));
            _onlineTimeoutMs = Math.Max(5000, onlineTimeoutSeconds * 1000);
            _fullScaleDeg    = Math.Max(1, fullScaleRotations) * 360;
            _initialized     = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            DeConzBroker.RegisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.RegisterConnectedCallback(_uniqueId, ScheduleGetState);

            if (!string.IsNullOrEmpty(rotaryUniqueId))
            {
                _rotaryUid = rotaryUniqueId.Trim().ToLowerInvariant();
                DeConzBroker.RegisterDevice(_rotaryUid, OnRotaryWs);
                DebugLog("[Keypad] Rotary endpoint registered uid=" + _rotaryUid);
            }

            // 30-minute polling timer (starts after first connect)
            _pollTimer = new CTimer(_ => GetState(), null, PollIntervalMs, PollIntervalMs);

            DebugLog(string.Format(
                "[Keypad] Initialized uid={0}  buttons={1}  onlineTimeout={2}s",
                _uniqueId, _numberOfButtons, _onlineTimeoutMs / 1000));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
            _permRun = true;
            ArmPermTimer();
        }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }
        public void SetDebug(ushort enable) { _debugEnabled = (enable != 0); }

        /// <summary>
        /// Preset the accumulated rotary level (0-65535 = 0-100%) so the dial
        /// tracks the real device level. Echoes Level_fb with the new value.
        /// </summary>
        public void SetLevel(ushort raw)
        {
            _accumDeg = (raw / 65535.0) * _fullScaleDeg;
            FireLevel();
        }

        private void FireLevel()
        {
            double frac = (_fullScaleDeg > 0) ? (_accumDeg / _fullScaleDeg) : 0.0;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            Fire(OnLevelFb, (ushort)Math.Round(frac * 65535.0));
        }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            DeConzBroker.UnregisterDevice(_uniqueId, OnWsUpdate);
            DeConzBroker.UnregisterConnectedCallback(_uniqueId);
            if (!string.IsNullOrEmpty(_rotaryUid)) DeConzBroker.UnregisterDevice(_rotaryUid, OnRotaryWs);
            StopOnlineTimer();
            if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer = null; }
            _initialized = false;
        }

        // ── GetState (HTTP GET) ───────────────────────────────────────────

        public void GetState()
        {
            string url = BaseUrl();
            if (string.IsNullOrEmpty(url))
            {
                DebugLog("[Keypad:" + _uniqueId + "] GetState deferred – URL not yet known");
                return;
            }
            _staticInfoSent = false;   // re-send static device info on manual refresh
            DebugLog("[Keypad:" + _uniqueId + "] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnHttpResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Keypad:" + _uniqueId + "] GET error: " + ex.Message);
            }
        }

        private void ScheduleGetState()
        {
            _staticInfoSent = false;  // re-publish identity after reconnect
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format(
                "[Keypad:{0}] GetState in {1} s", _uniqueId, delayMs / 1000));
            CTimer t = null;
            t = new CTimer(_ => { GetState(); if (t != null) t.Dispose(); }, null, delayMs);
        }

        // ── HTTP response ─────────────────────────────────────────────────

        private void OnHttpResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            {
                CrestronConsole.PrintLine("[Keypad:" + _uniqueId + "] HTTP error: " + err);
                return;
            }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Keypad:" + _uniqueId + "] HTTP resp: " + body);
            FireRawJson(body);
            ParseStatus(body);        // battery + lowbattery only – no button events
            ParseDeviceInfo(body);
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

            string url = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
            _idLock.Enter();
            try
            {
                // TOCTOU guard: a concurrent thread may have finished first
                if (!string.IsNullOrEmpty(_baseUrl)) return;
                _baseUrl = url;
            }
            finally { _idLock.Leave(); }

            DebugLog("[Keypad:" + _uniqueId + "] URL: " + url);
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

        // ── WS callback ───────────────────────────────────────────────────

        private void OnWsUpdate(string json)
        {
            if (_debugEnabled) DebugLog("[Keypad:" + _uniqueId + "] WS: " + json);
            // Resolve device ID + resource on first frame
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
                        _resource = string.IsNullOrEmpty(res) ? "sensors" : res.Trim();
                    }
                    finally { _idLock.Leave(); }
                    DebugLog("[Keypad:" + _uniqueId + "] Resolved id=" + _deviceId + " resource=" + _resource);
                    BuildUrls();
                }
            }

            SetOnline(1);
            RestartOnlineTimer();
            FireRawJson(json);
            ParseStatus(json);        // battery + lowbattery
            ParseButtonEvent(json);   // button events – WS only, never from HTTP GET
            ParseDeviceInfo(json);    // lastseen, name, etc. from attr events
        }

        // ── Rotary WS callback (ZHARelativeRotary, optional endpoint) ──────
        private void OnRotaryWs(string json)
        {
            if (_debugEnabled) DebugLog("[Keypad rotary:" + _rotaryUid + "] WS: " + json);
            SetOnline(1);
            RestartOnlineTimer();
            FireRawJson(json);
            ParseRotaryEvent(json);   // rotary events – WS only (transient)
        }

        /// <summary>
        /// Parses the relative rotary state (expectedrotation / rotaryevent /
        /// expectedeventduration). expectedrotation is signed: positive = CW,
        /// negative = CCW (read as signed in SIMPL+). Called ONLY from WS events
        /// (transient, like buttonevent), never from an HTTP GET.
        /// </summary>
        private void ParseRotaryEvent(string json)
        {
            try
            {
                int? rot = DeConzJsonParser.ExtractInt(json, "expectedrotation", 2);
                if (rot.HasValue)
                {
                    Fire(OnRotationFb, (ushort)rot.Value);   // signed (two's complement)
                    if      (rot.Value > 0) Fire(OnRotateCw, 1);
                    else if (rot.Value < 0) Fire(OnRotateCcw, 1);
                    _accumDeg += rot.Value;
                    if      (_accumDeg < 0)             _accumDeg = 0;
                    else if (_accumDeg > _fullScaleDeg) _accumDeg = _fullScaleDeg;
                    FireLevel();
                }
                int? ev = DeConzJsonParser.ExtractInt(json, "rotaryevent", 2);
                if (ev.HasValue) Fire(OnRotaryEventFb, (ushort)Math.Max(0, Math.Min(65535, ev.Value)));
                int? dur = DeConzJsonParser.ExtractInt(json, "expectedeventduration", 2);
                if (dur.HasValue) Fire(OnRotationDurationFb, (ushort)Math.Max(0, Math.Min(65535, dur.Value)));
                DebugLog(string.Format("[Keypad rotary:{0}] rotation={1} event={2} duration={3}",
                    _rotaryUid, rot, ev, dur));
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Keypad rotary] ParseRotaryEvent error: " + ex.Message);
            }
        }

        // ── JSON parser ───────────────────────────────────────────────────

        /// <summary>
        /// Parses battery and low-battery status. Safe to call from both
        /// HTTP GET responses and WS events.
        /// </summary>
        private void ParseStatus(string json)
        {
            try
            {
                int? battery = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (battery.HasValue)
                {
                    var cb = OnBatteryLevel;
                    if (cb != null) cb((ushort)Math.Max(0, Math.Min(100, battery.Value)));
                }

                bool? lowBat = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (lowBat.HasValue)
                {
                    var cb = OnBatteryLow;
                    if (cb != null) cb(lowBat.Value ? (ushort)1 : (ushort)0);
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Keypad:" + _uniqueId + "] ParseStatus error: " + ex.Message);
            }
        }

        /// <summary>
        /// Parses and fires button events from buttonevent.
        /// Called ONLY from WS events – never from HTTP GET responses,
        /// because the GET response always contains the last button press
        /// which would re-fire stale events on every state refresh.
        /// </summary>
        private void ParseButtonEvent(string json)
        {
            try
            {
                int? bEvent = DeConzJsonParser.ExtractInt(json, "buttonevent", 2);
                if (!bEvent.HasValue || bEvent.Value < 0) return;

                int buttonNum = bEvent.Value / 1000;
                int eventCode = bEvent.Value % 1000;

                DebugLog(string.Format("[Keypad:{0}] button={1} event={2}",
                    _uniqueId, buttonNum, eventCode));

                if (buttonNum >= 1 && buttonNum <= _numberOfButtons)
                {
                    ushort btn = (ushort)buttonNum;
                    switch (eventCode)
                    {
                        case ButtonEvent.InitialPress:
                        case ButtonEvent.Hold:         FireButton(OnHold, btn);         break;
                        case ButtonEvent.ShortRelease: FireButton(OnShortRelease, btn); break;
                        case ButtonEvent.LongRelease:  FireButton(OnLongRelease, btn);  break;
                        case ButtonEvent.DoublePress:  FireButton(OnDoublePress, btn);  break;
                        case ButtonEvent.TreblePress:  FireButton(OnTreblePress, btn);  break;
                        default:
                            DebugLog("[Keypad:" + _uniqueId + "] Unknown event code: " + eventCode);
                            break;
                    }
                }
                else if (buttonNum != 0)
                {
                    DebugLog(string.Format("[Keypad:{0}] Button {1} out of range (configured: {2})",
                        _uniqueId, buttonNum, _numberOfButtons));
                }
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Keypad:" + _uniqueId + "] ParseButtonEvent error: " + ex.Message);
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
            DebugLog("[Keypad] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnRotationFb, 0);
            Fire(OnRotaryEventFb, 0);
            Fire(OnRotationDurationFb, 0);
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
                    DebugLog("[Keypad:" + _uniqueId + "] Online timeout");
                    SetOnline(0);
                }, null, _onlineTimeoutMs);
        }

        private void StopOnlineTimer()
        {
            if (_onlineTimer == null) return;
            _onlineTimer.Stop(); _onlineTimer = null;
        }

        // ── Device info parser ────────────────────────────────────────────

        /// <summary>
        /// Parses device-level fields (top-level, depth 1) from either the HTTP
        /// GET response or a WS "attr" event. Only fires a delegate when the
        /// corresponding field is actually present, so state-only WS frames
        /// (which carry none of these) cause no spurious updates.
        /// hascolor and groups are light-only and intentionally omitted here.
        /// </summary>
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
                v = DeConzJsonParser.ExtractTopLevelString(json, "swversion");
                if (v != null) FireStr(OnSwVersionFb, v);
                v = DeConzJsonParser.ExtractTopLevelString(json, "type");
                if (v != null) FireStr(OnTypeFb, v);
                _staticInfoSent = true;
            }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Keypad:" + _uniqueId + "] ParseDeviceInfo error: " + ex.Message);
            }
        }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(KeypadStringDelegate cb, string s)
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
        // ── Helpers ───────────────────────────────────────────────────────

        // ── Permanent string re-assert (Make-String-Permanent equivalent) ──
        // Periodically re-fire the cached string outputs while the global or
        // this module's local enable is high, so late joining sinks always see
        // the current values. Debug and raw JSON do not use FireStr here, so
        // every FireStr value is a cacheable (non-raw, non-debug) string.
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
                var cb = snap[i].Key as KeypadStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
        }

        private void FireStr(KeypadStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 250);
            _strLock.Enter();
            try { _lastStr[cb] = s; }
            finally { _strLock.Leave(); }
            try { cb(new SimplSharpString(s)); } catch { }
        }

        private void SetOnline(ushort v)
        { var cb = OnOnline; if (cb != null) try { cb(v); } catch { } }

        private static void Fire(KeypadBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(KeypadLevelDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void FireButton(KeypadButtonDelegate cb, ushort btn)
        { if (cb != null) try { cb(btn); } catch { } }

        private void FireRawJson(string json)
        {
            if (!_rawJsonEnabled) return;
            var cb = OnRawJson;
            if (cb == null) return;
            if (json.Length > 65000) json = json.Substring(0, 65000);
            try { cb(new SimplSharpString(json)); } catch { }
        }

        private void DebugLog(string msg)
        {
            if (!_debugEnabled) return;
            CrestronConsole.PrintLine(msg);
            var cb = OnDebugOut;
            if (cb == null) return;
            if (msg.Length > 250) msg = msg.Substring(0, 250) + "…";
            try { cb(new SimplSharpString(msg)); } catch { }
        }
    }
}
