# Agent notes

MSFS Web API server: a .NET 8 Windows tray app that exposes SimConnect through REST,
WebSocket and UDP. `README.md` documents the API and the client data area support.

## Build and run

- Build: `dotnet build src.sln -c Release` from this directory, or `.\build.ps1` from the
  workspace root to publish into `release/`. `/p:Platform=x64` is required for a publish
  because the projects declare `Platforms=x64`.
- A running `MsfsApiServer.exe` locks its own DLLs, so stop it before building.
- The server listens on <http://localhost:5018> (`/api/status` reports the SimConnect state).
- Test pages, opened as local files (they call localhost): `tools/get-test.html` (GET with
  presets), `tools/pmdg-fields.html` (PMDG SDK field browser), `tools/ws-test.html` (WebSocket).
- `MsfsApiServer/www/test.html` is served by the server itself at `/test.html`; the whole `www`
  directory is copied next to the exe on build, which is where the exe reads it from.

## Verify behaviour, not just the diff

Build after any edit, and exercise the API once (`curl` or a test page). Both bugs found in the
client data work looked fine in `git show` and only showed up when the code ran.

## Tests

`SimConnector.Tests` (xUnit) covers the reference parsing and the client data decoding:

```
dotnet test SimConnector.Tests/SimConnector.Tests.csproj -c Release
```

That path builds only `SimConnector` and the tests, so unlike a full `dotnet build src.sln` it
works while the server is running. Prefer adding a case there over re-checking by hand: the
decoding rules (masking, sign extension, float32 bits, two-word assembly of 64-bit fields) and the
`CLIENT:` grammar are exactly where a wrong byte or a swapped word hides.

## Line endings - read before committing

`.gitattributes` pins text files to LF in the repository *and* in the working tree
(`* text=auto eol=lf`), so a checkout always matches the stored blob. Keep it that way: if a file
ends up with CRLF, convert it (`sd '\r$' '' <file>` - `sed -i` is not reliable on this share, it
failed mid-write and removed files once) instead of committing it. `.bat`/`.cmd` are the
deliberate exception (CRLF, because cmd.exe mis-parses LF-only batch files).

Why it matters: committing *part of* a file whose working copy and blob disagree about line
endings can land the hunks on the wrong lines. We once committed four files with their added
lines hoisted above the `namespace`; it did not compile and was only caught by building
afterwards. After any commit, check `git show HEAD:<file> | cmp - <file>` for the committed
paths, then build.

## Status page

`MsfsApiServer/Status/` holds it: `ServerStatus` (runtime facts, filled in by `Program`, read by the
endpoints), `StatusSnapshot` (the `/api/stats` JSON, serialized camelCase), `StatusPage` (the page
itself - one html template with a script that polls `/api/stats` every 2 s) and `WwwFiles` (the
rules for the `www` directory). Lookup counters live in `SimConnector/SimVarStatistics.cs` and are
fed by `SimVarService`, so every caller is counted.

Two traps:

- Never call `SimConnectClient.RefreshConnection()` from a polled endpoint. That helper disconnects
  and reconnects, and the disconnect handler in `ClientDataService` clears the mapped areas,
  definitions and subscriptions, so a page polling every 2 s would wipe the client data state on
  every poll. Both `/api/status` and `/api/stats` read `IsConnected` instead; the connection
  monitor reconnects within 5 s on its own.
- `/` and `/status` are the built-in page, and `GET /{**path}` serves the `www` files (subdirectories
  included). Literal routes win over the catch-all, so a `www/status` file would never be reached;
  `WwwFiles.Resolve` refuses anything outside `www` or of an unknown type.

## Client data areas

Read `README.md` -> "Client Data Areas" before touching that code. In short: the managed
SimConnect wrapper returns one 32-bit word per definition whatever its type or size, a
definition can hold only one field (`DUPLICATE_ID` otherwise), and reads are served from an
`ON_SET` subscription cache.

Offsets are not part of this codebase: derive them from the installed `PMDG_NG3_SDK.h` with
`tools/pmdg-fields.html` (or `node tools/update-pmdg-fields.js <header>`) instead of computing
them by hand. A missed `unsigned short` shifts every following field, which is how the A/T arm
switch was once read from the wrong byte.
