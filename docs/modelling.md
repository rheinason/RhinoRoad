# Modelling notes

How RhinoRoad turns a designer's clicks into a drivable path, and the physical reasoning behind each
choice. For the commands that drive all this see [user-guide.md](user-guide.md); for what was
tried and rejected before the current route model see [path-model-notes.md](path-model-notes.md); for
how far each result can be trusted see [validation.md](validation.md).

## Contents

- [The binding constraint: how fast the wheel moves](#the-binding-constraint-how-fast-the-wheel-moves)
- [Per-leg speed](#per-leg-speed)
- [Aiming, and rewinding to ease the exit](#aiming-and-rewinding-to-ease-the-exit)
- [Squaring the exit and saying exactly which way to leave](#squaring-the-exit-and-saying-exactly-which-way-to-leave)
- [Locked turns](#locked-turns)
- [Turning the wheel at a standstill](#turning-the-wheel-at-a-standstill)
- [Articulated vehicles](#articulated-vehicles)
- [Fold limit](#fold-limit)
- [Reversing](#reversing)
- [Transitions on a drawn alignment](#transitions-on-a-drawn-alignment)
- [Kinks and grade](#kinks-and-grade)
- [Mirrors, protrusions and wheel-edge paths](#mirrors-protrusions-and-wheel-edge-paths)
- [Terrain draping](#terrain-draping)

## The binding constraint: how fast the wheel moves

The limit on these vehicles is not the turning radius but how fast the steering wheel moves. The
steering rate is a rate per second and the leg generators spend it per metre, so moving the wheel
always costs distance: **12.5 m from centre to full lock in mode A (15 km/h) and 4.17 m in mode B
(5 km/h)**, the same for every preset, because the rate and the lock scale together. Any model that
treats curvature as something the vehicle simply *has*, rather than something it takes metres to
acquire, describes a vehicle that does not exist.

The 6.0 s lock-to-lock behind those figures is a RhinoRoad assumption: the handbook publishes no
steering rate for any preset (see [validation.md](validation.md#what-is-assumed-rather-than-published)).

## Per-leg speed

Each control can carry its own speed, so a reverse or a shuffle can be driven at walking pace while
the rest of the journey keeps the mode's speed. A slower leg turns the wheel in *less distance*
(only the slew per metre changes; the lock stays the mode's), which gives the honest form of what
might otherwise have been called a "mode C": the same lock at a slower speed, with shorter
transitions, and no vehicle parameters invented.

- The interactive **Speed** option sets the speed for the legs that follow, until it is changed
  again: `ModeSpeed`, `Walk` (5 km/h), `Crawl` (3), `Inch` (1), or a typed km/h.
- The **Edit Road** palette has a speed dropdown on each control row: *Mode*, or one of the crawl
  speeds slower than the mode, plus whatever the control already holds.
- A Grasshopper `Drive Manoeuvre` takes a **Speeds** list (one per control; a short list repeats its
  last value, missing means the mode's speed).
- **A speed above the mode's is clamped to the mode's.** The modes are set out for their speed, and a
  faster leg would turn the wheel in less distance than the mode allows, which understates the swept
  path. The command line says so when it happens.
- A speed of `null` (every road saved before this existed) means the mode's speed — except on a
  reverse, below.
- **Reversing is a slow manoeuvre.** A reverse leg with no speed of its own is driven at the vehicle's
  *manoeuvring speed*: the speed of its mode B (5 km/h for every preset), or for a vehicle without one
  the lower of its mode's speed and 5 km/h, whatever mode the journey is in. The mode's lock is kept,
  so a mode-A reverse keeps mode A's smaller lock (the conservative side) and only moves the wheel in
  less distance: 4.2 m from centre to lock instead of 12.5 m. The source's only wording for reversing
  sits in køremåde B — *"på vendepladser fremføres det dimensionsgivende køretøj med meget lav
  hastighed og i visse tilfælde ved bakkemanøvrer"*. A reverse's own speed still wins (clamped to the
  mode's), and the samples carry the speed, so the analyzer prices the reverse's steering rate at it.
  This is decided in one place (`ManoeuvreReplayService.LegSpeedKilometresPerHour`), so the pick loop,
  every docking solver, the planner, the area search and replay agree.

## Aiming, and rewinding to ease the exit

Each click solves the arc from the rear axle through the picked point, and the vehicle drives that
arc under the mode's lock and slew rate. Each click does two jobs: it says where to go next, and
**the direction towards it says which way the vehicle should be travelling when it leaves the corner
it is in**. That is what lets the exit be driven properly instead of corrected afterwards.

An arc through a picked point ends the vehicle turned by *twice* the bearing of that point, so on its
own every corner overshoots and the next leg has to correct back — the unwanted S through a corner
exit. But a driver does not correct at the exit; they ease the wheel open through the second half of
the bend. Easing has to *begin* before the point you want to be straight at, and by then the leg has
already been driven past it. So RhinoRoad **rewinds**: it gives back the tail of the corner,
restarts the ease from where the wheel should have begun coming back, and drives the exit as one
continuous opening turn. The part being given back is drawn as a faint dotted line while you aim, so
the reshaping is visible as it happens.

Rewinding never reaches back past the start of the leg in hand, or through a change of travel
direction, so an earlier decision cannot be undone behind your back. Nor does it reach into a
**planned reverse**: the last control of an accepted `PlanReverse` proposal is saved as *sealing* its
leg (`ManoeuvreControl.SealsLeg`), and replay honours the seal exactly as the pick loop does, so the
control after a planned dock neither reshapes it while you drive nor when the road is updated. It also only happens when the
next point genuinely asks for less turn than the wheel is holding: halfway round a bend the right
answer is to keep turning, not to straighten and turn back in.

Aiming works on points **ahead of the vehicle's beam**. A point behind the beam is brought round to
the beam — same distance, the hardest turn aiming can ask for — and the command says so, because the
arc that would really reach such a point is a loop the size of whatever circle passes through the
cursor: a REN in mode B would drive 10.7 km to reach a point 60 m astern. Reversing is what puts you
there without noticing, since it turns "behind the direction of travel" into the ground in front of
the nose. To turn further than the beam allows, hold `Ctrl` (a locked turn); to go backwards use
`Reverse`.

Clicking legs is for authoring a manoeuvre where no line exists yet. To check a road that has already
been designed, select its centreline and use `ExistingCurve`: the checks then report against the line
as drawn instead of against a route improvised towards it.

## Squaring the exit and saying exactly which way to leave

**Shift** squares the leg up. Rhino's own ortho is switched off inside the command, on purpose: ortho
constrains the direction to the picked *point*, and the vehicle leaves on *twice* that bearing, so an
ortho band pointing north would leave the vehicle heading east. Shift constrains where the leg **ends
up pointing** instead, to a multiple of your ortho angle measured from the construction plane's X
axis, so a site drawn on a rotated CPlane squares up to the site. It composes: every leg leaves on an
axis, so a route drawn with Shift held stays on the axes it started on however many corners it takes.
Object snap still applies and still sets how far away you clicked.

A squared leg *arrives* on its axis rather than merely aiming at it. The direction is stored on the
control, so the leg eases onto it and then runs straight to the click, landing within a thousandth
of a degree. It takes however much room the turn needs, which may be more than you clicked for: a BUS
12 in mode A squared to a 90 degree departure needs about 30 m, and simply aiming at the same snapped
point would have reached only 55 degrees of it in 15 m.

Some exits are not on an ortho step — a skewed arm, a bay set at whatever angle the building is — and
those need the direction said exactly. The **Direction** option arms the next click, and the pick has
two stages: the click places the point, and the pick continues with that point fixed while the cursor
**swings the direction about it**. It is swung about the point just placed rather than pointed from
the vehicle, because the exit direction is a property of where the leg *ends*. The whole leg is
replanned and drawn on every move of that swing (planned off the drawing thread, the latest request
winning), so the direction is chosen against what it actually does. Rhino's ortho and object snaps
constrain the swing as they would drawing a line; an angle can be typed (measured from the
construction plane), and Esc abandons the click rather than committing a leg whose direction was
never settled.

**Finish** is the same idea at the end of a journey: pick the direction to end up travelling in and
the vehicle eases onto exactly that heading with the wheel centred. A Finish *ends the journey*;
nothing can be added after it.

## Locked turns

Ordinary aiming will turn the vehicle right round — click near abeam and it makes a U-turn — but only
on whatever radius the click implies: a PV in mode A clicked abeam at 40 m turns 175 degrees on a
20 m radius, against a minimum of 4.82 m. The arc through a nearer point has to reach full lock out of
its own length, and at minimum radius there is not enough of it. Aiming can turn the vehicle round;
only lock can turn it round *tightly*.

A locked turn (`Turn`, or hold `Ctrl`) asks for an *amount of heading* instead. The wheel goes to its
lock and stays there until the direction of travel has swung by twice the bearing of the cursor, so
clicking abeam is a true 180-degree U-turn and clicking behind the vehicle asks for 270. How far
away you click makes no difference. Typing a number sweeps exactly that many degrees (`180` for the
turning check), going the way the cursor last pointed. The leg ends with the wheel still at lock,
because straightening is the next control's job: the control after it opens the turn out from inside,
as for any other corner. Locked turns arrive exactly, because they drive their sweep rather than
aiming at it. The two dotted circles drawn while aiming are the tightest the rear axle can trace,
left and right.

## Turning the wheel at a standstill

Standing still, moving the wheel costs no distance. **A change of direction is a standstill and is
treated as one automatically**, since the vehicle has to stop to make it. That single change is what
makes three-point turns work: each leg of a shuffle begins at the lock it was always meant to be
driven on, instead of spending its whole length getting there. Every preset now needs less width to
shuffle round than to turn round:

| | U-turn | Shuffle | Saved |
| --- | --- | --- | --- |
| PV, mode B | 7.76 m | 5.87 m | 1.89 m |
| REN, mode A | 16.87 m | 12.85 m | 4.01 m |
| BUS 12, mode A | 21.66 m | 16.40 m | 5.26 m |
| BUS 15, mode A | 26.07 m | 19.68 m | 6.39 m |

Away from a direction change the stop has to be asked for: the **Stop** option arms the next click,
and it is spent on that click rather than latched, because a stop happens once. In the Edit Road
palette each control row has a **Stop first** toggle for the same thing (set and greyed where the
row changes direction, which is a stop already).

The planner does not propose stops of its own. Splitting a missed reverse at a third, half or two
thirds of its length into a reverse to that pose and a second reverse from a standstill was measured
on 440 planner layouts (every preset and mode, bays 0.5 to 3 scales back and 0.3 to 1.5 across,
plus perpendicular bays): it reached none of the 10 bays the ordinary candidates miss, so it was not
shipped. Inventing stops that
did not happen shrinks the swept envelope, and an envelope smaller than the vehicle really needs is
the unsafe direction to be wrong in, so it is never assumed.

A sample that begins from a standstill records the fact, and the steering-rate check (and the
inspector's steering-rate profile) skips it: a wheel turned while stopped covers no distance, and
measuring that movement against the distance to the next sample reads as an infinite steering rate.

**This is not a third køremåde.** The source publishes two, and the only wording it has for tight
turning areas sits inside køremåde B — *"på vendepladser fremføres det dimensionsgivende køretøj med
meget lav hastighed og i visse tilfælde ved bakkemanøvrer"*. A mode C would mean inventing a speed, a
lock and a clearance for every preset with nothing to check them against, sitting in the same table
as two modes that *are* corroborated against the official curves. Turning the wheel at a standstill
is a technique, not a vehicle property: you do it while driving køremåde B. Per-leg speed is the
supported way to go slower within a mode.

## Articulated vehicles

`SVT` (sættevogntog, 16,5 m), `PVT` (påhængsvogntog, 18,75 m), `SK` (specialkøretøj, 22 m) and `MVT`
(modulvogntog, 25,18 m) are modelled as a lead unit towing a chain of units, each with a hitch
offset from its tower's axle and its own wheelbase behind that hitch. A positive offset is a fifth
wheel ahead of the drive axle; a negative one is a drawbar eye hanging behind it, and the two behave
differently enough that the sign is part of the preset.

- SVT and SK are one joint (SK's trailer is 14.0 m from kingpin to axle group, a 22 m combination on
  a fixed rear axle).
- PVT is two joints: its published plan view runs the drawbar from a coupling 0,15 m inside the
  lorry's rear face to the trailer's *front* axle, and that axle is the turntable the trailer body
  pivots on, so the drawbar and the body swing separately. The drawbar carries no body outline and
  sweeps nothing.
- MVT is two joints too: a tractor, a link semitrailer on a fifth wheel, and a semitrailer on the
  link's second fifth wheel (a B-double). Its unit split is taken from the official MVT DWG.

Each unit's heading is integrated along the route rather than derived from the current sample, which
is what makes the history matter: a towed unit is placed from where the vehicle has been rather than
from where it is, so a trailer stays folded into the straight that follows a corner. Everything
downstream reads `VehiclePose.OccupiedOutlinesWorldMetres` rather than the lead body alone, so
envelopes, clearance, containment and footprint stamps all cover every trailer.

At full lock a combination may have no steady state at all: the tractor holds the lock but the
trailer folds without ever settling. `TurningGeometryCalculator` reports that as
`SteadyStateAttainable = false` alongside the smallest rear-axle radius the combination can hold,
and the dialog says so instead of quoting the width of a jackknife.

Articulated presets are only partly corroborated; see [validation.md](validation.md).

## Fold limit

The source publishes no per-vehicle fold limit, and it cannot be recovered from the presets: a
semitrailer's body legitimately overlaps its tractor's in plan, because it sits on top of it, so the
angle at which they would really collide is not a plan-geometry question. One limit is therefore
applied per towed unit, and **one number governs everything**: the reverse generator stops at it,
the planner and the area search reject a candidate that reaches it, and the analyzer reports a route
that passes it, so a route one of them refuses cannot be baked as feasible by another.

- **Default 65° per towed unit** (`TowedUnitDefinition.MaximumArticulationDegrees`). It is a
  RhinoRoad assumption, not a published figure, and the violation message says so. It can be set per
  unit in a vehicle file (at most 90°).
- **Reaching the limit counts.** A fold exactly at the limit is reaching it everywhere — the
  generators stop at it, `FoldLimit` and the analyzer both compare with `>=` — so a route sitting on
  the limit is never baked as feasible.
- **90° is the jackknife floor** (`VehicleAccessAnalyzer.JackknifeFoldRadians`). At a right angle the
  towed unit is no longer following the one towing it at all, so footprints past it are of a
  configuration no driver reaches. That floor applies whatever the per-unit limit says.
- A fold past the unit's limit is an `ArticulationAngle` **violation** (previously only the 90° floor
  was rejected). The maximum fold at each joint is also reported in the command summary and on
  `VehicleAccessResult`, shown as a fold-angle profile and check in the inspector.

## Reversing

### Reversing steers the trailer, not the tractor

A reversing combination is unstable by nature: the fold grows rather than settles, with an
e-folding distance of about one trailer wheelbase. Aimed the way a forward leg is aimed — tractor's
rear axle at the point you click — SVT passed a right angle after 13 m on a 15 m radius and PVT in
half that, which is what a driver would manage if they never countersteered.

So reversing an articulated vehicle, the point you click means **where the trailer should end up**,
and `TrailerReverseGenerator` drives the tractor to put it there. For a unit hitched at its tower's
axle, its curvature is `tan(fold) / wheelbase`: the fold *is* its steering angle, so the chain is a
stack of bicycles each steered by the joint ahead of it. Pure pursuit on the trailer gives a
curvature, that curvature asks for a fold, driving the fold asks for a curvature of the unit towing
it, and the question is put again one joint further in until it comes out as a wheel angle and goes
through the ordinary rate-limited step. Lock, slew rate and the kinematics stay in one place.

Measured for point aiming over eight targets from 18 to 40 m back and up to 14 m across (before the
simulation planner below), SVT reaches all eight within 0.1 m and never folds past 36°.

A reverse the combination cannot make **stops where it stops being drivable** rather than folding on
through it, which is what a driver does: stop, pull forward, start the reverse again on a better
line. The leg you keep is the drivable part, and the viewport says the trailer could not be put
there from here.

### Docking: an exit direction squares the arrival

Position and heading cannot both be had by aiming at a point — pursuit closes on a point from
whatever direction it happens to approach from, which is how a trailer ends up across the face of a
dock. So when a reversing leg carries an exit direction, what gets followed is the **approach
line** — the line through the point along that direction — and the point becomes the place along it
to stop. Two errors describe the body against that line, how far off it sits and how far off it
points; running the arc length backwards flips the sign of both, which is why the forward law
diverges here and the signs are derived rather than reused.

Cross-track error commands an approach *angle* rather than a curvature directly. Commanding
curvature is only valid near the line — eight metres off it asks for a radius no combination can
hold — and the bounded form is what lets the approach start from well off the line. Squaring up costs
distance: roughly the first fifteen metres go on establishing the fold and the last on taking it out
again. With about 50 m of run-in or more, SVT arrives within 0.25 m and under a degree off square.

**Every vehicle can be docked.** All the docking paths go through one seam (`ReverseDocking`), so a
vehicle gains docking, a fully honoured `PlanReverse` and the clear-area search together by gaining a
solver there:

| Vehicle | Docking solver |
| --- | --- |
| **Rigid** (PV, REN, LV 12, BUS 12/13,7/15) | `RigidReverseGenerator`: the rear axle follows the approach line to the bay. A rigid vehicle reversing has no lateral slip at its rear axle, so its midpoint is the stable reference, and the wheel sets curvature directly instead of through a fold. |
| **One joint** (SVT, SK) | The feedback cascade (`TrailerReverseGenerator`), with the trailer axle on the approach line. |
| **Two joints** (PVT, MVT) | `MultiJointReverseDocking`: the leg is *planned by simulation*. |

For a rigid vehicle the whole story is the wheel's own lag: it slews at a rate per second, so per
metre it moves one radian in `speed / slew rate` — 6.6 m for a car in mode B, 24 m in mode A — which
is as long as the manoeuvre itself. Commanding a curvature as if it arrived at once had the car
weaving into the bay, so curvature is a third state, driven towards what the heading loop asks for
at its own rate, and the three loops are placed together (a triple pole, critically damped as a
chain) so the vehicle settles rather than weaves. Rigid vehicles approach at up to 70° to the line,
steeper than the trailer's 50°, because a rigid body straightens within about one turning radius.

Two joints defeat the single-joint cascade: the heading loop has to be slower than the folds it
commands, and the folds cannot be driven fast enough to leave room for it, so every pairing of the
two gains measured on PVT either crawled, converged and then wandered off, or ran into the fold
stop. It is the second joint that does it rather than the drawbar it hangs from. So the outer loop is
replaced by a plan: the curvature asked of the last unit is a piecewise-linear profile over its own
travel, started from a cubic Hermite curve between the trailer's pose and the dock's, and corrected by
Levenberg–Marquardt on the arrival errors with a barrier on fold. The barrier is set against the
tightest unit's own limit — it starts 15° short of it and steepens 5° short, where a plan also stops
counting as converged — which is the 50° and 60° it was fixed at for the default 65°. For copies of
PVT and MVT limited to 45°, fixed barriers let the search fold into the stop (PVT mode B, bays 3–6
scales behind a turn-away: 24/32 docked, mean miss 0.42 m); derived ones keep it clear (27/32, mean
miss 0.07 m). Every step goes through the same
rate-limited step the analyzer and replay use, so the trailer the planner steers is the trailer
everything downstream sees (reversing a trailer, two integrations that differ in the last bit part
company exponentially). The search is fixed — iteration cap, knot count, rollout budget — and
consults no clock, so a saved journey replays sample for sample. Every solver and the planner take a
cancellation token, which can only stop them: the pick loop runs `PlanReverse` in the background with
Esc to cancel, and the area search stops inside a plan rather than after the grid point in hand. The simulation planner also catches
a semitrailer where the cascade misses: in mode A the wheel takes 12.5 m to reach its lock, the
cascade's fold loop closes over about three, and a plan that knows the lag in advance docks where
feedback chasing it folds into the stop. **Articulated vehicles therefore dock in mode A as well as
mode B.** With reverses at the 5 km/h manoeuvring speed the fallback is still needed in mode A: the
wheel catches up, but mode A's lock does not — over sixteen turn-away bays SVT's cascade alone docked
none in mode A at either speed (six in mode B), and with the fallback 14 of 16 dock at 5 km/h against
12 at 15.

**The "heading not honoured" case.** A chain of three joints or more has no solver: it is aimed at
the point instead, the leg is flagged, and the viewport says the heading was not honoured rather
than presenting a miss as a near dock. None of the presets is that long, so this only arises for a
user-defined vehicle with three or more towed units. Likewise a docking journey that cannot reach
the bay says so rather than claiming a dock it did not make.

One limit remains: a locked turn in reverse is still open-loop, so it will fold. It is flagged
rather than prevented.

### PlanReverse and the clear-area search for every preset

`ReversePlanner` is offered for every vehicle. It compares reversing immediately with forward
pull-ups at several distances and side offsets, and, if the best first reverse does not arrive
within 0.25 m and 2° of the bay, one forward correction and a second reverse. It then proposes the
most accurate drivable approach it finds. The pull-up distances scale with the vehicle (the larger of
its wheelbase, its rear-axle radius at full lock and, for a combination, the tightest radius it can
hold continuously): a car needs a few metres, a semitrailer a trailer's length. The search is
bounded by a count of candidates, never by time, so the same request produces the same proposal on
every machine. For a nearby bay it also tries stronger approach corrections and keeps the best
drivable arrival; a parallel approach followed by a turn away and a reverse into a bay 4 m to the
side finishes within 0.2 m and a degree of square in the regression case. The accepted proposal
becomes ordinary forward and reverse controls, so it can be replayed and edited.

The **clear-area search** (`ManoeuvringAreaSearch`) keeps the final body position and heading fixed,
tries pull-ups and at most one forward correction, and ranks passing routes by the clearance
footprint's area inside the yard. Each candidate is planned from replay's own leg boundary
(`ReplayedManoeuvre.LegStartIndex`), so the turn it moves can ease the leg before it exactly as replay
of the suggestion will. It needs a vehicle that can be docked, because a search that
relaxed the heading would compare journeys that end in different bays. It is gated on capability
(`ReverseCapabilities`), not on a preset id, so it serves every preset and any user vehicle that can
be docked. Its result is the smallest area found by a bounded search, not a proof of the global
minimum.

## Transitions on a drawn alignment

An alignment drawn as arcs joined straight onto tangents asks the steering wheel to change angle
instantly at every join, which no vehicle can do. Rather than report that as an anonymous violation
at each join, a selected curve is broken into its constant-curvature stretches and each is priced:

```
    transition length = speed x |atan(wheelbase / R2) - atan(wheelbase / R1)| / slew rate
```

Every stretch must be long enough for the transitions at both its ends, because the wheel has to
arrive at that curvature and then leave it. A short tangent between two bends is the usual place this
fails, since it has to hold the run-out of one and the run-in of the next. The report names the
stretches that are too short, with how much length they have and how much they need. The three ways
out are to lengthen the stretch, ease the radius either side of it, or use a slower driving mode (or
a slower leg): the wheel moves at the same rate either way, but a slower vehicle covers less ground
while it does. In practice a 25 m radius needs 2.6 m of transition for PV, 4.4 m for REN and 5.7 m for
BUS 12 in mode A, and under 1.3 m for any of them in mode B.

A curvature jump that breaks the steering-rate limit is explained as such in the violation text.

The tool deliberately does not smooth a selected line for you; what happened when it tried, with
measurements, is in [path-model-notes.md](path-model-notes.md).

## Kinks and grade

- **Kinks.** A selected curve's tangent breaks are found exactly (`Curve` discontinuities, down to
  half a degree) rather than by comparing neighbouring samples, so a **kink of under 10° is now
  flagged** where it used to pass as continuous. Curvature at a kink is not defined, so it is
  reported as a break in its own right. A polyline therefore reports tangent discontinuities at every
  corner, which is the honest answer: no vehicle turns a right angle. Draw the centreline smooth
  (`InterpCrv`, or `Fillet` at a radius the vehicle can hold), or drive it interactively.
- **Grade.** A route is sampled about every 0.10 m, so a millimetre of Z noise reads as 1 %. Grade
  is therefore **judged over a moving window** — the larger of 5 m and the vehicle's wheelbase — and
  the limit applies to that, and the road report states the windowed maximum. The point-to-point
  maximum is still shown in the command summary and the inspector. The window **restarts at every
  change of direction and every standstill**: at a cusp the vehicle drives back over the ground it has
  just climbed, and a window spanning it measures the net rise of up-and-back (3 m up a 10 % ramp and
  3 m back read as 2 %). A leg shorter than the window, like a route shorter than it, is judged end to
  end. An interactive route is flat (it carries its start Z), and for a flat route grade figures are
  hidden rather than shown as zero.

## Mirrors, protrusions and wheel-edge paths

- **Mirror allowance (optional).** The body outlines exclude mirrors. The handbook gives their width
  as up to **0.30 m** (p.37; also p.44) but not their position or height. The option
  *Include mirror allowance* widens the lead unit's front 1.0 m by 0.30 m on each side. The width is
  published; the **1.0 m position is a RhinoRoad assumption**, chosen so the allowance covers any
  plausible mirror near the cab. The vehicle itself is unchanged; the allowance is applied to a copy
  for the run, and the baked note and report state that it was on.
- **Protrusions.** A vehicle file can list protrusions (mirrors, steps, lamps) per unit, each a
  closed polygon in the unit's frame with optional height band. The plan sweep treats every
  protrusion as occupying its outline whatever its height; the height band is recorded for a later
  vertical check and is not yet a filter.
- **Wheel-edge paths (default on).** The outer edge of the front wheels and the inner edge of the
  rear wheels of the lead unit, and both edges of each towed unit's axle, are baked as curves on the
  `WheelTracks` layer: what kerb-overrun and bollard checks are made against. The offsets are the axle
  track plus or minus the tyre width over two. Front wheels are placed across the vehicle axis, not
  rotated with the steering, so the edge is at most a few centimetres off in a lock turn.

## Terrain draping

An interactive route is flat by construction. With *Drape onto terrain* ticked, the route's
elevations are taken from a selected surface, polysurface, SubD or mesh by projecting each sample
vertically onto it, so a driven route gets a real grade check. First runs and every Update drape the
same way, so a saved result matches what the command reported. A station that misses the terrain
between two that hit — a hole in the mesh, a gap between two surfaces — takes the height interpolated
by station between them, so the grade across the hole is the grade between its edges (holding the last
height instead turned a 10 m hole in a 5 % slope into a level shelf and a half-metre step). Misses
before the first hit or after the last, where there is nothing to interpolate towards, hold the
nearest hit's height, so a short gap at the edge of a surface reads as level; the number of misses is
reported. If every station missed, the route is left unchanged. The terrain
is saved with the road, and the plan-view geometry is unaffected (plan geometry is evaluated in World
XY; crossfall, vertical clearance and 3D road surfaces are out of scope).
