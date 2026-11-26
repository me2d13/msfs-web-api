# Development notes

How to build, run and test this project, and what sits behind some of the code. The user-facing
documentation - what the API is and how to call it - is in [README.md](README.md).

## What the application is

One Windows process in two projects, plus the pages it serves:

- **`MsfsApiServer`** - the application itself: a tray app (WinForms) that hosts a Kestrel web
  server. It owns the HTTP and WebSocket surface, the built-in status page, the `www` directory of
  pages next to the executable, and the configuration (YAML plus command-line overrides).
- **`SimConnector`** - everything that talks to the simulator: the SimConnect client, the SimVar
  reader/writer, the event sender, the client data reader, and the counters the status page shows.
- **`SimConnector.Tests`** - xUnit tests for the parts that can be checked without a simulator.

The SimConnect API is not thread-safe, so every call into it goes through one command queue that a
single reader drains; the services above it are async and wait on replies by request id. The
SimConnect *message* pump is a timer that calls `ReceiveMessage` on the same window handle the tray
app already owns.

## Prerequisites

- Windows (the SimConnect wrapper is a native Windows library).
- **.NET 8 SDK** to build; the .NET 8 desktop *and* ASP.NET Core runtimes to run a
  framework-dependent build.
- **Microsoft Flight Simulator 2020 or 2024** on the same machine, and the **SimConnect SDK**
  installed. The managed wrapper (`Microsoft.FlightSimulator.SimConnect.dll`) and the native
  `SimConnect.dll` are referenced from the SDK, and both must end up next to the built executable:
  the projects reference the SDK path in a `Reference`/`None` item and a post-build step copies the
  two DLLs into the output, so an SDK installed somewhere else means editing that path.
- Optional: the PMDG 737 SDK header (`PMDG_NG3_SDK.h`) shipped with the aircraft. Offsets are not
  compiled into this codebase - `tools/pmdg-fields.html` derives them from that header, and its
  checked-in copy can be refreshed with `node tools/update-pmdg-fields.js <header>`.

## Running from source

```
dotnet run --project MsfsApiServer/MsfsApiServer.csproj
```

The server starts in the tray and listens on port 5018 unless the config or `--port` says otherwise.
`/api/status` reports whether the SimConnect link is up; if the simulator is not running the
connection monitor keeps retrying in the background, so the server can be started before MSFS.

The `www` directory is copied next to the output on build, and that copy - not the one in the
repository - is what the running server serves, so a page edit needs a rebuild (or a copy) to show
up. The file logger appends across restarts and rolls at 20 MiB, retaining five numbered archives.
At `Debug`, connection/event and client-data subscription details are logged; per-read SimVar
request/response traffic is `Trace` to avoid flooding the file. Use `--logLevel Trace` when
investigating individual values.

## Building a release

`build.ps1` in the workspace root (outside this repository) publishes into `release/` and packages
the zips:

```
.\build.ps1                 # release/ + dist\MsfsApiServer.zip
.\build.ps1 -SelfContained   # also dist\MsfsApiServer-selfcontained.zip
```

- Publishing needs `/p:Platform=x64` (the projects declare `Platforms=x64`); the script passes it,
  along with `--runtime win-x64` and `--self-contained false` for the normal build.
- `dotnet publish` does not carry the two native SimConnect DLLs, so the script copies them into the
  publish output as well.
- A running `MsfsApiServer.exe` locks its own DLLs. Stop it first; the script refuses to run while
  it is up rather than failing halfway.
- Each zip has one root folder (`MsfsApiServer/`) and `/` separators, so unpacking gives a single
  folder. `packaging/README.txt` ships inside them as `README.txt` and is written for whoever
  downloads the zip - keep it in sync when a change alters what a downloader has to know.
- The self-contained build carries the .NET runtime; the normal one needs both .NET 8 runtimes
  installed on the target machine.

## Tests

```
dotnet test SimConnector.Tests/SimConnector.Tests.csproj -c Release
```

That path builds only `SimConnector` and the tests, so unlike a full `dotnet build src.sln` it works
while the server is running, and it does not need the simulator. The tests cover what can be checked
without one: the SimVar name grammar (unit and alias), the whole `CLIENT:` reference grammar, the
client data decoding rules (masking, sign extension, float32 bits, two-word assembly of 64-bit
fields), the chunking and assembly of a byte range, and the lookup counters behind the status page.
They reference the SimConnect wrapper for its datatype constants, so the SDK has to be installed.

Build after any edit and exercise the API once; more than one bug in this codebase looked fine in a
diff and only showed up when it ran. The working rules for agents - and the quick ways to verify
behaviour without a cockpit - are in [AGENTS.md](AGENTS.md).

## Notes behind the code

### Client data areas

An add-on publishes a named byte block; a client maps the name to the area's numeric ID and reads
fields out of it by offset. Things that are worth knowing before changing that code:

- The managed SimConnect wrapper returns **one 32-bit word per definition**, whatever the declared
  type or size, so fields are read as raw bytes and decoded here. Fields wider than four bytes
  (`int64`, `double`) are fetched as two 4-byte chunks.
- A definition may only hold one field; adding a second one is rejected with
  `SIMCONNECT_EXCEPTION_DUPLICATE_ID`. Each field therefore gets its own definition and its own
  subscription, and `RegisterDataDefineStruct` has no effect on client data replies.
- Reads are served from a cache fed by `SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET` subscriptions, with a
  one-shot request to prime a cold field. `ON_SET` delivered roughly 14 updates per second on a PMDG
  737, `SECOND` exactly one, and `VISUAL_FRAME` around 54.
- Areas are identified by the numeric ID their author used, so a name only maps if the ID matches
  the one the add-on published; the ones this server knows are in `KnownClientDataAreas`.
- A byte range is covered with 4-byte chunks, each its own definition and subscription, primed once
  when cold, with the last chunk clamped to the end of the range - a definition that reaches past
  the area is rejected (`OUT_OF_BOUNDS`) and never answered. A one-byte probe runs first, so an area
  that is not broadcasting costs one timeout instead of one per chunk.
- Measured on a PMDG 737: the 253 chunks of a CDU screen cost about 3% of one core, a cached read is
  2-4 ms, and a change-only stream pushes at the poll interval.

### PMDG 737 CDU keys

The keys of the CDU are not SimConnect events with names, and two routes that look obvious do not
work - measured, not assumed:

- Writing `{eventId, mouseFlag}` into the `PMDG_NG3_Control` client data area is ignored for CDU
  keys (the click sound plays, nothing registers).
- Sending the key's SDK event id as a SimConnect event is answered with
  `SIMCONNECT_EXCEPTION_NAME_UNRECOGNIZED` (7) when a name is used, and with `ERROR` (1) or
  `UNRECOGNIZED_ID` (3) when the bare id is transmitted.

What does work is the standard `ROTOR_BRAKE` event carrying PMDG's own action number as its value:
`(SDK offset * 100) + 1` presses, `+4` releases. The offset is the key's `EVT_CDU_L_*` /
`EVT_CDU_R_*` value in `PMDG_NG3_SDK.h` minus `THIRD_PARTY_EVENT_ID_MIN` (69632). The same numbers
are what MobiFlight's HubHop presets use, which is a good cross-check when a key misbehaves.

## Conventions

- `.gitattributes` pins text files to LF in the repository *and* in the working tree
  (`* text=auto eol=lf`), so a checkout always matches the stored blob. Keep it that way - if a file
  ends up with CRLF, convert it (`sd '\r$' '' <file>`; `sed -i` is not reliable on this share)
  rather than committing it. `.bat`/`.cmd` are the deliberate exception, because cmd.exe mis-parses
  LF-only batch files.
  It matters more than it sounds: committing *part of* a file whose working copy and blob disagree
  about line endings can land the hunks on the wrong lines - four files were once committed with
  their added lines hoisted above the `namespace`, and it took a build to notice.
- After a commit, check that the committed blobs are what the working tree holds
  (`git show HEAD:<file> | cmp - <file>`) and build.
