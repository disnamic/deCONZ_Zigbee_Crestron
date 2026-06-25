/*******************************************************************************
 * DeConzScene.cs
 *
 * SIMPL# class – deCONZ Zigbee scene control.
 *
 * Recalls and stores scenes within a group via HTTP PUT.
 * Group ID and Scene ID are supplied at runtime via SIMPL+ analog inputs.
 *
 * URL pattern:
 *   Recall  PUT  /api/<key>/groups/<group_id>/scenes/<scene_id>/recall
 *   Store   PUT  /api/<key>/groups/<group_id>/scenes/<scene_id>/store
 *   List    GET  /api/<key>/groups/<group_id>/scenes
 *
 * Pure HTTP module – no WebSocket / broker registration.
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
    public delegate void SceneBoolDelegate(ushort value);
    public delegate void SceneLevelDelegate(ushort value);
    public delegate void SceneStringDelegate(SimplSharpString value);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzScene
    {
        // ── Delegates ────────────────────────────────────────────────────

        public SceneBoolDelegate   OnRecallSuccessFb      { get; set; }  // pulse on 200 OK
        public SceneBoolDelegate   OnStoreSuccessFb       { get; set; }  // pulse on 200 OK
        public SceneBoolDelegate   OnCreateSuccessFb      { get; set; }  // pulse on POST OK
        public SceneLevelDelegate  OnNewSceneIdFb         { get; set; }  // ID of newly created scene
        public SceneBoolDelegate   OnDeleteSuccessFb      { get; set; }  // pulse on DELETE OK
        public SceneBoolDelegate   OnSetAttrSuccessFb     { get; set; }  // pulse on rename OK
        public SceneStringDelegate OnSceneAttrFb          { get; set; }  // GET single scene JSON
        public SceneLevelDelegate  OnLastRecalledGroupFb  { get; set; }  // last group ID recalled
        public SceneLevelDelegate  OnLastRecalledSceneFb  { get; set; }  // last scene ID recalled
        public SceneStringDelegate OnScenesListFb         { get; set; }  // "1:warmlight,2:reading"
        public SceneStringDelegate OnRawJsonFb            { get; set; }
        public SceneStringDelegate OnDebugOut             { get; set; }

        // ── Private state ─────────────────────────────────────────────────
        private string _apiKey { get { return DeConzBroker.ApiKey ?? ""; } }
        private bool   _debugEnabled;
        private bool   _rawJsonEnabled;

        // Runtime values set from SIMPL+ analog inputs
        private int _groupId;
        private int _sceneId;

        // Captured at RecallScene() time for use in the async callback
        private int    _pendingRecallGroup;
        private int    _pendingRecallScene;

        // Name used by CreateScene() and SetSceneName()
        private string _pendingSceneName = "";

        private readonly HttpClient       _http    = new HttpClient();
        private readonly CCriticalSection _cmdLock = new CCriticalSection();

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize()
        {
            _http.TimeoutEnabled = true;
            _http.Timeout        = 10;
            DebugLog("[Scene] Initialized");
        }

        public void SetDebug(ushort enable)          { _debugEnabled   = (enable != 0); }
        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }

        /// <summary>Set the target group ID at runtime.</summary>
        public void SetGroupId(ushort id) { _groupId = id; }

        /// <summary>Set the target scene ID at runtime.</summary>
        public void SetSceneId(ushort id)     { _sceneId = id; }
        public void SetNewSceneName(string n) { _pendingSceneName = n ?? ""; }

        // ── Commands ──────────────────────────────────────────────────────

        /// <summary>Create a new scene. Name set via SetNewSceneName().</summary>
        public void CreateScene()
        {
            if (string.IsNullOrEmpty(_pendingSceneName)) { Log("[Scene] CreateScene: no name set"); return; }
            if (_groupId <= 0) { Log("[Scene] CreateScene: no group ID"); return; }
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip)) { Log("[Scene] No gateway IP"); return; }
            string url = string.Format("http://{0}/api/{1}/groups/{2}/scenes", ip, _apiKey, _groupId);
            string body = "{\"name\":\"" + _pendingSceneName.Replace("\"", "") + "\""+"}";
            if (_debugEnabled) DebugLog("[Scene] POST create " + body);
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
            catch (Exception ex) { Log("[Scene] POST error: " + ex.Message); }
        }

        /// <summary>Delete scene (Group_ID + Scene_ID).</summary>
        public void DeleteScene()
        {
            if (!ValidIds("Delete")) return;
            string url = SceneUrl("");
            DebugLog(string.Format("[Scene] DELETE g={0} s={1}", _groupId, _sceneId));
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Delete;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnDeleteResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Scene] DELETE error: " + ex.Message); }
        }

        /// <summary>Rename scene. Name set via SetNewSceneName().</summary>
        public void SetSceneName()
        {
            if (string.IsNullOrEmpty(_pendingSceneName)) { Log("[Scene] SetSceneName: no name"); return; }
            if (!ValidIds("SetName")) return;
            string url = SceneUrl("");
            string body = "{\"name\":\"" + _pendingSceneName.Replace("\"", "") + "\""+"}";
            if (_debugEnabled) DebugLog("[Scene] PUT rename " + body);
            SendPut(url, body, OnSetAttrResponse);
        }

        /// <summary>GET attributes of the current scene (lights, name, stored states).</summary>
        public void GetSceneAttr()
        {
            if (!ValidIds("GetAttr")) return;
            string url = SceneUrl("");
            DebugLog(string.Format("[Scene] GET attr g={0} s={1}", _groupId, _sceneId));
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnSceneAttrResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Scene] GET attr error: " + ex.Message); }
        }

        /// <summary>Recall the scene – all lights in the group go to stored state.</summary>
        public void RecallScene()
        {
            if (!ValidIds("Recall")) return;
            _pendingRecallGroup = _groupId;
            _pendingRecallScene = _sceneId;
            string url = SceneUrl("recall");
            DebugLog(string.Format("[Scene] Recall g={0} s={1}", _groupId, _sceneId));
            SendPut(url, "", OnRecallResponse);
        }

        /// <summary>Store current group light states into the scene.</summary>
        public void StoreScene()
        {
            if (!ValidIds("Store")) return;
            string url = SceneUrl("store");
            DebugLog(string.Format("[Scene] Store g={0} s={1}", _groupId, _sceneId));
            SendPut(url, "", OnStoreResponse);
        }

        /// <summary>GET all scenes of the current group.</summary>
        public void GetScenes()
        {
            if (_groupId <= 0)
            { Log("[Scene] GetScenes: no group ID set"); return; }
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip))
            { Log("[Scene] No gateway IP"); return; }

            string url = string.Format("http://{0}/api/{1}/groups/{2}/scenes",
                             ip, _apiKey, _groupId);
            DebugLog("[Scene] GET " + url);
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType = RequestType.Get;
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, OnGetScenesResponse); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Scene] GET error: " + ex.Message); }
        }

        // ── HTTP helpers ──────────────────────────────────────────────────

        private bool ValidIds(string op)
        {
            var ip = DeConzBroker.GatewayIP;
            if (string.IsNullOrEmpty(ip))
            { Log("[Scene] " + op + ": no gateway IP"); return false; }
            if (_groupId <= 0)
            { Log("[Scene] " + op + ": no group ID set"); return false; }
            if (_sceneId <= 0)
            { Log("[Scene] " + op + ": no scene ID set"); return false; }
            return true;
        }

        private string SceneUrl(string action)
        {
            string base_ = string.Format("http://{0}/api/{1}/groups/{2}/scenes/{3}",
                               DeConzBroker.GatewayIP, _apiKey, _groupId, _sceneId);
            return string.IsNullOrEmpty(action) ? base_ : base_ + "/" + action;
        }

        private void SendPut(string url, string body,
                             HTTPClientResponseCallback callback)
        {
            try
            {
                var req = new HttpClientRequest();
                req.Url.Parse(url);
                req.RequestType   = RequestType.Put;
                req.ContentString = body;
                req.Header.SetHeaderValue("Content-Type", "application/json");
                _cmdLock.Enter();
                try { _http.DispatchAsync(req, callback); }
                finally { _cmdLock.Leave(); }
            }
            catch (Exception ex) { Log("[Scene] PUT error: " + ex.Message); }
        }

        // ── HTTP response handlers ────────────────────────────────────────

        private void OnRecallResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Scene] Recall error: " + err); return; }
            DebugLog("[Scene] Recall resp: " + resp.ContentString);
            if (_rawJsonEnabled) FireChunked(OnRawJsonFb, resp.ContentString);

            // Fire success pulse + update last-recalled feedback
            Fire(OnRecallSuccessFb, 1);
            CTimer tr = null;
            tr = new CTimer(_ => { Fire(OnRecallSuccessFb, 0); if (tr != null) tr.Dispose(); }, null, 200);
            Fire(OnLastRecalledGroupFb, (ushort)Math.Max(0, Math.Min(65535, _pendingRecallGroup)));
            Fire(OnLastRecalledSceneFb, (ushort)Math.Max(0, Math.Min(65535, _pendingRecallScene)));
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnStoreResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Scene] Store error: " + err); return; }
            DebugLog("[Scene] Store resp: " + resp.ContentString);
            Fire(OnStoreSuccessFb, 1);
            CTimer ts = null;
            ts = new CTimer(_ => { Fire(OnStoreSuccessFb, 0); if (ts != null) ts.Dispose(); }, null, 200);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnGetScenesResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED)
            { Log("[Scene] GET scenes error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Scene] GET scenes resp: " + body);
            if (_rawJsonEnabled) FireChunked(OnRawJsonFb, body);
            string list = ParseScenesList(body);
            if (list != null) FireStr(OnScenesListFb, list);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        // ── JSON parser ───────────────────────────────────────────────────

        private void OnCreateResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Scene] POST error: " + err); return; }
            DebugLog("[Scene] POST resp: " + resp.ContentString);
            int vp = DeConzJsonParser.FindValueStart(resp.ContentString, "id", 0);
            if (vp >= 0 && vp < resp.ContentString.Length && resp.ContentString[vp] == '"') {
                int end = resp.ContentString.IndexOf('"', vp + 1);
                if (end > vp) { int id; if (int.TryParse(resp.ContentString.Substring(vp+1,end-vp-1), out id)) Fire(OnNewSceneIdFb,(ushort)Math.Max(0,Math.Min(65535,id))); }
            }
            Fire(OnCreateSuccessFb, 1);
            CTimer tc = null;
            tc = new CTimer(_ => { Fire(OnCreateSuccessFb, 0); if (tc != null) tc.Dispose(); }, null, 200);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnDeleteResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Scene] DELETE error: " + err); return; }
            DebugLog("[Scene] DELETE resp: " + resp.ContentString);
            Fire(OnDeleteSuccessFb, 1);
            CTimer td = null;
            td = new CTimer(_ => { Fire(OnDeleteSuccessFb, 0); if (td != null) td.Dispose(); }, null, 200);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnSetAttrResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Scene] PUT attr error: " + err); return; }
            DebugLog("[Scene] PUT attr resp: " + resp.ContentString);
            Fire(OnSetAttrSuccessFb, 1);
            CTimer ta = null;
            ta = new CTimer(_ => { Fire(OnSetAttrSuccessFb, 0); if (ta != null) ta.Dispose(); }, null, 200);
            GetScenes();
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        private void OnSceneAttrResponse(HttpClientResponse resp, HTTP_CALLBACK_ERROR err)
        {
            try
            {
            if (err != HTTP_CALLBACK_ERROR.COMPLETED) { Log("[Scene] GET attr error: " + err); return; }
            var body = resp.ContentString;
            if (string.IsNullOrEmpty(body)) return;
            if (_debugEnabled) DebugLog("[Scene] GET attr resp: " + body);
            FireChunked(OnSceneAttrFb, body);
            if (_rawJsonEnabled) FireChunked(OnRawJsonFb, body);
        }
            finally { if (resp != null) resp.Dispose(); }
        }

        /// <summary>
        /// Parses GET /groups/<id>/scenes response.
        /// Format: {"1":{"lights":["1","2"],"name":"working"},"2":{...}}
        /// Returns: "1:working,2:reading"
        /// </summary>
        private static string ParseScenesList(string json)
        {
            var sb = new StringBuilder();
            int pos = 0;
            while (pos < json.Length)
            {
                // Find next top-level key (the scene ID)
                int q1 = json.IndexOf('"', pos);
                if (q1 < 0) break;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string sceneId = json.Substring(q1 + 1, q2 - q1 - 1);
                pos = q2 + 1;

                // Skip colon and whitespace
                while (pos < json.Length && json[pos] != '{') pos++;
                if (pos >= json.Length) break;

                // Find closing brace of this scene object (depth-aware)
                int depth = 0; int objEnd = pos;
                for (int i = pos; i < json.Length; i++)
                {
                    if (json[i] == '{') depth++;
                    else if (json[i] == '}') { if (--depth == 0) { objEnd = i; break; } }
                }
                string entry = json.Substring(pos, objEnd - pos + 1);

                // Extract name from entry
                string name = null;
                int vp = DeConzJsonParser.FindValueStart(entry, "name", 0);
                if (vp >= 0 && vp < entry.Length && entry[vp] == '"')
                {
                    int end = entry.IndexOf('"', vp + 1);
                    if (end >= 0) name = entry.Substring(vp + 1, end - vp - 1);
                }

                if (name != null && sceneId.Length > 0 &&
                    char.IsDigit(sceneId[0]))   // skip non-numeric keys
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(sceneId + ":" + name);
                }
                pos = objEnd + 1;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }


        /// <summary>Fires a potentially long string in 250-character chunks.</summary>
        private static void FireChunked(SceneStringDelegate cb, string s)
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

        private static void Fire(SceneBoolDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void Fire(SceneLevelDelegate cb, ushort v)
        { if (cb != null) try { cb(v); } catch { } }

        private static void FireStr(SceneStringDelegate cb, string s)
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
