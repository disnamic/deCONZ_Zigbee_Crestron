/*******************************************************************************
 * DeConzPower.cs
 *
 * SIMPL# class – deCONZ Zigbee smart plug / power meter.
 *
 * Three optional endpoints:
 *   Switch_UniqueID      – On/Off light endpoint (on/off commands + feedback)
 *   Power_UniqueID       – ZHAPower  (power W, voltage V, current A)
 *   Consumption_UniqueID – ZHAConsumption (total kWh + instantaneous power W)
 *
 * Note: consumption cannot be reset via the deCONZ API. The reported value
 * is the hardware counter from the device. Implement an offset in SIMPL+
 * to achieve a software reset (store value at reset time, subtract from all
 * subsequent readings).
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    public delegate void PowerBoolDelegate(ushort value);
    public delegate void PowerLevelDelegate(ushort value);
    public delegate void PowerStringDelegate(SimplSharpString value);

    public class DeConzPower
    {
        private bool _staticInfoSent;

        // ── Delegates ────────────────────────────────────────────────────

        // Shared
        public PowerBoolDelegate   OnOnline          { get; set; }
        public PowerBoolDelegate   OnBatteryLow      { get; set; }
        public PowerLevelDelegate  OnBatteryLevel    { get; set; }

        // Switch endpoint
        public PowerBoolDelegate   OnOnFb            { get; set; }
        public PowerBoolDelegate   OnOffFb           { get; set; }

        // Power endpoint (ZHAPower)
        public PowerLevelDelegate  OnPowerFb         { get; set; }   // Watts
        public PowerLevelDelegate  OnVoltageFb       { get; set; }   // Volts
        public PowerLevelDelegate  OnCurrentFb       { get; set; }   // Amps x10 (0.1A resolution)

        // Consumption endpoint (ZHAConsumption)
        public PowerLevelDelegate  OnConsumptionFb   { get; set; }   // Wh (divide by 1000 for kWh)
        public PowerLevelDelegate  OnConsumptionPowerFb { get; set; } // Watts from consumption endpoint

        // Device info
        public PowerStringDelegate OnLastSeenFb      { get; set; }
        public PowerStringDelegate OnLastAnnouncedFb { get; set; }
        public PowerStringDelegate OnManufacturerFb  { get; set; }
        public PowerStringDelegate OnModelIdFb       { get; set; }
        public PowerStringDelegate OnNameFb          { get; set; }

        // Raw JSON
        public PowerStringDelegate OnSwitchRawJson      { get; set; }
        public PowerStringDelegate OnPowerRawJson        { get; set; }
        public PowerStringDelegate OnConsumptionRawJson  { get; set; }
        public PowerStringDelegate OnDebugOut            { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;
        private bool   _initialized;

        // Switch endpoint
        private string _switchUid;
        private string _switchId;
        private string _switchRes;
        private string _switchUrl;
        private string _switchStateUrl;
        private readonly CCriticalSection _swLock = new CCriticalSection();

        // Power endpoint
        private string _powerUid;
        private string _powerId;
        private string _powerRes;
        private string _powerUrl;
        private readonly CCriticalSection _pwLock = new CCriticalSection();

        // Consumption endpoint
        private string _consUid;
        private string _consId;
        private string _consRes;
        private string _consUrl;
        private readonly CCriticalSection _consLock = new CCriticalSection();

        private int    _onlineTimeoutMs = 120000;
        private CTimer _onlineTimer;
        private CTimer   _staleTimer;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private bool     _staleResetDone;

        // ── Permanent string re-assert (Make-String-Permanent equivalent) ──
        private readonly System.Collections.Generic.Dictionary<object, string> _lastStr
            = new System.Collections.Generic.Dictionary<object, string>();
        private readonly CCriticalSection _strLock = new CCriticalSection();
        private bool _permLocal;
        private bool _permRun;

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // Pending HTTP slot for response routing
        private int _pendingGetSlot;   // 0=switch 1=power 2=cons

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize(string switchUid, string powerUid,
                               string consumptionUid)
        {
            _initialized = true;
            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;

            if (!string.IsNullOrEmpty(switchUid))
            {
                _switchUid = switchUid.Trim().ToLowerInvariant();
                DeConzBroker.RegisterDevice(_switchUid, OnSwitchWs);
                DeConzBroker.RegisterConnectedCallback(_switchUid, ScheduleGetSwitch);
            }
            if (!string.IsNullOrEmpty(powerUid))
            {
                _powerUid = powerUid.Trim().ToLowerInvariant();
                DeConzBroker.RegisterDevice(_powerUid, OnPowerWs);
                DeConzBroker.RegisterConnectedCallback(_powerUid, ScheduleGetPower);
            }
            if (!string.IsNullOrEmpty(consumptionUid))
            {
                _consUid = consumptionUid.Trim().ToLowerInvariant();
                DeConzBroker.RegisterDevice(_consUid, OnConsWs);
                DeConzBroker.RegisterConnectedCallback(_consUid, ScheduleGetCons);
            }
            DebugLog(string.Format("[Power] Initialized sw={0} pw={1} cons={2}",
                _switchUid ?? "(none)", _powerUid ?? "(none)", _consUid ?? "(none)"));

            _staleTimer = new CTimer(_ => CheckStale(), null, 300000, 300000);
            _permRun = true;
            ArmPermTimer();
        }

        public void SetOnlineTimeout(int seconds) { _onlineTimeoutMs = Math.Max(5, seconds) * 1000; }
        public void SetDebug(ushort e)            { _debugEnabled   = (e != 0); }
        public void SetRawJsonEnabled(ushort e)   { _rawJsonEnabled = (e != 0); }

        public void SetOn()  { SendSwitchPut("{\"on\":true}");  }
        public void SetOff() { SendSwitchPut("{\"on\":false}"); }

        public void GetState()
        {
            _staticInfoSent = false;   // re-send static device info on manual refresh
            if (!string.IsNullOrEmpty(_switchUid)) FetchSwitch();
            if (!string.IsNullOrEmpty(_powerUid))  FetchPower();
            if (!string.IsNullOrEmpty(_consUid))   FetchCons();
        }

        public void Dispose()
        {
            _permRun = false;
            if (_staleTimer != null) { _staleTimer.Stop(); _staleTimer = null; }
            if (!_initialized) return;
            if (!string.IsNullOrEmpty(_switchUid)) { DeConzBroker.UnregisterDevice(_switchUid, OnSwitchWs); DeConzBroker.UnregisterConnectedCallback(_switchUid); }
            if (!string.IsNullOrEmpty(_powerUid))  { DeConzBroker.UnregisterDevice(_powerUid, OnPowerWs);   DeConzBroker.UnregisterConnectedCallback(_powerUid); }
            if (!string.IsNullOrEmpty(_consUid))   { DeConzBroker.UnregisterDevice(_consUid, OnConsWs);     DeConzBroker.UnregisterConnectedCallback(_consUid); }
            StopOnlineTimer();
            _initialized = false;
        }

        // ── Switch commands ───────────────────────────────────────────────

        private void SendSwitchPut(string body)
        {
            string url; _swLock.Enter(); url = _switchStateUrl; _swLock.Leave();
            if (string.IsNullOrEmpty(url)) { Log("[Power] Switch URL not yet resolved"); return; }
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType   = RequestType.Put;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnSwitchPutResp); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Power] PUT error: " + ex.Message); }
        }

        private void OnSwitchPutResp(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) Log("[Power] PUT error: " + err);
            else DebugLog("[Power] PUT resp: " + resp.ContentString);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── Scheduling ────────────────────────────────────────────────────

        private void ScheduleGetSwitch() { Schedule(FetchSwitch, "switch"); }
        private void ScheduleGetPower()  { Schedule(FetchPower,  "power");  }
        private void ScheduleGetCons()   { Schedule(FetchCons,   "cons");   }

        private void Schedule(Action a, string lbl)
        {
            int d = DeConzJsonParser.NextStaggerMs();
            DebugLog(string.Format("[Power] GetState/{0} in {1} s", lbl, d / 1000));
            CTimer t = null;
            t = new CTimer(_ => { a(); if (t != null) t.Dispose(); }, null, d);
        }

        // ── HTTP fetch helpers ────────────────────────────────────────────

        private void FetchSwitch()
        {
            string url; _swLock.Enter(); url = _switchUrl; _swLock.Leave();
            if (string.IsNullOrEmpty(url)) return;
            _pendingGetSlot = 0;
            Fetch(url, OnGetResp);
        }
        private void FetchPower()
        {
            string url; _pwLock.Enter(); url = _powerUrl; _pwLock.Leave();
            if (string.IsNullOrEmpty(url)) return;
            _pendingGetSlot = 1;
            Fetch(url, OnGetResp);
        }
        private void FetchCons()
        {
            string url; _consLock.Enter(); url = _consUrl; _consLock.Leave();
            if (string.IsNullOrEmpty(url)) return;
            _pendingGetSlot = 2;
            Fetch(url, OnGetResp);
        }

        private void Fetch(string url, HTTPClientResponseCallback cb)
        {
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, cb); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Power] GET error: " + ex.Message); }
        }

        private void OnGetResp(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Power] GET error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            int slot = _pendingGetSlot;
            if (_debugEnabled) DebugLog("[Power] GET resp slot=" + slot + ": " + body);
            if (slot == 0)
            {
                if (_rawJsonEnabled) FireChunked(OnSwitchRawJson, body);
                ParseSwitch(body);
            }
            else if (slot == 1)
            {
                if (_rawJsonEnabled) FireChunked(OnPowerRawJson, body);
                ParsePower(body);
            }
            else
            {
                if (_rawJsonEnabled) FireChunked(OnConsumptionRawJson, body);
                ParseCons(body);
            }
            ParseBattery(body); ParseDeviceInfo(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── WS callbacks ──────────────────────────────────────────────────

        private void OnSwitchWs(string json)
        {
            if (_debugEnabled) DebugLog("[Power] WS switch: " + json);
            bool need; _swLock.Enter(); need = string.IsNullOrEmpty(_switchId); _swLock.Leave();
            if (need)
            {
                string id = DeConzJsonParser.ExtractTopLevelString(json, "id");
                string rs = DeConzJsonParser.ExtractTopLevelString(json, "r");
                if (!string.IsNullOrEmpty(id))
                {
                    var ip = DeConzBroker.GatewayIP;
                    _swLock.Enter();
                    _switchId  = id.Trim();
                    _switchRes = string.IsNullOrEmpty(rs) ? "lights" : rs.Trim();
                    _switchUrl      = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, _switchRes, _switchId);
                    _switchStateUrl = _switchUrl + "/state";
                    _swLock.Leave();
                    ScheduleGetSwitch();
                }
            }
            FireOnline(1); RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnSwitchRawJson, json);
            ParseSwitch(json); ParseBattery(json); ParseDeviceInfo(json);
        }

        private void OnPowerWs(string json)
        {
            if (_debugEnabled) DebugLog("[Power] WS power: " + json);
            ResolveUrl(json, _pwLock, ref _powerId, ref _powerRes, ref _powerUrl, "sensors", ScheduleGetPower);
            FireOnline(1); RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnPowerRawJson, json);
            ParsePower(json); ParseBattery(json); ParseDeviceInfo(json);
        }

        private void OnConsWs(string json)
        {
            if (_debugEnabled) DebugLog("[Power] WS cons: " + json);
            ResolveUrl(json, _consLock, ref _consId, ref _consRes, ref _consUrl, "sensors", ScheduleGetCons);
            FireOnline(1); RestartOnlineTimer();
            if (_rawJsonEnabled) FireChunked(OnConsumptionRawJson, json);
            ParseCons(json); ParseBattery(json); ParseDeviceInfo(json);
        }

        private void ResolveUrl(string json, CCriticalSection lk,
                                ref string id, ref string res, ref string url,
                                string defaultRes, Action scheduleGet)
        {
            bool need; lk.Enter(); need = string.IsNullOrEmpty(id); lk.Leave();
            if (!need) return;
            string newId = DeConzJsonParser.ExtractTopLevelString(json, "id");
            string newRs = DeConzJsonParser.ExtractTopLevelString(json, "r");
            if (string.IsNullOrEmpty(newId)) return;
            var ip = DeConzBroker.GatewayIP;
            lk.Enter();
            id  = newId.Trim();
            res = string.IsNullOrEmpty(newRs) ? defaultRes : newRs.Trim();
            url = string.Format("http://{0}/api/{1}/{2}/{3}", ip, _apiKey, res, id);
            lk.Leave();
            scheduleGet();
        }

        // ── JSON parsers ──────────────────────────────────────────────────

        private void ParseSwitch(string json)
        {
            try
            {
                bool? on = DeConzJsonParser.ExtractBool(json, "on", 2);
                if (on.HasValue) { Fire(OnOnFb, on.Value ? (ushort)1 : (ushort)0); Fire(OnOffFb, on.Value ? (ushort)0 : (ushort)1); }
            }
            catch (Exception ex) { Log("[Power] ParseSwitch error: " + ex.Message); }
        }

        private void ParsePower(string json)
        {
            try
            {
                int? pw = DeConzJsonParser.ExtractInt(json, "power", 2);
                if (pw.HasValue) Fire(OnPowerFb, (ushort)Math.Max(0, Math.Min(65535, pw.Value)));
                int? v = DeConzJsonParser.ExtractInt(json, "voltage", 2);
                if (v.HasValue) Fire(OnVoltageFb, (ushort)Math.Max(0, Math.Min(65535, v.Value)));
                int? a = DeConzJsonParser.ExtractInt(json, "current", 2);
                if (a.HasValue) Fire(OnCurrentFb, (ushort)Math.Max(0, Math.Min(65535, a.Value)));
            }
            catch (Exception ex) { Log("[Power] ParsePower error: " + ex.Message); }
        }

        private void ParseCons(string json)
        {
            try
            {
                int? c = DeConzJsonParser.ExtractInt(json, "consumption", 2);
                if (c.HasValue) Fire(OnConsumptionFb, (ushort)Math.Max(0, Math.Min(65535, c.Value)));
                int? pw = DeConzJsonParser.ExtractInt(json, "power", 2);
                if (pw.HasValue) Fire(OnConsumptionPowerFb, (ushort)Math.Max(0, Math.Min(65535, pw.Value)));
            }
            catch (Exception ex) { Log("[Power] ParseCons error: " + ex.Message); }
        }

        private void ParseBattery(string json)
        {
            try
            {
                int? b = DeConzJsonParser.ExtractInt(json, "battery", 2);
                if (b.HasValue) Fire(OnBatteryLevel, (ushort)Math.Max(0, Math.Min(100, b.Value)));
                bool? low = DeConzJsonParser.ExtractBool(json, "lowbattery", 2);
                if (low.HasValue) Fire(OnBatteryLow, low.Value ? (ushort)1 : (ushort)0);
            }
            catch (Exception ex) { Log("[Power] ParseBattery error: " + ex.Message); }
        }

        private void ParseDeviceInfo(string json)
        {
            try
            {
                string vDyn = DeConzJsonParser.ExtractTopLevelString(json, "lastseen");
                if (vDyn != null) FireStr(OnLastSeenFb, vDyn);
                vDyn = DeConzJsonParser.ExtractTopLevelString(json, "lastannounced");
                if (vDyn != null) FireStr(OnLastAnnouncedFb, vDyn);

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
            DebugLog("[Power] Stale (>1h no update) — resetting value outputs");
            ResetValueOutputs();
        }

        private void ResetValueOutputs()
        {
            Fire(OnOnline, 0);
            Fire(OnBatteryLow, 0);
            Fire(OnBatteryLevel, 0);
            Fire(OnOnFb, 0);
            Fire(OnOffFb, 0);
            Fire(OnPowerFb, 0);
            Fire(OnVoltageFb, 0);
            Fire(OnCurrentFb, 0);
            Fire(OnConsumptionPowerFb, 0);
        }

        private void RestartOnlineTimer()
        {
            _lastActivityUtc = DateTime.UtcNow;
            _staleResetDone  = false;
            if (_onlineTimer != null) _onlineTimer.Reset(_onlineTimeoutMs);
            else _onlineTimer = new CTimer(_ => { DebugLog("[Power] Online timeout"); FireOnline(0); _staticInfoSent = false; }, null, _onlineTimeoutMs);
        }
        private void StopOnlineTimer() { if (_onlineTimer == null) return; _onlineTimer.Stop(); _onlineTimer = null; }
        private void FireOnline(ushort v) { Fire(OnOnline, v); }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(PowerStringDelegate cb, string s)
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

        private static void Fire(PowerBoolDelegate cb, ushort v)  { if (cb != null) try { cb(v); } catch { } }
        private static void Fire(PowerLevelDelegate cb, ushort v) { if (cb != null) try { cb(v); } catch { } }
        private void FireStr(PowerStringDelegate cb, string s)
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

        // ── Permanent string re-assert ────────────────────────────────────
        // Periodically re-fire the cached (non-raw, non-debug) string outputs
        // while the global or this module's local enable is high, so late
        // joining sinks always see the current values.
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
                var cb = snap[i].Key as PowerStringDelegate;
                if (cb != null) try { cb(new SimplSharpString(snap[i].Value)); } catch { }
            }
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
