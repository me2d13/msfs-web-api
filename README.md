# MSFS Web API Server

A lightweight web API server that connects to Microsoft Flight Simulator (MSFS) and exposes SimConnect functionality through REST endpoints.

## Features

- System tray application with easy access to API documentation
- Swagger UI for API exploration and testing
- Real-time connection status monitoring
- SimVar get/set functionality through REST endpoints

## Running the Application

The application runs by default on port 5018. You can start it by running:

```bash
MsfsApiServer.exe
```

### Custom Port

You can optionally specify a different port using the command line parameter:

```bash
MsfsApiServer.exe --port 5000
```

## API Endpoints

- `GET /api/status` - Check API server and MSFS connection status
- `POST /api/simvar/get` - Get SimVar values
- `POST /api/simvar/set` - Set SimVar values

For detailed API documentation, access the Swagger UI at `http://localhost:{port}/swagger` when the application is running.

## System Requirements

- .NET 8.0 or later
- Microsoft Flight Simulator (2020) installed
- SimConnect SDK