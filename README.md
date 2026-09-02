# RhinoRoad

RhinoRoad is an internal Rhino 8 plugin for architect-friendly rigid-vehicle access screening.
It analyzes a rear-axle midpoint route, creates swept and clearance envelopes, checks preliminary
road geometry, and reports steering and grade feasibility.

## Build and install

Requirements:

- .NET SDK 8.0.100 or later (`global.json` rolls forward to the newest major installed);
- Rhino 8.19 or later using the .NET 8 runtime, to *run* the plugin;
- Windows for the current internal MVP.

Rhino is not needed to build or test. `RhinoCommon` comes from NuGet, pinned to 8.19 — the earliest
release whose package ships a real .NET target, and therefore the widest Rhino 8 audience the plugin
can support. That pin also sets the minimum Rhino the Yak package installs on. The solution builds on
a clean machine and in CI. It is referenced with
`ExcludeAssets="runtime"`: the host supplies `RhinoCommon.dll` at run time, and shipping a second
copy beside the `.rhp` risks loading two of them into one process.

Open `RhinoRoad.sln` in Visual Studio and build, or from a shell:

```powershell
dotnet build RhinoRoad.sln -c Release
dotnet test RhinoRoad.sln
```

The plugin is written to:

```text
src\RhinoRoad.Rhino\bin\Release\RhinoRoad.Rhino.rhp
```

In Rhino, open `Options > Plug-ins > Install`, select the `.rhp`, and restart Rhino if requested.
The adjacent `RhinoRoad.Core.dll` and `Clipper2Lib.dll` must remain beside the `.rhp`; both are
copied there by the build.

### Debugging in Visual Studio

All three projects are class libraries, so pressing F5 on the wrong one reports *"A project with an
Output Type of Class Library cannot be started directly."* Right-click **RhinoRoad.Rhino** and choose
**Set as Startup Project** — that choice lives in the per-user `.suo`, so each clone sets it once.

The plugin ships a launch profile that starts Rhino as the host:

- `Rhino 8` — normal session.
- `Rhino 8 (clean scheme)` — a separate `RhinoRoadDebug` settings scheme, so debugging cannot disturb
  your everyday Rhino toolbars, options, or plugin list.

Both set `RHINO_PACKAGE_DIRS` to the build output, so Rhino discovers the freshly built `.rhp`
automatically — there is no need to install it by hand, and no risk of debugging a stale copy that
was installed earlier. `/netcore` is required: the plugin targets .NET 8, so Rhino must start on the
.NET Core runtime rather than .NET Framework.

If Rhino is installed somewhere other than `C:\Program Files\Rhino 8`, edit `executablePath` in
`src\RhinoRoad.Rhino\Properties\launchSettings.json`. That is the one machine-specific path left in
the repository, and it affects debugging only — never the build.

For a quick visual check, open `samples\RhinoRoad-vehicle-access.3dm`. To regenerate it from the
installed build, run `RRVehicleAccessSample` in an empty metric document and save the result.

## Reference certification

Run `RRReferenceCertify` in an empty metric document to repeat the official-DWG comparison matrix.
It writes `reference\certification\reference-certification.json`; the checked-in report is covered by
the unit tests. The command refuses to run in a non-metric document, and builds its swept envelopes at
a fixed geometry tolerance rather than the document's, so verdicts do not depend on document setup.
The report records that context under `environment`.

The current run passes 6 of 18 required cases. **PV is `ReferenceValidated`** — all six of its
cases sit inside both tolerances, worst deviation 0.052 m against a 0.10 m limit. REN and BUS12
remain `SourceTranscribed`. Every failure records why:

| Cases | Cause |
| ----- | ----- |
| 4 | No pair of extracted wheel traces is consistent with the rigid-body chord |
| 3 | Red/yellow chains could not be joined into an envelope and wheel pair |
| 2 | Extracted reference envelope self-intersects |
| 2 | Measured, but outside tolerance |
| 1 | The DWG holds no extractable geometry for the annotated case |

Ten of the twelve failures are DWG extraction, not vehicle modelling: the route reconstruction
itself is exercised by unit tests and reproduces the official geometry wherever extraction succeeds.
A case only reports a deviation when the geometry behind it is sound, so a number in the report is
one the geometry can support.
See `reference-validation.md` for the measured ranges and the path to a trustworthy rerun.

## Adding a vehicle

The certification matrix is data. `reference\certification\certification-plan.json` lists each
vehicle, the drawings that cover its driving modes, and the block the cases live in; angles are
listed once for the matrix. Vehicles differ in how many modes they publish — a trailer drawn for mode B only has no mode A
case — so completeness is judged per vehicle against the modes its own plan entry names.

To add one:

1. Add a preset to `src\RhinoRoad.Core\Data\vehicles.json`, with its `source` and
   `validationStatus: "SourceTranscribed"`.
2. Add an entry to `certification-plan.json` with `"required": false`, naming each drawing and its
   block. Trial vehicles are measured without gating release.
3. Run `RRReferenceCertify`. It writes a report row per case and a fixture per extractable case.
4. Run `dotnet test`. The fixture tests pick the new cases up with no new test code.
5. Promote to `"required": true` once its cases pass.

### Fixtures

Each extractable case is dumped to `reference\certification\fixtures\<VEHICLE>_<MODE>_<ANGLE>.json`:
the reference envelope, the chosen wheel traces, and every candidate trace the extractor considered.

These exist because DWG extraction is the only per-vehicle-fragile stage, and it is the one stage
that cannot run outside Rhino. The fixtures move its output across that boundary, so the pure
reconstruction can be replayed against real drawing geometry in milliseconds, diffed in review, and
regression-fenced by name. `CertificationFixtureTests` keeps a ratchet list of the cases that
reconstruct today; it must never shrink.

## Use `RRVehicleAccess`

1. Select the rear-axle midpoint curve, if you already have one, and run `RRVehicleAccess`.
2. In the dialog, set vehicle, mode, path source, clearance, road-edge widths, and optional checks.
3. Press `Continue`.
4. For `ExistingCurve`, select the curve — skipped when exactly one curve was selected before the
   command started. Its parameter direction is the travel direction; choose `Reverse` when the
   vehicle faces opposite that direction.
5. For `Interactive`, pick the rear-axle start and vehicle heading, then click successive previewed
   legs. Use `Reverse`, `Undo`, and Enter to finish. Requested turns beyond wheel lock are clamped.
   The prompt shows how much lock is currently on, and a dotted ray marks the heading each leg
   would end on.

   Aiming at a point drives an arc that leaves the vehicle turned by **twice** the bearing you
   picked, so clicking on the line you want to end up travelling along overshoots it and has to be
   corrected back — which is what produces an unwanted S through the exit of a turn. To leave a turn
   the way a driver does, switch to `Straighten`: the wheel runs back to centre at the mode's
   lock-to-lock rate while the vehicle carries on, and the cursor sets how far to run it out. The
   vehicle keeps turning as the wheel centres, which is exactly the gradual exit a single arc cannot
   produce. Switch back with `Aim`.

   `Reverse` flips the travel direction for the next leg, so a three-point turn is drawn as forward
   legs, a reversing leg, then forward legs again — the cusp between them is where the vehicle stops
   and changes direction.
6. Obstacle curves and closed allowed-area boundaries are only asked for when their checks are
   ticked; both are off by default.
7. The PASS/FAIL report is written to the command line and the result is baked. Tick
   *Preview and confirm before baking* to stop at a transient viewport preview first.

A default run therefore asks for at most one thing after the dialog, and nothing at all when the
path was pre-selected. `-RRVehicleAccess` keeps the option prompt for scripting, with a `Preview`
toggle matching the dialog's.

## Selecting a drawn curve

A selected curve is taken as the rear-axle path directly, and the checks report where the vehicle
could not actually follow it. A **polyline therefore reports tangent discontinuities at its
corners**, which is the honest answer rather than a fault: no vehicle turns a right angle. For a
meaningful report, draw the centreline smooth — `InterpCrv`, or `Fillet` at a radius the vehicle can
hold — or drive it interactively instead, where every leg is feasible by construction.

The tool deliberately does not smooth the line for you. What happened when it tried, with the
measurements, is in [`path-model-notes.md`](path-model-notes.md).

### Editing a route

An interactive run bakes its driven path as an ordinary curve on `RhinoRoad::Paths`. Edit it with any
Rhino tool, select it, and run `RRVehicleAccess` again: it is analysed as a selected curve, and
because it carries the id this run's output was tagged with, the re-run replaces the previous output
rather than stacking another set beside it.

Generated objects are grouped and placed below `RhinoRoad` layers. Metadata records the source
GUID, analysis ID, vehicle/version, validation status, mode, clearance, and fixed widths. Re-running
with `ReplaceExisting=Yes` replaces only RhinoRoad objects linked to the same existing source curve.

## Meaning of the outputs

- **Driven rear-axle path:** what an interactive run drove, kept editable and re-runnable.
- **Rear/front axle tracks:** kinematic reference curves.
- **Vehicle footprints:** the body outline stamped along the route at the `FootprintInterval`
  station spacing; set the interval to 0 to omit them.
- **Body swept envelope:** theoretical occupied area from the sampled rigid-body poses, built as a
  polygon union. Where a manoeuvre encircles ground it does not cover — a roundabout island, say —
  the envelope carries that as a hole rather than reporting the island as occupied.
- **Clearance envelope / minimum access footprint:** body envelope plus the configured allowance;
  the Vejregler default is 0.30 m.
- **Fixed-width road edges:** asymmetric offsets from the route. A failure means the clearance
  envelope does not fit between them.
- **Warning points:** steering, steering-rate, tangent, grade, obstacle, boundary, or road-fit issues.

These outputs are screening geometry, not construction-ready kerb design or certified AutoTURN
results. See `plan.md` and `reference-validation.md` for scope and validation status.
