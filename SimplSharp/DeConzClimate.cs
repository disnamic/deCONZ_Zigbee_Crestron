/*******************************************************************************
 * DeConzClimate.cs
 *
 * SIMPL# class – deCONZ Zigbee climate / environment sensor.
 *
 * Supports up to three separate sensor endpoints:
 *   ZHATemperature  – state.temperature  (1/100 °C)
 *   ZHAHumidity     – state.humidity     (1/100 %)
 *   ZHAPressure     – state.pressure     (hPa, integer)
 *
 * All three UniqueIDs are optional. An empty/null value skips that endpoint.
 * Each active endpoint registers independently with DeConzBroker and fetches
 * its state via HTTP GET on connect/reconnect (random 1-15 s stagger).
 *
 * Battery / Online:
 *   Shared across all endpoints – any active endpoint can report battery
 *   level and low-battery flag. An optional fourth Battery_UniqueID registers
 *   a dedicated ZHABattery sensor endpoint (same pattern as Thermostat).
 *
 * A single online timer is shared; any WS activity from any active endpoint
 * resets it.
 *
 * All values use Option-C dual output:
 *   Analog  – raw integer (1/100 unit or hPa)
 *   String  – formatted one-decimal string ("21.5", "45.3", "1013")
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void ClimateBoolDelegate(ushort value);
    public delegate void ClimateLevelDelegate(ushort value);
    public delegate void ClimateStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzClimate
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        // Online / battery (shared)
        public ClimateBoolDelegate   OnOnline           { get; set; }
        public ClimateBoolDelegate   OnBatteryLow       { get; set; }
        public ClimateLevelDelegate  OnBatteryLevel     { get; set; }
        public ClimateLevelDelegate  OnVoltageFb        { get; set; }   // mV (ZHABattery)

        // Temperature
        public ClimateLevelDelegate  OnTemperatureFb    { get; set; }   // raw 1/100°C
        public ClimateStringDelegate OnTemperatureStr   { get; set; }   // "21.5"

        // Humidity
        public ClimateLevelDelegate  OnHumidityFb       { get; set; }   // raw 1/100%
        public ClimateStringDelegate OnHumidityStr      { get; set; }   // "45.3"

        // Pressure
        public ClimateLevelDelegate  OnPressureFb       { get; set; }   // hPa
        public ClimateStringDelegate OnPressureStr      { get; set; }   // "1013"

        // Device info
        public ClimateStringDelegate OnLastSeenFb       { get; set; }
        public ClimateStringDelegate OnLastAnnouncedFb  { get; set; }
        public ClimateStringDelegate OnManufacturerFb   { get; set; }
        public ClimateStringDelegate OnModelIdFb        { get; set; }
        public ClimateStringDelegate OnNameFb           { get; set; }

        // Raw JSON per endpoint
        public ClimateStringDelegate OnTempRawJson      { get; set; }
        public ClimateStringDelegate OnHumRawJson       { get; set; }
        public ClimateStringDelegate OnPresRawJson      { get; set; }
        public ClimateStringDelegate OnBatteryRawJson   { get; set; }
        public ClimateStringDelegate OnDebugOut         { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        // Endpoint slots: 0=temperature, 1=humidity, 2=pressure, 3=battery
        private readonly string[]              _uids     = new string[4];
        private readonly string[]              _ids      = new string[4];
        private readonly string[]              _res      = new string[4];
        private readonly string[]              _urls     = new string[4];
        private readonly CCriticalSection[]    _locks;

        // Online timer (shared)
        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        private int _pendingHttpSlot;   // slot index for current async HTTP request

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        private const int SlotTemp    = 0;
        private const int SlotHum     = 1;
        private const int SlotPres    = 2;
        private const int SlotBattery = 3;

        // ── Constructor ───────────────────────────────────────────────────

        public DeConzClimate()
        {
            _locks = new CCriticalSection[4];
            for (int i = 0; i < 4; i++) _locks[i] = new CCriticalSection();
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Call from SIMPL+ Main() after all RegisterDelegate calls.
        /// Any UID may be empty/null to skip that endpoint.
        /// </summary>
        public void Initialize(string tempUid, string humUid, string presUid,
                               string batteryUid)
        {
            _initialized = true;

            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            RegisterEndpoint(SlotTemp,    tempUid);
            RegisterEndpoint(SlotHum,     humUid);
            RegisterEndpoint(SlotPres,    presUid);
            RegisterEndpoint(SlotBattery, batteryUid);

            DebugLog(string.Format(
                "[Climate] Initialized  temp={0}  hum={1}  pres={2}  batt={3}",
                Uid(SlotTemp) ?? "(none)", Uid(SlotHum) ?? "(none)",
                Uid(SlotPres) ?? "(none)", Uid(SlotBattery) ?? "(none)"));

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
            for (int i = 0; i < 4; i++)
                if (!string.IsNullOrEmpty(Uid(i))) FetchHttp(i);
        }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            for (int i = 0; i < 4; i++)
            {
                string uid = Uid(i);
                if (!string.IsNullOrEmpty(uid))
                {
                    DeConzBroker.UnregisterDevice(uid, MakeCallback(i));
                    DeConzBroker.UnregisterConnectedCallback(uid);
                }
            }
            StopOnlineTimer();
            _initialized = false;
        }

        // ── Endpoint registration ─────────────────────────────────────────

        private void RegisterEndpoint(int slot, string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            _uids[slot] = uid.Trim().ToLowerInvariant();
            var cb = MakeCallback(slot);
            DeConzBroker.RegisterDevice(_uids[slot], cb);
            DeConzBroker.RegisterConnectedCallback(_uids[slot],
                () => ScheduleGet(slot));
            DebugLog(string.Format(
                "[Climate] Slot {0} registered uid={1}", slot, _uids[slot]));
        }

        private Action<string> MakeCallback(int slot)
        {
            // Capture slot in a local to avoid closure issues
            int s = slot;
            switch (s)
            {
                case SlotTemp:    return json => OnWsUpdate(SlotTemp,    json);
                case SlotHum:     return json => OnWsUpdate(SlotHum,     json);
                case SlotPres:    return json => OnWsUpdate(SlotPres,    json);
                case SlotBattery: return json => OnWsUpdate(SlotBattery, json);
                default: return null;
            }
        }

        // ── HTTP GET ──────────────────────────────────────────────────────

        private void ScheduleGet(int slot)
        {
            int delayMs = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Climate] Slot {0} GetState in {1} s",
                slot, delayMs / 1000));
            int s = slot;
            CTimer t = null;
            t = new CTimer(_ => { FetchHttp(s); if (t != null) t.Dispose(); }, null, delayMs);
        }

        private void FetchHttp(int slot)
        {
            if (!EnsureUrl(slot)) return;
            string url = GetUrl(slot);
            if (string.IsNullOrEmpty(url)) return;
            DebugLog("[Climate] GET slot=" + slot + " " + url);
            try
            {
                int s = slot;
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _pendingHttpSlot = s; _http.DispatchAsync(req, OnHttpResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex)
            {
                Log("[Climate] GET error slot=" + slot + ": " + ex.Message);
            }
        }

        private void OnHttpResponse(HttpClientResponse resp,
                                    HTTP_CALLBACK_ERROR err)
        {
            try
            {
            int slot = _pendingHttpSlot;
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Climate] HTTP error slot=" + slot + ": " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Climate] HTTP slot=" + slot + ": " + body);
            FireRaw(slot, body);
            ParseAndFire(slot, body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── URL builder ───────────────────────────────────────────────────

        private void BuildUrl(int slot)
        {
            var ip = DeConzBroker.GatewayIP;
            string id, res;
            _locks[slot].Enter();
            try { id = _ids[slot]; res = _res[slot]; }
            finally { _locks[slot].Leave(); }
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(id)) return;

            string url = string.Format("http://{0}/api/{1}/{2}/{3}",
                             ip, _apiKey, res, id);
            _locks[slot].Enter();
            try { _urls[slot] = url; }
            finally { _locks[slot].Leave(); }
            DebugLog("[Climate] Slot " + slot + " URL: " + url);
            ScheduleGet(slot);
        }

        private bool EnsureUrl(int slot)
        {
            _locks[slot].Enter();
            string snap; try { snap = _urls[slot]; } finally { _locks[slot].Leave(); }
            if (string.IsNullOrEmpty(snap)) BuildUrl(slot);
            _locks[slot].Enter();
            try { return !string.IsNullOrEmpty(_urls[slot]); }
            finally { _locks[slot].Leave(); }
        }

        private string GetUrl(int slot)
        {
            _locks[slot].Enter();
            try { return _urls[slot]; } finally { _locks[slot].Leave(); }
        }

        private string Uid(int slot)
        {
            return _uids[slot];
        }

        // ── WS callback ───────────────────────────────────────────────────

        private void OnWsUpdate(int slot, string json)
        {
            if (_debugEnabled) DebugLog("[Climate] WS slot=" + slot + ": " + json);
            // Resolve device ID on first frame
            bool need;
            _locks[slot].Enter();
            try { need = string.IsNullOrEmpty(_ids[slot]); }
            finally { _locks[slot].Leave(); }

            if (need)
            {
                string id  = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string res = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    _locks[slot].Enter();
                    try
                    {
                        _ids[slot] = id.Trim();
                        _res[slot] = string.IsNullOrEmpty(res) ? "sensors" : res.Trim();
                    }
                    finally { _locks[slot].Leave(); }
                    DebugLog(string.Format("[Climate] Slot {0} resolved id={1}", slot, id));
                    BuildUrl(slot);
                }
            }

            FireOnline(1);
            RestartOnlineTimer();
            FireRaw(slot, json);
            ParseAndFire(slot, json);
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        private void ParseAndFire(int slot, string json)
        {
            try
            {
                switch (slot)
                {
                    case SlotTemp:
                        int? temp = DeConzJsonParser.ExtractInt(json, "temperature", 2);
                        if (temp.HasValue)
                        {
                            Fire(OnTemperatureFb, ToSignedAnalog(temp.Value));
                            FireStr(OnTemperatureStr, FormatHundredths(temp.Value));
                        }
                        ParseBattery(json);
                        ParseDeviceInfo(json);
                        break;

                    case SlotHum:
                        int? hum = DeConzJsonParser.ExtractInt(json, "humidity", 2);
                        if (hum.HasValue)
                        {
                            Fire(OnHumidityFb, ToUshort(hum.Value));
                            FireStr(OnHumidityStr, FormatHundredths(hum.Value));
                        }
                        ParseBattery(json);
                        ParseDeviceInfo(json);
                        break;

                    case SlotPres:
                        int? pres = DeConzJsonParser.ExtractInt(json, "pressure", 2);
                        if (pres.HasValue)
                        {
                            Fire(OnPressureFb, ToUshort(pres.Value));
                            FireStr(OnPressureStr, pres.Value.ToString());
                        }
                        ParseBattery(json);
                        ParseDeviceInfo(json);
                        break;

                    case SlotBattery:
                        ParseBattery(json);
                        ParseDeviceInfo(json);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("[Climate] ParseAndFire error slot=" + slot + ": " + ex.Message);
            }
        }

        private void ParseBattery(string json)
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

        private void ParseDeviceInfo(string json)
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

        // ── Value formatting ──────────────────────────────────────────────

        private static ushort ToUshort(int value)
        {
            return (ushort)Math.Max(0, Math.Min(65535, value));
        }

        /// <summary>
        /// Converts a signed 1/100-unit value to a ushort bit pattern that SIMPL+
        /// can interpret as a signed analog. Negative values are sent as their
        /// 16-bit two's-complement (e.g. -500 → 65036 → read as -500 when the
        /// SIMPL signal is treated as signed).
        /// Range is clamped to the signed 16-bit window (-32768..32767), i.e.
        /// -327.68 .. 327.67 units — far beyond any real temperature.
        /// </summary>
        private static ushort ToSignedAnalog(int value)
        {
            int clamped = Math.Max(-32768, Math.Min(32767, value));
            return (ushort)(short)clamped;
        }

        /// <summary>Formats 1/100-unit integers as one-decimal string.
        /// e.g. 2150 → "21.5"  4532 → "45.3"  -50 → "-0.5"</summary>
        private static string FormatHundredths(int value)
        {
            double d = value / 100.0;
            return d.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture);
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
            DebugLog("[Climate] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnVoltageFb, 0);
            Fire(OnTemperatureFb, 0);
            FireStr(OnTemperatureStr, "");
            Fire(OnHumidityFb, 0);
            FireStr(OnHumidityStr, "");
            Fire(OnPressureFb, 0);
            FireStr(OnPressureStr, "");
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
                    DebugLog("[Climate] Online timeout");
                    FireOnline(0);
                    _staticInfoSent = false;
                }, null, _onlineTimeoutMs);
        }

        private void StopOnlineTimer()
        {
            if (_onlineTimer == null) return;
            _onlineTimer.Stop(); _onlineTimer = null;
        }

        private void FireOnline(ushort v) { Fire(OnOnline, v); }

        // ── Raw JSON routing ──────────────────────────────────────────────

        private void FireRaw(int slot, string json)
        {
            if (!_rawJsonEnabled) return;
            ClimateStringDelegate cb = null;
            switch (slot)
            {
                case SlotTemp:    cb = OnTempRawJson;    break;
                case SlotHum:     cb = OnHumRawJson;     break;
                case SlotPres:    cb = OnPresRawJson;    break;
                case SlotBattery: cb = OnBatteryRawJson; break;
            }
            FireChunked(cb, json);
        }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(ClimateStringDelegate cb, string s)
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

        private static void Fire(ClimateBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(ClimateLevelDelegate cb, ushort v)
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
                var cb = snap[i].Key as ClimateStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
        }

        private void FireStr(ClimateStringDelegate cb, string s)
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
