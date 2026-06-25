/*******************************************************************************
 * DeConzAlarm.cs
 *
 * SIMPL# class – deCONZ Zigbee alarm / safety sensors.
 *
 * Supports three optional sensor endpoints:
 *   Alarm_UniqueID – ZHAAlarm          (alarm_fb)
 *   Fire_UniqueID  – ZHAFire           (fire_fb)
 *   CO_UniqueID    – ZHACarbonMonoxide  (carbonmonoxide_fb)
 *
 * All three share: tampered_fb, lowbattery_fb, battery_level, online timer.
 * Optional Battery_UniqueID for a dedicated ZHABattery endpoint.
 *
 * Read-only module – no commands. State fetched on WS connect/reconnect.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    public delegate void AlarmBoolDelegate(ushort value);
    public delegate void AlarmLevelDelegate(ushort value);
    public delegate void AlarmStringDelegate(SimplSharpString value);

    public class DeConzAlarm
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        // Shared
        public AlarmBoolDelegate   OnOnline              { get; set; }
        public AlarmBoolDelegate   OnBatteryLow          { get; set; }
        public AlarmLevelDelegate  OnBatteryLevel        { get; set; }
        public AlarmLevelDelegate  OnVoltageFb           { get; set; }
        public AlarmBoolDelegate   OnTamperedFb          { get; set; }

        // Alarm (ZHAAlarm)
        public AlarmBoolDelegate   OnAlarmFb             { get; set; }

        // Fire (ZHAFire)
        public AlarmBoolDelegate   OnFireFb              { get; set; }

        // Carbon Monoxide (ZHACarbonMonoxide)
        public AlarmBoolDelegate   OnCarbonMonoxideFb    { get; set; }

        // Device info
        public AlarmStringDelegate OnLastSeenFb          { get; set; }
        public AlarmStringDelegate OnLastAnnouncedFb     { get; set; }
        public AlarmStringDelegate OnManufacturerFb      { get; set; }
        public AlarmStringDelegate OnModelIdFb           { get; set; }
        public AlarmStringDelegate OnNameFb              { get; set; }

        // Raw JSON
        public AlarmStringDelegate OnAlarmRawJson        { get; set; }
        public AlarmStringDelegate OnFireRawJson         { get; set; }
        public AlarmStringDelegate OnCORawJson           { get; set; }
        public AlarmStringDelegate OnBatteryRawJson      { get; set; }
        public AlarmStringDelegate OnDebugOut            { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        // Per-endpoint state (alarm=0, fire=1, CO=2, battery=3)
        private readonly string[] _uids  = new string[4];
        private readonly string[] _ids   = new string[4];
        private readonly string[] _res   = new string[4];
        private readonly string[] _urls  = new string[4];
        private readonly CCriticalSection[] _locks;

        private const int SlotAlarm   = 0;
        private const int SlotFire    = 1;
        private const int SlotCO      = 2;
        private const int SlotBattery = 3;

        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Constructor ───────────────────────────────────────────────────

        public DeConzAlarm()
        {
            _locks = new CCriticalSection[4];
            for (int i = 0; i < 4; i++) _locks[i] = new CCriticalSection();
        }

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize(string alarmUid, string fireUid,
                               string coUid, string batteryUid)
        {
            _initialized = true;
            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            RegisterSlot(SlotAlarm,   alarmUid);
            RegisterSlot(SlotFire,    fireUid);
            RegisterSlot(SlotCO,      coUid);
            RegisterSlot(SlotBattery, batteryUid);

            DebugLog(string.Format("[Alarm] Initialized alarm={0} fire={1} co={2} batt={3}",
                _uids[0] ?? "(none)", _uids[1] ?? "(none)",
                _uids[2] ?? "(none)", _uids[3] ?? "(none)"));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
            _permRun = true;
            ArmPermTimer();
        }

        public void SetOnlineTimeout(int seconds) { _onlineTimeoutMs = Math.Max(5, seconds) * 1000; }
        public void SetDebug(ushort e)            { _debugEnabled   = (e != 0); }
        public void SetRawJsonEnabled(ushort e)   { _rawJsonEnabled = (e != 0); }

        public void GetState()
        {
            _staticInfoSent = false;   // re-send static device info on manual refresh
            for (int i = 0; i < 4; i++)
                if (!string.IsNullOrEmpty(_uids[i])) FetchHttp(i);
        }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            for (int i = 0; i < 4; i++)
                if (!string.IsNullOrEmpty(_uids[i]))
                {
                    DeConzBroker.UnregisterDevice(_uids[i], MakeWsCb(i));
                    DeConzBroker.UnregisterConnectedCallback(_uids[i]);
                }
            StopOnlineTimer();
            _initialized = false;
        }

        // ── Slot registration ─────────────────────────────────────────────

        private void RegisterSlot(int slot, string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            _uids[slot] = uid.Trim().ToLowerInvariant();
            int s = slot;
            DeConzBroker.RegisterDevice(_uids[slot], json => OnWs(s, json));
            DeConzBroker.RegisterConnectedCallback(_uids[slot], () => ScheduleGet(s));
        }

        private Action<string> MakeWsCb(int slot)
        {
            int s = slot;
            return json => OnWs(s, json);
        }

        // ── Scheduling ────────────────────────────────────────────────────

        private void ScheduleGet(int slot)
        {
            int d = DeConzJsonParser.NextStaggerMs();
            int s = slot;
            CTimer t = null;
            t = new CTimer(_ => { FetchHttp(s); if (t != null) t.Dispose(); }, null, d);
        }

        // ── HTTP ──────────────────────────────────────────────────────────

        private void FetchHttp(int slot)
        {
            string url; _locks[slot].Enter(); url = _urls[slot]; _locks[slot].Leave();
            if (string.IsNullOrEmpty(url)) return;
            int s = slot;
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, (resp, err) => OnHttpResp(s, resp, err)); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Alarm] GET error slot=" + slot + ": " + ex.Message); }
        }

        private void OnHttpResp(int slot, HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Alarm] GET error slot=" + slot + ": " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            DispatchPayload(slot, body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── WS callback ───────────────────────────────────────────────────

        private void OnWs(int slot, string json)
        {
            if (_debugEnabled) DebugLog("[Alarm] WS slot=" + slot + ": " + json);
            bool need; _locks[slot].Enter(); need = string.IsNullOrEmpty(_ids[slot]); _locks[slot].Leave();
            if (need)
            {
                string id = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string rs = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    var ip = DeConzBroker.GatewayIP;
                    _locks[slot].Enter();
                    _ids[slot]  = id.Trim();
                    _res[slot]  = string.IsNullOrEmpty(rs) ? "sensors" : rs.Trim();
                    _urls[slot] = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, _res[slot], _ids[slot]);
                    _locks[slot].Leave();
                    ScheduleGet(slot);
                }
            }

            FireOnline(1); RestartOnlineTimer();
            DispatchPayload(slot, json);
        }

        // ── Payload dispatcher ────────────────────────────────────────────

        private void DispatchPayload(int slot, string json)
        {
            switch (slot)
            {
                case SlotAlarm:
                    if (_rawJsonEnabled) FireChunked(OnAlarmRawJson, json);
                    bool? alarm = DeConzJsonParser.ExtractBool(json, "alarm", 2);
                    if (alarm.HasValue) Fire(OnAlarmFb, alarm.Value ? (ushort)1 : (ushort)0);
                    break;
                case SlotFire:
                    if (_rawJsonEnabled) FireChunked(OnFireRawJson, json);
                    bool? fire = DeConzJsonParser.ExtractBool(json, "fire", 2);
                    if (fire.HasValue) Fire(OnFireFb, fire.Value ? (ushort)1 : (ushort)0);
                    break;
                case SlotCO:
                    if (_rawJsonEnabled) FireChunked(OnCORawJson, json);
                    bool? co = DeConzJsonParser.ExtractBool(json, "carbonmonoxide", 2);
                    if (co.HasValue) Fire(OnCarbonMonoxideFb, co.Value ? (ushort)1 : (ushort)0);
                    break;
                case SlotBattery:
                    if (_rawJsonEnabled) FireChunked(OnBatteryRawJson, json);
                    break;
            }
            // Shared fields across all slots
            bool? tamp = DeConzJsonParser.ExtractBool(json, "tampered", 2);
            if (tamp.HasValue) Fire(OnTamperedFb, tamp.Value ? (ushort)1 : (ushort)0);

            int? bat = DeConzJsonParser.ExtractInt(json, "battery", 2);
            if (bat.HasValue) Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, bat.Value)));
            bool? low = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
            if (low.HasValue) Fire(OnBatteryLow, low.Value ? (ushort)1 : (ushort)0);
            int? v = DeConzJsonParser.ExtractInt(json, "voltage", 2);
            if (v.HasValue) Fire(OnVoltageFb, (ushort)Math.Max(0, Math.Min(65535, v.Value)));

            string ls = DeConzJsonParser.ExtractTopLevelString(json, "lastseen");
            if (ls != null) FireStr(OnLastSeenFb, ls);
            string la = DeConzJsonParser.ExtractTopLevelString(json, "lastannounced");
            if (la != null) FireStr(OnLastAnnouncedFb, la);

            // Static device info changes practically never — parse once.
            if (!_staticInfoSent)
            {
                string mf = DeConzJsonParser.ExtractTopLevelString(json, "manufacturername");
                if (mf != null) FireStr(OnManufacturerFb, mf);
                string mi = DeConzJsonParser.ExtractTopLevelString(json, "modelid");
                if (mi != null) FireStr(OnModelIdFb, mi);
                string nm = DeConzJsonParser.ExtractTopLevelString(json, "name");
                if (nm != null) FireStr(OnNameFb, nm);
                _staticInfoSent = true;
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
            DebugLog("[Alarm] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnVoltageFb, 0);
            Fire(OnTamperedFb, 0);
            Fire(OnAlarmFb, 0);
            Fire(OnFireFb, 0);
            Fire(OnCarbonMonoxideFb, 0);
        }

        private void RestartOnlineTimer()
        {
            _lastActivityUtc = DateTime.UtcNow;
            _staleResetDone  = false;
            if (_onlineTimer != null) _onlineTimer.Reset(_onlineTimeoutMs);
            else _onlineTimer = new CTimer(_ => { DebugLog("[Alarm] Online timeout"); FireOnline(0); _staticInfoSent = false; }, null, _onlineTimeoutMs);
        }
        private void StopOnlineTimer() { if (_onlineTimer == null) return; _onlineTimer.Stop(); _onlineTimer = null; }
        private void FireOnline(ushort v) { Fire(OnOnline, v); }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(AlarmStringDelegate cb, string s)
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

        private static void Fire(AlarmBoolDelegate cb, ushort v)  { if (cb != null) try { cb(v); } catch { } }
        private static void Fire(AlarmLevelDelegate cb, ushort v) { if (cb != null) try { cb(v); } catch { } }
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
                var cb = snap[i].Key as AlarmStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
        }

        private void FireStr(AlarmStringDelegate cb, string s)
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

        private void Log(string m) { CrestronConsole.PrintLine(m); }
        private void DebugLog(string m)
        {
            if (!_debugEnabled) return;
            Log(m);
            FireStr(OnDebugOut, m.Length > 250 ? m.Substring(0, 250) + "…" : m);
        }
    }
}
