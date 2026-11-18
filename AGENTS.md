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

## Verify behaviour, not just the diff

Build after any edit, and exercise the API once (`curl` or a test page). Both bugs found in the
client data work looked fine in `git show` and only showed up when the code ran.

## Line endings - read before committing

The repository stores LF blobs while `core.autocrlf=true` writes CRLF into the working tree.
Committing *part of* a file whose working copy has CRLF can land the hunks in the wrong place:
we once committed four files with their added lines hoisted above the `namespace`, which did not
compile, and it was only caught by building afterwards. To avoid it:

- Keep the files you edit LF in the working tree. Convert with `sd '\r$' '' <file>`
  (`sed -i` is not reliable here - it failed mid-write and removed files once).
- After committing, check that the commit matches the tree:
  `git show HEAD:<file> | cmp - <file>` for every committed path, then build.
- Repo-wide alternative, not applied: a `.gitattributes` with `* text=auto eol=lf`, which makes
  checkouts LF everywhere and removes the mismatch.

## Client data areas

Read `README.md` -> "Client Data Areas" before touching that code. In short: the managed
SimConnect wrapper returns one 32-bit word per definition whatever its type or size, a
definition can hold only one field (`DUPLICATE_ID` otherwise), and reads are served from an
`ON_SET` subscription cache.

Offsets are not part of this codebase: derive them from the installed `PMDG_NG3_SDK.h` with
`tools/pmdg-fields.html` (or `node tools/update-pmdg-fields.js <header>`) instead of computing
them by hand. A missed `unsigned short` shifts every following field, which is how the A/T arm
switch was once read from the wrong byte.
