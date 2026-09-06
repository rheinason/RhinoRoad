# RhinoRoad

RhinoRoad is an internal Rhino 8 plugin for architect-friendly rigid-vehicle access screening.
Drive a complete journey through a site, check it against selected boundaries and obstacles, and
measure the space it needs. Consecutive bends retain the vehicle's steering state. Highway design,
automatic route finding, and automatic layout resizing are outside the current product scope.

## Commands

| Command | Action |
| --- | --- |
| `RRRoad` | Create a road access journey; configure a selected saved road. |
| `RREditRoad` | Open an edit session: drag the control points with a live sweep, and set driving intent. |
| `RRUpdateRoad` | Save the recalculated sweep, rerun checks, and refresh linked sizing results. |
| `RRInspectRoad` | Review the selected road's checks and measurements. |
| `RRMeasureRoad` | Dimension a section through the required clearance footprint. |
| `RRCombineRoads` | Combine the footprints of separate required movements. |

`-RRRoad` provides command-line settings for macros. The previous `RR…VehicleAccess` command names still work
as hidden compatibility commands. `RRReferenceCertify` and `RRVehicleAccessSample` are also hidden
from autocomplete; developers can invoke them by typing their full names.

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

## Reference certification (developers)

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

## Use `RRRoad`

1. Run `RRRoad` to drive a journey through the site.
2. Choose vehicle, mode, clearance, and optional site constraints. Interactive driving and a
   clearance footprint are the defaults. Fixed-width corridors and output options are under
   **Advanced geometry and output**. Existing-curve analysis still treats the selected curve as
   the rear-axle path, not as an approximate instruction for a driver.
3. Press `Continue`.
4. For `ExistingCurve`, select the curve — skipped when exactly one curve was selected before the
   command started. Its parameter direction is the travel direction; choose `Reverse` when the
   vehicle faces opposite that direction.
5. For `Interactive`, pick the rear-axle start and vehicle heading, then click successive previewed
   legs. Use `Reverse`, `Undo`, and Enter to finish. Requested turns beyond wheel lock are clamped.
   The prompt shows how much lock is currently on, and a dotted ray marks the heading each leg
   would end on.

   While aiming, blue outlines show the body sweep and green outlines show the clearance footprint
   of the complete proposed journey. The retained body boundary is darker; the discarded tail is
   dotted. A steering-limit message means the driven leg cannot follow the requested aim exactly.
   Preview uses the same polygon union and clearance offset as final output, caching retained
   geometry. Its extra boundary simplification is regression-tested to stay within 4 mm of the
   final sweep in the multi-turn REN scenario.

   Each click does two jobs. It says where to go next, and **the direction towards it says which
   way the vehicle should be travelling when it leaves the corner it is in**. That is what lets the
   exit be driven properly instead of corrected afterwards.

   The reason it matters: an arc through a picked point ends the vehicle turned by *twice* the
   bearing of that point, so on its own every corner overshoots and the next leg has to correct back
   — the unwanted S through a corner exit. But a driver does not correct at the exit; they ease the
   wheel open through the second half of the bend. Easing has to *begin* before the point you want
   to be straight at, and by then the leg has already been driven past it. So RhinoRoad rewinds: it
   gives back the tail of the corner, restarts the ease from where the wheel should have begun
   coming back, and drives the exit as one continuous opening turn. The part being given back is
   drawn as a faint dotted line while you aim, so the reshaping is visible as it happens.

   Rewinding never reaches back past the start of the leg in hand, or through a change of travel
   direction, so an earlier decision cannot be undone behind your back. It also only happens when
   the next point genuinely asks for less turn than the wheel is holding: halfway round a bend the
   right answer is to keep turning, not to straighten and turn back in.

   Aiming works on points **ahead of the vehicle's beam**. A point behind the beam is brought round
   to the beam — same distance, the hardest turn aiming can ask for — and the command says so, because
   the arc that would really reach such a point is a loop the size of whatever circle passes through
   the cursor: a REN in mode B would drive 10.7 km to reach a point 60 m astern. Reversing is what
   puts you there without noticing, since it turns "behind the direction of travel" into the ground in
   front of the nose. To turn further than the beam allows, hold `Ctrl`; to go backwards, use
   `Reverse`.

   Clicking legs is for authoring a manoeuvre where no line exists yet. To check a road that has
   already been designed, select its centreline and use `ExistingCurve` — the checks then report
   against the line as drawn instead of against a route improvised towards it.

   `Finish` ends the route instead of continuing it: pick the direction to end up travelling in, and
   the vehicle eases onto exactly that heading with the wheel centred. Switch back with `Aim`.

   `Reverse` flips the travel direction for the next leg, so a three-point turn is drawn as forward
   legs, a reversing leg, then forward legs again — the cusp between them is where the vehicle stops
   and changes direction.

   **Hold `Ctrl`, or use the `Turn` option, to turn at full lock.** Ordinary aiming will turn the
   vehicle right round — click near abeam and it makes a U-turn — but only on whatever radius the
   click implies: a PV in mode A clicked abeam at 40 m turns 175 degrees on a 20 m radius, against a
   minimum of 4.82 m. The arc through a nearer point has to reach full lock out of its own length,
   and at minimum radius there is not enough of it. Aiming can turn the vehicle round; only lock can
   turn it round *tightly*. A locked turn asks for an *amount of heading* instead. The wheel goes to its lock and stays there
   until the direction of travel has swung by twice the bearing of the cursor, so clicking abeam is
   a true 180-degree U-turn, and clicking behind the vehicle asks for 270. How far away you click
   makes no difference. The two dotted circles drawn while you aim are the tightest the rear axle
   can trace, left and right.

   Type a number instead of clicking to sweep exactly that many degrees — `180` for the turning
   check — and it goes the way the cursor last pointed. The leg ends with the wheel still at lock,
   because straightening is the next control's job: the control after it opens the turn out from
   inside, the same way it would any other corner.

   **Hold `Shift` to square the leg up.** Rhino's own ortho is switched off inside this command, on
   purpose: ortho constrains the direction to the picked point, and the vehicle leaves on *twice*
   that bearing, so an ortho band pointing north would leave the vehicle heading east. Shift here
   constrains where the leg **ends up pointing** instead, to a multiple of your ortho angle measured
   from the construction plane's X axis — so a site drawn on a rotated CPlane squares up to the site.
   It works the same held with `Ctrl`, and it composes: every leg leaves on an axis, so a route drawn
   with Shift held stays on the axes it started on however many corners it takes. Object snap still
   applies and still sets how far away you clicked; Shift only takes the direction.

   A squared leg *arrives* on its axis rather than merely aiming at it. The direction is stored on the
   control, so the leg eases onto it and then runs straight to the click, landing within a thousandth
   of a degree. It takes however much room the turn actually needs, which may be more than you clicked
   for: a BUS 12 in mode A squared to a 90 degree departure needs about 30 m, and simply aiming at the
   same snapped point would have reached only 55 degrees of it in 15 m. Locked turns arrive exactly
   too, because they drive their sweep rather than aiming at it.

   A locked turn either side of a `Reverse` is what a three-point turn is made of. Note that
   shuffling only saves width in the slow mode: in mode A the wheel needs 12.5 m of travel to reach
   lock and no leg of a shuffle is that long, so the vehicle never gets near its minimum radius and
   the manoeuvre ends up wider than simply turning round.
6. Obstacle curves and closed allowed-area boundaries are only asked for when their checks are
   ticked; both are off by default.
7. The movement-fit report is written to the command line and the result is baked. With no site
   references selected, it says **Sweep generated — site fit not checked**. A successful check
   applies to the tested movement and selected constraints; a failed movement does not establish
   that access is impossible. Tick
   *Preview and confirm before baking* to stop at a transient viewport preview first.

A default run therefore asks for at most one thing after the dialog, and nothing at all when the
path was pre-selected. `-Road` keeps the option prompt for scripting, with a `Preview`
toggle matching the dialog's.

## Selecting a drawn curve

A selected curve is taken as the rear-axle path directly, and the checks report where the vehicle
could not actually follow it. A **polyline therefore reports tangent discontinuities at its
corners**, which is the honest answer rather than a fault: no vehicle turns a right angle. For a
meaningful report, draw the centreline smooth — `InterpCrv`, or `Fillet` at a radius the vehicle can
hold — or drive it interactively instead, where every leg is feasible by construction.

The tool deliberately does not smooth the line for you. What happened when it tried, with the
measurements, is in [`path-model-notes.md`](path-model-notes.md).

### Transitions

An alignment drawn as arcs joined straight onto tangents asks the steering wheel to change angle
instantly at every join, which no vehicle can do. Rather than report that as an anonymous violation
at each join, a selected curve is broken into its constant-curvature stretches and each is priced:

```
    transition length = speed x |atan(wheelbase / R2) - atan(wheelbase / R1)| / slew rate
```

Every stretch must be long enough for the transitions at both its ends, because the wheel has to
arrive at that curvature and then leave it. A short tangent between two bends is the usual place
this fails, since it has to hold the run-out of one and the run-in of the next.

The report names the stretches that are too short, with how much length they have and how much they
need. The three ways out are to lengthen the stretch, ease the radius either side of it, or use a
slower driving mode — the wheel moves at the same rate either way, but a slower vehicle covers less
ground while it does.

The numbers are small in practice. A 25 m radius needs 2.6 m of transition for PV, 4.4 m for REN and
5.7 m for BUS 12 in mode A, and under 1.3 m for any of them in mode B.

### Editing a route

An interactive run keeps the sparse clicks as an ordinary polyline on `RhinoRoad::Controls`. Run
`RREditRoad`, or press **Edit road** in the inspector, to open an edit session on it. The session
turns the control points on, numbers them in the viewport, and opens the modeless **Edit Road**
palette. Drag the points to see a live replay of the path, body sweep, and clearance footprint. The
affected path is teal; the previous path is dotted grey. The preview includes any reshaping of the
preceding bend and warns when the steering limit prevents an aim from being followed exactly.

The live preview belongs to that session and to nothing else. Moving a stored control line outside
an edit session changes no display: the baked result stays on screen and the inspector reports the
road as out of date, as it does for any other edit.

Replay runs off the drawing thread. While it catches up with a moving cursor, the previous preview
is faded and labelled as the previous position. Esc cancels the native point move; no preview
changes are written to the drawing. **Save** in the palette keeps the edit, reruns the site checks,
and refreshes any linked sizing results; **Discard** — and closing the palette — restores the
control line as it was. This live feedback applies to saved click-to-drive controls; existing-curve
analysis still uses its original curve and the explicit `RRUpdateRoad` workflow.

Select the control line (or any generated part of the analysis) and run `RRUpdateRoad` to
save the result and rerun the site checks. Live editing previews geometry; the inspector's previous
check results remain saved results until Update. Update replays the same rewind-aware driving logic
with the saved vehicle, road, clearance, footprint, grade, obstacle, and boundary settings; it does
not reopen the setup dialog. The control line keeps its object id and sits outside the generated
output group.

Run `RRRoad` against a saved source when you do want to change its setup. The dialog is
preloaded from the source and the accepted settings are used for the rerun. Choose **Reselect
obstacle and boundary curves** when the checks should use a different reference set. Existing
user-supplied curves use the same workflow and remain the editable source geometry.

Generated objects are grouped and placed below `RhinoRoad` layers. The source stores a versioned
`RhinoRoad.AccessDefinition`; every related object carries the same stable source id and a role.
Update creates and validates a complete replacement before removing the previous generated set. If
a saved obstacle or boundary has been deleted, Update stops, identifies the missing object, and
leaves the previous result in place so it can be repaired through Configure.

### Inspecting an analysis

Select any source, control, track, envelope, footprint, or warning marker and run
`RRInspectRoad`. One modeless inspector is kept per document. It shows the overall status,
checks, measurements, grouped problem station ranges, and profiles for steering angle, steering
rate, clearance/road margin, and grade. The profile sits at the top of the panel: hovering it marks
that station on the road itself — a point on the route, a leader, and the station and reading in a
label — draws the vehicle pose there, and prints the same reading under the graph. Click a failed
check or problem area to zoom to it. Display toggles control temporary viewport
overlays without changing the baked drawing. The control-intent overlay labels the initial travel
direction, reversals, and Finish control so the non-positional intent is visible while reviewing.

Editing the source or a referenced road/obstacle marks the inspector **Out of date**. Press Update
when ready; nothing recomputes or rebakes automatically.

### Editing driving intent

The **Edit Road** palette opened by `RREditRoad` carries the driving intent alongside the point
editing, so both are changed in one session. Set the starting heading, the starting travel
direction, or an individual control's direction and Aim/Turn/Finish intent; the rows are numbered to
match the dots in the viewport, and hovering a row highlights its control. Finish is available only
on the last control; Turn is available anywhere, and re-reads its point as a full-lock sweep. Changing the starting direction preserves the existing reversal pattern;
changing a single control changes that leg. Intent changes take effect in the preview immediately
and are kept, with the point positions, by **Save**.

Scripted `-RREditRoad` cannot raise a palette. It turns the control points on and leaves
`RRUpdateRoad` to commit, which is the behaviour macros already relied on.

### Measuring bends and junctions

- `RRMeasureRoad`: select one or more saved journeys (or a combined footprint), then pick
  a straight section across their clearance footprint. Both endpoints must be beyond the occupied
  area. Each occupied interval gets an ordinary Rhino linear dimension using the document's current
  dimension style. Gaps and islands remain unmeasured. Sections are evaluated in World XY.
- `RRCombineRoads`: select at least two saved journeys to produce their combined clearance
  footprint, including disconnected areas and holes. Use this for the required movements through a
  T or crossroads. It represents separate journeys, not simultaneous passing. Each journey retains
  its own fit result and vehicle validation status.
- Select any part of a sizing result and run `RRInspectRoad` to read its status and individual
  movement results in the command history. `RRUpdateRoad` refreshes the selected sizing result.

Sizing objects live on `RhinoRoad::Sizing` and retain stable journey IDs and section geometry.
Changed or missing dependencies mark their names **Out of date** and colour them orange. Updating a
journey also refreshes its linked sizing results when all required journeys are current. If another
journey is stale, update that journey first. A failed update retains the previous sizing output.
Section endpoints can be grip-edited and refreshed through Update. Saved definitions persist in the
Rhino document; existing journey definitions keep their original schema.

The footprint and dimensions describe the space required by the tested journeys. They are not a
search for the globally smallest possible bend or junction and do not propose kerb geometry.

## Meaning of the outputs

- **Vehicle access controls:** the sparse editable start/Aim/Finish intent for an interactive run.
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
