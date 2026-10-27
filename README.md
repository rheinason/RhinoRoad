# RhinoRoad

RhinoRoad is an internal Rhino 8 plugin for architect-friendly vehicle access screening. Drive a
complete journey through a site, check it against selected boundaries and obstacles, and measure the
space it needs. Rigid and articulated vehicles are both supported: a towed unit is placed from where
the vehicle has been rather than from where it is, reversing steers the trailer, and every vehicle can
reverse square into a bay. Highway design, automatic route finding and automatic layout resizing are
outside the product scope.

Ten Danish type vehicles ship as presets (PV, REN, LV 12, BUS 12, BUS 13,7, BUS 15, SVT, PVT, SK and
MVT), and you can import your own. Each preset records how far it can be trusted: only PV is
reference-validated against the official drawings; the rest are source-transcribed, and user vehicles
are marked as such. Nothing is presented as certified AutoTURN output. See
[docs/validation.md](docs/validation.md).

## Commands

| Command | Action |
| --- | --- |
| `RRRoad` | Create a road access journey; configure a selected saved road. |
| `RREditRoad` | Open an edit session: drag the control points with a live sweep, and set driving intent. |
| `RRUpdateRoad` | Save the recalculated sweep, rerun checks, and refresh linked sizing results. |
| `RRInspectRoad` | Review the selected road's checks and measurements. |
| `RRMeasureRoad` | Dimension a section through the required clearance footprint. |
| `RRCombineRoads` | Combine the footprints of separate required movements. |
| `RRCompareRoads` | Compare the clearance footprints of two journeys. |
| `RRReportRoad` | Write an HTML report and per-station CSV for saved roads. |
| `RRImportVehicle` | Import a user-defined vehicle from a JSON file. |
| `RRVehicles` | List the vehicles, their validation status and any rejected user files. |

`-RRRoad` provides command-line settings for macros. The previous `RR…VehicleAccess` command names
still work as hidden compatibility commands; `RRReferenceCertify` and `RRVehicleAccessSample` are
hidden developer commands. A RhinoRoad toolbar ships with the plugin, and Grasshopper components
(`Vehicle`, `Analyse Path`, `Drive Manoeuvre`) sit beside it.

## Build and install

Requirements: .NET SDK 8.0.100 or later; Rhino 8.19 or later (on the .NET 8 runtime) to *run* the
plugin; Windows. Rhino is not needed to build or test.

```powershell
dotnet build RhinoRoad.sln -c Release
dotnet test RhinoRoad.sln
```

In Rhino, open `Options > Plug-ins > Install` and select
`src\RhinoRoad.Rhino\bin\Release\RhinoRoad.Rhino.rhp` (restart Rhino if asked). The adjacent
`RhinoRoad.Core.dll` and `Clipper2Lib.dll` must stay beside the `.rhp`. Debugging in Visual Studio,
live testing, the Yak package and CI are in [docs/developer.md](docs/developer.md). For a quick visual
check open `samples\RhinoRoad-vehicle-access.3dm`.

## Documentation

- [docs/user-guide.md](docs/user-guide.md) — commands, interactive driving, reversing into a bay,
  editing, the inspector, sizing, reports, outputs, your own vehicles.
- [docs/modelling.md](docs/modelling.md) — how a route gets its shape: rewind-ease, the standstill,
  articulation and the fold limit, reversing and docking, per-leg speed, mirrors and wheel-edge paths,
  terrain draping.
- [docs/validation.md](docs/validation.md) — what was checked against the official drawings, what
  passed, and what is assumed rather than published.
- [docs/developer.md](docs/developer.md) — build, debugging, live testing, adding a vehicle, fixtures,
  CI, Yak.
- [docs/path-model-notes.md](docs/path-model-notes.md) — the route models that were tried and rejected.
- [CHANGELOG.md](CHANGELOG.md) — what changed in each release, and what needs an Update.
- [tests/vehicle-access-rhino-checks.md](tests/vehicle-access-rhino-checks.md) — the manual Rhino
  acceptance checklist.
