# MSFS Web API Server

A lightweight web API server that connects to Microsoft Flight Simulator (MSFS) and exposes SimConnect functionality through REST endpoints.

## Features

- System tray application with easy access to API documentation
- Swagger UI for API exploration and testing
- Real-time connection status monitoring
- SimVar get/set functionality through REST endpoints
- REST endpoint for SimConnect event sending (simulator commands)
- WebSocket streaming of SimVar updates (same request body as getMultiple)
- YAML configuration file support with command-line override
- Variable UDP output - simulation variables can be streamed via UDP (e.g. for home cockipt hw)

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
  - `variables`: List of SimVar names to stream via UDP

#### Command-Line Arguments

Command-line arguments override configuration file values:

```bash
MsfsApiServer.exe --port 5000 --logFile "C:\\Temp\\msfsapi.log" --config myconfig.yaml
```

Available arguments:

- `--port <number>`: Web server port (default:5018, overrides `webApi.port` in config)
- `--logFile <path>`: Log file path (overrides `general.logFile` in config)
- `--config <filename>`: Configuration file name or path (default: `config.yaml`)

### Logging

The app writes logs to a file so you can inspect the last run even when started without a console.

- Default file: next to the executable, with the same name and a `.log` extension (e.g., `MsfsApiServer.log`).
- The file is recreated (truncated) on each start and kept after the program exits.
- You can override the file via configuration file (`general.logFile`) or command line (`--logFile`):

```bash
MsfsApiServer.exe --logFile "C:\\Temp\\msfsapi.log"
```

- If the file cannot be written (permissions, path), file logging is silently disabled.

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
- Server messages: JSON array of `SimVarReference` with `value` populated for each requested SimVar.
- Default behavior: Server sends only when at least one value changed since the last sent snapshot (with small tolerance; `NaN` equals `NaN`).

### Query Parameters

- `interval` (int, optional): update interval in seconds. Default: `2`.
- `alwaysUpdate` (bool, optional):
 - `false` (default) – send only when values change.
 - `true` – force send every interval even if values did not change.

### Example WebSocket URL

```
ws://localhost:5018/api/simvar/register?interval=1&alwaysUpdate=true
```

### Simple Test Page

A basic test page is included at `tools/ws-test.html`. It connects to the WebSocket endpoint, sends a JSON array as the first message, and prints responses.

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

## System Requirements

- .NET 8.0 or later
- Microsoft Flight Simulator (2020) installed
- SimConnect SDK

## Example use - MSFS views
- cockpit view: {"simVarName": "CAMERA STATE", "value":2}
- external view: {"simVarName": "CAMERA STATE", "value":3}
- FMS view (instrument view3): {"simVarName":"CAMERA VIEW TYPE AND INDEX:1","value":2}

Basicaly for `CAMERA STATE` : `CAMERA VIEW TYPE AND INDEX:0` : `CAMERA VIEW TYPE AND INDEX:1` we have:
- 2:1:1 - cockpit, pilot, close
- 2:1:4 - cockpit, pilot, copilot
- 2:2:0 - cockipt, instrument, instrument01 (close look at glareshield)
- 2:2:2 - cockpit, instrument, instrument03 (FMS view)
- 3:0:0 - external, default
- 3:4:1 - external, quickview, quickview02 (front)