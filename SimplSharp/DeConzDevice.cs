/*******************************************************************************
 * DeConzDevice.cs
 *
 * SIMPL# class – raw JSON passthrough for one or more Zigbee endpoints.
 *
 * One primary UniqueID is always required (Initialize param 1).
 * Up to 5 additional UniqueIDs can be registered via AddUniqueId(n, uid),
 * called from SIMPL+ Main() before Initialize(). Each additional UID has its
 * own callback slot (OnRawJson2 … OnRawJson6) so payloads from different
 * endpoints are delivered on separate SIMPL+ STRING_OUTPUTs.
 *
 * Additional UIDs are optional: an empty/null value is silently skipped.
 * This matches the Valve module pattern where two endpoints share one object.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using Crestron.SimplSharp;

namespace DeConzZigbee
{
    // ── Delegates ────────────────────────────────────────────────────────────
    public delegate void DeviceRawJsonDelegate(SimplSharpString json);

    // ────────────────────────────────────────────────────────────────────────
    public class DeConzDevice
    {
        // ── Delegates (one per UID slot) ─────────────────────────────────
        public DeviceRawJsonDelegate OnRawJson  { get; set; }   // UID 1 (primary)
        public DeviceRawJsonDelegate OnRawJson2 { get; set; }   // UID 2 (optional)
        public DeviceRawJsonDelegate OnRawJson3 { get; set; }   // UID 3 (optional)
        public DeviceRawJsonDelegate OnRawJson4 { get; set; }   // UID 4 (optional)
        public DeviceRawJsonDelegate OnRawJson5 { get; set; }   // UID 5 (optional)
        public DeviceRawJsonDelegate OnRawJson6   { get; set; }  // UID 6 (optional)
        public DeviceRawJsonDelegate OnRawJsonAll { get; set; }  // all UIDs combined

        // ── Private state ─────────────────────────────────────────────────
        private string _uid1;
        private bool   _initialized;
        private bool   _rawJsonEnabled;
        private bool   _debugEnabled;

        // Additional UIDs and their callback index (2-6)
        // Stored as parallel arrays for Crestron .NET 4.7 compatibility
        private readonly string[]                  _extraUids  = new string[5];
        private readonly DeviceRawJsonDelegate[]   _extraCbs   = new DeviceRawJsonDelegate[5];

        // The exact Action<string> delegates handed to the broker, kept so we
        // can unregister precisely our own callbacks (multicast-safe).
        private Action<string>   _primaryHandler;
        private readonly Action<string>[] _extraHandlers = new Action<string>[5];

        // ── Additional-UID registration (call BEFORE Initialize) ──────────

        /// <summary>
        /// Register an additional UniqueID. Call from SIMPL+ Main() before Initialize().
        /// </summary>
        /// <param name="slot">2–6  (matches OnRawJson2 … OnRawJson6)</param>
        /// <param name="uniqueId">Zigbee uniqueid; empty/null = skip</param>
        public void AddUniqueId(int slot, string uniqueId)
        {
            if (_initialized)
            {
                CrestronConsole.PrintLine(
                    "[Device] AddUniqueId must be called BEFORE Initialize – ignored");
                return;
            }
            if (slot < 2 || slot > 6) return;
            if (string.IsNullOrEmpty(uniqueId)) return;
            _extraUids[slot - 2] = uniqueId.Trim().ToLowerInvariant();
        }

        // ── Public API ────────────────────────────────────────────────────

        public void Initialize(string uniqueId)
        {
            if (_initialized)
            {
                if (_debugEnabled) CrestronConsole.PrintLine("[Device] Initialize: already initialised – ignored");
                return;
            }
            if (string.IsNullOrEmpty(uniqueId))
            {
                CrestronConsole.PrintLine("[Device] Initialize: empty primary uniqueId – ignored");
                return;
            }

            _uid1        = uniqueId.Trim().ToLowerInvariant();
            _initialized = true;

            // Wire delegate array (index 0 = slot 2, index 4 = slot 6)
            _extraCbs[0] = OnRawJson2;
            _extraCbs[1] = OnRawJson3;
            _extraCbs[2] = OnRawJson4;
            _extraCbs[3] = OnRawJson5;
            _extraCbs[4] = OnRawJson6;

            // Register primary UID (store the handler for precise unregister)
            _primaryHandler = OnUpdate1;
            DeConzBroker.RegisterDevice(_uid1, _primaryHandler);
            if (_debugEnabled) CrestronConsole.PrintLine("[Device] Registered uid=" + _uid1);

            // Register additional UIDs
            for (int i = 0; i < _extraUids.Length; i++)
            {
                if (string.IsNullOrEmpty(_extraUids[i])) continue;
                int captured = i;   // capture loop variable for lambda
                _extraHandlers[i] = json => FireExtra(captured, json);
                DeConzBroker.RegisterDevice(_extraUids[i], _extraHandlers[i]);
                if (_debugEnabled) CrestronConsole.PrintLine("[Device] Registered uid" + (i + 2) + "=" + _extraUids[i]);
            }
        }

        public void SetRawJsonEnabled(ushort enable) { _rawJsonEnabled = (enable != 0); }

        public void Dispose()
        {
            if (!_initialized) return;
            DeConzBroker.UnregisterDevice(_uid1, _primaryHandler);
            for (int i = 0; i < _extraUids.Length; i++)
                if (!string.IsNullOrEmpty(_extraUids[i]) && _extraHandlers[i] != null)
                    DeConzBroker.UnregisterDevice(_extraUids[i], _extraHandlers[i]);
            _initialized = false;
        }

        // ── Broker callbacks ──────────────────────────────────────────────

        private void OnUpdate1(string json) { Fire(OnRawJson, _uid1, json); }

        private void FireExtra(int idx, string json)
        {
            Fire(_extraCbs[idx], _extraUids[idx], json);
        }

        private void Fire(DeviceRawJsonDelegate cb, string uid, string json)
        {
            if (!_rawJsonEnabled) return;
            if (json.Length > 65000) json = json.Substring(0, 65000);

            // Always forward to the combined output first
            var all = OnRawJsonAll;
            if (all != null)
                try { all(new SimplSharpString(json)); }
                catch (Exception ex)
                {
                    CrestronConsole.PrintLine("[Device] OnRawJsonAll error uid=" + uid + ": " + ex.Message);
                }

            if (cb == null) return;
            try { cb(new SimplSharpString(json)); }
            catch (Exception ex)
            {
                CrestronConsole.PrintLine("[Device] OnRawJson error uid=" + uid + ": " + ex.Message);
            }
        }
    }
}
