# Changelog

Notable changes to what RhinoRoad draws and reports, newest first. Versions are the beta releases
published on Yak; each earlier entry is summarised from the release's commit titles. Where a change
alters an existing result, it says whether a saved road needs an **Update** (`RRUpdateRoad`) to pick it
up. Nothing recomputes or rebakes on its own.

## Unreleased

This is the 0.5.0-beta → next tranche: most of the first-draft review (see
[docs/history/improvements.md](docs/history/improvements.md)) is implemented.

### Behaviour changes you should know about

These can change the result of a road you have already saved. Run `RRUpdateRoad` on a saved road to see
its new result, and review it before relying on it.

- **Saved rigid journeys with a reverse `Aim` and a direction now replay as a docking reverse.** A rigid
  vehicle reversing to a point with an exit direction used to square its heading first and then run
  straight, finishing parallel to the bay, a metre or two beside it. It now follows the approach line and
  arrives on the point *and* square to the direction. **The route is different** (and more accurate) after
  an Update. Articulated journeys that dock with a direction also replay through the shared docking
  seam, and a two-joint chain (PVT, and MVT) is now planned by simulation, so a saved PVT reverse with a
  direction can also follow a different route.
- **Folds past 65° are now violations.** Each towed unit has a fold limit, default 65° (a RhinoRoad
  assumption; the source publishes none). Previously only a fold past 90° was rejected. The 90°
  jackknife floor remains. The reverse generator, the planner, the area search and the analyzer now use the
  same limit, so a route one refuses cannot be baked as feasible by another.
- **Grade is checked over a moving window** (the larger of 5 m and the wheelbase) instead of between
  adjacent 0.10 m samples, so Z noise on a sampled curve no longer produces spurious grade violations. A
  flat interactive route no longer shows grade figures.
- **Kinks of under 10° are now flagged.** Tangent breaks on a selected curve are found exactly rather than
  by comparing sample headings, so a 6° polyline corner is reported as a kink instead of passing.
- **Obstacle, boundary and road-fit conflicts are reported with station ranges** rather than all at
  station 0. The minimum clearance accounts for holes (an obstacle on an island), and an obstacle fully
  covered by the body (a bollard under a trailer) is a conflict.
- **Finish ends the journey.** After a Finish leg is committed the pick loop ends instead of staying
  armed for another Finish.
- **Esc keeps your legs.** With legs driven, Esc asks whether to keep, discard or continue instead of
  discarding everything.
- **Steering-rate checks skip standstill samples**, which previously could read as an infinite rate.
- **Edit Road**: **Save** and **Discard** are undoable; **Settings…** no longer saves the draft as a side
  effect (it is part of the draft until Save). The palette follows document changes instead of polling.
- A saved road's **Source cannot be changed** while reconfiguring it (this previously started a new
  route), and the mode's default clearance is applied when the field has not been edited.
- A road saved by a **newer RhinoRoad** than the one installed is refused with an explicit message;
  older saved roads are upgraded by schema version when read.
- **A hole in the terrain is bridged, not held level.** A draped station that misses the terrain between
  two that hit now takes the height interpolated by station between them; only misses before the first
  hit or after the last hold the nearest hit's height. Holding the last height turned a 10 m hole in a
  5 % slope into a 500 % step (14.9 % over the window). Draped roads need an Update.
- **The grade window restarts at every change of direction and every standstill**, so the up and back
  legs of a shuffle on a slope no longer cancel (3 m up 10 % and 3 m back read as 2 %, now 10 %). A leg
  shorter than the window is judged end to end. Roads with reversals on a slope need an Update.
- **An open obstacle line through the body via one of its corners is a body conflict.** The nearest-pair
  search stopped at the exact touch at the corner and never saw the crossing beyond it, so such a line
  read as "the body clears… but the clearance does not fit" while its conflict area said the body was
  in it; a line drawn corner to corner through the body is now caught too.
- **Allowed areas drawn side by side a hairline apart act as one.** A gap of under 2 mm between separate
  boundaries is closed before the check, so the sliver no longer reads as outside the allowed area; a
  notch within one boundary is kept exactly as drawn.
- **Every piece of a disjoint sweep is checked against the site**, not only the largest. The sweep is
  still reported as disjoint.
- **Editing a terrain marks its roads out of date.** A terrain (mesh, surface, polysurface, extrusion or
  SubD) is fingerprinted by its vertices or control points, not only its bounding box, so moving an
  interior vertex no longer leaves a road "current" with stale grades. Curve fingerprints are unchanged.
  A road draped on a terrain and saved by an earlier build reads as **out of date once** after upgrading;
  an Update records the new fingerprint.
- **The mirror allowance is in every preview.** The live sweep while driving, the Edit Road preview and
  the clear-area search and readout now sweep the same vehicle the bake does, so a road with the mirror
  allowance no longer previews (or searches) narrower than it bakes.
- **Remembered defaults come only from a new road.** Configuring a saved road (`RRRoad` on it, or the
  palette's **Settings…**) no longer overwrites the settings the next new road starts from.
- **Grasshopper `Analyse Path` samples a curve exactly as `RRRoad` does** (it compiles the plugin's own
  sampler): curvature is taken from the curve and kinks are found on it, including those under 10°, so
  its violations can differ from earlier builds. Plan outputs (envelopes, holes, towed tracks,
  footprints) now sit at the route's start elevation instead of Z = 0.

### Added

- **Reversing for every vehicle**: rigid docking to a bay rear; one-joint docking in mode A and B;
  two-joint docking (PVT, MVT) by a simulation planner; `PlanReverse` and the clear-area search offered
  for every preset and any user vehicle that can be docked; a mode-A fallback where the single-joint
  cascade misses; "heading not honoured" reported for chains of three joints or more.
- **Per-leg speed**: the interactive `Speed` option, a speed dropdown per control in the Edit Road
  palette, and a Grasshopper `Speeds` input. A speed above the mode's is driven at the mode's.
- **Per-unit fold limit** (default 65°) plus the 90° jackknife floor.
- **SK and MVT presets**. SK: mode A at 5 km/h, mode B 50 gon justified by the SKT B sheet. MVT: a B-double
  taken from the official AutoTURN DWG definition, mode B 38.0°. Caveats in
  [docs/validation.md](docs/validation.md).
- **User-defined vehicles**: `RRImportVehicle`, `RRVehicles`, a user vehicles folder, the `UserDefined`
  status, and `samples/user-vehicle-example.json`.
- `RRCompareRoads` (area only A / only B / both, with hatches) and `RRReportRoad` (self-contained HTML
  plus per-station CSV).
- **Terrain draping** of a driven or selected route for real grade checks.
- **Wheel-edge paths** (on by default) and an optional **mirror allowance** (0.30 m published width,
  p.37; the 1.0 m position is a RhinoRoad assumption).
- **Live site-clash preview while driving**: the site is picked first and the sweep shows red where it
  clashes.
- Site checks with **station ranges**, **conflict hatches measured as area**, **one marker per problem
  range**, a **baked note** (vehicle, mode, clearance, validation, date) and filled envelopes.
- Inspector: a fold check and fold-angle profile, a swept-path **scrub slider, playback and trail**.
- `TangentToCurve` start; background (off-thread) preview; insert and delete control rows in the palette.
- A **RhinoRoad toolbar** (`.rui`), **persisted settings**, clearer dialog labels.
- **Grasshopper components** (`Vehicle`, `Analyse Path`, `Drive Manoeuvre`) and a **CI workflow**.
- The documentation is split into `docs/` (user guide, modelling, validation, developer notes).
- `-RRRoad` options `DrapeTerrain`, `WheelEdges` and `MirrorAllowance`. In a scripted run the terrain
  prompt accepts Enter for none, which turns draping off for that run instead of stalling the macro.
- Grasshopper `Drive Manoeuvre` has an optional **Stops** input (last, so existing wiring is kept): per
  control, true stops before that leg and turns the wheel at a standstill.
- `-RRImportVehicle` reads the file path from the command line.

### Fixed or corrected

- Documentation contradictions: the stored certification run passes **6 of 18** and PV is
  `ReferenceValidated`; the plugin is no longer described as rigid-only; the steering rate
  (`lockToLockSeconds` 6.0 s) is now stated to be a RhinoRoad assumption, not a handbook figure.
- A user vehicle file containing JSON nulls (`"vehicles": null`, a null vehicle, a null driving mode, a
  null outline or outline point, a null towed unit) is reported as a file error by `RRVehicles` instead
  of crashing the catalog, which every command then retried and failed on. One bad file never stops the
  others loading.
- User vehicle validation rejects a maximum wheel angle outside (0°, 90°), negative overhangs, a negative
  default clearance (which used to fail only later, when the road was saved) and non-finite values.
- Analysis messages (steering, grade and site-fit violations, the swept-region summary, problem-range
  labels and the drawn-alignment join note) are formatted with invariant numbers, so the HTML report no
  longer mixes "12,5" from a message with "12.5" beside it on a comma-decimal machine. The join note lists
  stations as "12.5 m, 30.0 m" instead of "12,5, 30,0 m".
- A steering-rate violation between two drawn-alignment joins less than 2 m apart is counted once, at the
  nearer join.
- Holes are assigned to their regions from Clipper's own nesting (envelopes, comparisons, combined
  footprints and site-fit conflict areas). A hole Clipper started on the boundary it touches was dropped
  or given to the wrong region, so the region drew and measured without it.
- The report's CSV writes a road name starting with `=`, `+`, `-`, `@`, a tab or a carriage return as
  text (prefixed `'`), so a spreadsheet does not run it as a formula.
- The baked note keeps a version that merely begins with "v" ("vejregler-1"); only a "v" prefix before a
  digit or capital ("v2.1", "vVD-…") is dropped.
- The baked note is **0.25 m tall whatever the annotation scaling**: with model-space scaling on and a
  style scaled 100 it was drawn 25 m tall. The note overrides its own dimension scale to 1.
- A road whose **user vehicle is no longer loaded** no longer throws in the Edit Road palette, its
  viewport labels or the area search: they say "vehicle X not found — import it again (RRImportVehicle)
  or change the road's vehicle", and `RRRoad` on such a road falls back to a loaded vehicle.
- Palette and inspector buttons (Save, Discard, Settings, the picks, Update, Edit, Measure) pressed
  while another Rhino command is running say **"Finish the running command first"** instead of nesting
  into that command's undo step or prompt.
- An edit palette or inspector queued to open is not shown if its document closed in the meantime, and
  a closing document is no longer redrawn by the inspector.
- The clear-area search no longer fills suggestions into a panel hidden because the vehicle became
  unsupported, and abandoned searches and readouts no longer leave faults unobserved.
- The interactive preview stops scheduling work once its pick ends, and an exception while planning a
  leg in the frame is shown as the preview's error instead of escaping the draw.
- The inspector's site-fit fallback keeps the fixed-width road check when the corridor region is
  missing, measuring against the road boundary's outline as the run does.
- `RRCompareRoads` draws its regions at each side's route elevation with the solid hatch pattern (it
  used Z = 0 and pattern index 0).
- `-RRReportRoad` defaults to the document's folder (Documents for an unsaved document) and takes a path
  with spaces; a relative path is resolved against that folder rather than Rhino's working directory.
- Grasshopper: `Clearance` and `Interval` are described as model units, which is how they were always
  read (like every other length on the components); vehicle-id descriptions list the loaded catalogue;
  the user vehicles folder is re-read when its files change; model units come from the definition's own
  Rhino document.
- PV's validation note and the Yak manifest quote the stored run's worst envelope deviation, 0.054 m,
  and the manifest lists the ten presets and user vehicles.

## 0.5.0-beta (2026-09-17)

- Plan a reverse into a bay (`PlanReverse` for SVT) and search the yard for a less clear approach, with a
  clear-area search in the Edit Road palette.
- Replace output now works when generated objects are on hidden layers; site curves are measured by their
  own vertices.

## 0.4.1-beta (2026-09-16)

- Docking into a nearby bay tries stronger approach corrections and keeps the best drivable arrival.
- Live-testing workflow documented.

## 0.4.0-beta (2026-09-09)

- Turning the wheel at a standstill; a change of direction is a standstill, so a shuffle needs less width
  than a U-turn. A **Stop** option arms a standstill on the next click.
- A run that has jackknifed is rejected instead of reporting its envelope.
- Reversing steers the trailer, and an exit direction backs it square onto a dock (SVT).
- The exit direction can be swung about the point just placed (`Direction`).
- Documentation of reversing, docking, the standstill and the exit swing.

## 0.3.0-beta (2026-09-08)

- Towed units: **SVT** and **PVT** articulated presets, with each unit's heading integrated along the route.
- Five more presets checked against the official legacy curve library (steady-turn annulus).

## 0.2.0-beta (2026-09-06)

- Editable saved journeys, a review inspector (`RRInspectRoad`) and access sizing
  (`RRMeasureRoad`, `RRCombineRoads`).
- Turn at full lock, square the exit, and bound every aimed leg; transitions reported for a drawn
  alignment; the exit is eased only when the corner is actually being left, and the next point picked
  supplies the corner's exit direction.
- State the preset validation honestly in the Yak manifest.

## Before 0.2.0-beta

- Initial MVP with the DWG certification baseline (`RRReferenceCertify`), the vehicle dialog, and
  waypoint-driven, replayable route authoring with reverse legs.
