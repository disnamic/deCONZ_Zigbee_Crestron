# DeConzZigbee — Chat-Übergabe

Stand: 2026-06-23. Diese Datei macht einen neuen Chat sofort arbeitsfähig.
Alle Fakten, Regeln und offenen Punkte sind hier; bei Detailbedarf in die
referenzierten Dateien schauen.

================================================================================
## DeConzZigbee   (AKTUELL v4.0.0)
================================================================================

Crestron 4-Series SIMPL# Suite für deCONZ Zigbee Gateway. 14 Module.

### Orte
- Quellcode:  /home/claude/DeConzZigbee/
- Paket:      /mnt/user-data/outputs/DeConzZigbee_v3.6.zip
- VS2022:     C:\Users\Martin\source\repos\DeConzZigbee_v4.0\
- Credit:     martin@disnamic.com
- Umgebung:   VS2022, .NET 4.8, Crestron SimplSharp SDK (NuGet)
- balance.py: C#-Klammer-Checker (siehe Abschnitt "Tooling" unten)

### Versionsschema (IMMER FRAGEN vor Bump)
- Kleine Änderung, kein Wrapper-Change → gleiche Version
- Bugfix → 3. Stelle (3.6.0 → 3.6.1)
- Neues Modul / größeres Feature → 2. Stelle (3.6 → 3.7)
- Hinweis: 3.4 wurde übersprungen (3.3.1 → 3.5 auf Wunsch des Users)

### Architektur — statischer Broker
```
deCONZ Gateway
  → DeConzWebSocketClient  (TCP/TLS, RFC6455, reconnect, ping/pong)
  → DeConzBroker (static, AppDomain-scoped)
       GatewayIP (static)
       Dictionary<uid, List<Action<string>>>  multicast
       RegisterDevice / UnregisterDevice(uid, callback)
       RegisterConnectedCallback / NotifyWsConnected
  → Gerätemodule
```
- Befehle = HTTP PUT/POST/DELETE an deCONZ REST API
- Feedback = WebSocket-Events via Broker
- Device ID + Resource automatisch aus erstem WS-Event (id, r)
- GetState mit random 1–15 s Stagger (DeConzJsonParser.NextStaggerMs())
- GatewayIP aus DeConzBroker.GatewayIP — kein IP-Signal pro Modul

### Die 14 Module
Gateway, Device(1+5 raw JSON), Keypad, LightWS, Valve(On/Off+ZHAWater),
Shade, Thermostat(34 delegates), Climate(Temp+Hum+Pres), Contact,
Motion(ZHAPresence+ZHALightLevel, 24 delegates), Power(Switch+Power+Consumption),
Alarm(Alarm+Fire+CO), GroupControl, Scene.
Shared: DeConzBroker.cs, DeConzJsonParser.cs, DeConzWebSocketClient.cs.

### KRITISCHE SIMPL+ Wrapper-Regeln

**_SKIP_ Regel (korrigiert in v3.3.1 — WICHTIGSTE REGEL):**
  Führende _SKIP_ gehören NUR in den ersten DIGITAL_INPUT-Block und den ersten
  DIGITAL_OUTPUT-Block. Anzahl = Anzahl der PARAMETER. Damit werden die ersten
  Signalnamen unter die Parameterfelder geschoben (Parameter überdecken sonst
  die obersten Signalnamen).
  ANALOG_INPUT, STRING_INPUT, ANALOG_OUTPUT, STRING_OUTPUT brauchen KEINE
  führenden Skips — alle Eingänge teilen sich EINE linke Spalte, alle Ausgänge
  EINE rechte Spalte. Die Analog/String-Blöcke laufen unterhalb der bereits
  freigeräumten Parameter-Region weiter. Führende Skips dort = tote Zeilen.
  (Hinweis Device-Modul: hat absichtlich KEINEN DIGITAL_OUTPUT-Block, nur DI+SO.)

**Gruppen-Skips (optisch):** _SKIP_ zwischen Funktionsgruppen ist erlaubt/gewollt
  wo es die Lesbarkeit erhöht (z.B. Thermostat trennt Befehlspaare; LightWS
  trennt XY-/RGB-Farbmodelle; SO trennt Geräteinfo von raw_json-Ausgängen).

**Main()-Reihenfolge:** SetRawJsonEnabled(...) MUSS vor Initialize(...) stehen.

**Enable_Raw_Json:** Default AUS. Alle raw-JSON-Ausgänge (OnXxxRawJson) nur wenn
  enabled. all_groups_fb / scene_attr_fb feuern immer (dedizierte Ausgänge).

**Enable_Debug:** Nur echte Fehler (HTTP/Parse/Config) erscheinen ohne Debug.
  Operative Logs (Initialized, GetState in X s, Resolved id=) nur mit Debug=1.

**FireChunked():** Alle raw-JSON-Ausgänge in 250-Zeichen-Häppchen.
  FireStr() für kurze Strings (Namen, formatierte Werte), 250-Zeichen-Limit.

**Temperatur (Climate, Thermostat):** Analog-Ausgang ist 16-Bit
  Zweierkomplement (signed). In SIMPL+ als signed analog lesen. Der String-
  Ausgang ist immer korrekt. Skala ×100 (deCONZ liefert 1/100 °C).

**One-shot CTimer (memory-safe):**
```csharp
CTimer t = null;
t = new CTimer(_ => { Work(); if (t != null) t.Dispose(); }, null, ms);
```

### VERWORFENE Architektur-Vorschläge — NICHT umsetzen
1. CCriticalSection → lock()            (funktioniert, kein Vorteil)
2. CrestronInvoke-Queue zur Ordnung      (Events selten genug)
3. Newtonsoft / System.Text.Json Parser  (Deployment-Problem Crestron Mono)
4. ClientWebSocket                        (Mono-Kompatibilität unbewiesen)
5. Delegate-Validierung in Initialize()   (SIMPL+-Compiler fängt Typos)

### Versionsverlauf (Kurzform)
- v3.1.x: raw-JSON-enable-gating, Debug-gating
- v3.2.0: Thermostat erweitert (34 delegates), Shade Open/Closed_Threshold,
          raw-JSON 250-Zeichen-Chunking überall
- v3.2.1: _SKIP_-Counts in 6 Wrappern korrigiert (alte Regel: count=params)
- v3.3.0: Motion erweitert (ZHAPresence config: on/led/usertest/sensitivity/
          delay; 24 delegates)
- v3.3.1: _SKIP_-Regel KORRIGIERT (führende Skips nur in erstem DI+DO;
          AI/AO/SI/SO ohne führende Skips). Gateway Doppel-Skip, Keypad
          trailing-Skip, LightWS DI/SO Gruppierung bereinigt.
- v3.5.0: Performance/Memory-Review-Fixes:
          #1 HTTP-Response disposal (31 Callbacks, alle Module)
          #2 DebugLog-Guards für Payload-Logs (43 Stellen, if(_debugEnabled))
          #4 Static-Device-Info nur einmal parsen statt jedes Event
             (_staticInfoSent Flag, 10 Module; Reset bei Reconnect)
- v3.6.0: Stale-Value-Reset (10 Gerätemodule). Wenn >1h kein Update empfangen,
          werden Live-Werte/Status-Ausgänge zurückgesetzt (Bool/Analog→0,
          String→""). Erhalten: Identität, lastseen/lastannounced, Capabilities
          (sensitivitymax, hascolor), Config/Setpoints, Verbrauchszähler, raw
          JSON, Debug. Empfangszeit-basiert (_lastActivityUtc, _staleTimer alle
          5 min, CheckStale/ResetValueOutputs). Re-armt bei neuen Daten.
          GroupControl/Scene/Device ausgenommen. Keine Wrapper-Änderung.
- v4.0.0: Major-Release. Symbol-Rename auf v4.0-Generation (alle 14
          #SYMBOL_NAME + .usp-Dateinamen → "v4.0"; breaking in SIMPL Windows,
          Symbole müssen neu eingefügt werden). Gateway: zwei neue Parameter
          Alive_Timeout_Seconds (Default 15, 5-3600) und Use_TLS (0=ws/1=wss);
          vorher hardcodiert bzw. neu → Signal-Layout des Gateway verschiebt
          sich. Build auf .NET 4.8 (vorher 4.7.2), AssemblyVersion 4.0.0.0.
          KEINE neuen Module/Geräte-Features ggü. 3.6.0.
          Build-Fixes (Regressionen aus der v4.0-Erzeugung, ohne die die Suite
          nicht kompiliert): Device _debugEnabled-Feld ergänzt; Keypad Fire()-
          Overloads (Bool/Level) für ResetValueOutputs ergänzt; "DebugDebugLog"-
          Typo → DebugLog in LightWs/Shade/Thermostat; Shade undefiniertes "raw"
          → lift.Value (open/closed gegen deCONZ-Skala 0=offen/100=zu, invert-
          unabhängig); Light-Wrapper LastAnnounced_fb/SwVersion_fb/Type_fb-
          Outputs wieder ergänzt (Callbacks+Delegates waren da, STRING_OUTPUT
          fehlte → Error 1001; existieren laut Changelog seit v2.11.0).

### OFFENE Performance-Punkte (analysiert, NICHT umgesetzt)
- #3 WS Frame-Buffer O(n²): bei jedem TCP-Chunk wird _frameBuffer komplett neu
     alloziert+kopiert (DeConzWebSocketClient ~Z.259). Nur bei großen
     fragmentierten Payloads relevant. Mittel-Aufwand.
- #5 Mehrfach-JSON-Scans pro Event: ~20 lineare Scans desselben Strings pro
     Thermostat-WS-Event (jeder Extract* ein FindValueStart-Volldurchlauf).
     Hebel: Einzelfelder nur INNERHALB des bereits per ExtractBlock isolierten
     Blocks suchen statt im Gesamt-JSON. Struktureller Eingriff, groß.

### OPTIONAL/PENDING (auf Freigabe wartend)
- Wrapper-Signal-Alignment (Aktion↔Feedback auf gleicher Zeile, z.B. Set_On↔
  On_fb, Brightness_In↔Brightness_fb). Wurde analysiert+visualisiert, aber NUR
  die SKIP-Fixes wurden umgesetzt, NICHT das Alignment-Redesign.

### Verifikations-Checkliste vor jedem Paket
- python3 /tmp/balance.py SimplSharp/*.cs            (Klammern)
- Delegate-Namen Wrapper RegisterDelegate ↔ Klasse public *Delegate
- #SYMBOL_NAME Version konsistent über alle .usp
- AssemblyVersion in Properties/AssemblyInfo.cs
- _SKIP_-Regel: führende Skips nur erstes DI + erstes DO, count=params
- SetRawJsonEnabled vor Initialize in Main()

================================================================================
## TOOLING — balance.py (in neuem Chat neu anlegen unter /tmp/balance.py)
================================================================================
Python-Skript, das C#-Dateien auf ausbalancierte {}, (), [] prüft —
string-, char- und kommentar-aware. Aufruf: python3 /tmp/balance.py file1.cs ...
Gibt pro Datei "braces=0 parens=0 brackets=0 OK" oder UNBALANCED aus.
(Der vollständige Quelltext kann bei Bedarf neu geschrieben werden; er parst
zeichenweise und ignoriert Inhalte in "..", '..', // und /* */.)
