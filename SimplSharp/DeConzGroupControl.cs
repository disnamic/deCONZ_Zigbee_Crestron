/*******************************************************************************
 * DeConzGroupControl.cs
 *
 * SIMPL# class – deCONZ Zigbee group control.
 *
 * Controls one or more groups via HTTP PUT to /groups/<id>/action.
 * Group IDs are supplied at runtime via SIMPL+ analog inputs – no parameters
 * required beyond API_Key. Up to 4 group IDs can be targeted simultaneously;
 * the same command is sent to each active ID sequentially.
 *
 * State feedback is obtained via HTTP GET /groups/<id> (primary ID only).
 *
 * Pure HTTP module – no WebSocket / broker registration.
 *
 * URL pattern:
 *   State   GET  /api/<key>/groups/<id>
 *   Action  PUT  /api/<key>/groups/<id>/action
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using System.Text;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void GroupBoolDelegate(ushort value);
    public delegate void GroupLevelDelegate(ushort value);
    public delegate void GroupStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzGroupControl
    {
        // ── Delegates ────────────────────────────────────────────────────

        // State feedback (from GET /groups/<id>)
        public GroupBoolDelegate   OnAllOnFb       { get; set; }   // state.all_on
        public GroupBoolDelegate   OnAnyOnFb       { get; set; }   // state.any_on
        public GroupLevelDelegate  OnBrightnessFb  { get; set; }   // action.bri
        public GroupLevelDelegate  OnColortempFb   { get; set; }   // action.ct → Kelvin
        public GroupStringDelegate OnColormodeFb   { get; set; }   // action.colormode
        public GroupStringDelegate OnGroupNameFb   { get; set; }   // name
        public GroupStringDelegate OnMembersFb     { get; set; }   // lights → "3,42,43"
        public GroupStringDelegate OnScenesFb      { get; set; }   // scenes → "1:warmlight"
        public GroupBoolDelegate   OnDeleteSuccessFb  { get; set; }  // 200ms pulse on DELETE OK
        public GroupBoolDelegate   OnCreateSuccessFb  { get; set; }  // 200ms pulse on POST OK
        public GroupLevelDelegate  OnNewGroupIdFb     { get; set; }  // ID of newly created group
        public GroupBoolDelegate   OnSetAttrSuccessFb { get; set; }  // 200ms pulse on PUT attr OK
        public GroupStringDelegate OnAllGroupsFb      { get; set; }  // GET /groups JSON
        public GroupStringDelegate OnRawJsonFb        { get; set; }
        public GroupStringDelegate OnDebugOut      { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey;
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;

        // Runtime group IDs (set from SIMPL+ analog inputs)
        // Slot 0 = primary. Slots 1-3 = optional (0 = not active)
        private readonly int[] _groupIds = new int[4];

        // Last transitiontime sent with commands (1/10 seconds)
        private int    _transitiontime;

        // String inputs set from SIMPL+ before a command pulse
        private string _pendingGroupName = "";
        private string _pendingMembers   = "";

        private int _lastActionSlot;   // slot index of last dispatched action

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize(string apiKey)
        {
            _apiKey = apiKey ?? "";
            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;
            DebugLog("[GroupControl] Initialized");
        }

        public void SetDebug(ushort enable)          { _debugEnabled    = (enable != 0); }
        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled  = (enable != 0); }

        /// <summary>Set primary group ID (slot 1).</summary>
        public void SetGroupId(ushort id)  { _groupIds[0] = id; }
        /// <summary>Set optional group IDs (slots 2-4). 0 = disabled.</summary>
        public void SetGroupId2(ushort id) { _groupIds[1] = id; }
        public void SetGroupId3(ushort id) { _groupIds[2] = id; }
        public void SetGroupId4(ushort id) { _groupIds[3] = id; }

        /// <summary>Transition time in 1/10 seconds, applied to next command.</summary>
        public void SetTransitiontime(ushort tt) { _transitiontime = tt; }

        /// <summary>Group name used by CreateGroup() and SetName().</summary>
        public void SetNewGroupName(string name) { _pendingGroupName = name ?? ""; }

        /// <summary>Comma-separated light IDs used by SetMembers(), e.g. "3,42,43".</summary>
        public void SetMembersIn(string members) { _pendingMembers = members ?? ""; }

        // ── Commands ──────────────────────────────────────────────────────

        public void SetOn()  { SendAction("{\"on\":true}");  }
        public void SetOff() { SendAction("{\"on\":false}"); }
        public void Toggle() { SendAction("{\"toggle\":true}"); }

        public void SetBrightness(ushort bri)
        {
            SendAction(BuildJson("\"bri\":" + Math.Min(254, (int)bri)));
        }

        public void SetColortemp(ushort kelvin)
        {
            if (kelvin < 1) return;
            int mired = Math.Min(500, Math.Max(153, 1000000 / kelvin));
            SendAction(BuildJson("\"ct\":" + mired));
        }

        public void SetHue(ushort hue)
        {
            SendAction(BuildJson("\"hue\":" + hue));
        }

        public void SetSat(ushort sat)
        {
            SendAction(BuildJson("\"sat\":" + Math.Min(254, (int)sat)));
        }

        public void AlertSelect() { SendAction("{\"alert\":\"select\"}");  }
        public void AlertLong()   { SendAction("{\"alert\":\"lselect\"}"); }
        public void AlertNone()   { SendAction("{\"alert\":\"none\"}");    }

        public void EffectColorloop() { SendAction("{\"effect\":\"colorloop\"}"); }
        public void EffectNone()      { SendAction("{\"effect\":\"none\"}");      }

        public void GetState()
        {
            int id = _groupIds[0];
            if (id <= 0)
            {
                Log("[GroupControl] GetState: no group ID set");
                return;
            }
            string url = BuildBaseUrl(id);
            DebugLog("[GroupControl] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnGetResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[GroupControl] GET error: " + ex.Message); }
        }

        public void DeleteGroup()
        {
            int id = _groupIds[0];
            if (id <= 0) { Log("[GroupControl] DeleteGroup: no group ID set"); return; }
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip)) { Log("[GroupControl] No gateway IP"); return; }
            string url = BuildBaseUrl(id);
            DebugLog("[GroupControl] DELETE g=" + id);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Delete;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnDeleteResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[GroupControl] DELETE error: " + ex.Message); }
        }

        /// <summary>Create a new group with the name set via SetNewGroupName().
        /// The new group ID is reported on OnNewGroupIdFb.</summary>
        public void CreateGroup()
        {
            if (string.IsNullOrEmpty(_pendingGroupName))
            { Log("[GroupControl] CreateGroup: no name set via New_Group_Name"); return; }
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip)) { Log("[GroupControl] No gateway IP"); return; }
            string url = string.Format("http://{0}/api/{1}/groups", ip, _apiKey);
            string body = "{\"name\":\"" + _pendingGroupName.Replace("\"", "") + "\"}";
            if (_debugEnabled) DebugLog("[GroupControl] POST (create) " + body);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType   = RequestType.Post;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnCreateResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[GroupControl] POST error: " + ex.Message); }
        }

        /// <summary>Rename the primary group to the name set via SetNewGroupName().</summary>
        public void SetName()
        {
            if (string.IsNullOrEmpty(_pendingGroupName))
            { Log("[GroupControl] SetName: no name set via New_Group_Name"); return; }
            SendAttrPut("{\"name\":\"" + _pendingGroupName.Replace("\"", "") + "\"}");
        }

        /// <summary>Set group members from comma-separated light IDs in SetMembersIn().
        /// e.g. "3,42,43" → {"lights":["3","42","43"]}</summary>
        public void SetMembers()
        {
            if (string.IsNullOrEmpty(_pendingMembers))
            { Log("[GroupControl] SetMembers: no members set via Members_In"); return; }
            string json = BuildLightsJson(_pendingMembers);
            if (json == null) { Log("[GroupControl] SetMembers: invalid members string"); return; }
            SendAttrPut(json);
        }

        /// <summary>GET /groups – list all groups as raw JSON on OnAllGroupsFb.</summary>
        public void GetAllGroups()
        {
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip)) { Log("[GroupControl] No gateway IP"); return; }
            string url = string.Format("http://{0}/api/{1}/groups", ip, _apiKey);
            DebugLog("[GroupControl] GET all groups");
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnAllGroupsResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[GroupControl] GET all error: " + ex.Message); }
        }

        // ── HTTP helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Wraps a JSON body fragment with optional transitiontime and sends
        /// to all active group IDs.
        /// </summary>
        private string BuildJson(string innerField)
        {
            if (_transitiontime > 0)
                return "{" + innerField + ",\"transitiontime\":" + _transitiontime + "}";
            return "{" + innerField + "}";
        }

        private void SendAction(string body)
        {
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip))
            {
                Log("[GroupControl] No gateway IP – is Gateway module initialized?");
                return;
            }

            for (int i = 0; i < _groupIds.Length; i++)
            {
                int id = _groupIds[i];
                if (id <= 0) continue;   // 0 = slot not active

                string url = string.Format("http://{0}/api/{1}/groups/{2}/action",
                                 ip, _apiKey, id);
                if (_debugEnabled) DebugLog("[GroupControl] PUT g=" + id + " " + body);
                int captured = i;
                string capturedBody = body;
                string capturedUrl  = url;
                try
                {
                    _cmdLock.Enter();
                    try
                    {
                        _lastActionSlot = captured;
                        var req = new HttpClientRequest();
                        req.Url.Parse(capturedUrl);
                        req.RequestType   = RequestType.Put;
                        req.ContentString = capturedBody;
                        req.Header.SetHeaderValue("Content-Type", "application/json");
                        _http.DispatchAsync(req, OnActionResponse);
                    }
                    finally { _cmdLock.Leave(); }
                }
                catch (Exception ex)
                {
                    Log("[GroupControl] PUT error g=" + id + ": " + ex.Message);
                }
            }
        }

        /// <summary>PUT to /groups/<id> (attributes, not action).</summary>
        private void SendAttrPut(string body)
        {
            int id = _groupIds[0];
            if (id <= 0) { Log("[GroupControl] SendAttrPut: no group ID"); return; }
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip)) { Log("[GroupControl] No gateway IP"); return; }
            string url = BuildBaseUrl(id);
            if (_debugEnabled) DebugLog("[GroupControl] PUT attr g=" + id + " " + body);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType   = RequestType.Put;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnSetAttrResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[GroupControl] PUT attr error: " + ex.Message); }
        }

        /// <summary>Converts "3,42,43" to {"lights":["3","42","43"]}.</summary>
        private static string BuildLightsJson(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            var sb = new StringBuilder("{\"lights\":[");
            string[] parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string id = parts[i].Trim();
                if (string.IsNullOrEmpty(id)) continue;
                if (i > 0) sb.Append(',');
                sb.Append('\"').Append(id).Append('\"');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string BuildBaseUrl(int groupId)
        {
            return string.Format("http://{0}/api/{1}/groups/{2}",
                       DeConzBroker.GatewayIP, _apiKey, groupId);
        }

        // ── HTTP response handlers ────────────────────────────────────────

        private void OnActionResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[GroupControl] PUT error slot=" + _lastActionSlot + ": " + err); return; }
            DebugLog("[GroupControl] PUT resp slot=" + _lastActionSlot + ": " + resp.ContentString);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnGetResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[GroupControl] GET error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[GroupControl] GET resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnRawJsonFb, body);
            ParseState(body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnDeleteResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[GroupControl] DELETE error: " + err); return; }
            DebugLog("[GroupControl] DELETE resp: " + resp.ContentString);
            Fire(OnDeleteSuccessFb, 1);
            CTimer t = null;
            t = new CTimer(_ => { Fire(OnDeleteSuccessFb, 0); if (t != null) t.Dispose(); }, null, 200);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnCreateResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[GroupControl] POST error: " + err); return; }
            DebugLog("[GroupControl] POST resp: " + resp.ContentString);
            // Parse new group ID from [{"success":{"id":"3"}}]
            int vp = DeConzJsonParser.FindValueStart(resp.ContentString, "id", 0);
            if (vp >= 0)
            {
                string body = resp.ContentString;
                if (body[vp] == '"')
                {
                    int end = body.IndexOf('"', vp + 1);
                    if (end > vp)
                    {
                        string idStr = body.Substring(vp + 1, end - vp - 1);
                        int newId;
                        if (int.TryParse(idStr, out newId))
                            Fire(OnNewGroupIdFb, (ushort)Math.Max(0, Math.Min(65535, newId)));
                    }
                }
            }
            Fire(OnCreateSuccessFb, 1);
            CTimer t = null;
            t = new CTimer(_ => { Fire(OnCreateSuccessFb, 0); if (t != null) t.Dispose(); }, null, 200);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnSetAttrResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[GroupControl] PUT attr error: " + err); return; }
            DebugLog("[GroupControl] PUT attr resp: " + resp.ContentString);
            Fire(OnSetAttrSuccessFb, 1);
            CTimer t = null;
            t = new CTimer(_ => { Fire(OnSetAttrSuccessFb, 0); if (t != null) t.Dispose(); }, null, 200);
            // Refresh state so name/members outputs update
            GetState();
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnAllGroupsResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[GroupControl] GET all error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            DebugLog("[GroupControl] GET all resp len=" + body.Length);

            // Deliver in 250-character chunks so SIMPL+ string outputs are not overflowed.
            // The receiving SIMPL+ logic can reassemble by concatenating until an empty chunk.
            const int ChunkSize = 250;
            int pos = 0;
            while (pos < body.Length)
            {
                int len = Math.Min(ChunkSize, body.Length - pos);
                FireChunked(OnAllGroupsFb, body.Substring(pos, len));
                pos += len;
            }
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── JSON parser ───────────────────────────────────────────────────

        private void ParseState(string json)
        {
            try
            {
                // state.all_on / state.any_on (depth 2)
                bool? allOn = DeConzJsonParser.ExtractBool(json, "all_on", 2);
                if (allOn.HasValue) Fire(OnAllOnFb, allOn.Value ? (ushort)1 : (ushort)0);

                bool? anyOn = DeConzJsonParser.ExtractBool(json, "any_on", 2);
                if (anyOn.HasValue) Fire(OnAnyOnFb, anyOn.Value ? (ushort)1 : (ushort)0);

                // action.bri / action.ct / action.colormode (depth 2)
                int? bri = DeConzJsonParser.ExtractInt(json, "bri", 2);
                if (bri.HasValue)
                    Fire(OnBrightnessFb, (ushort)Math.Max(0, Math.Min(254, bri.Value)));

                int? ct = DeConzJsonParser.ExtractInt(json, "ct", 2);
                if (ct.HasValue && ct.Value > 0)
                    Fire(OnColortempFb, (ushort)Math.Min(65535, 1000000 / ct.Value));

                string cm = DeConzJsonParser.ExtractString(json, "colormode", 2);
                if (cm != null) FireStr(OnColormodeFb, cm);

                // Top-level name (depth 1)
                string name = DeConzJsonParser.ExtractTopLevelString(json, "name");
                if (name != null) FireStr(OnGroupNameFb, name);

                // lights array → comma-separated string
                string members = ExtractArray(json, "lights");
                if (members != null) FireStr(OnMembersFb, members);

                // scenes array → "id:name,id:name"
                string scenes = ExtractScenes(json);
                if (scenes != null) FireStr(OnScenesFb, scenes);
            }
            catch (Exception ex)
            {
                Log("[GroupControl] ParseState error: " + ex.Message);
            }
        }

        /// <summary>Extracts a JSON string array and returns as comma-separated.</summary>
        private static string ExtractArray(string json, string key)
        {
            int keyPos = json.IndexOf("\"" + key + "\"",
                             StringComparison.OrdinalIgnoreCase);
            if (keyPos < 0) return null;
            int bracket = json.IndexOf('[', keyPos);
            if (bracket < 0) return null;
            int end = json.IndexOf(']', bracket);
            if (end < 0) return null;
            string inner = json.Substring(bracket + 1, end - bracket - 1);
            var sb = new StringBuilder();
            int i = 0;
            while (i < inner.Length)
            {
                int q1 = inner.IndexOf('"', i);
                if (q1 < 0) break;
                int q2 = inner.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(inner.Substring(q1 + 1, q2 - q1 - 1));
                i = q2 + 1;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>Extracts scenes array → "1:warmlight,2:reading".</summary>
        private static string ExtractScenes(string json)
        {
            int scenesPos = json.IndexOf("\"scenes\"",
                               StringComparison.OrdinalIgnoreCase);
            if (scenesPos < 0) return null;
            int bracket = json.IndexOf('[', scenesPos);
            if (bracket < 0) return null;
            int end = json.IndexOf(']', bracket);
            if (end < 0) return null;
            string inner = json.Substring(bracket + 1, end - bracket - 1);
            // Each scene: {"id":"1","name":"warmlight"}
            var sb = new StringBuilder();
            int pos = 0;
            while (pos < inner.Length)
            {
                int obj = inner.IndexOf('{', pos);
                if (obj < 0) break;
                int objEnd = inner.IndexOf('}', obj);
                if (objEnd < 0) break;
                string entry = inner.Substring(obj + 1, objEnd - obj - 1);
                string id   = ExtractSimpleString(entry, "id");
                string nm   = ExtractSimpleString(entry, "name");
                if (id != null && nm != null)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(id + ":" + nm);
                }
                pos = objEnd + 1;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string ExtractSimpleString(string json, string key)
        {
            int vp = DeConzJsonParser.FindValueStart(json, key, 0);
            if (vp < 0 || vp >= json.Length || json[vp] != '"') return null;
            int end = json.IndexOf('"', vp + 1);
            if (end < 0) return null;
            return json.Substring(vp + 1, end - vp - 1);
        }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(GroupStringDelegate cb, string s)
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

        private static void Fire(GroupBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(GroupLevelDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void FireStr(GroupStringDelegate cb, string s)
        {
            if (cb == null || s == null) return;
            if (s.Length > 250) s = s.Substring(0, 65000);
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
