# RhinoRoad

RhinoRoad is an internal Rhino 8 plugin for architect-friendly vehicle access screening.
Drive a complete journey through a site, check it against selected boundaries and obstacles, and
measure the space it needs. Consecutive bends retain the vehicle's steering state. Highway design,
automatic route finding, and automatic layout resizing are outside the current product scope.

Rigid and articulated presets are both supported. A towed unit is placed from where the vehicle has
been rather than from where it is, so a trailer stays folded into the straight that follows a corner
and runs away rather than settling when the combination reverses — see
[Articulated vehicles](#articulated-vehicles).

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

## Vehicle presets

Eight of the Danish type vehicles ship in `vehicles.json`: **PV**, **REN**, **LV 12**, **BUS 12**,
**BUS 13,7**, **BUS 15**, **PVT** and **SVT**. BUS 13,7 and BUS 15 carry a *medløbsaksel* — a
self-steering trailing axle, which the published curves assume — so it carries no side force and the
drive axle is the effective rear-axle reference, with the distance to the trailing axle folded into
the rear overhang. The legacy curve library confirms that choice.

Five type vehicles are deliberately absent:

| Vehicle | Why not |
| ------- | ------- |
| AUT (autocamper) | The figure dimensions only overall length and width — no wheelbase or overhangs — and AUT has no row in Figure 6.7, so there is no mode-B wheel angle either. |
| BUS 25 (flerleddet bus) | No row in Figure 6.7, and the source says the rear-axle steering is manufacturer-specific and computer-controlled, and "særligt bagakslens bevægelser kan være vanskelige at forudsige". |
| T (traktor med anhænger og kost) | The drawbar hitch position is not dimensioned. |
| MVT (modulvogntog) | Three units and two joints, which the articulation chain handles, but the unit split and the two hitch offsets need more source work than the figure gives directly. |
| SK (specialkøretøj) | Figure 6.7 publishes three alternative locks rather than one, and the 30 m variant has force-steered axles the model does not represent. |

MVT and SK are the two worth doing next: both are ordinary articulation chains, and SK is
structurally identical to SVT (kingpin 0.6 m ahead of the drive axle, 14.0 m trailer wheelbase).

## Articulated vehicles

`SVT` (sættevogntog, 16,5 m) and `PVT` (påhængsvogntog, 18,75 m) are modelled as a lead unit towing a
chain of units, each with a hitch offset from its tower's axle and its own wheelbase behind that
hitch. A positive offset is a fifth wheel ahead of the drive axle; a negative one is a drawbar eye
hanging behind it, and the two behave differently enough that the sign is part of the preset.

SVT is one joint. PVT is two: its published plan view runs the drawbar from a coupling 0,15 m inside
the lorry's rear face to the trailer's *front* axle, and that axle is the turntable the trailer body
pivots on — so the drawbar and the body swing separately. The drawbar carries no body outline and
sweeps nothing.

Each unit's heading is integrated along the route rather than derived from the current sample, which
is what makes the history matter. Everything downstream reads
`VehiclePose.OccupiedOutlinesWorldMetres` rather than the lead body alone, so envelopes, clearance,
containment and footprint stamps all cover the trailer.

Two things this does not yet do:

- **No per-vehicle fold limit.** The source publishes none, and it cannot be recovered from the
  presets either: a semitrailer's body legitimately overlaps its tractor's in plan, because it sits
  on top of it, so the angle at which they would really collide is not a plan-geometry question. The
  maximum fold at each joint is therefore reported — in the command summary and on
  `VehicleAccessResult` — rather than judged against a number. A fold past a right angle *is*
  rejected, as `ArticulationAngle`: at that point the towed unit is no longer following the one
  towing it at all, so the footprints past it are of a configuration no driver reaches. That is a
  floor, not a threshold — real jackknife happens earlier, typically past 60° — so clearing it does
  not mean the manoeuvre is drivable.

- **Only partial corroboration.** The official-DWG matrix reconstructs a *rigid* body from paired
  wheel traces and has no articulated case, so both presets stay `SourceTranscribed` and are absent
  from `certification-plan.json` rather than failing in it. What they are checked against instead is
  the legacy curve library — see [Checking presets against the legacy curve library](#checking-presets-against-the-legacy-curve-library).
  PVT's lorry matches it to 0.036 m; neither trailer is corroborated, because neither combination
  ever reaches a steady turn on those sheets.

At full lock a combination may have no steady state at all: the tractor holds the lock but the
trailer folds without ever settling. `TurningGeometryCalculator` reports that as
`SteadyStateAttainable = false` alongside the smallest rear-axle radius the combination can hold, and
the dialog says so instead of quoting the width of a jackknife.

**Reversing steers the trailer, not the tractor.** A reversing combination is unstable by nature:
the fold grows rather than settles, with an e-folding distance of about one trailer wheelbase. Aimed
the way a forward leg is aimed — tractor's rear axle at the point you click — SVT passed a right
angle after 13 m on a 15 m radius and PVT in half that, which is what a driver would manage if they
never countersteered.

So reversing an articulated vehicle, the point you click means **where the trailer should end up**,
and `TrailerReverseGenerator` drives the tractor to put it there. The structure follows from one
observation: for a unit hitched at its tower's axle, its curvature is `tan(fold) / wheelbase` — the
fold *is* its steering angle, so the chain is a stack of bicycles each steered by the joint ahead of
it. Pure pursuit on the trailer gives a curvature, that curvature asks for a fold, driving the fold
asks for a curvature of the unit towing it, and the question is put again one joint further in until
it comes out as a wheel angle and goes through the ordinary rate-limited step. Lock, slew rate and
the kinematics stay in one place.

Measured over eight targets from 18 to 40 m back and up to 14 m across, SVT reaches all eight within
0.1 m and never folds past 36°. PVT — whose body hangs two joints out — reaches the moderate ones
and stops short on shifts steeper than about 0.35 across per metre back, missing by around half a
metre.

A reverse the combination cannot make **stops where it stops being drivable** rather than folding on
through it, which is what a driver does: stop, pull forward, start the reverse again on a better
line. The leg you keep is the drivable part, and the viewport says the trailer could not be put
there from here.

**Docking: set an exit direction and the trailer arrives square to it.** Position and heading cannot
both be had by aiming at a point — pursuit closes on a point from whatever direction it happens to
approach from, which is how a trailer ends up across the face of a dock. So when a reversing leg
carries an exit direction, what gets followed is the **approach line** — the line through the point
along that direction — and the point becomes the place along it to stop. Two errors describe the
trailer against that line, how far off it sits and how far off it points; running the arc length
backwards flips the sign of both, which is why the forward law diverges here and the signs are
derived rather than reused.

Cross-track error commands an approach *angle* rather than a curvature directly. Commanding
curvature is only valid near the line — eight metres off it asks for a radius no combination can
hold — and the bounded form is what lets the approach start from well off the line.

Squaring up costs distance: roughly the first fifteen metres go on establishing the fold and the last
on taking it out again. With about 50 m of run-in or more, SVT arrives within 0.25 m and under a
degree off square. Given less, it gets as close as it can — within about 0.6 m and 3° — and reports
that it did not square rather than claiming a dock it did not make.

**Docking is a single-joint capability**, enforced by `TrailerReverseGenerator.CanDock`. A two-joint
chain does not hold: the heading loop has to be slower than the folds it commands, and the folds
cannot be driven fast enough to leave room for it, so every pairing of the two gains measured on PVT
either crawled, converged and then wandered off, or ran into the fold stop. It is the second joint
that does it rather than the drawbar it hangs from — rebuilding PVT with its dolly on a fifth wheel,
and again with the hitch on the axle, changed nothing. Aiming at a point is stable for both, so a
chain that cannot be docked is aimed instead and the viewport says the heading was not honoured.

One limit remains: a locked turn in reverse is still open-loop, so it will fold. It is flagged
rather than prevented.

## Turning the wheel at a standstill

The steering rate is a rate per second and the leg generators spend it per metre, so moving the wheel
always costs distance — **12.5 m from centre to full lock in mode A and 4.17 m in mode B**, the same
for every preset, because the rate and the lock scale together. Standing still, that cost is zero.

**A change of direction is a standstill and is treated as one automatically**, since the vehicle has
to stop to make it. That single change is what makes three-point turns work: each leg of a shuffle
now begins at the lock it was always meant to be driven on, instead of spending its whole length
getting there. Every preset now needs less width to shuffle round than to turn round, where before
only the heavy ones in mode B did:

| | U-turn | Shuffle | Saved |
| --- | --- | --- | --- |
| PV, mode B | 7.76 m | 5.87 m | 1.89 m |
| REN, mode A | 16.87 m | 12.85 m | 4.01 m |
| BUS 12, mode A | 21.66 m | 16.40 m | 5.26 m |
| BUS 15, mode A | 26.07 m | 19.68 m | 6.39 m |

Away from a direction change the stop has to be asked for — the **Stop** option arms the next click,
and it is spent on that click rather than latched, because a stop happens once. Inventing stops that
did not happen shrinks the swept envelope, and an envelope smaller than the vehicle really needs is
the unsafe direction to be wrong in, so it is never assumed.

A sample that begins from a standstill records the fact, and the steering-rate check skips it. It
has to: a wheel turned while stopped covers no distance, and measuring that movement against the
distance to the next sample reads as an infinite steering rate.

**This is not a third køremåde.** The source publishes two, and the only wording it has for tight
turning areas sits inside køremåde B — *"på vendepladser fremføres det dimensionsgivende køretøj med
meget lav hastighed og i visse tilfælde ved bakkemanøvrer"*. A mode C would mean inventing a speed, a
lock and a clearance for every preset with nothing to check them against, sitting in the same table
as two modes that *are* corroborated against the official curves. Turning the wheel at a standstill
is a technique, not a vehicle property: you do it while driving køremåde B.

## Saying exactly which way to leave

**Shift** squares the exit onto an ortho step from the construction plane, which is what most
junctions want. Some are not on a step — a skewed arm, a bay set at whatever angle the building is —
and those need the direction said exactly.

The **Direction** option arms the next click, and the pick then has two stages: the click places the
point, and the pick continues with that point fixed while the cursor **swings the direction about
it**. It is swung about the point just placed rather than pointed from the vehicle, because the exit
direction is a property of where the leg *ends*.

The whole leg is replanned and drawn on every move of that swing — swept band, path, and the vehicle
ghosted at its finish with its trailer — so the direction is chosen against what it actually does
rather than in the abstract and previewed afterwards. Shift still squares it while swinging, an angle
can be typed instead (measured from the construction plane), and Escape abandons the click rather
than committing a leg whose direction was never settled.
## Checking presets against the legacy curve library

`VejReferenceTemplateCatalog` parses the official Vejdirektoratet design envelopes embedded in the
legacy plugin — every preset, both modes, 40–180 gon, no Rhino needed. `LegacyCurveCheck` and
`LegacyCurveAgreementTests` use them to check the presets.

**What is compared, and why it is not the whole envelope.** Reproducing a whole sheet needs the
manoeuvre it was drawn to — where the wheel starts moving and how fast — and that convention is not
published. Reconstructing it as "straight in, full lock, straight out" does not work: measured
against PV, the one preset independently validated against the official DWGs to 0.052 m, that
reconstruction disagrees by up to 1.2 m at 100 gon and 7 m at 180 gon. The disagreement is the
assumed manoeuvre, not the preset. A comparison built on it would report every preset as broken, so
there isn't one.

What is compared is the **steady-turn annulus**. Where the vehicle is turning steadily the radius is
fixed by wheelbase and wheel angle alone, whatever route led into it, so it can be predicted from the
preset and measured off the sheet with nothing assumed in between. Two gates keep it honest: the
sheet must pass `LegacyCurveCheck.Inspect`, and both its boundaries must resolve to arcs about a
**common centre** — which is what distinguishes the sustained arc from a transition that merely looks
circular over a short run.

**The library is partly corrupt.** 19 of its 121 sheets are structurally unusable and are pinned by
name in `TheBrokenSheetsInTheLegacyLibraryAreTheOnesWeKnowAbout`, so a comparison can never quietly
run over one:

| Sheets | Defect |
| ------ | ------ |
| all 8 of LV 12's mode-A sheets | three to five boundary chains instead of two |
| 7 of BUS 12's 8 mode-A sheets | a stray third boundary chain |
| REN A 180, REN B 120 | a stray third boundary chain |
| SVT B 140, SVT B 160 | a boundary that crosses itself |

Eight PV mode-B sheets store a vertex twice. That does not change the curve, so it is recorded as a
blemish rather than treated as a defect.

**What passes.** Every mode-B sheet that clears both gates agrees with its preset's own full-lock
geometry, inside the 0.10 m tolerance the DWG certification uses. The measured quantity is the
rear-axle radius, because both boundaries derive from it:

| Case | Rear-axle radius | Outer radius |
| ---- | ---------------- | ------------ |
| REN 140/160/180 gon | 0.031–0.071 m | 0.024–0.055 m |
| LV 12 120/140/160 gon | 0.001–0.082 m | 0.001–0.062 m |
| BUS 12 160 gon | 0.042 m | 0.027 m |
| BUS 13,7 140/160/180 gon | 0.003–0.021 m | 0.002–0.015 m |
| PVT 140/160/180 gon (lorry only) | — | 0.036 m |

BUS 15 produces no steady mode-B turn, so it is corroborated through its mode-A sheets only.

Two findings came out of this and are pinned as tests rather than left as prose:

- **The mode-A sheets for large vehicles are drawn at 30.0°, not 29.7°.** Converting the published
  33 gon exactly gives 29.7°, which is what every preset holds; but REN, BUS 13,7 and BUS 15 mode-A
  sheets all imply 29.9–30.1°, and the source says 30° itself in the caption to Figure 6.1
  ("hjuldrejning på 30 grader (svarende til 33 gon)"). The 0.3° costs about 0.15 m on the inner
  radius. It is **not applied**, because mode A is shared with PV, whose `ReferenceValidated` status
  rests on a stored certification run computed at 29.7°; changing it needs a Rhino re-run so the
  report and the preset stay in step. See `ModeASheetsForLargeVehiclesAreDrawnAtThirtyDegrees`.
- **PV's mode-A sheets are not drawn at a steering lock at all.** They imply about 18°. The source
  runs personbiler through junctions at 20 km/h against 15 for large vehicles, and at that speed the
  curve is set by comfort, not by the lock. See `PvModeASheetsAreNotDrawnAtTheSteeringLock`.

This is also what settled SVT's and PVT's mode-B lock as 38.7° — the exact conversion of the
published 43 gon — rather than the 38 the same table prints beside it: at 38° the prediction misses
the PVT sheet by 0.169 m, at 38.7° by 0.036 m. The same exact-gon convention gives LV 12 39.6°
(44 gon), BUS 13,7 41.4° (46 gon) and BUS 15 53.1° (59 gon).

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
   `validationStatus: "SourceTranscribed"`. An articulated preset adds a `towedUnits` array and
   stops here: the steps below reconstruct a rigid body from wheel traces and do not cover one yet.
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

   A locked turn either side of a `Reverse` is what a three-point turn is made of. The cusp between
   the legs is a standstill, so the wheel arrives at each leg already at lock rather than spending
   the leg getting there, and shuffling round now needs less width than turning round for every
   preset in both modes — see [Turning the wheel at a standstill](#turning-the-wheel-at-a-standstill).

   `Stop` arms a standstill on the next click, for a stop the route would not otherwise have had:
   the vehicle is taken to a halt, the wheel is turned where it stands, and the leg is driven from
   there. It is spent on that click rather than latched, because a stop happens once. `Direction`
   arms an exact exit direction on the next click: the click places the point, and the pick then
   continues with the point fixed while the cursor swings the direction about it, with the whole leg
   replanned on every move — see [Saying exactly which way to leave](#saying-exactly-which-way-to-leave).
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
- **Towed axle tracks:** the same for each towed unit of an articulated preset, nearest first.
- **Vehicle footprints:** the body outline stamped along the route at the `FootprintInterval`
  station spacing — one outline per rigid unit, so an articulated stamp reads as a tractor and its
  trailer folded against each other; set the interval to 0 to omit them.
- **Body swept envelope:** theoretical occupied area from the sampled poses, every unit included, built as a
  polygon union. Where a manoeuvre encircles ground it does not cover — a roundabout island, say —
  the envelope carries that as a hole rather than reporting the island as occupied.
- **Clearance envelope / minimum access footprint:** body envelope plus the configured allowance;
  the Vejregler default is 0.30 m.
- **Fixed-width road edges:** asymmetric offsets from the route. A failure means the clearance
  envelope does not fit between them.
- **Warning points:** steering, steering-rate, articulation, tangent, grade, obstacle, boundary, or
  road-fit issues.

These outputs are screening geometry, not construction-ready kerb design or certified AutoTURN
results. See `plan.md` and `reference-validation.md` for scope and validation status.
