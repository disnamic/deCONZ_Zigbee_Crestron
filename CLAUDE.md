# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Workflow gates (from global instructions — do not skip)

- No source code changes without explicit confirmation from Martin.
- No build/packaging without a **separate** explicit confirmation, even after code is approved.
- Ask before any version bump (see scheme below).
- Conversation in German; all code, comments, changelogs, logs, READMEs in English. No AI/personal-name references in deliverables.

## What this is

A Crestron 4-Series **SIMPL# module suite** for controlling and monitoring Zigbee devices through a deCONZ gateway (dresden elektronik / Phoscon). Two layers:

- `SimplSharp/` — the C# library, builds a single `DeConzZigbee.clz` (+ `.dll`).
- `SimplPlus/` — thin `.usp` wrapper symbols (one per device module) exposing the SIMPL# classes to SIMPL Windows.

One `DeConzZigbee.clz` is dropped into a SIMPL Windows project alongside the chosen `.usp` wrappers. There is **no signal wiring** between the gateway symbol and device symbols — a static broker links them by Zigbee `uniqueid`.

## Build

- Build with Visual Studio 2022 / MSBuild against the solution `DeConzZigbee.sln` (project `SimplSharp/DeConzZigbee.csproj`).
- Classic MSBuild csproj (`ToolsVersion="14.0"`), `TargetFrameworkVersion` **v4.8**, `GenerateAssemblyInfo=false` (real `Properties/AssemblyInfo.cs`, empty `AssemblyCopyright`).
- NuGet via **`packages.config` + HintPaths** (not PackageReference). Restore lands in solution-level `..\packages\`. The only package is `Crestron.SimplSharp.SDK.Library` **2.21.252** (referenced `lib\net47`, reference-compatible with v4.8).
- The SDK `.targets` is the CLZ archiver and emits `DeConzZigbee.clz`; interface DLLs use `<Private>False</Private>` (provided by processor firmware at runtime).
- The `.usp` wrappers compile inside SIMPL+ / SIMPL Windows, not via this solution.
- This is **not a git repository** and there is no test suite. Verification is done by inspection plus the brace checker described below.

## Verification before packaging

There is no automated test harness. The handover checklist (`HANDOVER.md`) is the source of truth. Key steps:

- Brace balance: a small Python `balance.py` checks every `.cs` for balanced `{} () []` (string/char/comment-aware). It is not committed — recreate under the scratchpad dir when needed; it scans char-by-char and ignores content inside `"…"`, `'…'`, `//` and `/* */`.
- Delegate names match: wrapper `RegisterDelegate(...)` ↔ class `public *Delegate` property.
- `#SYMBOL_NAME` version consistent across all `.usp`; `AssemblyVersion` in `AssemblyInfo.cs`; filenames; csproj — all kept in sync.
- `_SKIP_` rule (below) holds in every wrapper.
- In each wrapper `Main()`, `SetRawJsonEnabled(...)` is called **before** `Initialize(...)`.
- `.usp` files must have **CRLF** line endings (Unix LF → SIMPL+ Error 1700).

## Architecture — static broker

```
deCONZ Gateway
  → DeConzWebSocketClient   (TCP/TLS, RFC-6455 handshake + frame decoder, ping/pong, exponential reconnect)
  → DeConzBroker            (static, AppDomain-scoped)
       GatewayIP (static, set once by the gateway)
       Dictionary<uniqueid, List<Action<string>>>   multicast dispatch
       RegisterDevice / UnregisterDevice(uid, callback)
       RegisterConnectedCallback / NotifyWsConnected
  → Device modules
```

- **Commands** always go out over **HTTP** (PUT/GET/POST/DELETE) to the deCONZ REST API — deCONZ does not accept external commands over the WebSocket.
- **Feedback** always arrives over the **WebSocket** as event frames, distributed by the broker. Never mix the two paths.
- `DeConzBroker.DispatchUpdate` snapshots the handler list under `CCriticalSection`, then invokes each callback on a `CrestronInvoke` worker thread (outside the lock).
- Multicast: multiple modules may register the **same** uniqueid (e.g. a generic `DeConzDevice` reading raw JSON alongside a typed module controlling the same physical device).
- Device numeric `id` and resource type (`r`) are resolved automatically from the first WebSocket event — never configured by hand.
- Device modules read `DeConzBroker.GatewayIP` to build HTTP URLs; the IP is never wired per module.
- On WS connect/reconnect the broker fires `RegisterConnectedCallback` handlers, which schedule an HTTP GET for initial state with a random **1–15 s stagger** (`DeConzJsonParser.NextStaggerMs()`) so modules don't all hit the gateway at once.

## Shared infrastructure

- `DeConzBroker.cs` — static message bus + gateway IP holder + WS send hook.
- `DeConzJsonParser.cs` — depth-aware JSON micro-parser (no Newtonsoft/System.Text.Json — deliberately, for Crestron Mono deployment safety). Depth 0 = first match anywhere; depth 1 = top-level event keys (`e/id/r/t/uniqueid/name/type/lastseen`); depth 2 = inside `state`/`config`/`attr`. String-literal- and key-vs-value-aware. Also owns the shared seeded RNG for stagger.
- `DeConzWebSocketClient.cs` — hand-rolled RFC-6455 client on native TCPClient/SecureTCPClient (~618 lines). This is the proven path; `ClientWebSocket` is not used (Mono compatibility unproven).

## Device modules (one C# class + one `.usp` wrapper each)

Gateway (one per program), Device (generic raw-JSON passthrough, 1+5 endpoints), Keypad, LightWs, Valve, Shade, Thermostat, Climate, Contact, Motion, Power, Alarm, GroupControl, Scene. See `README.md` for per-module behavior. Note `README.md` is currently **stale** (says v3.6 / .NET 4.7.2 and predates Motion/Power/Alarm and the v4.0 bump) — trust the code, `HANDOVER.md`, and csproj over it.

Cross-module conventions:

- Every REST module takes `API_Key` and a device `uniqueid`. Modules that report battery may take an optional separate `Battery_UniqueID` (dedicated ZHABattery endpoint); leave empty when battery is on the main endpoint.
- `Enable_Raw_Json` (default **off**) gates the `*RawJson` outputs; `Enable_Debug` gates verbose console output — only genuine errors (HTTP/parse/config) print without it.
- Sensor modules expose `Online_Timeout_Seconds` (default 120): `online` goes high on WS activity, low after the timeout with no traffic.
- Static device info (manufacturer/model/name/type/swversion) is parsed once (`_staticInfoSent` flag), reset on reconnect.
- **Stale-value reset**: a per-module `CTimer` checks every 5 min; if no update for >1 h, live value/status outputs reset (Bool/Analog→0, String→"") once and re-arm on fresh data. Identity, timestamps, capabilities, config/setpoints, cumulative meters and raw JSON are **preserved**. GroupControl/Scene/Device are exempt. See `CHANGELOG.txt` v3.6.0 for the exact per-module reset lists.
- HTTP: every `HttpClientResponse` callback disposes the response in a `finally` block.

## deCONZ data conventions

- Commands target `state` — **except Thermostat**, whose commands target the `config` object. Keep `config.on` (thermostat enabled) distinct from `state.on`/valve (currently heating).
- Temperature/humidity are integers in hundredths (`2150` = 21.50 °C, `4532` = 45.32 %); pressure is integer hPa. Modules expose both a raw analog and a formatted string.
- Analog temperature is a **signed 16-bit two's-complement** value — read as signed in SIMPL+ (e.g. -5 °C = 65036). The string output is always correct.
- Keypad `buttonevent` = button×1000 + event code: 000 initial_press, 001 hold, 002 short_release, 003 long_release, 004 double_press (e.g. 1002 = button 1 short release).
- `HttpsClient` Content-Type must be `application/json` only — `; charset=utf-8` causes failures.

## SIMPL+ wrapper rules (critical)

- **`_SKIP_` rule**: leading `_SKIP_` belong **only** in the first `DIGITAL_INPUT` block and the first `DIGITAL_OUTPUT` block; **count of leading skips = number of parameters** (pushes the first signal names below the parameter fields). `ANALOG_INPUT/OUTPUT` and `STRING_INPUT/OUTPUT` get **no** leading skips. Group-separator `_SKIP_` between functional groups is allowed for readability. (The Device module intentionally has no `DIGITAL_OUTPUT` block.)
- Delegate binding via auto-properties + `RegisterDelegate` (not `REGISTEREVENT`); the `event` keyword causes Error 1510. Use bare class names in declarations.
- `#BEGIN_PARAMETER_PROPERTIES`: `propBounds` before `propDefaultValue` (else Error 1325).
- `#SYMBOL_NAME` must carry the version, e.g. `"DeConz_Contact v4.0"`. Namespace and class name must differ.
- Long strings: SIMPL+ caps at 250 chars — raw JSON outputs are emitted via `FireChunked()`; short strings via `FireStr()`.
- SIMPL+ compiler has no Select/Case (use If/Else If), no `And` (nest Ifs), no `ClearString`; `STRING_OUTPUT` takes no length suffix; `Wait` units are 1/100 s.

## Threading & memory (device-uptime sensitive — runs for weeks)

- Sync via `CCriticalSection` (Enter/Leave in try/finally) — **no** `lock()` on shared state, no `Mutex`.
- Workers via `CrestronInvoke.BeginInvoke`.
- One-shot self-disposing `CTimer`:
  ```csharp
  CTimer t = null;
  t = new CTimer(_ => { Work(); if (t != null) t.Dispose(); }, null, delayMs);
  ```
- Reconnect backoff: exponential 5→10→20→30 s.
- LINQ allowed in cold/setup paths; **avoid in hot paths** (per-event dispatch, timer callbacks) to limit GC pressure.

## Versioning (ask before bumping)

- Bugfix → 3rd digit (4.0.0 → 4.0.1). New module / larger feature → 2nd digit (4.0 → 4.1). Breaking → major (x.0).
- No wrapper signal change + tiny tweak → keep the version.
- Keep version in sync across: `.usp` `#SYMBOL_NAME`, `AssemblyVersion`/`AssemblyFileVersion` in `AssemblyInfo.cs`, filenames, csproj.
- History note: 3.4 was skipped (3.3.1 → 3.5). Current is **4.0.0**.

## Rejected approaches — do not re-propose

`lock()` instead of `CCriticalSection`; a `CrestronInvoke` ordering queue; a Newtonsoft/System.Text.Json parser; `ClientWebSocket`; delegate validation inside `Initialize()` (the SIMPL+ compiler already catches typos).

## Known open performance items (analyzed, not implemented)

- WS frame buffer is O(n²): `_frameBuffer` is fully reallocated+copied on each TCP chunk (`DeConzWebSocketClient` ~line 259) — only matters for large fragmented payloads.
- Multiple linear JSON scans per event (~20 full-string `FindValueStart` passes per Thermostat event); could scan within the already-isolated block instead. Structural change.
