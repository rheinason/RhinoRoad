# Review — editable routes, interaction modes, and tweakability

Reviewing the working tree on `feature/dialog-and-prompt-reduction` against `6aa776c`.
Build and tests are green: 160 passed, 0 failed.

**Status: all findings below are resolved.** The numbered sections are kept as the record of what
was found and why each fix was made; each now carries its resolution. The closing section on
tweakability is design commentary, not a defect, and remains open work.

## What the branch actually does

Three interaction modes now exist, split cleanly by intent:

| Command | Asks | Reanalyses | Rebakes |
| --- | --- | --- | --- |
| `RRVehicleAccess` | dialog + geometry picks | yes | yes |
| `RRVehicleAccess` on a saved source | dialog only | yes | yes |
| `RRUpdateVehicleAccess` | nothing | yes | yes |
| `RRInspectVehicleAccess` | nothing | on demand | no |

The architectural move underneath is the good one: `ManoeuvreReplayService` is now the single
implementation of "what a click does", and `InteractiveRouteBuilder` calls it for both the
dynamic-draw preview and the committed pick (`InteractiveRouteBuilder.cs:198` re-plans from
`getter.Point()` rather than trusting the last mouse-move — right call). `ManoeuvreReplayTests`
pins recording and replay to sample-for-sample equality through rewind, reversal and a `Finish`.
That is the property the whole editable-route feature rests on, and it is tested.

Two other things worth keeping: `RhinoOutputWriter.Bake` now builds the new set *before* deleting
the old one and rolls back on throw (`RhinoOutputWriter.cs:26-66`), and `Commit` restores the
source's attributes if baking fails. A failed update genuinely leaves the previous result intact.

## Findings, worst first

### 1. ~~`VehicleAccessReviewService.Events` is O(n²) and runs on every commit~~ — fixed

`AddMarginEvents` passes lambdas that call `IndexAt(samples, sample.StationMetres)` — a full
`Enumerable.Range(...).MinBy` — once per sample, for each of three margin kinds
(`VehicleAccessReviewService.cs:174-182`). `NearestRoute` in the tangent-continuity grouping does
the same (`:158`). At the 0.10 m step the 571 m test road is 5,710 samples, so that is ~230M
delegate-driven iterations per commit, and again every time the inspector recomputes. The margins
arrays are already index-aligned with `samples` (both are built one-per-route-sample), so the
lookup is pure waste — give `GroupFailures` an index-aware predicate and read `margins[i]` directly.

### 2. ~~`ReplaceExisting` and `PreviewBeforeBaking` are stored, editable, and ignored on every rerun~~ — fixed

The Configure path calls `VehicleAccessRunService.Commit(document, configuredRun)`
(`RRVehicleAccessCommand.cs:79`) without passing `settings.ReplaceExisting`, so the default `true`
wins. Neither rerun path calls `ShowReport` or `PreviewAndConfirm` — those live only in the
first-run branch (`RRVehicleAccessCommand.cs:194`). A user who unticks "Replace previous result"
and reruns gets a replacement anyway, silently; a user who ticks "Preview and confirm" never sees a
preview again after the first run.

### 3. ~~Travel direction is uneditable for an interactive route~~ — fixed

`Apply` reads it out of `Manoeuvre.StartDirection` for display (`:404`), but `WithSettings` writes
it only to `ExistingCurveDirection` (`:426`), which the interactive replay branch never reads.
Changing the dropdown looks like it worked and does nothing. Either write it through to
`Manoeuvre.StartDirection` (and the leading controls) or disable the field when the source is
interactive.

### 4. ~~Grade checking on an interactive route is vacuous but reports as a pass~~ — fixed

`RateLimitedTrajectoryGenerator` carries `state.RearAxleCentreMetres.Z` through unchanged (`:70`),
so every interactively driven route is flat. `ManoeuvreControl` faithfully stores each pick's Z and
then `PlanControl` uses only `.XY`. The inspector prints "Maximum grade 0.00%" and a green Grade
check with the same confidence it uses for a real curve. Either drape Z along the controls or mark
the check `Unavailable` for interactive sources.

### 5. ~~The colour ramp is metric-blind~~ — fixed

`ColorFor` divides by a hard 10.0 in both `VehicleAccessReviewConduit.cs:145` and
`VehicleAccessProfileControl.cs:154` — and the two are duplicates of each other. Steering angle in
degrees, rate in °/s, grade in %, and clearance margin in metres are not on one scale: any
clearance margin over 7.5 m paints red, which inverts the meaning for the one metric where large is
good. Scale per metric against that metric's limit, and share one implementation.

### 6. ~~There is no way to change which obstacles or boundaries are checked~~ — fixed

The Configure path reprompts only when the stored list is empty or an id has gone missing
(`RRVehicleAccessCommand.cs:53-64`). Keeping the check on but swapping the curves requires two runs
— turn the check off, accept, turn it on, accept — because `WithSettings` clears the list when the
check is off. Offering the reselect whenever the dialog was opened (or a "Reselect references"
button) closes this.

### 7. ~~Freshness polling hashes geometry twice a second~~ — fixed

`PollFreshness` fires on a 0.5 s `UITimer` and `DefinitionIsCurrent` calls
`AccessDefinitionStore.Fingerprint` on the source plus every referenced obstacle and boundary —
each a `DivideByCount(64)` plus SHA-256. With a handful of boundary curves this is constant
background work for a modeless panel. `RhinoDoc.ReplaceRhinoObject` / `DeleteRhinoObject` events
would give the same staleness signal for free; keep the fingerprint as the cross-session check on
load.

### 8. ~~Smaller things~~ — all fixed

- ~~`ManoeuvreDefinition.TerminalHeadingRadians` is written in two places and read in none — a dead
  field in a versioned schema, which is the expensive place to leave one.~~ Removed. Nothing had
  ever read it, and the schema has not shipped, so dropping it beats freezing it into version 1.
- ~~`RRVehicleAccessSample` now does `document.Objects.AddCurve(routeCurve)` with default attributes,
  landing the sample's source curve unnamed on the user's active layer while everything else goes
  under `RhinoRoad::*`.~~ It now goes through `AccessDefinitionStore.AddSampleSource`.
- ~~The start heading is unreachable after creation — grips move the start point, but `Reconcile`
  preserves `StartHeadingRadians` from the stored definition, so the only way to change it is to
  re-drive.~~ `RREditVehicleJourney` now sets `StartHeadingRadians` from a picked direction.
- ~~`VehicleAccessReviewService.Cache` and `VehicleAccessInspectorService.OpenForms` are static and
  never evicted on document close; the snapshot holds full routes, poses and geometry.~~ Both now
  drop their entries on `RhinoDoc.CloseDocument`.
- ~~Passing checks render as *disabled buttons* in the inspector, which reads as broken UI rather
  than "passed", and `Reading()` falls back to the check's own message, giving rows like
  "✓ Tangent continuity — Route tangent continuity".~~ Only failing checks are buttons now; the rest
  are labels, and `Reading()` returns Pass/Off rather than echoing the check name.
- ~~`ResolveCurves` in `TryPrepareUpdate` is not gated on `CheckObstacles`/`CheckAllowedArea` the way
  the missing-id check above it is.~~ Both resolutions are now gated on their check flags.

## On tweakability specifically

The split is right, and it matches rule 3 in `path-model-notes.md`: sparse controls are the
authored intent, the polyline is the editable handle, and replay is deterministic.
`ManoeuvreControlReconciler`'s cost design — gap cost above any possible match distance, so ordered
controls are preserved through large grip moves — is a genuinely thoughtful choice, and the comment
explains why.

What is missing is round-tripping the *non-positional* intent. A control's `Direction` and `Kind`
are the two things that make a manoeuvre a three-point turn rather than a set of points, and
neither is visible or editable in Rhino. A vertex you drag keeps its Reverse flag invisibly; a
vertex you insert inherits its predecessor's, silently. There is no way to see which legs reverse
without re-running and reading the route, and no way to flip one. Given the rest of the design,
per-vertex point objects tagged with role and colour — or a `Direction`/`Finish` toggle in the
inspector's problem list — is the piece that would make the control line actually editable rather
than merely movable.
