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
  `www/737views.html` (PMDG 737 view switcher, pictures in `www/img`) is the file to edit when a
  view index turns out wrong: the whole set is its `VIEWS` table. `www/737nav.html` (COM/NAV radio
  panel, ported from the xplapi project) reads `COM/NAV ACTIVE/STANDBY FREQUENCY:n` and changes the
  radios with key events; if a radio does not respond in a given aircraft, the names are in its
  `RADIOS` table. `www/cdu.html` draws the PMDG 737 CDU screen
  (`PMDG_NG3_CDU_0`/`PMDG_NG3_CDU_1`, 24x14 cells of symbol/color/flags plus `Powered`) from
  `WS /api/clientdata/register`; the cell layout and flags are in its `render()`, the areas in its
  `AREAS` table.
- `build.ps1` (in the workspace root, outside this repository) also packages the release:
  `dist\MsfsApiServer.zip` every time, plus `dist\MsfsApiServer-selfcontained.zip` (the .NET runtime
  inside it) with `-SelfContained`. Both zips have one root folder `MsfsApiServer\` and `/`
  separators. It refuses to run while `MsfsApiServer.exe` is up, because that locks the DLLs.
- `packaging\README.txt` is the file that ships inside those zips as `README.txt`. It is written for
  whoever downloads the zip, not for us: requirements (both .NET 8 runtimes, or none for the
  self-contained build), how to run it, the config file, and where the `www` pages go. Keep it in
  sync whenever a change alters what a downloader has to know.

## Verify behaviour, not just the diff

Build after any edit, and exercise the API once (`curl` or a test page). Both bugs found in the
client data work looked fine in `git show` and only showed up when the code ran.

To watch a range that is *changing* without touching the cockpit, stream `PMDG_NG3_Data` at offset
`0x74` length 12: that is `FUEL_QtyCenter/Left/Right` as floats, and with the engines running they
fall as fuel burns, so a change-only stream has to push a frame at every poll interval. A static
screen (the CDU menu page) legitimately sends one frame and nothing after it - that is the
change-detection path working, not the pipe being dead; `?alwaysUpdate=1` shows the loop instead.

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

A whole block is read as a byte range instead of one field per byte:
`ClientDataService.GetRawBytesAsync` covers it with 4-byte chunks, each with its own definition and
`ON_SET` subscription (the same cache as the single fields), primes them once when the cache is
cold, and clamps the last chunk to the end of the range - a definition that reaches past the area
is rejected with `OUT_OF_BOUNDS` and never answered. A one-byte probe runs first, so an area that
is not broadcast costs one timeout instead of one per chunk, and raw chunks are trusted for 10 s
rather than 2 s so a static screen is not re-read 250 times. `GET /api/clientdata/raw` and
`WS /api/clientdata/register` are the endpoints; both share `TryValidateRange`,
`ClientDataRangeJson` and `TryParseFlag` in `WebApiHost`. Measured on a PMDG 737: 253 chunks ≈ 3%
of one core, 2-4 ms per read from the cache, and a change-only stream that pushes at the poll
interval (the CDU screen is the page that exercises it).

Offsets are not part of this codebase: derive them from the installed `PMDG_NG3_SDK.h` with
`tools/pmdg-fields.html` (or `node tools/update-pmdg-fields.js <header>`) instead of computing
them by hand. A missed `unsigned short` shifts every following field, which is how the A/T arm
switch was once read from the wrong byte.
