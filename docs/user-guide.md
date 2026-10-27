# User guide

RhinoRoad is an internal Rhino 8 plugin for architect-friendly vehicle access screening. Drive a
complete journey through a site, check it against selected boundaries and obstacles, and measure the
space it needs. Consecutive bends retain the vehicle's steering state, and a towed unit is placed from
where the vehicle has been rather than from where it is. Highway design, automatic route finding and
automatic layout resizing are outside the product scope.

The outputs are **screening geometry**, not construction-ready kerb design or certified AutoTURN
results. How far each vehicle can be trusted is in [validation.md](validation.md); why the paths look
the way they do is in [modelling.md](modelling.md).

## Contents

- [Commands](#commands) and the toolbar
- [Vehicles](#vehicles), including your own
- [Drive a journey with `RRRoad`](#drive-a-journey-with-rrroad)
- [Interactive driving](#interactive-driving)
- [Reversing into a bay](#reversing-into-a-bay)
- [Selecting a drawn curve](#selecting-a-drawn-curve)
- [Site checks](#site-checks)
- [Editing a route](#editing-a-route)
- [Inspecting an analysis](#inspecting-an-analysis)
- [Sizing bends and junctions](#sizing-bends-and-junctions)
- [Comparing and reporting](#comparing-and-reporting)
- [Meaning of the outputs](#meaning-of-the-outputs)
- [Settings and saved roads](#settings-and-saved-roads)
- [Grasshopper](#grasshopper)

## Commands

| Command | Action |
| --- | --- |
| `RRRoad` | Create a road access journey; configure a selected saved road. |
| `RREditRoad` | Open an edit session: drag the control points with a live sweep, and set driving intent. |
| `RRUpdateRoad` | Save the recalculated sweep, rerun checks, and refresh linked sizing results. |
| `RRInspectRoad` | Review the selected road's checks and measurements. |
| `RRMeasureRoad` | Dimension a section through the required clearance footprint. |
| `RRCombineRoads` | Combine the footprints of separate required movements. |
| `RRCompareRoads` | Compare the clearance footprints of two journeys: what only A needs, what only B needs, what both need. |
| `RRReportRoad` | Write an HTML report and a per-station CSV for one or more saved roads. |
| `RRImportVehicle` | Import a user-defined vehicle from a JSON file. |
| `RRVehicles` | List every vehicle, its validation status and any user-vehicle files that were skipped. |

`-RRRoad` provides command-line settings for macros. The previous `RR…VehicleAccess` command names
still work as hidden compatibility commands. `RRReferenceCertify` and `RRVehicleAccessSample` are also
hidden from autocomplete; developers can invoke them by typing their full names.

A **RhinoRoad toolbar** (`RhinoRoad.rui`, shipped with the plugin) carries Road, Edit, Update,
Inspect, Measure, Combine, Compare, Report, Import vehicle and Vehicles, since the commands are hard
to discover by name alone.

## Vehicles

Ten Danish type vehicles ship in `vehicles.json`: **PV**, **REN**, **LV 12**, **BUS 12**,
**BUS 13,7**, **BUS 15**, **SVT**, **PVT**, **SK** and **MVT**. Rigid and articulated presets are
both supported; the articulated ones are SVT and SK (one joint) and PVT and MVT (two joints).

- **BUS 13,7 and BUS 15** carry a *medløbsaksel* (a self-steering trailing axle that the published
  curves assume), so the drive axle is the effective rear-axle reference and the distance to the
  trailing axle is folded into the rear overhang.
- **SK** is the 22 m special vehicle with a fixed rear axle. Mode A runs at **5 km/h** (the source
  publishes no SK mode-A curves), and mode B uses a 50 gon lock, justified by the official SKT B
  sheet.
- **MVT** is the B-double modular combination (three units, two joints), taken from the official
  AutoTURN DWG definition, with mode B at 38.0°.

The assumptions and transcription caveats behind each preset are in
[validation.md](validation.md). Each preset carries a validation status (`ReferenceValidated`,
`SourceTranscribed` or `UserDefined`), which the dialog, the baked note, the inspector and the report
all repeat. Only PV is `ReferenceValidated`.

Two type vehicles from the source are still absent, with reasons: **AUT** (autocamper; the figure
dimensions only overall length and width and has no row in Figure 6.7) and **T** (traktor med anhænger
og kost; the drawbar hitch position is not dimensioned). **BUS 25** (flerleddet bus) has no row in
Figure 6.7 and the source says its rear-axle steering is manufacturer-specific and computer-controlled.

### Your own vehicle

Projects bring their own refuse truck or fire tender.

1. Copy `samples/user-vehicle-example.json` (a copy of LV 12 under the id `MY-LORRY`) and replace every
   dimension with your vehicle's. The format is the same as the built-in `vehicles.json`: a
   `vehicles` array of objects with `id`, `name`, `version`, `widthMetres`, `wheelbaseMetres`, the
   front and rear overhangs, axle track, tyre width, `bodyOutline` (at least three points in the
   rear-axle frame, +X forward), `drivingModes` (A and/or B, each with speed, maximum wheel angle,
   `lockToLockSeconds` and default clearance), and optionally `towedUnits` (each with a hitch offset,
   wheelbase, width, outline and an optional `maximumArticulationDegrees`) and `protrusions`.
2. Run `RRImportVehicle` and choose the file (`-RRImportVehicle` takes the path on the command line,
   for scripts). It is validated against the built-in presets and the
   other files already in the folder, and copied into the user vehicles folder only if it passes. The
   folder is the `vehicles` folder under the plugin's settings directory; `RRVehicles` prints its
   exact location, and a `*.json` dropped there by hand is read the same way.
3. Run `RRVehicles` to list every vehicle. A file or vehicle that was rejected is listed with the
   reason, and the rest of the file is still loaded.

Rules:

- The **id must be unique**: it may not duplicate a built-in preset or a vehicle in another user file.
  The offending vehicle is skipped with a message ("id … is already used by a built-in preset;
  choose a different id").
- Dimensions are validated: wheelbase and width positive, axle track narrower than the body, tyre
  width narrower than the axle track, an outline of at least three points, at least one driving mode,
  and a fold limit above zero and at most 90°.
- **Whatever `validationStatus` the file says, a user vehicle loads as `UserDefined`**: not from the
  Vejregler source and not validated. Every output that quotes a status says so.
- User vehicles appear in the dialog, the edit palette, the Grasshopper components and the area search
  alike, and are docked, planned and searched by capability (see
  [Reversing into a bay](#reversing-into-a-bay)).

## Drive a journey with `RRRoad`

1. Run `RRRoad`.
2. Choose vehicle, mode, clearance, and optional site constraints. Interactive driving and a clearance
   footprint are the defaults. The dialog fills in the mode's default clearance (Vejregler 0.30 m)
   and shows it beside the field until you change it. Fixed-width corridors and output options are
   under **Advanced geometry and output**: wheel-edge paths (on by default), the optional mirror
   allowance, terrain draping, grade limit, footprint interval, and whether to preview before baking.
   Existing-curve analysis still treats the selected curve as the rear-axle path, not as an
   approximate instruction for a driver.
3. Press `Continue`.
4. For `ExistingCurve`, select the curve (skipped when exactly one curve was selected before the
   command started). Its parameter direction is the travel direction; choose `Reverse` when the
   vehicle faces opposite that direction.
5. For `Interactive`, **pick the site first** if site checks are ticked (see
   [Site checks](#site-checks)), then the terrain if draping is ticked, then drive.
6. The movement-fit report is written to the command line and the result is baked. With no site
   references selected, it says **Sweep generated — site fit not checked**. A successful check applies
   to the tested movement and selected constraints; a failed movement does not establish that access
   is impossible. Tick *Preview and confirm before baking* to stop at a transient viewport preview
   first.

A default run asks for at most one thing after the dialog, and nothing at all when the path was
pre-selected. `-RRRoad` keeps the option prompt for scripting, with `Preview`, `DrapeTerrain`,
`WheelEdges` and `MirrorAllowance` toggles matching the dialog's. In a scripted run the terrain prompt
also accepts Enter for none, which turns draping off for that run (and says so) rather than leaving a
macro waiting for a pick. **Your last settings are remembered** between Rhino sessions (vehicle, mode,
clearance, output options) from the dialog or prompt of a *new* road; configuring a saved road, here or
in the Edit Road palette, keeps its settings to that road. The one-run "reselect references" request is
deliberately not remembered.

## Interactive driving

Pick the rear-axle start and the vehicle's heading, then click successive previewed legs. The prompt
is kept short and shows how much wheel lock is on; a legend in the viewport lists the modifier keys,
and a dotted ray marks the heading each leg would end on.

**Starting.** At *Place vehicle* either click a point (then the heading), or choose
**`TangentToCurve`** and pick an existing curve: the journey starts at the curve's end nearer your
pick and carries on past it, which is the usual case when a journey continues a road centreline into a
site. Picked part-way along with `StartAt` off, the vehicle stands where you picked and heads towards
that end. A closed curve is started where picked and driven the way it runs. Escaping the curve pick
returns to the start prompt.

**Aiming.** While aiming, blue outlines show the body sweep and green outlines show the clearance
footprint of the complete proposed journey (with the mirror allowance when it is ticked, as baked); the retained body boundary is darker and the discarded tail
dotted. Forward and reverse travel are coloured differently (blue and purple). A steering-limit
message means the driven leg cannot follow the requested aim exactly. Preview uses the same polygon
union and clearance offset as final output, caching retained geometry; its extra boundary
simplification is regression-tested to stay within 4 mm of the final sweep in the multi-turn REN
scenario. Planning runs off the drawing thread with the latest request winning, so the cursor stays
responsive.

**Live site clashes.** When obstacles or boundaries were picked first, the preview sweep is drawn
**red where it overlaps an obstacle or leaves the yard**, so a clash shows before the click rather than
in the report afterwards. The preview is a picture quantised to a millimetre; the committed analysis
is the check, and anything red here is red in the report.

**Options** at the pick prompt:

| Option | Does |
| --- | --- |
| `Reverse` | Flips the travel direction for the next leg. A three-point turn is forward legs, a reversing leg, then forward legs again; the cusp is a standstill. |
| `Undo` | Removes the last leg. |
| `Turn` / `Aim` | Turn at full lock (also by holding `Ctrl`), or go back to aiming. |
| `Finish` / `Aim` | Pick the direction to end up travelling in; the vehicle eases onto exactly that heading with the wheel centred. **Finish ends the journey** and nothing can follow it. |
| `Stop` / `Rolling` | Arms a standstill on the next click: the vehicle is taken to a halt, the wheel turned where it stands, and the leg driven from there. Spent on that click. |
| `Direction` | Arms an exact exit direction on the next click: place the point, then swing the direction about it with the whole leg replanned on every move. Ortho and snaps apply, or type an angle. |
| `Speed` | Sets the speed of the legs that follow: `ModeSpeed`, `Walk` 5, `Crawl` 3, `Inch` 1, or type km/h. A speed above the mode's is driven at the mode's. |
| `PlanReverse` / `Manual` | Offered when a bay can be planned: pick the bay and let RhinoRoad find the approach (see below). |

**Modifiers.** `Ctrl` turns at full lock; `Shift` squares the leg to your ortho angle (measured from
the construction plane, so a rotated CPlane squares to the site). Type a number to sweep exactly that
many degrees. Object snap applies. Requested turns beyond wheel lock are clamped. Rhino's own ortho is
switched off inside this command on purpose; see [modelling.md](modelling.md#squaring-the-exit-and-saying-exactly-which-way-to-leave).

**Finishing and Esc.** Press Enter to finish. **Esc no longer throws the journey away:** with legs
driven it asks *Keep the N legs driven so far?* — Enter (or `Keep`) keeps them as if the journey had
been finished there, `Discard` abandons it, `Continue` carries on. With no legs, Esc cancels.

**Turning round.** Hold `Ctrl` (or `Turn`) and click abeam for a true 180° U-turn; type `180` to sweep
exactly that. Shuffling round needs less width than turning round for every preset, because the cusp
is a standstill. All of this, with the numbers, is in [modelling.md](modelling.md).

## Reversing into a bay

All vehicles, rigid or articulated, can reverse into a bay square to a direction. Pick the bay by
the **rear of the vehicle's reversing body**: the rear of the trailer for a combination, the rear of
the vehicle for a rigid one. The prompts name the right body (a refuse lorry is never asked for "the
rear of the trailer").

- **By hand.** Drive forward past the bay, choose `Reverse`, choose `Direction`, pick the rear of the
  body in the bay, then point the direction it will **travel while reversing**. The docked body is
  drawn while you point, and the axle position stored is derived from the rear. For an articulated
  vehicle the point you click means where the *trailer* should end up. The preview shows the whole
  sweep before you commit. If the bay cannot be reached from that setup, pull farther forward or change
  the turn-away point and preview again.
- **`PlanReverse`** (every preset). Choose it *before* switching to reverse, pick the rear of the body
  at the bay, then point the way it reverses in. The planner compares reversing immediately with
  forward pull-ups at several distances and side offsets (scaled to the vehicle). If the best first
  reverse does not arrive within 0.25 m and 2° of the bay, it also tries one forward correction and a
  second reverse, and proposes the most accurate drivable approach it finds. Review the full forward
  and reverse sweep, then press Enter to accept or Esc to choose another destination. The accepted
  proposal becomes ordinary forward and reverse controls, so it can be replayed and edited. If the
  destination still cannot be reached, the review prompt reports the remaining position and heading
  error.
- **A saved reverse docks.** A rigid journey saved with a reverse `Aim` and a direction replays as a
  **docking reverse** (see [CHANGELOG.md](../CHANGELOG.md): this changes the route of such journeys).
- **Heading not honoured.** A chain of three or more joints cannot be docked; its reverse is aimed at
  the point and the viewport says the heading was not honoured. None of the shipped presets is that
  long.

In mode A as well as mode B, articulated vehicles dock (the wheel takes 12.5 m to reach lock at 15 km/h
and the planner allows for that).

## Selecting a drawn curve

A selected curve is taken as the rear-axle path directly, and the checks report where the vehicle
could not actually follow it. A **polyline therefore reports tangent discontinuities at its corners**,
including kinks under 10°, which is the honest answer: no vehicle turns a right angle. For a
meaningful report draw the centreline smooth (`InterpCrv`, or `Fillet` at a radius the vehicle can
hold), or drive it interactively, where every leg is feasible by construction. The tool deliberately
does not smooth the line for you; what happened when it tried is in
[path-model-notes.md](path-model-notes.md).

An alignment drawn as arcs joined straight onto tangents is broken into its constant-curvature
stretches, and the report names those too short for the transition their neighbours need, with the
length they have and the length they need. The three ways out are to lengthen the stretch, ease the
radius either side, or use a slower mode or leg (see
[modelling.md](modelling.md#transitions-on-a-drawn-alignment)).

## Site checks

Obstacle curves and closed allowed-area boundaries are only asked for when their checks are ticked;
both are off by default. For an interactive journey they are picked **before** driving. Open obstacle
curves (a wall or kerb line) are lines the sweep must not cross; closed ones have an inside.

The checks are reported **with station ranges, not at station 0**: an obstacle, boundary or road-fit
conflict is located on the road as the stretch of stations whose poses come within the clearance, with
the worst margin in that stretch. The inspector groups them as problem ranges. The minimum clearance
accounts for holes (an obstacle on a roundabout island is measured to the island's edge, not the outer
boundary), and an obstacle completely covered by the body, such as a bollard under a trailer, counts
as a conflict.

On bake:

- **Conflict regions are hatched** on the `Conflicts` layer and **measured as area** ("how much is the
  overlap?"), not only reported as a clash.
- **One marker per problem range** replaces a red point every 0.25 m: each carries the check and the
  stations it covers.
- A **note is baked beside the envelope** (below its lower-left corner, 0.25 m high on the site whatever
  the document's annotation scaling): vehicle and
  version, mode, clearance, the mirror allowance if used, the validation status (with a warning line
  where the preset is not `ReferenceValidated`), and the date.
- The clearance and body envelopes are filled with light hatches behind everything else.

## Editing a route

An interactive run keeps the sparse clicks as an ordinary polyline on `RhinoRoad::Controls`. Run
`RREditRoad`, or press **Edit road** in the inspector, to open an edit session on it. The session
turns the control points on, numbers them in the viewport, and opens the modeless **Edit Road**
palette. Drag the points to see a live replay of the path, body sweep and clearance footprint. The
affected path is teal; the previous path is dotted grey. The preview includes any reshaping of the
preceding bend and warns when the steering limit prevents an aim from being followed exactly.

The live preview belongs to that session and to nothing else. Moving a stored control line outside an
edit session changes no display: the baked result stays on screen and the inspector reports the road
as out of date. Replay runs off the drawing thread. While it catches up with a moving cursor, the
previous preview is faded and labelled as the previous position. Esc cancels the native point move.

**The draft is kept until you Save.** **Save** keeps the edit, reruns the site checks and refreshes
any linked sizing results; **Discard** (and closing the palette) restores the control line as it was.
Both are **undoable** with Ctrl+Z (each palette-initiated change is one undo record). The palette
follows document changes rather than polling. **Settings…** in the palette opens the road's settings
as part of the draft: the blue sweep previews them, and nothing is saved until Save (it no longer
commits by side effect).

The palette carries the driving intent alongside the point editing. Set the starting heading, the
starting travel direction, or an individual control's direction and Aim/Turn/Finish intent; rows are
numbered to match the dots in the viewport, and hovering a row highlights its control. Finish is
available only on the last control; Turn is available anywhere. Each row also has:

- a **direction button** showing the direction that control leaves along (or `Dir…` when it has
  none): click it and point the new direction in the viewport, or press Enter to clear it. The final
  reverse into a bay turns about the body's rear, so the bay is re-angled without moving the rear;
- a **speed dropdown**: *Mode*, or a crawl speed slower than the mode (see
  [Per-leg speed](modelling.md#per-leg-speed));
- a **`…` menu** with *Insert control before*, *Insert control after* and *Delete control*. An inserted
  control sits halfway to its neighbour and carries the direction and speed of the leg it splits.

Scripted `-RREditRoad` cannot raise a palette. It turns the control points on and leaves
`RRUpdateRoad` to commit.

The palette and the inspector are modeless, so their buttons stay clickable while another command is
waiting at its prompt. Anything that would write to the document or ask for a pick then says **Finish
the running command first** and does nothing, rather than landing inside that command.

A road drawn with a **user vehicle that is no longer loaded** (its file removed from the user vehicles
folder) still opens: the palette, the labels and the area search say *vehicle X not found — import it
again (RRImportVehicle) or change the road's vehicle*, and **Settings…** (or `RRRoad` on the road) can
choose another vehicle.

Select the control line (or any generated part of the analysis) and run `RRUpdateRoad` to save the
result and rerun the site checks. Update replays the same rewind-aware driving logic with the saved
vehicle, road, clearance, footprint, grade, obstacle and boundary settings; it does not reopen the
setup dialog. The control line keeps its object id and sits outside the generated output group.

Run `RRRoad` against a saved source when you do want to change its setup. The dialog is preloaded from
the source (its Source field is locked) and the accepted settings are used for the rerun. Choose
**Reselect obstacle and boundary curves** when the checks should use a different reference set.

Generated objects are grouped and placed below `RhinoRoad` layers. The source stores a versioned
`RhinoRoad.AccessDefinition`; every related object carries the same stable source id and a role.
Update creates and validates a complete replacement before removing the previous generated set. If a
saved obstacle or boundary has been deleted, Update stops, identifies the missing object and leaves the
previous result in place so it can be repaired through Configure.

### Clear-area search

For a saved journey that **ends reversing into a bay** (any dockable vehicle, not only SVT) the palette
has a **Clear-area search** group, collapsed by default and hidden for roads it does not apply to. It works in four steps:

1. **Site.** **Pick yard…** takes one closed curve around the area the vehicle may use; **Pick
   obstacles…** takes closed curves around buildings and storage (Enter with none selected clears
   them). Both start from the references the road was saved with, and the picked curves are
   highlighted green and red.
2. **Bay.** The palette states where the body's rear stops and at what angle, drawn as a purple docked
   body. **Set bay…** picks the rear, then the reversing direction.
3. **Search.** Choose the control the search may move from (control 1 lets it redesign the whole
   approach; a later one keeps the controls before it as drawn), then **Find less clear area…**. It
   keeps the final position and heading fixed, tries pull-ups and at most one forward correction, and
   ranks passing routes by the clearance footprint's area in the yard. It runs for 30 seconds by
   default, with a 2-minute option.
4. **Suggestions.** Up to three appear once the search finishes, and the best is applied straight away
   as the unsaved draft, so Save keeps it. Choosing another entry switches the draft to it, and
   **Current route (unchanged)** switches back; the viewport shows each suggestion's clearance
   boundary with an orange dot at its tightest location. Drag its controls or change their directions
   to refine it; the palette reports live clear area, bay error and yard/obstacle fit. Save stores the
   picked yard as the allowed area and the picked curves as obstacles; Discard restores the original.

Every step reports what it is waiting for, or why it cannot run. The result is the smallest area found
by this bounded search, not a proof of the global minimum.

## Inspecting an analysis

Select any source, control, track, envelope, footprint or warning marker and run `RRInspectRoad`. One
modeless inspector is kept per document. It shows the overall status, checks, measurements, grouped
problem station ranges, and profiles for steering angle, steering rate, clearance/road margin, grade
and, for articulated vehicles, **fold angle**. There is a dedicated **articulation (fold) check**
(a jackknife is no longer filed under the envelopes),.

The profile sits at the top: hovering it marks that station on the road itself (a point, a leader, and
the station and reading in a label), draws the vehicle pose there and prints the reading under the
graph. Click a failed check or problem area to zoom to it.

**Swept-path scrub.** A slider moves the pose along the whole route, with `◀ 1 m` and `1 m ▶` buttons
(or the arrow keys) to step, and **Play** to animate at 1×, 5× or 20× real time, or at a fixed 2 or
10 m/s. **Show swept trail** draws the footprints swept so far (a bounded subset on a long route).
Clicking the profile moves the scrub to that station.

Display toggles control temporary viewport overlays without changing the baked drawing. The
control-intent overlay labels the initial travel direction, reversals and the Finish control so the
non-positional intent is visible while reviewing. Editing the source or a referenced road/obstacle
marks the inspector **Out of date**; nothing recomputes or rebakes automatically. Press Update when
ready.

## Sizing bends and junctions

- `RRMeasureRoad`: select one or more saved journeys (or a combined footprint), then pick a straight
  section across their clearance footprint. Both endpoints must be beyond the occupied area. Each
  occupied interval gets an ordinary Rhino linear dimension using the document's current dimension
  style. Gaps and islands remain unmeasured. Sections are evaluated in World XY.
- `RRCombineRoads`: select at least two saved journeys to produce their combined clearance footprint,
  including disconnected areas and holes. Use this for the required movements through a T or
  crossroads. It represents separate journeys, not simultaneous passing. Each journey retains its own
  fit result and vehicle validation status.
- Select any part of a sizing result and run `RRInspectRoad` to read its status and individual
  movement results in the command history. `RRUpdateRoad` refreshes the selected sizing result.

Sizing objects live on `RhinoRoad::Sizing` and retain stable journey ids and section geometry. Changed
or missing dependencies mark their names **Out of date** and colour them orange. Updating a journey
also refreshes its linked sizing results when all required journeys are current; if another journey is
stale, update it first. A failed update retains the previous sizing output. Section endpoints can be
grip-edited and refreshed through Update.

The footprint and dimensions describe the space required by the tested journeys. They are not a search
for the globally smallest possible bend or junction and do not propose kerb geometry.

## Comparing and reporting

- **`RRCompareRoads`**: select two saved journeys or combined footprints (A, then B). It prints the
  area of each, the area **only A** needs, **only B** needs and both need, and the net difference
  (B − A), which answers "what does designing for BUS 15 instead of BUS 12 cost?". The regions are
  drawn and hatched on `RhinoRoad::Comparison`; these are static outputs, not tracked or updated.
- **`RRReportRoad`**: select one or more saved roads (or preselect them) and choose an `.html` file.
  It writes a self-contained, print-friendly HTML page (checks, maxima, problem ranges, a plan of the
  envelopes, route and conflict areas, validation status, whether the road is out of date) and a CSV of
  the per-station samples beside it (road name first). A flat route's grade figures are omitted rather
  than shown as zero, and terrain draping is stated when used. `-RRReportRoad` takes the `.html` path
  on the command line (spaces allowed); it defaults to the document's folder, or Documents for an
  unsaved document, and a relative path is taken from there.

## Meaning of the outputs

- **Vehicle access controls:** the sparse editable start/Aim/Finish intent for an interactive run.
- **Rear/front axle tracks:** kinematic reference curves.
- **Towed axle tracks:** the same for each towed unit of an articulated preset, nearest first.
- **Wheel-edge paths:** the outer front and inner rear wheel edges of the lead unit and both edges of
  each towed axle, on the same layer as the axle tracks (switch off in the dialog).
- **Vehicle footprints:** the body outline stamped along the route at the `FootprintInterval` station
  spacing, one outline per rigid unit, so an articulated stamp reads as a tractor and its trailer
  folded against each other; set the interval to 0 to omit them.
- **Body swept envelope:** the theoretical occupied area from the sampled poses, every unit included,
  built as a polygon union. Where a manoeuvre encircles ground it does not cover (a roundabout island,
  say) the envelope carries that as a hole.
- **Clearance envelope / minimum access footprint:** body envelope plus the configured allowance; the
  Vejregler default is 0.30 m.
- **Fixed-width road edges:** asymmetric offsets from the route. A failure means the clearance
  envelope does not fit between them.
- **Warning markers:** one per problem range for steering, steering-rate, articulation (fold), tangent,
  grade, obstacle, boundary or road-fit issues.
- **Conflict hatches, fills and note:** see [Site checks](#site-checks).

A saved road's definition (vehicle, mode, controls, speeds, settings) persists in the Rhino document.
Definitions are **upgraded by schema version** when opened, so roads saved by an older RhinoRoad still
load and keep their original interpretation. A road saved by a **newer** RhinoRoad than the one
installed is refused with *"…uses schema N, saved by a newer RhinoRoad; update the plugin."*
Nothing is rewritten silently.

## Settings and saved roads

The last-used vehicle, mode, clearance and output options are stored in the plugin's settings and
restored next session; a vehicle that no longer exists falls back to a valid one. Settings are shared
across open documents. Each saved road carries its own settings, which are used when it is updated.

## Grasshopper

`RhinoRoad.Grasshopper.gha` ships beside the plugin (category *RhinoRoad*), over the same Core:

- **Vehicle**: looks up a preset (including user vehicles) and returns its name, validation status,
  dimensions, minimum radius, swept width and outline.
- **Analyse Path**: checks a curve as a rear-axle path (optionally reversed) and returns the rear
  path, body and clearance envelopes with holes, towed tracks, maxima, fold angles per joint,
  violations and a feasible flag, and footprints. The curve is sampled by the plugin's own sampler,
  so the checks match `RRRoad` on the same curve.
- **Drive Manoeuvre**: drives a start pose through control points with per-control directions
  (Forward/Reverse text or Boolean), kinds (Aim/Turn/Finish), exit headings, **Speeds** (km/h) and
  **Stops** (true to halt before that leg and turn the wheel at a standstill), with the same outputs.
  Core is Rhino-free, so this opens parametric studies of the same replay the plugin uses.

Every length on the components is in the **model units** of the definition's Rhino document: the
points and curves, `Clearance` and `Interval`, and every output. The plan outputs (envelopes, holes,
towed tracks, footprints) sit at the route's start elevation, as the plugin bakes them, while the Rear
Path keeps its Z. The user vehicles folder is re-read when its files change, so an imported vehicle is
available at the next solve.
