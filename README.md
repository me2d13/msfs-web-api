# MSFS Web API Server

A small web API server that connects to Microsoft Flight Simulator (2020 and 2024) and exposes
SimConnect over HTTP, WebSocket and UDP - so a page in a browser, a phone, a tablet or a bit of
cockpit hardware can read and set simulation variables, send simulator events, and read the data
add-ons such as PMDG broadcast. It runs in the Windows system tray and ships with example pages
(a PMDG 737 radio panel, a view switcher and a full CDU with working keys).

It exists for two reasons of mine: to make SimConnect reachable from JavaScript so web-based
cockpit panels can be built against it (see [fs-web-panels](https://github.com/me2d13/fs-web-panels)),
and to feed my [motorized throttle quadrant](https://github.com/me2d13/mtu-pio) over UDP so it
moves in MSFS too, not only in X-Plane.

## Features

- **SimVars over REST** - get and set, single or multiple, with optional output aliases
- **SimConnect events** - send any simulator event by name with a value
- **Client data areas** - read the shared data blocks add-ons publish (PMDG and friends), by field,
  and whole byte ranges for the blocks that are not a number per field (a CDU screen, for example)
- **WebSocket streaming** - SimVar values, or a client data byte range, pushed as they change
- **UDP output** - stream SimVars to home cockpit hardware at a fixed interval
- **Status page** - connection state, lookup counters and the running configuration, served by the
  server itself; your own html pages dropped into `www` next to the exe are served and listed there
- **Swagger UI** - every endpoint, browsable and testable in the browser
- **Tray application with a YAML config file**, overridable from the command line

## Disclaimer

I'm a Java developer with no C# or .NET experience; the application is largely AI-generated, kept
in a maintainable shape. It is built for my own cockpit and shared in case it saves you some work.

## Getting started

Unpack the release zip and run `MsfsApiServer.exe`. It sits in the tray and listens on
<http://localhost:5018> - open that address (or double click the tray icon) for the status page,
which shows whether the SimConnect link to the simulator is up.

Needs Windows and the .NET 8 desktop *and* ASP.NET Core runtimes; the `-selfcontained` build in the
release carries the runtime itself and needs nothing installed. See
[System requirements](#system-requirements).

### Configuration

The application is configured by a YAML file and/or command-line arguments; command-line arguments
win.

By default it looks for `config.yaml` next to the executable:

```bash
MsfsApiServer.exe --config msfs2024.yaml
```

```yaml
general:
  logFile: "C:\\Logs\\msfsapi.log"   # Optional: path to the log file

webApi:
  port: 5018                         # Optional: port for the web server

udp:
  enabled: false                     # UDP streaming is off unless enabled here
  targetHost: "192.168.1.100"
  targetPort: 49002
  interval: 100                      # milliseconds
  variables:                         # SimVars to stream, aliases allowed after "|"
    - "PLANE ALTITUDE"
    - "AIRSPEED INDICATED"
    - "GENERAL ENG THROTTLE LEVER POSITION:1|THR1"
    - "BRAKE PARKING INDICATOR|PRK_BRAKE"
```

Sections:

- **`general`** - `logFile`: where to write the log (same as `--logFile`).
- **`webApi`** - `port`: the port to listen on (same as `--port`).
- **`udp`** - `enabled` (default `false`), `targetHost`, `targetPort`, `interval` (default 100 ms)
  and `variables`, the list to stream (aliases allowed).

Command-line arguments:

- `--port <number>` - web server port (default 5018)
- `--logFile <path>` - log file path
- `--logLevel <level>` - Trace, Debug, Information (default), Warning, Error, Critical or None
- `--config <filename>` - configuration file name or path (default `config.yaml`)

```bash
MsfsApiServer.exe --port 5000 --logLevel Debug --logFile "C:\\Logs\\debug.log"
```

### Logging

Logs go to a file, so the last run can be read even when the app starts without a console.

- Default: next to the executable, same name with `.log` (e.g. `MsfsApiServer.log`).
- The file is truncated on each start and kept after the program exits.
- `--logFile` or `general.logFile` overrides it; if the file cannot be written, file logging is
  silently disabled.

## API

Everything lives under `/api` on the configured port. Swagger UI at
`http://localhost:{port}/swagger` documents the same surface interactively.

| Endpoint | What it does |
|---|---|
| `GET /api/status` | Server and MSFS connection state |
| `GET /api/stats` | Everything the status page shows, as JSON |
| `POST /api/simvar/get` | Read one SimVar |
| `POST /api/simvar/set` | Write one SimVar |
| `POST /api/simvar/getMultiple` | Read several SimVars (in parallel) |
| `POST /api/simvar/setMultiple` | Write several SimVars (in order) |
| `POST /api/event/send` | Send a SimConnect event by name |
| `WS /api/simvar/register` | Stream SimVar values |
| `GET /api/clientdata/raw` | Read a byte range of a client data area (base64) |
| `WS /api/clientdata/register` | Stream a client data byte range |
| `GET /{file}` | A page from the `www` directory next to the exe |

### SimVars

```bash
curl -X POST http://localhost:5018/api/simvar/get \
  -H 'Content-Type: application/json' \
  -d '{"simVarName": "PLANE ALTITUDE", "unit": "feet"}'
```

```json
{"PLANE ALTITUDE": 5234.2}
```

`getMultiple` takes an array and answers a map; `setMultiple` writes them in the order given, which
matters for MSFS actions:

```bash
curl -X POST http://localhost:5018/api/simvar/getMultiple \
  -H 'Content-Type: application/json' \
  -d '[{"simVarName":"CAMERA STATE","unit":""},
       {"simVarName":"AIRSPEED INDICATED","unit":"knots"}]'
```

```json
{"CAMERA STATE": 2, "AIRSPEED INDICATED": 142.5}
```

A value that could not be read comes back as `null` (the SimVar timed out, or the add-on is not
broadcasting) - never as `0`, since zero is a real value.

### Output aliases

Any name may carry an alias after a pipe, so responses, WebSocket messages and UDP payloads use the
short name while the full SimConnect name is what gets queried:

```bash
curl -X POST http://localhost:5018/api/simvar/get \
  -H 'Content-Type: application/json' \
  -d '{"simVarName": "BRAKE PARKING INDICATOR|PRK_BRAKE", "unit": ""}'
```

```json
{"PRK_BRAKE": 1}
```

Aliases work on every endpoint, the WebSocket and the UDP stream.

### Events

```bash
curl -X POST http://localhost:5018/api/event/send \
  -H 'Content-Type: application/json' \
  -d '{"name": "COM_STBY_RADIO_SET_HZ", "value": 120000000}'
```

`value` is the event's data word (a frequency, a position, a click flag). Events are looked up by
name the first time and reused after that.

Some add-ons take their own numbers as the *value* of an event the simulator knows - PMDG's CDU
keys are driven that way, which is how `www/cdu.html` presses them:

```bash
curl -X POST http://localhost:5018/api/event/send \
  -H 'Content-Type: application/json' \
  -d '{"name": "ROTOR_BRAKE", "value": 55201}'      # FMS Left LEGS
```

### Client data areas (`CLIENT:` variables)

Add-ons such as the PMDG 737 share state through SimConnect **client data areas** - named byte
blocks other SimConnect clients can read. They are not SimVars, so they are addressed with a
`CLIENT:` prefix:

```
CLIENT:AREA_NAME[@AREA_ID]:OFFSET[:TYPE]|OUTPUT_ALIAS
```

- `AREA_NAME` - the published name of the area, e.g. `PMDG_NG3_Data`.
- `@AREA_ID` - optional. Some areas are identified by the numeric ID their author published
  (`PMDG_NG3_Data` is `0x4E473331`). Areas this server knows are pre-registered; for anything else
  supply the ID.
- `OFFSET` - byte offset inside the area, decimal or `0x` hex, from the add-on's SDK header.
- `TYPE` - optional: `bool`/`byte`, `int8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`,
  `float`, `double` (default `double`). The unsigned variants matter for C `unsigned char` /
  `unsigned short` fields.
- `OUTPUT_ALIAS` - the same pipe syntax as SimVars.

```bash
curl -X POST http://localhost:5018/api/simvar/get \
  -H 'Content-Type: application/json' \
  -d '{"simVarName": "CLIENT:PMDG_NG3_Data:0x1B5:bool|AT_ARM"}'
```

```json
{"AT_ARM": 1}
```

`CLIENT:` fields work everywhere SimVars do - `get`, `getMultiple`, the WebSocket and the UDP
stream - and they are read-only. A field that cannot be read (add-on not loaded, broadcast
disabled, offset past the end of the area) comes back as `null` rather than `0`. With a wrong
offset, expect the first read of that field to wait out the 2 s timeout: SimConnect rejects the
definition and never replies.

Getting the offsets right is most of the work, so this repository ships a helper for the PMDG NG3:
`tools/pmdg-fields.html` reads the aircraft's `PMDG_NG3_SDK.h` (one is embedded, and you can pick a
newer one), shows every field with its offset and type, and hands you the ready-to-paste `CLIENT:`
reference.

### Reading a whole block

Not everything in a client data area is a number: the PMDG 737 shares each CDU screen as one block
of 1009 bytes (24x14 cells of `Symbol`, `Color`, `Flags` plus a `Powered` bool), which no single
field read can carry. Two endpoints read a byte range instead, as base64:

```bash
curl 'http://localhost:5018/api/clientdata/raw?area=PMDG_NG3_CDU_0&offset=0&length=1009'
```

```json
{"area":"PMDG_NG3_CDU_0","offset":0,"length":1009,"available":true,"data":"IA…"}
```

- `area` - the published area name; only an area this server knows may omit its ID, anything else
  passes `&areaId=0x4E473331` (decimal works too).
- `offset` - byte offset, decimal or `0x` hex. Default `0`.
- `length` - number of bytes, 1..8192. Required.
- `available` is `false` and `data` is `null` when the area does not answer - never a block of
  zeros, since zeros are a legitimate screen.

The same range can be streamed, and then only when it changed:

```
ws://localhost:5018/api/clientdata/register
```

The first text message describes the range and the pace:

```json
{"area":"PMDG_NG3_CDU_0","offset":0,"length":1009,"intervalMs":200,"alwaysUpdate":false}
```

- `intervalMs` - how often the range is compared and sent, 50..5000, default 200.
- `alwaysUpdate` (`true`/`1`) sends every interval instead of only on change.
- Both may also be given as query parameters (`?intervalMs=200&alwaysUpdate=1`), which is handy from
  a generic WebSocket client; the query wins over the body.
- Server messages have the same shape as the REST response, so a page decodes one base64 string.
- An invalid range is answered with `{"error":"..."}` and the socket is closed.

#### The PMDG 737 CDU screen, as the data is laid out

- `Symbol` - `0` for a blank cell, ASCII up to `0x7F`, and above that a glyph of the CDU's own font
  rather than the Latin-1 character with the same number: `0xA1`..`0xA4` are the left, right, up and
  down arrows, `0xB0` is the degree sign and `0xEA` is the hollow box that stands for a numeric
  field the FMC has no value for.
- `Color` - `0` white, `1` cyan, `2` green, `3` magenta, `4` amber, `5` red.
- `Flags` - bit `0x01` small font (line headers), `0x02` reverse video (highlighted), `0x04` dimmed
  (unused or inoperative entry).
- Cells are `Cells[24][14]`, so cell `(column, row)` starts at byte `(column * 14 + row) * 3`; byte
  1008 is `Powered`, and a powered-off CDU keeps answering with its last content.
- `PMDG_NG3_CDU_0` is the captain's screen, `PMDG_NG3_CDU_1` the first officer's; each is broadcast
  only with `EnableCDUBroadcast.0=1` / `.1=1` in the `[SDK]` section of the aircraft's options file.

`www/cdu.html` draws one of them.

### WebSocket streaming of SimVars: `/api/simvar/register`

- URL: `ws://{host}:{port}/api/simvar/register`
- First client message: a JSON array with the same schema as `getMultiple`, aliases allowed:

```json
[{"simVarName": "PLANE ALTITUDE", "unit": "feet"},
 {"simVarName": "GENERAL ENG THROTTLE LEVER POSITION:1|THR1", "unit": ""}]
```

- Server messages: `{outputName: value}` for the values that changed.
- Default behaviour: send only when at least one value changed (`NaN` counts as equal to `NaN`).

Query parameters:

- `interval` - update interval in **seconds**, default `2`.
- `alwaysUpdate` - `true` sends every interval even when nothing changed.

```
ws://localhost:5018/api/simvar/register?interval=1&alwaysUpdate=true
```

`tools/ws-test.html` is a minimal client for it (`wscat -c "ws://localhost:5018/api/simvar/register?interval=1"`
works too, paste the array as the first message), and `tools/get-test.html` is a page for trying
`get`/`getMultiple` by hand, including `CLIENT:` fields.

### UDP streaming

With `udp.enabled: true` in the config, the server sends the configured variables to
`udp.targetHost:udp.targetPort` every `udp.interval` milliseconds, as one JSON object per packet:

```json
{"THR1": 85.5, "PRK_BRAKE": 0, "PLANE ALTITUDE": 5234.2}
```

Values that could not be read are left out of the packet rather than sent as `0`. To watch what
arrives, listen on the target port with `nc -ul 49002` (Linux/macOS) or `ncat -ul 49002` (Windows) -
my motorized throttle quadrant does exactly that.

## Status page

`http://localhost:{port}/` (also `/status`) shows what the server is doing right now; the tray menu
opens it, and a double click on the tray icon does the same. It re-reads `/api/stats` every two
seconds, so it can be left open while you fly.

It reports the server (path, port, URLs, log file, pid, runtime, uptime), the configuration it was
loaded from, each stream (target, interval, variables, packets sent, last error), the SimConnect
state with the client data areas seen so far, every variable that was looked up with its read and
write counters, and one link per html page in the `www` directory.

### Your own pages

Drop html files into the `www` directory next to `MsfsApiServer.exe` (it is created at startup, and
the repository's pages are copied there on build). Each page is served at its own path -
`http://localhost:5018/views.html` - and listed on the status page under the name without its
extension.

Subdirectories work, so a page can bring its own assets (`<img src="/img/plane.jpg">`,
`<link rel="stylesheet" href="/css/panel.css">`), and only file types a page can need are served
(html, css, js, json, txt, csv, xml, wasm, images, fonts, audio, video, pdf). Everything else, and
anything outside `www`, is a 404; `/`, `/status`, `/api/...` and `/swagger` keep their own routes,
so a `www/index.html` would never be reached. Pages are served with `Cache-Control: no-cache`, so a
reload picks up your edits. Only the html files in the top level are listed on the status page.

Because the pages are served by the API itself, `fetch(location.origin + '/api/simvar/get', ...)`
works from any machine without CORS setup.

**Pages that ship with the server**

- `www/test.html` - a small example: a read with optional polling, a write, and a multiple read.
- `www/737views.html` - a view switcher for the PMDG 737. Buttons sit on a cockpit photo (pilot,
  copilot, MCP, FMC, pedestal, throttle quadrant, overhead, tablet) and, on a second page, on an
  aircraft picture for the external quickviews. A click sends `CAMERA STATE`,
  `CAMERA VIEW TYPE AND INDEX:0` and `:1` in one `setMultiple` call, reads the view back once
  shortly after (retried a few times, never polled) and highlights the matching button.
- `www/737nav.html` - a COM/NAV radio panel with act/stby lines, a digit keypad, a swap button and
  zoom for a small tablet. It reads `COM/NAV ACTIVE/STANDBY FREQUENCY:n` from the SimVar stream and
  changes the radios with key events (`COM_STBY_RADIO_SET_HZ`, `COM1_RADIO_SWAP`, the NAV1/NAV2
  equivalents); the number you typed is confirmed a second later by the stream, because MSFS does
  not let a client write the COM/NAV simvars directly.
- `www/cdu.html` - the PMDG 737 CDU: the screen of the captain's or the first officer's CDU as a
  tablet-sized unit with working keys. The screen comes from the client data areas over
  `/api/clientdata/register` (colours, the small-font and dimmed flags, reverse video and the
  powered state as the data says); `CAPT`/`F/O` picks which CDU is shown, `A-`/`A+` sizes the unit,
  and typing on a computer keyboard sends the matching key. The keys press the FMC through the
  event endpoint described above - nothing was added to the API for them. A **log** button (or
  `?debug=1`) shows what the socket did, which is the first thing to look at if the screen stops
  updating; a dropped connection keeps the last screen visible and reconnects on its own.

## Example: driving the MSFS views from a panel

`CAMERA STATE` and `CAMERA VIEW TYPE AND INDEX:0`/`:1` are enough to place the camera exactly, and
they are the reason `www/737views.html` exists. Concretely, for `state:type:index`:

- `2:1:1` cockpit, pilot, close - `2:1:4` cockpit, pilot, copilot
- `2:2:0` cockpit, instrument 01 (glareshield); on the PMDG 737 `2:2:2` is the FMC, `2:2:3` the
  pedestal, `2:2:4` the throttle quadrant, `2:2:5` the overhead, `2:2:6` behind the overhead,
  `2:2:8` the tablet
- `3:0:0` external chase - `3:4:1` external quickview 02 (front), `3:4:0` left, `3:4:2` right,
  `3:4:4` fly-by

```bash
curl -X POST http://localhost:5018/api/simvar/setMultiple \
  -H 'Content-Type: application/json' \
  -d '[{"simVarName":"CAMERA STATE","unit":"","value":2},
       {"simVarName":"CAMERA VIEW TYPE AND INDEX:0","unit":"","value":2},
       {"simVarName":"CAMERA VIEW TYPE AND INDEX:1","unit":"","value":2}]'
```

## System requirements

- Windows
- Microsoft Flight Simulator 2020 or 2024, running on the same machine
- The .NET 8 desktop and ASP.NET Core runtimes - or the self-contained release, which needs nothing
  installed

## Working on the code

Prerequisites, how to run it from source, how to build a release, the tests and the notes behind
the client data implementation are in [DEVELOPMENT.md](DEVELOPMENT.md).
