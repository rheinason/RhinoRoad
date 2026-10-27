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

### Fixed or corrected

- Documentation contradictions: the stored certification run passes **6 of 18** and PV is
  `ReferenceValidated`; the plugin is no longer described as rigid-only; the steering rate
  (`lockToLockSeconds` 6.0 s) is now stated to be a RhinoRoad assumption, not a handbook figure.

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
