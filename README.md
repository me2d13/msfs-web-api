# MSFS Web API Server

A lightweight web API server that connects to Microsoft Flight Simulator (MSFS) and exposes SimConnect functionality through REST endpoints.

## Features

- SimVar get/set functionality through REST endpoints with optional output aliases
- REST endpoint for SimConnect event sending (simulator commands)
- WebSocket streaming of SimVar updates (same request body as getMultiple)
- Variable UDP output - simulation variables can be streamed via UDP (e.g. for home cockpit hw)
- System tray application with easy access to API documentation
- Swagger UI for API exploration and testing
- YAML configuration file support with command-line override

## Disclaimer

I'm Java developer and have no experience with C# or .NET development. 
The application is 80% AI-generated, just keeping maintainable structure.
It has 2 main purposes for me:
1. make SimConnect accessible from javascript so I can build web-based cockpit panels (see [fs-web-panels](https://github.com/me2d13/fs-web-panels) repo)
1. send UDP data to my [motorized thtrottle quadrant](https://github.com/me2d13/mtu-pio) so it moves even for MSFS (not only X-Plane))

## Running the Application

The application runs by default on port 5018. You can start it by running:

```bash
MsfsApiServer.exe
```

### Configuration

The application can be configured via a YAML configuration file and/or command-line arguments. Command-line arguments take precedence over configuration file values.

#### Configuration File

By default, the app looks for `config.yaml` in the same directory as the executable. You can specify a different configuration file using:

```bash
MsfsApiServer.exe --config msfs2024.yaml
```

**Configuration file structure (`config.yaml`):**

```yaml
general:
  logFile: "C:\\Logs\\msfsapi.log"  # Optional: path to log file

webApi:
  port: 5018  # Optional: port for the web server

udp:
  enabled: false  # Enable/disable UDP streaming (default: false)
  targetHost: "192.168.1.100"  # Target host for UDP output
  targetPort: 49002      # Target port for UDP output
  interval: 100   # Update interval in milliseconds
  variables:    # List of SimVars to stream via UDP
    - "PLANE ALTITUDE"
    - "AIRSPEED INDICATED"
    - "HEADING INDICATOR"
# You can also use output aliases with pipe delimiter:
    - "GENERAL ENG THROTTLE LEVER POSITION:1|THR1"
    - "BRAKE PARKING INDICATOR|PRK_BRAKE"
```

**Configuration sections:**

- **`general`**: Application-wide settings
  - `logFile`: Log file path (same as `--logFile` command-line argument)

- **`webApi`**: Web API server settings
  - `port`: Port number for the web server (same as `--port` command-line argument)

- **`udp`**: UDP streaming configuration
  - `enabled`: Enable/disable UDP streaming (default: `false`). UDP streaming is disabled by default and must be explicitly enabled in the configuration file.
  - `targetHost`: Target host for UDP output
  - `targetPort`: Target port for UDP output
  - `interval`: Update interval in milliseconds (default: 100)
  - `variables`: List of SimVar names to stream via UDP (supports output aliases, see below)

#### Command-Line Arguments

Command-line arguments override configuration file values:

```bash
MsfsApiServer.exe --port 5000 --logFile "C:\\Temp\\msfsapi.log" --config myconfig.yaml
```

Available arguments:

- `--port <number>`: Web server port (default: 5018, overrides `webApi.port` in config)
- `--logFile <path>`: Log file path (overrides `general.logFile` in config)
- `--logLevel <level>`: Log level - Trace, Debug, Information (default), Warning, Error, Critical, or None
- `--config <filename>`: Configuration file name or path (default: `config.yaml`)

**Examples:**
```bash
# Run with debug logging to see all SimVar operations
MsfsApiServer.exe --logLevel Debug

# Run with custom config and warning-only logs
MsfsApiServer.exe --config myconfig.yaml --logLevel Warning

# Combine multiple parameters
MsfsApiServer.exe --port 5000 --logLevel Debug --logFile "C:\\Logs\\debug.log"
```

### Logging

The app writes logs to a file so you can inspect the last run even when started without a console.

- Default file: next to the executable, with the same name and a `.log` extension (e.g., `MsfsApiServer.log`).
- The file is recreated (truncated) on each start and kept after the program exits.
- You can override the file via configuration file (`general.logFile`) or command line (`--logFile`):

```bash
MsfsApiServer.exe --logFile "C:\\Temp\\msfsapi.log"
```

- If the file cannot be written (permissions, path), file logging is silently disabled.

## Output Aliases

SimVar names can include optional output aliases using pipe delimiter (`|`). This allows you to use shorter, more convenient names in API responses while still querying the full SimConnect variable name.

**Format:** `ACTUAL_VAR_NAME|OUTPUT_ALIAS`

**Example:**
- Request: `"GENERAL ENG THROTTLE LEVER POSITION:1|THR1"`
- SimConnect queries: `GENERAL ENG THROTTLE LEVER POSITION:1`
- Response uses: `THR1`

**Supported everywhere:**
- Single get/set endpoints
- Multiple get/set endpoints
- WebSocket streaming
- UDP streaming (via config file)

**Example request with alias:**
```bash
curl -X POST http://localhost:5018/api/simvar/get \
  -H 'Content-Type: application/json' \
  -d '{"simVarName": "BRAKE PARKING INDICATOR|PRK_BRAKE", "unit": ""}'
```

**Response:**
```json
{"PRK_BRAKE": 1}
```

Without aliases, responses use the full variable name. Mix and match as needed!

## Client Data Areas (`CLIENT:` variables)

Add-ons such as the PMDG 737 share state through SimConnect **client data areas** - named byte
blocks that other SimConnect clients can read. These are not SimVars, so they are addressed
explicitly with a `CLIENT:` prefix:

```
CLIENT:AREA_NAME[@AREA_ID]:OFFSET[:TYPE]|OUTPUT_ALIAS
```

- `AREA_NAME` - the published name of the area, e.g. `PMDG_NG3_Data`.
- `@AREA_ID` - optional. Some areas are identified by a numeric ID published by the add-on
  (`PMDG_NG3_Data` is `0x4E473331`). Known areas are pre-registered; for anything else supply it.
- `OFFSET` - byte offset inside the area, decimal or `0x` hex. Offsets come from the add-on's SDK header.
- `TYPE` - optional: `bool`/`byte`, `int8`, `int16`, `uint16`, `int32`, `uint32`, `int64`, `uint64`,
  `float`, `double` (default `double`). Unsigned variants matter for C `unsigned char`/`unsigned short` fields.
- `OUTPUT_ALIAS` - same pipe syntax as SimVars, so UDP/WS/JSON keys stay short.

```bash
curl -X POST http://localhost:5018/api/simvar/get \
  -H 'Content-Type: application/json' \
  -d '{"simVarName": "CLIENT:PMDG_NG3_Data:0x1B5:bool|AT_ARM"}'
```

```json
{"AT_ARM": 1}
```

Works everywhere SimVars do: `get`, `getMultiple`, WebSocket streaming and UDP streaming.
Read-only, and a missing area (add-on not loaded, broadcast disabled) never reaches SimConnect:
the value comes back as `NaN`, which serializes to `null` in JSON and is dropped from
UDP/WebSocket payloads rather than reported as `0`.

**Caching.** Each field is subscribed once with `SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET`, so
SimConnect pushes new data as the add-on writes it and reads are served from a cache instead of
a request per read. The first read of a field is primed with a one-shot request, and a cached
value that has not been refreshed for two seconds is primed again. That keeps streaming loops at
their configured rate: 21 fields stream at 50 ms with the cache, while eight fields could not
hold a 100 ms interval when every read was a round trip.

Implementation notes, verified against MSFS 2024 and the PMDG 737:

- The managed SimConnect wrapper hands back client data as a single 32-bit word per definition,
  whatever the declared type or size, so fields are read as raw byte ranges and decoded here.
  Fields wider than 4 bytes (`int64`, `double`) are fetched as two 4-byte chunks.
- A definition may only hold one field: adding a second one makes SimConnect reject it with
  `SIMCONNECT_EXCEPTION_DUPLICATE_ID`. Each field therefore gets its own definition, and its own
  subscription.
- `RegisterDataDefineStruct` has no effect on client data replies.
- `ON_SET` delivered roughly 14 updates/second on a PMDG 737, `SECOND` exactly one, and
  `VISUAL_FRAME` about 54 - too many to be worth subscribing to.
- Areas are identified by the numeric ID their creator used, so the published ID has to match
  (see `KnownClientDataAreas`).

## API Endpoints

- `GET /api/status` - Check API server and MSFS connection status
- `POST /api/simvar/get` - Get a single SimVar value
- `POST /api/simvar/set` - Set a single SimVar value
- `POST /api/simvar/getMultiple` - Get multiple SimVar values (parallel execution)
- `POST /api/simvar/setMultiple` - Set multiple SimVar values (sequential execution)
- `POST /api/event/send` - Send a SimConnect event (name mandatory, value optional)
- `WS /api/simvar/register` - WebSocket streaming of SimVar updates (see details below)

For detailed API documentation, access the Swagger UI at `http://localhost:{port}/swagger` when the application is running.

### Examples

#### Get Single SimVar

```bash
curl -X 'POST' 'http://localhost:5018/api/simvar/get' \
  -H 'Content-Type: application/json' \
  -d '{
    "simVarName": "PLANE ALTITUDE",
    "unit": "feet"
  }'
```

**Response:**
```json
{"PLANE ALTITUDE": 5234.2}
```

#### Get Single SimVar with Alias

```bash
curl -X 'POST' 'http://localhost:5018/api/simvar/get' \
  -H 'Content-Type: application/json' \
  -d '{
    "simVarName": "BRAKE PARKING INDICATOR|PRK_BRAKE",
    "unit": ""
  }'
```

**Response:**
```json
{"PRK_BRAKE": 1}
```

#### Set Single SimVar

```bash
curl -X 'POST' 'http://localhost:5018/api/simvar/set' \
-H 'Content-Type: application/json' \
  -d '{
    "simVarName": "CAMERA STATE",
    "unit": "",
    "value": 2
  }'
```

#### Get Multiple SimVars

Read multiple variables in parallel (order doesn't matter):

```bash
curl -X 'POST' 'http://localhost:5018/api/simvar/getMultiple' \
  -H 'Content-Type: application/json' \
  -d '[
    {
      "simVarName": "CAMERA STATE",
      "unit": ""
    },
    {
      "simVarName": "PLANE ALTITUDE",
      "unit": "feet"
    },
    {
      "simVarName": "AIRSPEED INDICATED",
      "unit": "knots"
    }
  ]'
```

**Response:**
```json
{
  "CAMERA STATE": 2,
  "PLANE ALTITUDE": 5234.2,
  "AIRSPEED INDICATED": 142.5
}
```

#### Get Multiple SimVars with Aliases

```bash
curl -X 'POST' 'http://localhost:5018/api/simvar/getMultiple' \
  -H 'Content-Type: application/json' \
  -d '[
    {
      "simVarName": "GENERAL ENG THROTTLE LEVER POSITION:1|THR1",
      "unit": ""
    },
    {
      "simVarName": "BRAKE PARKING INDICATOR|PRK_BRAKE",
      "unit": ""
    },
    {
      "simVarName": "PLANE ALTITUDE",
      "unit": "feet"
    }
  ]'
```

**Response:**
```json
{
  "THR1": 85.5,
  "PRK_BRAKE": 0,
  "PLANE ALTITUDE": 5234.2
}
```

#### Set Multiple SimVars

Set multiple variables sequentially (order is preserved for MSFS actions):

```bash
curl -X 'POST' 'http://localhost:5018/api/simvar/setMultiple' \
  -H 'Content-Type: application/json' \
  -d '[
    {
    "simVarName": "CAMERA STATE",
      "unit": "",
      "value": 2
    },
    {
      "simVarName": "FUEL TANK CENTER QUANTITY",
      "unit": "gallons",
      "value": 50
    }
  ]'
```

## WebSocket Streaming: `/api/simvar/register`

Stream live updates for one or more SimVars over WebSocket.

- URL: `ws://{host}:{port}/api/simvar/register`
- First client message (text): JSON array with the same schema as `POST /api/simvar/getMultiple`
  - Example first message:
  ```json
  [
    { "simVarName": "PLANE ALTITUDE", "unit": "feet" },
    { "simVarName": "AIRSPEED INDICATED", "unit": "knots" }
  ]
  ```
  - You can also use output aliases:
  ```json
  [
    { "simVarName": "GENERAL ENG THROTTLE LEVER POSITION:1|THR1", "unit": "" },
    { "simVarName": "BRAKE PARKING INDICATOR|PRK_BRAKE", "unit": "" }
  ]
  ```
- Server messages: JSON object with `{outputName: value}` for each requested SimVar
- Default behavior: Server sends only when at least one value changed since the last sent snapshot (with small tolerance; `NaN` equals `NaN`)

### Query Parameters

- `interval` (int, optional): update interval in seconds. Default: `2`.
- `alwaysUpdate` (bool, optional):
  - `false` (default)   send only when values change.
  - `true`   force send every interval even if values did not change.

### Example WebSocket URL

```
ws://localhost:5018/api/simvar/register?interval=1&alwaysUpdate=true
```

### Simple Test Page

A basic test page is included at `tools/ws-test.html`. It connects to the WebSocket endpoint, sends a JSON array as the first message, and prints responses.

A plain GET test page for single/multiple SimVars and `CLIENT:` fields is at `tools/get-test.html`.

`tools/pmdg-fields.html` browses the PMDG NG3 SDK fields: search by name or description, filter by
section, and get the ready-to-paste `CLIENT:` variable for a field (offsets and types are computed
from the SDK header with C alignment rules). Each row has a *Test* button that reads the field from
the running API. The SDK header is embedded in the page, so it works offline; after a PMDG update,
either pick the installed `PMDG_NG3_SDK.h` with the page's file input (parsed in the browser, nothing
is uploaded) or refresh the checked-in copy with:

```bash
node tools/update-pmdg-fields.js "C:\...\Community\pmdg-aircraft-738\Documentation\SDK\PMDG_NG3_SDK.h"
```

That page is why no PMDG field table is compiled into the server: the offset contract lives in the
header, and regenerating it is a command, not a code change.

### CLI Test with wscat

Use the `wscat` CLI to test from a terminal.

- Install (Node.js required):

```bash
npm install -g wscat
```

- Connect, then paste a one-line JSON array as the first message:

```bash
wscat -c "ws://localhost:5018/api/simvar/register?interval=1&alwaysUpdate=false"
# After connected, paste:
# [{"simVarName":"PLANE ALTITUDE","unit":"feet"},{"simVarName":"AIRSPEED INDICATED","unit":"knots"}]
```

You should start seeing JSON snapshots streamed back at the chosen interval, subject to the `alwaysUpdate` setting.

## UDP Streaming

When enabled in the configuration file, the server streams SimVar values via UDP at the configured interval. This is useful for home cockpit hardware that expects UDP data feeds.

**Configuration:**
```yaml
udp:
  enabled: true
  targetHost: "192.168.1.100"
  targetPort: 49002
  interval: 500  # milliseconds
  variables:
    - "GENERAL ENG THROTTLE LEVER POSITION:1|THR1"
    - "BRAKE PARKING INDICATOR|PRK_BRAKE"
    - "PLANE ALTITUDE"
```

**UDP output format:** JSON object `{"THR1": 85.5, "PRK_BRAKE": 0, "PLANE ALTITUDE": 5234.2}`

**Testing UDP output:**
```bash
# Linux/Mac
nc -ul 49002

# Windows (with netcat installed)
ncat -ul 49002
```

## System Requirements

- .NET 8.0 or later
- Microsoft Flight Simulator (2020 or 2024)
- SimConnect SDK

## Example use - MSFS views
- cockpit view: `{"simVarName": "CAMERA STATE", "value": 2}`
- external view: `{"simVarName": "CAMERA STATE", "value": 3}`
- FMS view (instrument view 3): `{"simVarName": "CAMERA VIEW TYPE AND INDEX:1", "value": 2}`

Basically for `CAMERA STATE` : `CAMERA VIEW TYPE AND INDEX:0` : `CAMERA VIEW TYPE AND INDEX:1` we have:
- 2:1:1 - cockpit, pilot, close
- 2:1:4 - cockpit, pilot, copilot
- 2:2:0 - cockpit, instrument, instrument 01 (close look at glareshield)
- 2:2:2 - cockpit, instrument, instrument 03 (FMS view)
- 3:0:0 - external, default
- 3:4:1 - external, quickview, quickview 02 (front)