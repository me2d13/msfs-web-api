# Agent notes

MSFS Web API server: a .NET 8 Windows tray app that exposes SimConnect over REST, WebSocket and UDP.
`README.md` is the user-facing API documentation. `DEVELOPMENT.md` holds what a developer needs -
prerequisites, how to run and build it, the tests, and the notes behind the client data
implementation; read it when you touch the build, the packaging, the SimConnect wiring or the client
data internals, not on your first pass.

## How to work here

The maintainer's rules for this repository:

1. **Never commit without approval.** Make the change, verify it, report it - and wait for the word.
2. **For a bigger task, share the plan first and ask.** A short plan is enough; get a yes before
   writing the code.
3. **Anything unexpected, or not working the way it should: stop and say so.** Do not quietly work
   around it.
4. **You may suggest next steps, but have them approved** before starting on them.
5. **Side effects need approval as well.** If a task's solution means touching more than was asked -
   the exe sources when the ask was just a web page, for instance - stop, explain why, and ask.
6. **Keep the API lean.** It is user-facing and has to stay simple and understandable; the pages in
   `www` are demonstrations of what it can already do. Add an endpoint only when it is really
   needed, with one clear purpose and good documentation.

## Build and verify

- Build: `dotnet build src.sln -c Release` from this directory, or `.\build.ps1` in the workspace
  root to publish into `release/` and package the zips. `/p:Platform=x64` is required to publish.
- A running `MsfsApiServer.exe` locks its own DLLs - stop it before building.
- Tests: `dotnet test SimConnector.Tests/SimConnector.Tests.csproj -c Release`. That path builds
  only `SimConnector` and the tests, so unlike a full `dotnet build src.sln` it works while the
  server is running. Prefer adding a case there over re-checking by hand: the decoding rules
  (masking, sign extension, float32 bits, two-word assembly of 64-bit fields) and the `CLIENT:`
  grammar are exactly where a wrong byte or a swapped word hides.
- The server listens on <http://localhost:5018> (`/api/status` reports the SimConnect state). The
  `www` directory next to the exe is what gets served, so a page edit needs a rebuild or a copy.
- Line endings are LF (`DEVELOPMENT.md` -> Conventions). After a commit, confirm the blobs match the
  working tree (`git show HEAD:<file> | cmp - <file>`) and build.

## Verify behaviour, not just the diff

Build after any edit, and exercise the API once (`curl` or a test page). Several bugs in this
codebase looked fine in `git show` and only showed up when the code ran.

A socket bug in a page shows up as a close every couple of seconds in its log panel: open
`www/cdu.html?debug=1`, click CAPT/F/O a few times and read what the socket did (that is how a
reconnect storm was found). Restarting the server with the page open is the cheap check that a drop
recovers: the page keeps the last screen, dimmed, and reconnects with a growing delay.

To watch a range that is *changing* without touching the cockpit, stream `PMDG_NG3_Data` at offset
`0x74` length 12: that is `FUEL_QtyCenter/Left/Right` as floats, and with the engines running they
fall as fuel burns, so a change-only stream has to push a frame at every poll interval. A static
screen (the CDU menu page) legitimately sends one frame and nothing after it - that is the
change-detection path working, not the pipe being dead; `?alwaysUpdate=1` shows the loop instead.

The CDU is a good end-to-end probe: `POST /api/event/send` with `{"name":"ROTOR_BRAKE","value":55201}`
turns its page to `RTE 1 LEGS 1/1`, and `55101` brings it back to `MENU`.

## Status page

`MsfsApiServer/Status/` holds it: `ServerStatus` (runtime facts, filled in by `Program`, read by the
endpoints), `StatusSnapshot` (the `/api/stats` JSON, serialized camelCase), `StatusPage` (one html
template whose script polls `/api/stats` every 2 s) and `WwwFiles` (the rules for the `www`
directory). Lookup counters live in `SimConnector/SimVarStatistics.cs` and are fed by
`SimVarService`, so every caller is counted.

Two traps:

- Never call `SimConnectClient.RefreshConnection()` from a polled endpoint. That helper disconnects
  and reconnects, and the disconnect handler in `ClientDataService` clears the mapped areas,
  definitions and subscriptions, so a page polling every 2 s would wipe the client data state on
  every poll. Both `/api/status` and `/api/stats` read `IsConnected` instead; the connection monitor
  reconnects within 5 s on its own.
- `/` and `/status` are the built-in page, and `GET /{**path}` serves the `www` files
  (subdirectories included). Literal routes win over the catch-all, so a `www/status` file would
  never be reached; `WwwFiles.Resolve` refuses anything outside `www` or of an unknown type.

## Client data areas

Read `README.md` -> "Client Data Areas" and `DEVELOPMENT.md` -> "Notes behind the code" before
touching that code. In short: the managed wrapper returns one 32-bit word per definition whatever
its type or size, a definition can hold only one field (`DUPLICATE_ID` otherwise), reads are served
from an `ON_SET` subscription cache, and a whole block is read as a range of 4-byte chunks with the
last one clamped to the end of the range.

One limitation to know before a page sends something over the streaming socket: it reads exactly one
message - the range spec - because `DetectDisconnect` keeps a receive outstanding for the rest of the
connection, so a later client message is never read (a second `ReceiveAsync` throws and is
swallowed) and the close handshake is never finished (clients see code 1006). `www/cdu.html` keys
the FMC through `POST /api/event/send` instead, which is why the page needs nothing from the socket
beyond frames; replacing that helper with a single long-lived receive the loop checks each tick is
the change that would make client-to-server messages possible.

Offsets are not part of this codebase: derive them from the installed `PMDG_NG3_SDK.h` with
`tools/pmdg-fields.html` (or `node tools/update-pmdg-fields.js <header>`) instead of computing them
by hand. A missed `unsigned short` shifts every following field, which is how the A/T arm switch was
once read from the wrong byte.

## The demo pages

They are the examples users copy from, so they have to keep working and stay readable.

- `www/737views.html` - the whole view set is its `VIEWS` table; a view index that is wrong is a
  one-line change there.
- `www/737nav.html` - the simvar and event names are in its `RADIOS` table; an aircraft that keeps
  its own radio state (PMDG, Fenix, iniBuilds) may need different names.
- `www/cdu.html` - the client data areas are in its `AREAS` table, the key table in `CDU_KEYS`.
  Three things in it are easy to get wrong:
  - The CDU font is not ASCII above `0x7F`: `0xB0` is the degree sign and `0xEA` the hollow box for a
    field with no value, which is why `GLYPHS` exists. Reading those codes as Latin-1 is what put
    `ê` on the screen where the aircraft shows a box. Unknown codes are drawn as a box and named in
    the page log.
  - One socket at a time: every handler checks that it still belongs to the current socket
    (`socket !== s`) and `closeSocket()` detaches the handlers before closing. Without that, a stale
    `onclose` schedules a reconnect which closes the *live* socket, and the page flickers between
    "live" and "disconnected" forever - the log panel is what showed it.
  - Keys go out as a standard `ROTOR_BRAKE` event with PMDG's action number as its value:
    `(SDK offset * 100) + 1` to press, `+4` to release (only CLR needs the release), the offset being
    the key's `EVT_CDU_L_*`/`EVT_CDU_R_*` value minus 69632. Measured dead ends: a client data write
    to `PMDG_NG3_Control` is ignored for CDU keys, and sending the SDK id as a SimConnect event is
    answered with `NAME_UNRECOGNIZED` (7) by name or `ERROR`/`UNRECOGNIZED_ID` (1/3) as a bare id.
    The `_R_` numbers are HubHop's: the F/O letters, digits and DEL do type into the F/O scratchpad
    on the NG3, while the F/O function keys (RTE, HOLD, MENU) do nothing - a PMDG-side gap, not a
    wrong offset.
