# DeConz Zigbee – Crestron SIMPL# Module Suite v4.0

**Latest release: 4.0.** Contact: martin@disnamic.com

A SIMPL# module suite for controlling and monitoring Zigbee devices through a
deCONZ gateway (dresden elektronik / Phoscon). Commands are sent over the
deCONZ REST API via HTTP; live feedback arrives over a WebSocket connection.
No signal routing between modules is required — a static broker distributes
gateway data to every device module automatically.

---

## Architecture

A single gateway module owns the WebSocket connection to deCONZ. It feeds a
static broker (`DeConzBroker`) that holds the gateway IP address and a list of
registered device callbacks. Each device module registers itself with the
broker by its Zigbee `uniqueid` and receives the matching WebSocket payloads.
Commands are sent directly over HTTP using the gateway IP the broker provides.

The data flow is:

deCONZ gateway sends RFC-6455 WebSocket frames to `DeConzWebSocketClient`, which
hands each decoded payload to `DeConzBroker.DispatchUpdate`. The broker looks up
all device modules registered for that `uniqueid` and invokes their callbacks on
a worker thread. For commands, device modules read `DeConzBroker.GatewayIP` and
issue HTTP PUT/GET/POST requests straight to the REST API.

Because the broker is a static, app-domain-scoped class, no wires are needed
between the gateway symbol and the device symbols in SIMPL Windows. A device is
linked to the gateway purely by its `uniqueid`.

### Broker pattern

A device module registers on start-up with its uniqueid and a callback:

    DeConzBroker.RegisterDevice(uniqueid, myCallback);

The gateway sets the IP once on initialize, and device modules read it directly
when building HTTP request URLs:

    DeConzBroker.GatewayIP = ipAddress;

Multiple modules may register the same uniqueid. The broker is multicast: every
registered callback for a uniqueid receives each payload. This lets a generic
Device module observe raw JSON alongside a typed module controlling the same
physical device.

---

## Command vs. feedback model

Two transport paths are used, and it is important to keep them apart:

Commands always go out over HTTP (PUT, GET, POST, DELETE) to the deCONZ REST
API. deCONZ does not accept commands over the WebSocket from external clients.

Feedback always comes in over the WebSocket as event frames, distributed by the
broker. Modules that also need an initial state (after connect or reconnect)
issue an HTTP GET, staggered by a random 1–15 second delay so that many modules
do not hit the gateway at the same moment after a reconnect.

Device IDs and resource types are resolved automatically from the first
WebSocket event for a device (`id` and `r` fields), so there is no need to
configure a numeric device ID by hand.

---

## Files

The C# project under `SimplSharp/` builds a single `DeConzZigbee.clz`
(also a `.dll`) targeting .NET Framework 4.8 against the Crestron SimplSharp
SDK. The SIMPL+ wrappers under `SimplPlus/` are thin symbols that expose the
SIMPL# classes to SIMPL Windows.

Core infrastructure:

- `DeConzBroker.cs` — static message bus and gateway IP holder
- `DeConzJsonParser.cs` — shared depth-aware JSON micro-parser
- `DeConzWebSocketClient.cs` — gateway connection: TCP/TLS, RFC-6455 handshake
  and frame decoder, ping/pong keepalive, exponential reconnect

Device modules:

- `DeConzDevice.cs` — generic raw-JSON passthrough for one or more endpoints
- `DeConzKeypad.cs` — Zigbee keypads / remotes: button events, battery, online
- `DeConzLightWs.cs` — lights: HTTP commands with WebSocket feedback
- `DeConzValve.cs` — irrigation valve: on/off endpoint plus water sensor
- `DeConzShade.cs` — window coverings: lift, tilt, stop, position feedback
- `DeConzThermostat.cs` — thermostats (ZHAThermostat): setpoints, mode, valve
- `DeConzClimate.cs` — environment sensors: temperature, humidity, pressure
- `DeConzContact.cs` — door/window contact sensors (ZHAOpenClose)
- `DeConzMotion.cs` — presence + light level (ZHAPresence + ZHALightLevel)
- `DeConzPower.cs` — smart plug / power meter (switch + ZHAPower + ZHAConsumption)
- `DeConzAlarm.cs` — alarm, fire and carbon-monoxide sensors
- `DeConzGroupControl.cs` — group control (on/off, level, colour, scenes)
- `DeConzScene.cs` — scene recall and store

Each device module has a matching `DeConz_*_Wrapper` SIMPL+ symbol.

---

## Common conventions

Several conventions are shared across all modules.

Every module that talks to the REST API takes an `API_Key` parameter (the
deCONZ API key) and identifies its device by a Zigbee `uniqueid` parameter.
The gateway IP is never wired or configured per module — it comes from the
broker.

Most sensor modules expose an `Online_Timeout_Seconds` parameter (default 120).
The `online` output goes high on any WebSocket activity and falls back to low if
no traffic arrives within the timeout.

Every device module has an `Enable_Raw_Json` digital input. Raw JSON output is
disabled by default; raising this input activates the raw JSON output (or
outputs) of that module. Modules with several raw JSON outputs share a single
enable. Similarly, `Enable_Debug` gates all verbose console output; only
genuine errors are printed to the Crestron console without debug enabled.

Modules that report battery can optionally take a separate `Battery_UniqueID`
for a dedicated ZHABattery endpoint. Leave it empty when the device reports
battery on its main endpoint.

In the SIMPL+ symbols the number of skipped signal positions before the first
input or output equals the number of parameters, so the signals line up below
the parameter fields in SIMPL Windows.

Temperature, humidity and similar values follow deCONZ scaling: temperature and
humidity are integers in hundredths (2150 = 21.50 °C, 4532 = 45.32 %), pressure
is an integer in hPa. Where applicable a module provides both a raw analog
output for logic and a formatted string output for display.

---

## Modules

### DeConz_Gateway (one per program)

Owns the WebSocket connection. Inputs let you enable or disable the connection
and toggle debug output. Outputs report whether WebSocket traffic is present,
the TCP socket status, the gateway IP for optional external use, and debug text.
Parameters are the IP address or hostname, the port (80 for ws, 443 for wss),
an alive timeout in seconds for the traffic indicator, and a TLS flag selecting
ws:// or wss://.

### DeConz_Device (generic)

Raw JSON passthrough with no parsing of its own. Takes one primary uniqueid plus
up to five optional additional uniqueids, each delivering its payloads on its
own raw JSON output. A combined output receives every payload from all active
uniqueids. Useful as a catch-all for sensors that do not yet have a dedicated
module.

### DeConz_Light_WS

Controls a light over HTTP and receives feedback over the WebSocket. Supports
on/off, brightness, colour temperature (entered in Kelvin, converted to mired),
hue, saturation, CIE xy and RGB (Philips wide-gamut conversion). Provides
on/off, brightness and colour-temperature feedback as well as device
information such as model, manufacturer and group membership.

### DeConz_Keypad

For Zigbee keypads and remotes with up to ten buttons. Decodes the deCONZ
`buttonevent` into per-button pulses for press/hold, short release, long release
and double press. Reports battery level, low battery and online state, plus
device information. State is fetched on connect and refreshed every 30 minutes.
Button events fire only from WebSocket frames, never from a state poll, so a
state refresh never re-triggers the last button press.

### DeConz_Valve

An irrigation valve modelled as two deCONZ endpoints in one symbol: a valve
endpoint (on/off, exposed as a light resource) and a water sensor endpoint
(ZHAWater). Reports valve state, water detection, flow and battery. The flow
JSON key is configurable, since it varies by device model.

### DeConz_Shade

Window coverings, blinds and drapes. Commands are move up, move down, stop, set
lift position (0–100) and set tilt (0–100, for venetian blinds). deCONZ uses
0 for open and 100 for closed; an invert option mirrors this for SIMPL. Derived
outputs report fully open, fully closed and a moving heuristic. An optional
ZHABattery endpoint provides battery level, low battery and voltage.

### DeConz_Thermostat

Thermostats (ZHAThermostat). Unlike other modules, commands are sent to the
device `config` object rather than `state`. Supports thermostat on/off, lock,
window-open detection, heat and cool setpoints, mode (off/auto/heat/cool) and a
calibration offset. Temperature and setpoints are provided both as raw analog
values and formatted strings. The on/off state of the thermostat
(`config.on`) is kept distinct from whether it is currently heating
(`state.on`/valve). An optional ZHABattery endpoint is supported.

### DeConz_Climate

Environment sensor suite combining up to three deCONZ endpoints — temperature
(ZHATemperature), humidity (ZHAHumidity) and pressure (ZHAPressure) — in one
symbol. Every uniqueid except temperature is optional. Each measurement is
provided as a raw analog value and a formatted string. Battery and online state
are shared across the active endpoints, with an optional dedicated ZHABattery
endpoint.

### DeConz_Contact

Door and window contact sensors (ZHAOpenClose). Provides complementary open and
closed outputs, battery level, low battery and online state. State is polled on
connect and every 30 minutes, with a manual refresh input. An optional
ZHABattery endpoint is supported.

### DeConz_Motion

Presence and light-level sensors combining a ZHAPresence and a ZHALightLevel
endpoint in one symbol; both are optional. Outputs presence, tampered, lux,
light level, dark and daylight. The presence timeout (duration) and the light
level thresholds (tholddark, tholdoffset) are writable via analog inputs at
runtime. ZHAPresence configuration is also exposed: enable/disable the sensor,
LED indication, user-test mode, plus sensitivity and delay (Philips-specific,
silently ignored by other brands); `Sensitivity_Max_fb` reports the device's
maximum sensitivity value. State is fetched on connect and reconnect; no
background poll is used.

### DeConz_Power

Smart plug and power meter modelling up to three deCONZ endpoints in one symbol:
an on/off switch endpoint, ZHAPower (watts, volts, amps) and ZHAConsumption
(cumulative watt-hours plus instantaneous watts). All three are optional. On/off
commands and feedback work like the valve module. The consumption value is a
hardware counter that cannot be reset through the deCONZ API; implement a
software offset in SIMPL+ for a reset function. `Consumption_fb` is in
watt-hours — divide by 1000 for kWh.

### DeConz_Alarm

Alarm, fire and carbon-monoxide safety sensors. One symbol covers ZHAAlarm,
ZHAFire and ZHACarbonMonoxide as three independent optional endpoints, suitable
both for single-type detectors and for combination devices that expose all three
under different uniqueids. Tampered flag, battery level, low battery and online
state are shared across the active endpoints, with an optional dedicated
ZHABattery endpoint.

### DeConz_Group_Control

Controls one to four light groups at once. Group IDs are supplied at runtime via
analog inputs rather than parameters, so the target groups can be chosen from
SIMPL logic; setting an optional group ID to zero disables that slot. Supports
on/off, toggle, brightness, colour temperature, hue, saturation, alert and
effect, with an optional transition time. State feedback (all-on, any-on,
brightness, colour temperature, colour mode, name, members, scenes) is read from
the primary group. This is a pure HTTP module.

### DeConz_Scene

Recalls and stores scenes within a group. Group ID and scene ID are supplied at
runtime via analog inputs. Recall applies the stored scene to the group's
lights; store captures the current light states into the scene slot; a list
function returns the group's scenes as "id:name" pairs. Success pulses and
last-recalled feedback are provided. This is a pure HTTP module.

---

## Deployment

Copy `DeConzZigbee.clz` into the SIMPL project folder, add the desired `.usp`
wrapper symbols as modules, set their parameters, and you are done — no further
signal wiring is needed between the gateway and the device modules.

To find the values needed for the parameters, use the Phoscon app at
`http://<gateway-ip>/pwa/`:

- uniqueid: open Lights or Sensors, select the device, view its Advanced details
- API key: Settings, Gateway, Advanced, then create or read the API key
- group and scene IDs: visible in the Phoscon group and scene configuration

---

## deCONZ buttonevent encoding

The keypad module decodes `buttonevent` as the button number times 1000 plus an
event code:

    000  initial_press
    001  hold
    002  short_release
    003  long_release
    004  double_press

For example, 1002 is button 1 short release, 2001 is button 2 hold, 3003 is
button 3 long release, and 10002 is button 10 short release.

---

## Improvements over the original SIMPL+ implementation

The WebSocket accept key is generated per RFC 6455 rather than hardcoded, and a
full RFC-6455 frame decoder replaces the original raw string scan. Reconnection
uses exponential back-off, and ping/pong keepalive supplements the traffic
timer. TLS/WSS is supported through `SecureTCPClient`, selectable by parameter.

Device routing no longer relies on a string-output array; the static broker
removes all signal wiring between modules, and the gateway IP is shared
automatically instead of being buffered into every module. HTTP uses the
Crestron-native `HttpClient` rather than an external command library.

On the keypad side, the original two-dimensional button-feedback array bug is
fixed, double-press events are supported, and the online timeout is a parameter
rather than hardcoded. Throughout, work runs on `CrestronInvoke` worker threads
rather than the SIMPL+ event loop.
