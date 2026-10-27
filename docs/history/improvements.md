# RhinoRoad — first-draft review and improvement list

> **Status note (written after the improvements tranche; the review below is kept as it was written).**
> Nearly every item in this list was implemented in the tranche that followed the review: the
> correctness bugs (B1–B12), the modelling items (M1–M10: one per-unit fold limit, mirrors and
> wheel-edge paths, per-leg speed, reversing for every vehicle, schema migration, windowed grade,
> terrain draping), the UI items (U1–U14), the features (F1–F11), the performance items (P1–P5), the
> architecture items (A1–A4), CI and regression tests (T2–T4), and the docs items D1–D3 and D5–D7 (D5 is
> documented rather than moved: the tests parse `Previous Attempts/VehicleTurn_VD_v12.py`, so it stays
> where it is). See `CHANGELOG.md` for what changed for users.
>
> **Still open:**
>
> - **F12** localisation (Danish UI strings), and **A5** centralising the user-facing strings.
> - **D4** Git LFS for the reference DWG/ZIP/PDF data. Migrating rewrites history, so it needs a decision
>   from the repository owner.
> - **T5** transcribing the DWG axle tracks so REN and BUS 12 can pass the certification matrix.
> - **T6** re-running the certification with the mode-A 30° lock before applying it.
> - **T1** running the manual Rhino checklist (`tests/vehicle-access-rhino-checks.md`) in a live
>   session and recording the result per release; the checklist has been updated, the run has not.
> - `.codex/config.toml` still contains a machine-specific path (D3); whether it should be untracked is
>   an open question.
>
> File references below point at the code as it stood at `d911fb4`; several of those lines have since
> moved.


Reviewed at `d911fb4` (0.5.0-beta), 2026-10-25. The build is clean (0 warnings, warnings are
errors) and all 303 core tests pass. This review comes from reading the source. Nothing here was
exercised in a live Rhino session. An item marked **(verify in Rhino)** is runtime behaviour
inferred from the code that still needs reproducing before a fix.

Priority: **P1** wrong answer or lost work · **P2** friction or misleading output · **P3** polish
or long-term health.

---

## What to keep

These are the strong parts of the draft. They should survive any rewrite.

- **Core is free of Rhino and deterministic.** Kinematics, sweeps (Clipper), replay and planners
  all live in `RhinoRoad.Core` and run in unit tests in seconds.
- **One authority for a click.** `ManoeuvreReplayService.PlanControl` serves the live preview,
  the commit and the replay, and tests pin them sample for sample.
- **Sparse, editable intent.** Controls are stored as a polyline plus a versioned JSON definition,
  so a design can be replayed and reconciled after grip edits.
- **Honest validation.** Validation status travels with each preset, a failed extraction never
  reports a number, and fixture ratchets mean a regression cannot pass quietly.
- **Safe rebake.** The new output is built before the old output is removed, with a rollback on
  failure.
- **The modelling is unusually well argued.** The rewind-to-ease exit, the standstill at cusps,
  trailer-led reversing and the single-joint docking limit are all reasoned in comments and tests.

---

## Top ten, in suggested order

1. **B1** Obstacle, boundary and road-fit violations are all reported at station 0.
2. **B2** The minimum clearance ignores holes, so an obstacle on an island reads as clear.
3. **B3** The per-pose obstacle margin misses an obstacle fully covered by the body (a bollard).
4. **B4** The Edit Road palette computes SVT trailer and bay figures for every road, rigid ones
   included.
5. **B5** A jackknife is filed under "Swept and clearance envelopes", and that check still shows
   Pass.
6. **U1** Esc while driving throws away the whole journey.
7. **U2** Site constraints are picked *after* driving, so clashes cannot be seen while aiming.
8. **B6** Palette and inspector edits are made outside a command with no undo record.
9. **M1** Three different fold limits are in use (65°, 65°, 90°).
10. **D1** The docs contradict each other on validation status and scope.

---

## 1. Correctness bugs

**B1 · P1 — Site-check violations have no station.**
`RhinoGeometryBuilder.CheckClearance` creates `ObstacleClearance`, `OutsideAllowedArea` and
`FixedWidthRoad` violations with `StationMetres: 0.0` (`src/RhinoRoad.Rhino/Services/RhinoGeometryBuilder.cs:198`,
`:216`, `:228`). The command report says `ObstacleClearance @ 0.00 m`, and the inspector's fallback
events sort these to the start of the road. Fix: project the conflict point onto the nearest pose
and use that pose's station. Better still, report the station *range* of the poses whose outlines
touch the conflict polygon.

**B2 · P1 — Minimum clearance is measured to the outer boundary only.**
`CheckClearance` measures `MinimumDistance` against `BodyRegion.OuterBoundary` (`:186`). An
obstacle inside an enclosed hole, such as a roundabout island or a planter in a loop, is measured
to the far outside edge. The reported "Minimum obstacle clearance" can therefore be metres too
generous. The conflict test is correct here, because `AccessFootprint` honours holes. Fix: take the
minimum over the outer boundary and every hole.

**B3 · P1 — The per-pose obstacle margin misses containment.**
`Geometry2D.MinimumDistance` returns 0 only when segments cross
(`src/RhinoRoad.Core/Geometry.cs:91`). A small closed obstacle that lies entirely inside a body
outline, such as a bollard or a column under a trailer, gets a positive distance.
`VehicleAccessReviewService.MarginsToObstacles` (`:203`) uses that distance, so the inspector
profile and the problem ranges show that stretch as clear. Only the region check still raises the
(station-0) violation. The same gap applies to `MarginsToAllowedArea` and `MarginsToRoad`, which
test vertices only. Fix: add a polygon-in-polygon test (or a Clipper intersection) before the
distance, and return a negative margin for overlap.

**B4 · P1 — The Edit Road palette runs SVT maths on any road.**
`JourneyEditForm.Poll` (`src/RhinoRoad.Rhino/Services/JourneyEditForm.cs:1035`) runs every 0.4 s
whenever a yard is set. The yard is pre-filled from any saved allowed area, whatever the vehicle.
`Poll` then uses `Catalog.Get("SVT")` (`:1064`) to synthesise a trailer on the route and prints a
"bay error … / ∞°" line followed by "Adjust the controls before saving" for a PV or bus road.
**(verify in Rhino)** Fix: gate the whole search group on `TrailerReverseGenerator.CanDock(vehicle)`,
and hide it (not just its text) for other vehicles.

**B5 · P2 — Jackknife violations are misfiled in the inspector.**
`VehicleAccessReviewService.CheckFor` maps `ArticulationAngle` through its default case to
`EnvelopeGeneration` (`:258`). The inspector has no articulation check, so a jackknifed run lists
"Swept and clearance envelopes ✓" while the overall status is infeasible. Fix: add an
`ArticulationAngle` review check, and a fold-angle profile for articulated presets.

**B6 · P2 — Edits made from the modeless palette and inspector cannot be undone.**
No `BeginUndoRecord` appears anywhere. Commands get an undo record automatically. Palette **Save**
(`RRUpdateVehicleAccessCommand.Update` called directly), `WriteIntent`, `WriteDraft`, and the
discard restore all change the document outside a command. Ctrl+Z after Save will not bring back
the previous result, and Rhino may log an undo warning. **(verify in Rhino)** Fix: wrap each
palette-initiated change in `document.BeginUndoRecord("RhinoRoad …")`/`EndUndoRecord`, or route
it through a hidden command.

**B7 · P2 — Finish stays armed after it is used.**
In `InteractiveRouteBuilder` the `finishing` flag toggles at `:347`, but nothing clears it after a
Finish leg is committed (`:548`). The next click is another Finish, which builds a route with a
Finish part-way through. The Edit Road palette then cannot show that row, because Finish is only
offered for the last control. Fix: end the pick loop after a Finish is committed, or clear the flag.

**B8 · P2 — "Settings…" in the palette saves the draft by side effect.**
`JourneyEditForm.RunSettings` runs `_RRRoad` against the source (`:990`). The configure path
reconciles the dragged polyline and **commits** it, while the palette still says the edit is
unsaved. A later Discard restores the original curve and definition but leaves the rebaked output
of the discarded draft in place. **(verify in Rhino)** Fix: open the settings dialog in the
palette and keep the result as part of the draft, or block Settings while the draft is dirty.

**B9 · P2 — Changing Source while reconfiguring silently starts a new route.**
`RRRoad` takes the reconfigure path only when `settings.Source == storedDefinition.SourceKind`
(`src/RhinoRoad.Rhino/Commands/RRVehicleAccessCommand.cs:54`). If the user switches Source in
"Road settings", the command falls through to a fresh run. With a generated object preselected and
Source set to ExistingCurve, that object (an envelope, say) is analysed as a rear-axle path. Fix:
disable the Source field when configuring a saved road.

**B10 · P3 — Scripted `-RRRoad` builds the mode list once.**
`modeIds` comes from the vehicle at prompt start (`:522`), so choosing a different vehicle in the
same prompt keeps the old list. It is harmless today, because every preset has A/B. The scripted
prompt also has no `Footprints` mode option.

**B11 · P3 — Sampler kink detection is resolution-dependent.**
`RhinoRouteSampler` flags a tangent discontinuity when adjacent sampled headings differ by more
than 10° (`src/RhinoRoad.Rhino/Services/RhinoRouteSampler.cs:53`). A 6° polyline kink passes as
continuous and appears as a curvature spike instead. Curvature is also a finite difference of
tangents (`:49`). Fix: use `Curve.GetNextDiscontinuity(G1_continuous)` to find kinks exactly, and
`Curve.CurvatureAt` for curvature.

**B12 · P3 — `SweptRegionBuilder` drops secondary regions.**
When unions are disjoint, the extra outer loops are counted but discarded, and their holes are
attached to the largest outer loop. An "island inside a hole" also counts as disjoint. Return all
regions (as `AccessFootprint.Combine` already does) rather than the largest one plus a count.

---

## 2. Methods and modelling

**M1 · P1 — Pick one fold limit and make it per-vehicle.**
The analyzer uses 90° (`VehicleAccessAnalyzer.JackknifeFoldRadians`). The reverse generator
(`TrailerReverseGenerator.cs:59`) and the area search (`ManoeuvringAreaSearch.cs:157`) hard-code
65°. A route the search rejects can still be baked as feasible. Put a
`MaximumArticulationDegrees` on `TowedUnitDefinition` (default 65°, with the source noted) and
use it in all three places. Keep 90° as the absolute kinematic floor.

**M2 · P2 — Body outlines are rectangles.**
Every preset has a 4-point outline. Real sweeps are set by chamfered corners, front-overhang
geometry and, above all, **mirrors**: at 15 km/h a mirror's outer sweep often governs a kerb or
column. Add optional outline polygons and named protrusions (mirrors, steps), each with a height
band so it can be checked against low obstacles separately. `reference-validation.md` already
names this as the next validation step.

**M3 · P2 — Speed is fixed per mode.**
The rate limit turns seconds into metres using one speed for the whole route. Allow a speed per
leg, or a "crawl" control at manoeuvring speed. That is the honest form of the "mode C"
discussion: the same lock with a slower speed, and so shorter transitions. It needs no invented
vehicle parameters.

**M4 · P2 — Wheel tracks are not baked.**
Outputs are axle-centre tracks and body envelopes. Architects and reviewers expect **outer front
wheel and inner rear wheel paths** (kerb overrun, bollards). The offsets already exist
(`RearWheelInnerEdgeOffsetMetres`, `WheelOuterEdgeOffsetMetres`). Bake the four wheel-edge traces
for each unit as an option.

**M5 · P2 — Docking is limited to SVT, and the search is SVT by id.**
`ManoeuvringAreaSearch` checks `vehicle.Id != "SVT"`. The planner and docking need only
`TowedUnits.Count == 1`, so SK, once added, would be excluded. Gate on capability, not id. A rigid
lorry reversing into a bay (LV 12, REN) is the most common bay case on a real site, and needs the
same "point plus heading" reverse without a trailer.

**M6 · P2 — The planner grid ignores the vehicle.**
`TrailerReversePlanner` tries pull-ups at fixed {0, 5, 10, 15, 20, 30} m with ±5 m offsets.
Scale these by the wheelbase or the minimum turn radius, or search adaptively: coarse first, then
refine around the best candidate.

**M7 · P3 — The schema has no migration path.**
`AccessDefinitionSerializer` rejects any `SchemaVersion != 1`
(`src/RhinoRoad.Core/AccessDefinitions.cs:152`). The first schema change will make every saved
road unreadable. Add an upgrade chain (v1 → v2 …) and a test fixture of a v1 JSON payload that
must always load. The same applies to `ManoeuvreDefinition`.

**M8 · P3 — Grade is checked between adjacent 0.10 m samples.**
On a sampled NURBS, a little Z noise gives spurious grade spikes. Check grade over a moving
window (for example 5–10 m, or one wheelbase) as well as point to point, and report both.

**M9 · P3 — Interactive routes are flat.**
They carry the start Z throughout (correctly reported as "unavailable"). Optionally drape onto a
selected surface or mesh, which would give grade checks to driven routes too.

**M10 · P3 — `RoadEdgeMethod.MinimumFootprint` and `None` behave identically.**
Only `Both`/`FixedWidth` are ever tested. Either give `None` a meaning (no clearance envelope
baked) or collapse the enum into a "fixed-width corridor" checkbox.

---

## 3. UI / UX

**U1 · P1 — Esc loses the journey.** `InteractiveRouteBuilder.cs:317` returns `Cancel` on Esc at
any leg. After ten careful clicks, one stray Esc discards everything. Fix: on Esc with at least
one leg, ask "Keep the N legs driven so far? Yes / No / Continue", or treat Esc as Undo of the
last leg and require a second Esc to abandon.

**U2 · P1 — Pick site constraints before driving, and show clashes live.**
Obstacles and boundaries are selected after the route is driven (`RRVehicleAccessCommand.cs:166`).
The live sweep is therefore drawn blind, and the designer finds the clash only at the report.
Pick them first, or reuse the last set, then colour the preview sweep red where it overlaps an
obstacle or leaves the yard. The Clipper calls are cheap enough for a preview at reduced
precision.

**U3 · P2 — Show the edit-palette search only where it applies.**
The "SVT clear-area search" group takes half of an 860 px-tall palette for every vehicle. Hide it
for any vehicle the search does not support (see B4), and collapse it by default. 360×860 is
taller than many laptop working areas. Consider a dockable Rhino panel (`Panels.RegisterPanel`)
instead of a floating form placed over the viewport.

**U4 · P2 — Controls cannot be added or deleted in the palette.** Grips move vertices, but there
is no Insert after/Delete per row. The reconciler exists to absorb such edits, so expose them:
right-click a row → Insert control after, Delete control.

**U5 · P2 — Raw enum names in the dialog.** "Road edges" lists `Both / MinimumFootprint /
FixedWidth / None` (`VehicleAccessDialog.cs:73`). Use plain labels ("Clearance footprint only",
"Footprint + fixed-width road", …), and disable the width fields unless a fixed-width corridor is
chosen. "Travel direction" means "starting direction" for interactive routes, so label it that
way.

**U6 · P2 — The mode's default clearance is never applied.** `DrivingModeDefinition` carries
`DefaultClearanceMetres`, but changing mode or vehicle does not update the Allowance field. Fill
it in when the user has not edited the field, and show "(Vejregler default 0.30 m)" beside it.

**U7 · P2 — Viewport text is at fixed pixel positions.** Messages are drawn with `Draw2dText` at
hard-coded `(24, 12/36/60/84/108)` in several places, and several states share y=60. On HiDPI
screens, or when two messages fire together, they overlap. Use a single status-line helper that
stacks messages, scaled by `Viewport.DpiScale`, or move long guidance to the command prompt and
keep the viewport for one-line status.

**U8 · P2 — Settings are lost between sessions.** `_settings` is a static in-memory object.
Persist the last vehicle, mode, clearance and output options in `PlugIn.Settings`. Settings are
also shared across open documents, so consider per-document defaults stored in document user text.

**U9 · P2 — Prompt length.** The main pick prompt can run past 200 characters ("Pick next point
(Forward, wheel 12.3 deg, STOP first, then swing the exit; Ctrl for full lock, Shift to square
the exit); Enter to finish"). Keep the prompt short and move the modifier hints to a one-time
command-line tip or a viewport legend.

**U10 · P2 — Start tangent to existing geometry.** The start is always "pick point, pick
heading". Add an option to start tangent to a selected curve at its end, which is the usual case
when a journey continues a road centreline.

**U11 · P3 — Preview colours.** Forward is blue and reverse is purple in the planner review, but
the main preview uses one colour for both. Use the forward/reverse colouring everywhere, and add
a small legend.

**U12 · P3 — Baked output has no fills or annotation.** Envelopes are bare curves. Offer hatches
(clearance as a light tint, body as a darker one), and a text block at the start with vehicle,
mode, clearance, validation status and date. That is what goes on a drawing sheet.

**U13 · P3 — One marker per violation.** Long violations produce one red point every 0.25 m of
station. Bake one marker per *problem range* (as the inspector already groups them), with a
leader and text dot.

**U14 · P3 — No toolbar or icons.** Ship a `.rui` toolbar (Road, Edit, Update, Inspect, Measure,
Combine). The commands are hard to discover by name alone.

---

## 4. Features

Prioritised for the stated scope of architect-level screening, not highway design.

| ID | P | Feature | Why |
| --- | --- | --- | --- |
| F1 | P1 | Live site-clash preview while driving (see U2) | The single biggest workflow gain. |
| F2 | P2 | Wheel-edge paths and mirror sweep (M2, M4) | What reviewers check against kerbs and columns. |
| F3 | P2 | Rigid-vehicle reverse into a bay with heading | The most common bay manoeuvre on a site. |
| F4 | P2 | Report / export: PDF or CSV of checks, maxima, ranges, and validation status | Evidence for a design review, not only the command history. |
| F5 | P2 | Add MVT and SK presets | The README names both as ordinary chains; SK mirrors SVT. |
| F6 | P2 | Custom vehicle editor (JSON import with validation, marked `UserDefined`) | Projects bring their own refuse truck or fire tender. |
| F7 | P3 | Conflict regions as hatches with area | "How much is the overlap?" beats "it clashes". |
| F8 | P3 | Envelope comparison between two journeys or two vehicles (difference area) | Answers "what does designing for BUS 15 instead of BUS 12 cost?" |
| F9 | P3 | Swept-path animation or a scrub slider in the inspector | The hover pose already exists; a slider makes it presentable. |
| F10 | P3 | Grasshopper components over Core (vehicle, replay, sweep) | Core is Rhino-free, so this is cheap and opens parametric studies. |
| F11 | P3 | Drape to terrain (M9) and vertical clearance for low obstacles (M2 height bands) | Ramps and canopies on real sites. |
| F12 | P3 | Localisation (Danish UI strings) | The audience and the sources are Danish. |

---

## 5. Performance

**P1 · P2 — Freshness polling.** The inspector polls every 0.5 s and the edit palette every
0.4 s. The palette's `Poll` does Clipper intersections, a full `ArticulationTrace.Follow` and a
SHA-256 fingerprint on the UI thread on every tick. The inspector's runtime-serial short-circuit
helps there, but the palette has none. Drive both from `ReplaceRhinoObject`/`DeleteRhinoObject`
events, and recompute only when the preview result changes.

**P2 · P2 — Planning inside `DynamicDraw`.** The main pick and the exit-direction swing call
`PlanControl` synchronously on mouse move. For a docking reverse this runs
`DockTowedUnitInAvailableSpace`, which tries several correction strengths. The code already notes
that plan-reverse lagged and was removed from the swing, but the plain Direction docking swing
still plans live. Move planning to a background task with "latest wins" cancellation, as
`LiveJourneyEditConduit` already does.

**P3 · P3 — Per-frame serialisation.** `LiveJourneyEditConduit.PreDrawObjects` deserialises
every source and serialises two definitions on every redraw to compare them (`:131`). Cache the
definition by the source's `RuntimeSerialNumber` and compare a hash.

**P4 · P3 — Quadratic distance.** `Geometry2D.MinimumDistance` is all segments against all
segments. The review margins call it for each pose × outline × obstacle, which is fine for small
sites and slow for a campus. Add a bounding-box reject per pose, or a simple grid.

**P5 · P3 — `ResolveSource` scans every document object** (`DocumentObjects.All`) for each
lookup. Keep a stable-id → object-id map, invalidated on add/delete events.

---

## 6. Architecture and code health

- **A1 · P2 — Move review logic into Core.** `VehicleAccessReviewService`'s margins and
  `RhinoGeometryBuilder.CheckClearance` are pure geometry, but they live in the Rhino project and
  are untested. Moving them to Core lets B1–B3 get regression tests.
- **A2 · P2 — Split the large files.** `JourneyEditForm` (1,176 lines) and
  `InteractiveRouteBuilder` (966 lines, a single `TryBuild` loop with nine pieces of captured
  state) each do several jobs. Extract a small state machine for the pick loop (armed modifiers,
  history, commit) and separate the area-search panel from the edit palette.
- **A3 · P3 — Remove duplicates and dead code.** `RhinoOutputWriter.EnsureLayer` duplicates
  `AccessDefinitionStore.EnsureChildLayer`. `StateSample` exists in three places.
  `RhinoGeometryBuilder` has unused `TryFindOutside`, `Area`, `IntersectsOrContains`,
  `ToPoint3d` and `PlaneAt`, `SampleCurve` ignores its `spacingMetres`, and `Build` and
  `CheckClearance` take a `tolerance` they never use. `VehicleCatalog.LoadEmbedded()` is called
  in five static fields; make it one shared instance.
- **A4 · P3 — Static per-document dictionaries.** `OpenForms`, `Sessions`, `Documents` and
  `ForcedDrafts` (this one keyed by stable id only, not by document) are scattered. One
  per-document session object, disposed on `CloseDocument`, would remove the leak and ordering
  risks.
- **A5 · P3 — Hard-coded user strings.** Message text is spread inline through the commands and
  services. Gathering it in one place helps both consistency and F12.

---

## 7. Testing and validation

- **T1 · P1 — No Rhino-side tests and no record of the manual checks.**
  `tests/vehicle-access-rhino-checks.md` says its checks "have **not been verified**". Run them
  through the Rhino-MCP slot workflow in `live-testing.md`, record pass or fail per release, and
  script the repeatable ones (SVT bay, PV U-turn, obstacle on an island) as `run_csharp` smoke
  tests.
- **T2 · P2 — CI.** The README says the solution "builds … in CI", but the repository has no
  workflow. Add a GitHub Actions job (`windows-latest`, `dotnet build` + `dotnet test`); the
  NuGet RhinoCommon reference already makes this possible.
- **T3 · P2 — Regression tests for B1–B5 and B7** once A1 has moved the logic into Core.
- **T4 · P2 — Fixture for the v1 schema** (M7).
- **T5 · P3 — Finish the certification.** 12 of 18 required DWG cases still fail, 10 of them in
  extraction. The documented next step is transcribing the axle tracks from the official
  diagrams. Until that is done, REN and BUS 12, the most-used presets, stay `SourceTranscribed`.
  Show that status prominently in baked output, not only in the dialog.
- **T6 · P3 — Apply the mode-A 30.0° lock.** The finding is pinned as a test, but the presets
  still use 29.7° pending a certification re-run. Schedule that re-run and apply the change.

---

## 8. Docs, release and repository hygiene

- **D1 · P1 — The docs contradict each other.**
  - `README.md` says the stored run passes **6 of 18** and PV is `ReferenceValidated`;
    `reference-validation.md` says **0 of 18** and that PV "cannot be labelled
    `ReferenceValidated`". `vehicles.json` says PV is validated. Update `reference-validation.md`.
  - `plan.md` lists "articulated vehicles, trailers" and "Yak distribution" as *deferred*, but
    both have shipped. It also still calls the command `Road`.
  - The `AssemblyDescription` in `RhinoRoad.Rhino.csproj:16` says "rigid vehicle access
    screening".
- **D2 · P2 — The README carries too much.** At 683 lines it is user guide, design rationale and
  developer notes in one. Split it into `docs/user-guide.md` (commands, workflow, outputs),
  `docs/modelling.md` (rewind, standstill, trailer reverse, docking) and
  `docs/validation.md`. Keep the README to install, the command table and links.
- **D3 · P2 — Machine-specific paths in tracked files.** `.codex/config.toml` points to
  `C:\Users\hbxma\AppData\…`, and `live-testing.md` names `C:\Users\hbxma\Dropbox\TopoTest`.
  Move them to an untracked local config, or describe them as placeholders.
- **D4 · P3 — Large binaries in git.** About 23 MB of DWG/ZIP/PDF reference data is tracked
  directly (`.git` is 30 MB). Consider Git LFS for `reference/**/*.{dwg,zip,pdf}` before the
  library grows.
- **D5 · P3 — `Previous Attempts/`.** The folder name has a space, and the folder doubles as a
  test data source (`VehicleTurn_VD_v12.py` is parsed by the tests). Move the parsed payload to
  `reference/legacy/` and archive the rest.
- **D6 · P3 — Changelog.** Releases are commit titles only. Add a `CHANGELOG.md`, so beta users
  can see what changed in output and whether old roads need an Update.
- **D7 · P3 — Superseded review.** Every finding in `editable-route-review.md` is resolved. Move
  it to `docs/history/`, so it is not read as current status.

---

## Suggested sequencing

1. **Stabilise (0.5.x):** fix B1–B9, U1 and D1. Add CI (T2). Move the check logic to Core with
   tests (A1, T3).
2. **Workflow (0.6):** live site clashes (U2/F1), undo (B6), palette add/delete (U4), dialog
   clean-up (U5/U6), persisted settings (U8), one fold limit (M1).
3. **Fidelity (0.7):** wheel-edge paths and mirrors (M2/M4), real outlines, per-leg speed (M3),
   rigid bay reverse (F3), the schema migration (M7).
4. **Breadth (0.8+):** MVT and SK presets, a custom vehicle editor, reports, Grasshopper, terrain.
