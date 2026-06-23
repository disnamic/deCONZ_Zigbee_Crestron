/*******************************************************************************
 * DeConzJsonParser.cs
 *
 * Static utility class – shared JSON micro-parser for all deCONZ modules.
 *
 * All deCONZ WebSocket events and HTTP GET responses are compact JSON objects.
 * This parser avoids a full JSON deserialiser dependency and is tuned for the
 * specific key/value patterns that deCONZ produces.
 *
 * DEPTH CONVENTION
 *   depth 0  – match at any nesting level (first occurrence)
 *   depth 1  – top-level object keys:
 *                "e","id","r","t","uniqueid","name","type","lastseen", …
 *   depth 2  – keys inside a nested object:
 *                "state":{ on, bri, ct, hue, sat, lift, temperature, … }
 *                "config":{ heatsetpoint, battery, locked, … }
 *                "attr":{ name, lastseen, … }
 *
 * STRING SAFETY
 *   The parser tracks string literals so braces / brackets inside JSON string
 *   values never affect the nesting depth counter.
 *   It also verifies that a matched key token is immediately followed by ':'
 *   (with optional whitespace), so a string *value* that happens to equal a
 *   key name is never mistakenly matched.
 *
 * Programmer : martin@disnamic.com
 *******************************************************************************/

using System;
using System.Text;
using Crestron.SimplSharp;

namespace DeConzZigbee
{
    public static class DeConzJsonParser
    {
        // ── Shared RNG for connect-stagger delays ─────────────────────────────
        // A single static instance seeded once avoids the new Random() same-seed
        // problem: when multiple modules receive NotifyWsConnected() simultaneously
        // they all call ScheduleGetState(). If each creates its own new Random()
        // within the same system tick they receive identical seeds and therefore
        // identical delays, defeating the stagger. The lock costs microseconds.

        private static readonly Random _sharedRng = new Random();

        /// <summary>
        /// Returns a random delay in [minMs, maxMs] milliseconds.
        /// Thread-safe; uses a single shared Random instance.
        /// </summary>
        public static int NextStaggerMs(int minMs = 1000, int maxMs = 15000)
        {
            lock (_sharedRng) { return _sharedRng.Next(minMs, maxMs + 1); }
        }

        // ── Boolean ───────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts a JSON boolean (true/false) at the specified nesting depth.
        /// Returns null when the key is absent or the value is not a literal bool.
        /// </summary>
        public static bool? ExtractBool(string json, string key, int depth = 0)
        {
            int vp = FindValueStart(json, key, depth);
            if (vp < 0) return null;
            if (vp + 4 <= json.Length && json.Substring(vp, 4) == "true")  return true;
            if (vp + 5 <= json.Length && json.Substring(vp, 5) == "false") return false;
            return null;
        }

        // ── Integer ───────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts a JSON integer (including negative values) at the given depth.
        /// Returns null when the key is absent or the value is not a digit sequence.
        /// </summary>
        public static int? ExtractInt(string json, string key, int depth = 0)
        {
            int vp = FindValueStart(json, key, depth);
            if (vp < 0) return null;

            bool neg = (vp < json.Length && json[vp] == '-');
            if (neg) vp++;
            if (vp >= json.Length || !char.IsDigit(json[vp])) return null;

            int ep = vp;
            while (ep < json.Length && char.IsDigit(json[ep])) ep++;

            int val;
            if (!int.TryParse(json.Substring(vp, ep - vp), out val)) return null;
            return neg ? -val : val;
        }

        // ── String ────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts a JSON string value at the given depth.
        /// Returns null when the key is absent or the value is not a quoted string.
        /// </summary>
        public static string ExtractString(string json, string key, int depth = 0)
        {
            int vp = FindValueStart(json, key, depth);
            if (vp < 0 || vp >= json.Length || json[vp] != '"') return null;

            // Walk forward respecting escape sequences
            int i = vp + 1;
            while (i < json.Length)
            {
                if (json[i] == '\\') { i += 2; continue; }   // skip escaped char
                if (json[i] == '"')  break;
                i++;
            }
            return i >= json.Length ? null : json.Substring(vp + 1, i - vp - 1);
        }

        /// <summary>
        /// Convenience: extract a top-level (depth 1) string value.
        /// Used for "id", "r", "e", "uniqueid", "name", "lastseen" etc.
        /// in deCONZ WebSocket event frames.
        /// </summary>
        public static string ExtractTopLevelString(string json, string key)
        {
            return ExtractString(json, key, 1);
        }

        // ── XY colour ─────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the deCONZ "xy" array, e.g. [0.175, 0.2722].
        /// Searches for the first occurrence of "xy" regardless of depth.
        /// Returns false when the key or a valid float pair cannot be found.
        /// </summary>
        public static bool ExtractXY(string json, out double x, out double y)
        {
            x = y = 0.0;
            const string key = "\"xy\"";
            int keyPos = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyPos < 0) return false;

            int bracket = json.IndexOf('[', keyPos + key.Length);
            if (bracket < 0) return false;
            int bracketEnd = json.IndexOf(']', bracket);
            if (bracketEnd < 0) return false;

            string inner = json.Substring(bracket + 1, bracketEnd - bracket - 1).Trim();
            int comma = inner.IndexOf(',');
            if (comma < 0) return false;

            string xs = inner.Substring(0, comma).Trim().Replace(",", ".");
            string ys = inner.Substring(comma + 1).Trim().Replace(",", ".");

            return double.TryParse(xs,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out x)
                && double.TryParse(ys,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out y);
        }

        // ── Groups list ───────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the "groups" string array from inside the "config" block and
        /// returns it as a comma-separated string, e.g. "0,9,17,23".
        /// Returns null when the key or a valid array cannot be found.
        /// </summary>
        public static string ExtractGroupsList(string json)
        {
            // Locate the "config" block first
            int configPos = json.IndexOf("\"config\"", StringComparison.OrdinalIgnoreCase);
            if (configPos < 0) return null;
            int brace = json.IndexOf('{', configPos + 8);
            if (brace < 0) return null;

            // Find "groups" array within the config block
            int groupsPos = json.IndexOf("\"groups\"", brace, StringComparison.OrdinalIgnoreCase);
            if (groupsPos < 0) return null;
            int bracket = json.IndexOf('[', groupsPos + 8);
            if (bracket < 0) return null;
            int bracketEnd = json.IndexOf(']', bracket);
            if (bracketEnd < 0) return null;

            // inner looks like: "0","9","17","23"
            string inner = json.Substring(bracket + 1, bracketEnd - bracket - 1);
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

        // ── Core depth-aware scanner ──────────────────────────────────────────

        /// <summary>
        /// Returns the index of the first character of the value for the given key,
        /// or -1 when not found.
        ///
        /// The scanner tracks JSON nesting depth (ignoring braces/brackets inside
        /// string literals) and only matches a key whose depth equals targetDepth.
        /// When targetDepth == 0 the first occurrence at any depth is returned.
        ///
        /// A candidate key token is only accepted when it is immediately followed
        /// by ':' (with optional whitespace), so a string *value* that equals the
        /// key name is never mistakenly matched.
        /// </summary>
        public static int FindValueStart(string json, string key, int targetDepth)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return -1;

            var search = "\"" + key + "\"";
            int depth = 0;
            bool inString = false;

            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];

                // ── String literal handling ──────────────────────────────────
                if (ch == '"')
                {
                    // Count preceding backslashes to detect escaped quote
                    int bs = 0;
                    int k  = i - 1;
                    while (k >= 0 && json[k] == '\\') { bs++; k--; }
                    bool escaped = (bs % 2 != 0);

                    if (!escaped)
                    {
                        if (!inString)
                        {
                            // Opening quote – check if this is our key
                            bool depthOk = (targetDepth == 0) || (depth == targetDepth);
                            if (depthOk
                                && i + search.Length <= json.Length
                                && string.Compare(json, i, search, 0, search.Length,
                                                  StringComparison.OrdinalIgnoreCase) == 0)
                            {
                                // Verify the token is a key (followed by ':')
                                int p = i + search.Length;
                                while (p < json.Length && (json[p] == ' ' || json[p] == '\t'
                                                        || json[p] == '\r' || json[p] == '\n'))
                                    p++;
                                if (p < json.Length && json[p] == ':')
                                {
                                    int vp = p + 1;
                                    while (vp < json.Length && (json[vp] == ' ' || json[vp] == '\t'
                                                             || json[vp] == '\r' || json[vp] == '\n'))
                                        vp++;
                                    return vp;
                                }
                            }
                            inString = true;
                        }
                        else
                        {
                            inString = false;
                        }
                    }
                    continue;
                }

                if (inString) continue;

                // ── Nesting depth tracking ───────────────────────────────────
                if      (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
            }
            return -1;
        }
    }
}
