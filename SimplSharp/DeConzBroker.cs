/*******************************************************************************
 * DeConzBroker.cs
 *
 * Static message broker for inter-module communication.
 *
 * PATTERN:
 *   - The Gateway module calls DispatchUpdate() when a WebSocket message arrives.
 *   - Each remote Device module calls RegisterDevice() on startup with its
 *     uniqueid and a callback delegate.
 *   - The broker routes the JSON payload directly to the correct device module,
 *     without any STRING_OUTPUT signals in the SIMPL program.
 *
 *   Gateway Module                    DeConzBroker (static)
 *       │                                    │
 *       │── DispatchUpdate(uid, json) ───────►│
 *                                            │── callback(json) ──► Device Module A
 *                                            │── (no match)         Device Module B
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using System.Collections.Generic;
using Crestron.SimplSharp;

namespace DeConzZigbee
{
    /// <summary>
    /// Thread-safe static broker. All device modules share this single instance
    /// across the AppDomain – no signals needed between SIMPL modules.
    /// </summary>
    public static class DeConzBroker
    {
        private static readonly CCriticalSection _lock = new CCriticalSection();

        // uniqueid (lowercase, trimmed) → list of registered device callbacks.
        // Multiple modules may register the SAME uniqueid (e.g. a generic Device
        // module reading raw JSON alongside a typed Light module controlling the
        // same lamp). All registered callbacks receive every payload.
        private static readonly Dictionary<string, List<Action<string>>> _handlers
            = new Dictionary<string, List<Action<string>>>(StringComparer.OrdinalIgnoreCase);

        // uniqueid → WS-connected callback (fires after every successful handshake)
        // Used by light modules to schedule a delayed GetState() on connect/reconnect.
        private static readonly Dictionary<string, Action> _connectedCallbacks
            = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);

        // ── Gateway IP – written by DeConzWebSocketClient.Initialize() ──────
        // All device modules read this to build their HTTP request URLs.
        // No signal wiring required.
        public static string GatewayIP { get; internal set; }

        // ── WS send callback – set by DeConzWebSocketClient after handshake ───
        // Device modules call SendCommand() to push a command over the live WS.
        internal static Action<string> SendWsFrame { get; set; }

        /// <summary>
        /// Send a raw JSON string as a masked WebSocket text frame.
        /// Returns false when no WS connection is active.
        /// </summary>
        public static bool SendCommand(string json)
        {
            var cb = SendWsFrame;
            if (cb == null) return false;
            try { cb(json); return true; }
            catch (Exception ex)
            {
                Log("[Broker] SendCommand error: " + ex.Message);
                return false;
            }
        }

        // ── Debug hook (optional, gateway module can wire this up) ──────────
        public static Action<string> OnBrokerLog { get; set; }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Called by a remote Device module during its initialisation.
        /// </summary>
        /// <param name="uniqueId">Zigbee uniqueid as shown by deCONZ</param>
        /// <param name="callback">Delegate invoked with the raw JSON payload on every update</param>
        public static void RegisterDevice(string uniqueId, Action<string> callback)
        {
            if (string.IsNullOrEmpty(uniqueId) || callback == null) return;

            uniqueId = uniqueId.Trim().ToLowerInvariant();

            _lock.Enter();
            try
            {
                List<Action<string>> list;
                if (!_handlers.TryGetValue(uniqueId, out list))
                {
                    list = new List<Action<string>>();
                    _handlers[uniqueId] = list;
                }
                // Avoid registering the same delegate twice (idempotent re-init)
                if (!list.Contains(callback)) list.Add(callback);
            }
            finally { _lock.Leave(); }

            Log(string.Format("[Broker] Device registered – uid={0}", uniqueId));
        }

        /// <summary>
        /// Remove a single previously-registered callback for this uniqueid.
        /// Other modules sharing the same uniqueid remain registered.
        /// </summary>
        public static void UnregisterDevice(string uniqueId, Action<string> callback)
        {
            if (string.IsNullOrEmpty(uniqueId)) return;
            uniqueId = uniqueId.Trim().ToLowerInvariant();

            _lock.Enter();
            try
            {
                List<Action<string>> list;
                if (_handlers.TryGetValue(uniqueId, out list))
                {
                    if (callback != null) list.Remove(callback);
                    if (callback == null || list.Count == 0) _handlers.Remove(uniqueId);
                }
            }
            finally { _lock.Leave(); }

            Log(string.Format("[Broker] Device unregistered – uid={0}", uniqueId));
        }

        /// <summary>
        /// Backwards-compatible overload: removes ALL callbacks for this uniqueid.
        /// Prefer the (uniqueId, callback) overload when multiple modules may
        /// share a uniqueid.
        /// </summary>
        public static void UnregisterDevice(string uniqueId)
        {
            UnregisterDevice(uniqueId, null);
        }

        /// <summary>
        /// Called internally by the Gateway when a WebSocket message is parsed.
        /// Finds the registered handler and invokes it on a worker thread.
        /// </summary>
        internal static void DispatchUpdate(string uniqueId, string jsonPayload)
        {
            if (string.IsNullOrEmpty(uniqueId)) return;
            uniqueId = uniqueId.Trim().ToLowerInvariant();

            Action<string>[] handlers = null;

            _lock.Enter();
            try
            {
                List<Action<string>> list;
                if (_handlers.TryGetValue(uniqueId, out list) && list.Count > 0)
                    handlers = list.ToArray();   // snapshot so we invoke outside the lock
            }
            finally { _lock.Leave(); }

            if (handlers != null)
            {
                foreach (var h in handlers)
                {
                    var localH = h;
                    CrestronInvoke.BeginInvoke(_ =>
                    {
                        try { localH(jsonPayload); }
                        catch (Exception ex)
                        {
                            Log(string.Format("[Broker] Handler exception uid={0}: {1}", uniqueId, ex.Message));
                        }
                    });
                }
            }
            else
            {
                Log(string.Format("[Broker] Unregistered device – uid={0}", uniqueId));
            }
        }

        /// <summary>Number of distinct uniqueids with at least one handler.</summary>
        public static int RegisteredCount
        {
            get
            {
                _lock.Enter();
                try { return _handlers.Count; }
                finally { _lock.Leave(); }
            }
        }

        // ── WS-connected notification ────────────────────────────────────────

        /// <summary>
        /// Called by light / device modules that want to refresh state on
        /// every new WS connection (first connect and reconnects).
        /// callback is invoked on a worker thread with a random 1-15 s delay.
        /// </summary>
        internal static void RegisterConnectedCallback(string uniqueId, Action callback)
        {
            if (string.IsNullOrEmpty(uniqueId) || callback == null) return;
            uniqueId = uniqueId.Trim().ToLowerInvariant();
            _lock.Enter();
            try { _connectedCallbacks[uniqueId] = callback; }
            finally { _lock.Leave(); }
        }

        internal static void UnregisterConnectedCallback(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return;
            uniqueId = uniqueId.Trim().ToLowerInvariant();
            _lock.Enter();
            try { _connectedCallbacks.Remove(uniqueId); }
            finally { _lock.Leave(); }
        }

        /// <summary>
        /// Called by DeConzWebSocketClient after each successful WS handshake.
        /// Notifies all registered modules so they can schedule a GetState().
        /// </summary>
        internal static void NotifyWsConnected()
        {
            Action[] snapshot;
            _lock.Enter();
            try
            {
                snapshot = new Action[_connectedCallbacks.Count];
                _connectedCallbacks.Values.CopyTo(snapshot, 0);
            }
            finally { _lock.Leave(); }

            foreach (var cb in snapshot)
            {
                var localCb = cb;
                CrestronInvoke.BeginInvoke(_ =>
                {
                    try { localCb(); }
                    catch (Exception ex)
                    {
                        Log("[Broker] ConnectedCallback error: " + ex.Message);
                    }
                });
            }

            Log(string.Format("[Broker] WS connected – notified {0} module(s)", snapshot.Length));
        }

        private static void Log(string message)
        {
            var cb = OnBrokerLog;
            if (cb != null) try { cb(message); } catch { }
        }
    }
}
