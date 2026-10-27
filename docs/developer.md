# Developer guide

Build, debug, test, extend and release RhinoRoad. For using the plugin see
[user-guide.md](user-guide.md); for how the kinematics are reasoned see [modelling.md](modelling.md);
for what has been validated see [validation.md](validation.md). Past reviews and plans are kept in
[history/](history/).

## Scope and architecture

RhinoRoad is a Rhino 8 vehicle-access screening plugin for architects and early-stage site design. It
is deliberately narrower than a general road-design or Civil 3D replacement: drive a complete vehicle
journey, rigid or articulated, check it against the site, and measure the space required through
consecutive bends and junctions.

Plan geometry is evaluated in World XY. A selected path retains its Z values for longitudinal grade
reporting (an interactive route can be draped onto a terrain), but crossfall, vertical clearance and
3D road surfaces are out of scope. Still out of scope: bicycles, persistent custom road objects,
cut/fill, kerbs, markings and highway intersection design, automatic route finding, automatic layout
resizing and simultaneous vehicle passing. Rear-steered vehicles are not represented (BUS 25 and the
force-steered SK variants are absent for that reason). No result is presented as certified AutoTURN
output.

Projects:

- `src/RhinoRoad.Core` — no Rhino dependency. SI-unit vehicle definitions, kinematic analysis,
  rate-limited trajectory generation, reversing and docking, planners, geometry (Clipper2), site-fit
  analysis, reports, schema migration, and the legacy Vej/QGIS reference-template loading.
- `src/RhinoRoad.Rhino` — the plugin (`.rhp`): Rhino curve sampling, Boolean swept envelopes, offsets,
  feasibility checks, interactive input, preview conduits, the edit palette and inspector, layers,
  grouping, metadata, the commands, the toolbar (`RhinoRoad.Rhino.rui`) and the `RRReferenceCertify` DWG
  harness. Rhino model units are converted at this adapter boundary.
- `src/RhinoRoad.Grasshopper` — Grasshopper components (`.gha`) over Core: `Vehicle`, `Analyse Path`,
  `Drive Manoeuvre`.
- `tests/RhinoRoad.Core.Tests` — deterministic geometry, vehicle, steering, grade, reverse, planner,
  reference-asset and stored-certification report tests.

Generated Rhino objects remain ordinary curves, points, hatches, dimensions and text. Two design
rules recur: **one authority for a click** (`ManoeuvreReplayService.PlanControl` serves the live
preview, the commit and the replay, and tests pin them sample for sample), and **deterministic
planners** (anything replayed from a saved journey consults no clock and no randomness, so it comes
out sample for sample the same on every machine).

**Numbers in analysis text are invariant.** A violation message, a region summary, a range label or
the join note can end up in the HTML report beside numbers the report formats itself, so Core formats
them with `FormattableString.Invariant` rather than the current culture. Interactive prompts and
dialog fields may follow the user's culture. Clipper output is turned into regions only through
`SweptRegionBuilder.Nest`, which reads the nesting from Clipper's `PolyTreeD` instead of guessing it
from where each hole starts.

Dimensions and driving-mode limits come from *Grundlag for udformning af trafikarealer* and are
stored with source, date, figure, version, validation status and notes. Existing AutoTURN `.veh`
payloads are not reverse-engineered.

## Build

Requirements:

- .NET SDK 8.0.100 or later (`global.json` rolls forward to the newest major installed);
- Rhino 8.19 or later using the .NET 8 runtime, to *run* the plugin;
- Windows for the current internal builds.

Rhino is not needed to build or test. `RhinoCommon` (and `Grasshopper`) come from NuGet, pinned to
8.19 — the earliest release whose package ships a real .NET target, and therefore the widest Rhino 8
audience the plugin can support. That pin also sets the minimum Rhino the Yak package installs on.
They are referenced with `ExcludeAssets="runtime"`: the host supplies `RhinoCommon.dll`, and shipping
a second copy beside the `.rhp` risks loading two of them into one process.

```powershell
dotnet build RhinoRoad.sln -c Release
dotnet test RhinoRoad.sln
```

The plugin is written to `src\RhinoRoad.Rhino\bin\Release\RhinoRoad.Rhino.rhp` and the Grasshopper
components to `src\RhinoRoad.Grasshopper\bin\Release\RhinoRoad.Grasshopper.gha`. In Rhino, open
`Options > Plug-ins > Install`, select the `.rhp`, and restart Rhino if requested. The adjacent
`RhinoRoad.Core.dll` and `Clipper2Lib.dll` must remain beside the `.rhp`; both are copied there by the
build. Warnings are errors (`TreatWarningsAsErrors`). The version is a single value in
`Directory.Build.props` (`RhinoRoadVersion`), consumed by the plugin, the Grasshopper assembly and the
Yak package.

For a quick visual check open `samples\RhinoRoad-vehicle-access.3dm`. To regenerate it from the
installed build, run `RRVehicleAccessSample` in an empty metric document and save the result. The file
`samples\user-vehicle-example.json` is a template for a user-defined vehicle.

### Debugging in Visual Studio

All projects are class libraries, so pressing F5 on the wrong one reports *"A project with an Output
Type of Class Library cannot be started directly."* Right-click **RhinoRoad.Rhino** and choose **Set as
Startup Project** — that choice lives in the per-user `.suo`, so each clone sets it once.

The plugin ships a launch profile that starts Rhino as the host:

- `Rhino 8` — normal session.
- `Rhino 8 (clean scheme)` — a separate `RhinoRoadDebug` settings scheme, so debugging cannot disturb
  your everyday Rhino toolbars, options or plugin list.

Both set `RHINO_PACKAGE_DIRS` to the build output, so Rhino discovers the freshly built `.rhp`
automatically — no need to install it by hand and no risk of debugging a stale copy. `/netcore` is
required: the plugin targets .NET 8, so Rhino must start on the .NET Core runtime. If Rhino is
installed somewhere other than `C:\Program Files\Rhino 8`, edit `executablePath` in
`src\RhinoRoad.Rhino\Properties\launchSettings.json`. That is the one machine-specific path in the
build configuration, and it affects debugging only — never the build.

## Live testing in Rhino 8

Use a disposable Rhino session for release checks. Build before starting Rhino: a running instance
holds the `.rhp` and adjacent `RhinoRoad.Core.dll` open, so a later build can fail or leave you testing
an older binary. The manual acceptance checklist is
[`tests/vehicle-access-rhino-checks.md`](../tests/vehicle-access-rhino-checks.md).

### Start a clean session

1. From the repository root, run `dotnet build RhinoRoad.sln -c Debug` and
   `dotnet test RhinoRoad.sln -c Debug`.
2. Start the Rhino-MCP live client from `<TopoTest checkout>/tools/rhino-live-client.py` in a shell that
   can launch Rhino. Send `{"tool":"spawn_slot","args":{"version":"8"}}` and record the returned slot
   ID, PID and `adopted` flag. Use this slot ID on every later call. Never use the user's existing
   Rhino window as a test session.
3. Use `run_csharp` to confirm the slot responds: `Console.WriteLine(__rhino_doc__.Objects.Count);`.
4. Load the exact Debug plugin at `src/RhinoRoad.Rhino/bin/Debug/RhinoRoad.Rhino.rhp` with
   `Rhino.PlugIns.PlugIn.LoadPlugIn`. Check the loaded plugin's assembly location. In a fresh slot,
   load its adjacent `RhinoRoad.Core.dll` with `Assembly.LoadFrom` first if Rhino would otherwise
   resolve an installed release copy. Do not infer the loaded version from the `LoadPlugIn` success
   result alone.

The TopoTest guide, `docs/rhino-live-testing.md` in that checkout, describes the router and its
fallback client. The path to TopoTest depends on each developer's checkout: `<TopoTest checkout>` is a
placeholder for wherever yours lives.

### Test the actual interaction

Open `RRRoad` in **interactive** mode so the Road modal appears. Select the vehicle, driving mode and
`Drive the vehicle interactively` in the modal; inspect the displayed vehicle and mode before clicking
**Continue**. `-RRRoad` exercises a different command-line settings path and does not verify the modal.

In the Rhino viewport and command prompt, complete each point or option and wait until the next prompt
appears before acting again. Rapid input can be processed after a delayed redraw and accidentally add
the same point twice. Read command history after each step, and use **Undo** if an extra control was
committed. Test the on-screen **Reverse**, **Direction**, **Undo**, **Speed** and **Finish** options, plus
the live sweep preview. Watch for orange steering, reach or articulation warnings before finishing.

For a repeatable SVT loading-bay check in a metric document:

| Step | UI action or point |
| --- | --- |
| Modal | SVT, driving mode B, interactive source, Forward |
| Place vehicle | Rear axle `0,0`; forward heading through `1,0` |
| Drive parallel | Pick `25,0` |
| Turn away | Pick `35,5` |
| Reverse | Select **Reverse** |
| Set bay direction | Select **Direction**, pick the trailer rear at `6.8,-4`, then point/type `180` degrees (the SVT trailer rear is 3.2 m behind its axle, so the stored axle is `10,-4`) |
| Complete | Inspect the reverse preview and press Enter to finish |

Coordinates may be typed into Rhino's active `GetPoint` prompt for a measured regression; also make at
least one physical viewport pick when testing pointer usability and snapping. If automating the
desktop, use the computer-use tool's native window actions for modal and pointer interaction. In the
current Sky-based setup, send coordinate characters with individual `press_key` calls after focusing
Rhino's prompt; `type_text` invokes Rhino Paste and is not a reliable coordinate entry method. The
desktop may contain unrelated windows, so check the Rhino window title and prompt before clicking.

### Check the saved result

After finishing, confirm Rhino reports objects created and that the viewport shows the baked path, body
sweep, clearance and wheel tracks. Inspect the source object's `RhinoRoad.AccessDefinition` user string
(or use `RRInspectRoad`) and verify the vehicle, mode and ordered controls. For the SVT example there
should be three controls: `(25,0)` Forward, `(35,5)` Forward, and `(10,-4)` Reverse with an exit heading
of 180 degrees. Core regression tests in `TrailerReverseTests` also check the trailer's final position,
squareness, articulation and exact replay. A successful bake alone does not establish that the trailer
reached the bay.

Close only the owned test slot with `close_slot`. If it times out, inspect the recorded PID and its
command line before stopping that exact process. Then quit the client, rebuild once if any source
changed during the test, and check `git status` for temporary files. Do not save test documents into
the repository unless they are intentional fixtures.

## Adding a vehicle

Vehicles are data in `src\RhinoRoad.Core\Data\vehicles.json` (embedded in Core). Every preset records
its `source` (title, date, figure, URL, notes) and a `validationStatus`; keep the notes honest about
what is transcribed, what is measured and what is assumed. The catalogue is shared by every command,
dialog, palette and Grasshopper component (`RhinoRoadCatalog.Current`), which is also where user
vehicles are merged in. Reversing, docking, planning and the area search are chosen by **capability**
(`ReverseCapabilities`, `ReverseDocking.SolverFor`), not by preset id, so a new vehicle gets whatever
its shape supports without a list being edited elsewhere.

The certification matrix is data too. `reference\certification\certification-plan.json` lists each
vehicle, the drawings that cover its driving modes, and the block the cases live in; angles are
listed once for the matrix. Vehicles differ in how many modes they publish — a trailer drawn for mode B
only has no mode-A case — so completeness is judged per vehicle against the modes its own plan entry
names.

To add one:

1. Add a preset to `vehicles.json`, with its `source` and `validationStatus: "SourceTranscribed"`. An
   articulated preset adds a `towedUnits` array (and optionally a `maximumArticulationDegrees` per
   unit; the default is 65°) and stops here: the steps below reconstruct a rigid body from wheel traces
   and do not cover one yet.
2. Add an entry to `certification-plan.json` with `"required": false`, naming each drawing and its
   block. Trial vehicles are measured without gating release.
3. Run `RRReferenceCertify`. It writes a report row per case and a fixture per extractable case.
4. Run `dotnet test`. The fixture tests pick the new cases up with no new test code.
5. Promote to `"required": true` once its cases pass.

Status may not run ahead of the evidence: the report tests prevent a preset's status from advancing
beyond what the stored certification supports.

### Fixtures

Each extractable case is dumped to `reference\certification\fixtures\<VEHICLE>_<MODE>_<ANGLE>.json`: the
reference envelope, the chosen wheel traces, and every candidate trace the extractor considered. They
let the pure reconstruction be replayed against real drawing geometry outside Rhino, in milliseconds.
`CertificationFixtureTests` keeps a ratchet list of the cases that reconstruct today; it must never
shrink. See [validation.md](validation.md).

## Tests

`dotnet test RhinoRoad.sln` runs the Core test project. It covers the multi-turn REN journey,
preview/final sweep agreement, retained-geometry invalidation, disconnected footprints, islands, section
intersections, body versus clearance conflicts, concave site boundaries, movement-fit wording, reverse,
Finish, editable control reconciliation, reversing and area search for every preset and mode, windowed
grade, schema migration, user-vehicle catalogue rules, wheel edges and mirrors, and the stored
certification report. There are no Rhino-side automated tests; the Rhino behaviour is checked by hand
with the [acceptance checklist](../tests/vehicle-access-rhino-checks.md).

## `Previous Attempts/`

`Previous Attempts/` holds the earlier QGIS/Eto prototypes (`VehicleTurn_Eto_v11.py`,
`VehicleTurn_VD_v12.py`). The folder name has a space and it is not archive-only: **the tests depend on
it**. `VehicleTurn_VD_v12.py` embeds the official Vejdirektoratet design-envelope templates, and
`tests\RhinoRoad.Core.Tests\RhinoRoad.Core.Tests.csproj` links it into the test output as
`Reference\VehicleTurn_VD_v12.py`, where `VejReferenceTemplateCatalog` parses it for
`VejReferenceTemplateCatalogTests` and `LegacyCurveAgreementTests`. Do not move, rename or edit that
file without updating the project link and re-running the tests. The other prototype is not used. (The
tidier layout — moving the parsed payload to `reference/legacy/` — is deliberately not done yet.)

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request on `windows-latest`: it sets up .NET from
`global.json`, then `dotnet build RhinoRoad.sln -c Release` and `dotnet test RhinoRoad.sln -c Release
--no-build`. The NuGet RhinoCommon reference is what lets it build without Rhino installed.

## Yak package

`build-yak-package.ps1` stages the plugin into a framework folder, writes a manifest from the single
version in `Directory.Build.props`, and runs `yak build` (Yak ships with Rhino; the default path is
`C:\Program Files\Rhino 8\System\Yak.exe`). It builds and tests in the chosen configuration first.
Staged: `RhinoRoad.Rhino.rhp`, `RhinoRoad.Core.dll`, `Clipper2Lib.dll`, the `.deps.json` and
`.runtimeconfig.json`, the `.rui` toolbar, the Grasshopper `.gha` with its runtime metadata, and the
`LICENSES` folder. `RhinoCommon.dll`, `Grasshopper.dll` and `GH_IO.dll` must never be staged and the
script throws if they are.

```powershell
./build-yak-package.ps1                                            # build only
./build-yak-package.ps1 -Push -Source https://test.yak.rhino3d.com/ # rehearse on the test server
./build-yak-package.ps1 -Push                                      # publish for real
```

Publishing is opt-in (`-Push`) because a push to the McNeel server is public and permanent: a version can
be yanked, but the package name is claimed for good. The version carries a pre-release suffix
(`0.5.0-beta`), so only people who ask for pre-releases receive it. The manifest text is written inside
the script; **it still describes eight presets, so update it** when the release adds SK, MVT, user
vehicles and the other new features, and keep its validation wording in step with
[validation.md](validation.md). Add a [CHANGELOG.md](../CHANGELOG.md) entry for every release.

## Machine-specific files

- `.codex/config.toml` is checked in and contains an **absolute path under one user's `%APPDATA%`** (the
  Rhino-MCP router executable of that machine). It is a local tool configuration, machine-specific, and
  will not work on another machine without editing. It is left in place in case it is someone's working
  configuration; whether it should be untracked is an open question for the repository owner.
- `src\RhinoRoad.Rhino\Properties\launchSettings.json` names the Rhino install path (see above).
- The live-testing section refers to a TopoTest checkout only through the `<TopoTest checkout>`
  placeholder; substitute your own.

## Open items

Tracked in [history/improvements.md](history/improvements.md): localisation of the Danish UI strings
(F12) and gathering the user-facing strings in one place (A5), a Git LFS migration for the ~23 MB of
DWG/ZIP/PDF reference data (D4; it rewrites history, so it needs an owner decision), the DWG axle-track
transcription that would let REN and BUS 12 pass certification (T5), and the mode-A 30° re-run (T6).
