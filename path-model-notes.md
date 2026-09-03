# How a route gets its shape — what was tried, and what it cost

RhinoRoad has to turn a designer's intent into a path a real vehicle could drive. Four ways of doing
that were built and measured. Three failed, and they failed for reasons worth writing down, because
each looks obviously correct until it meets the numbers. This is the record so none of them gets
rebuilt by accident.

All figures are measured, not estimated, using the shipped presets.

## The constraint everything runs into

The binding limit on these vehicles is not the turning radius. It is how fast the steering wheel
moves.

| | speed | wheel lock | centre → full lock | R min |
| --- | --- | --- | --- | --- |
| PV / REN / BUS12, **mode A** | 15 km/h | 29.7° | 3.0 s = **12.5 m of travel** | 4.8 / 8.0 / 10.5 m |
| PV / REN / BUS12, **mode B** | 5 km/h | 36 / 35 / 45° | 3.0 s = **4.2 m of travel** | 3.8 / 6.5 / 6.0 m |

In mode A no vehicle here can develop full lock in less than 12.5 m of travel. Any model that treats
curvature as something the vehicle simply *has* — rather than something it takes metres to acquire —
is describing a vehicle that does not exist. This single number kills two of the four approaches
below, and it is why the handbook defines `LockToLockSeconds` at all.

## 1. Aiming at a point, one leg per click — **in use**

Each click solves the arc from the rear axle through the picked point, and the vehicle drives that
arc under the mode's lock and slew rate. `Straighten` instead takes the direction the vehicle should
end up travelling in, and lands on it exactly.

**Why it works.** Every leg begins from the state the vehicle is actually in, so the result is
drivable by construction — there is no gap between what is previewed and what is possible, and no
way to ask for something the vehicle cannot do. Reversing is free: flip the travel direction and the
next leg backs up from where the last one stopped, which is exactly a three-point turn.

**Its known flaw.** An arc through a point at bearing *b* ends turned by **2*b*** — an inscribed
angle result, not a bug. So a leg aimed along the line you mean to leave on overshoots it, and needs
correcting back. That is what `Straighten` is for, and it is why aiming and straightening are two
separate modes rather than one.

**How the flaw is handled.** Not by correcting after the fact, which is what puts the S in. The
*next* point picked supplies the direction the vehicle should leave the current corner on, and that
is enough to know when the wheel should have started coming back:

```
    dpsi = direction * (speed / (wheelbase * slewRate)) * -ln(cos delta)
```

is the closed form for the heading still to come while the wheel runs back to centre. Unwinding from
early in a corner finishes on a shallower heading than unwinding from late in it, so there is exactly
one station whose unwind lands on the chosen direction. The route is rewound to it and the exit
re-driven as one continuous ease-out. Nothing counter-steers, and nothing is corrected.

Three details earned their keep:

- **The rewind is bounded** at the start of the leg in hand and at any change of travel direction,
  so easing a corner cannot silently undo a reversing leg on the far side of a cusp.
- **Headings are unwrapped** for the search. A corner can turn past half a circle — PV at full lock
  in mode B turns 377 degrees in 25 m — and comparing wrapped angles puts spurious crossings in.
  The latest crossing is the one taken, giving back as little of the corner as will do.
- **The switch from turning to unwinding is one-way**, and the step that crosses it is bisected.
  Re-deciding every step leaves the command flipping either side of the answer and the wheel never
  settles; stopping at the next step boundary instead overshoots by a whole step's worth of turn,
  most of a degree at full lock.

Measured across 50 combinations of starting wheel angle and target direction, both modes, forwards
and reversing: every leg lands on its direction within 0.01 degrees with the wheel centred.

An earlier attempt had `Straighten` take a *distance* — run the wheel out this far — which left the
finishing direction to fall out of the arithmetic and so overshot in its own right. A control that
exists to cure an overshoot must not have one.

**When the exit is taken.** Only when the next point asks for materially less lock than the wheel
is already holding — below 0.85 of it. Easing always finishes with the wheel centred, so easing
towards a point that still wants most of the current lock straightens the vehicle in the middle of a
bend and then turns it back in. Halfway round a corner the right answer is to keep turning.

Measured against a customer test road: 571 m, four 25 m radius corners, 3 m wide, driven by clicking
along its centreline.

| clicks every 20 m | never ease | ease every click | ease only on a real exit |
| --- | --- | --- | --- |
| PV max deviation | 5.34 m | 2.87 m | **1.31 m** (at 15 m clicks, nothing off the road) |
| REN max deviation | 30.60 m | 26.87 m | **3.36 m** |
| BUS12 max deviation | 122.72 m | 8.20 m | **4.92 m** |

Never easing diverges outright at close click spacing — REN reached 2.5 km off a 571 m road — so the
ease is doing real work; it was simply being applied when the vehicle was still cornering.

**What this method cannot do.** Even at its best it is metres, not centimetres, and only PV stays
inside a 3 m road. Clicking legs has no mechanism that pulls the vehicle back towards an intended
line: each leg starts wherever the last one ended, and the rate limit guarantees the vehicle lags
entering every corner. That is fine for authoring a manoeuvre where no line exists yet, and wrong for
checking a road that has already been designed. For that, select the alignment and let the checks
report against it — see rule 3 at the end.

Measured: PV mode A, target 20 m away.

| bearing clicked | 5° | 15° | 30° | 45° | 60° |
| --- | --- | --- | --- | --- | --- |
| heading on arrival | 10° | 30° | 60° | 90° | 120° |

## 2. Re-aiming every step (pursuit) — **abandoned**

Same steering law, recomputed every 0.10 m instead of driven to the end of its arc, on the theory
that never committing to one curvature would remove the overshoot.

**It does not.** Pursuit converges on a goal point by spiralling onto it, and arrives pointing at the
goal from behind — slightly *worse* than the single arc:

| bearing clicked | 5° | 15° | 30° | 45° | 60° |
| --- | --- | --- | --- | --- | --- |
| single arc | 10° | 30° | 60° | 90° | 120° |
| re-aiming | 10.2° | 31.4° | **65.9°** | **102.3°** | **137.3°** |

Everything else about it was good — arrival within 0.00 mm, exact limit compliance, replay identical
to the recording — which is the trap: the property being chased was the one thing it did not fix.

**The lesson.** Overshoot is not caused by committing to an arc. It is caused by specifying a
*position* and saying nothing about *direction*. No amount of re-solving fixes an under-specified
request.

**Could it work another way?** Only with an arrival heading in the specification, which makes it
approach 4 rather than 2. As a way to follow a line it is hopeless — see 4.

## 3. Bounded-curvature arcs between poses (Dubins) — **abandoned**

Make each waypoint a pose (point *plus* heading) and join consecutive poses with the shortest path
made of minimum-radius arcs and tangents. Solved exactly, all six words, validated over 200,000
random pose pairs: worst position error 6.6 × 10⁻¹⁴ m, worst heading error 2.3 × 10⁻¹³ °. The
geometry was correct.

**It is undrivable.** For a 3 m lane shift over 30 m the solver returns

```
LSR  [ Left 0.488 m,  Straight 29.174 m,  Right 0.488 m ]
```

which asks for full lock within 0.488 m of travel. PV needs 12.5 m. The vehicle does not lag the
path slightly; it never reaches the commanded curvature before the arc is over. Every attempt to
track these paths sat pinned at the maximum slew rate and still missed by metres.

**The lesson.** Curvature-continuous is not the same as drivable. An arc-and-tangent path steps
curvature instantaneously at every join, and instantaneous curvature change is precisely the thing
these vehicles cannot do.

**Could it work another way?** Yes, and this is the most promising unbuilt idea. Replace the
instantaneous curvature steps with **clothoid transitions**, where curvature grows linearly with
distance — which is exactly what a wheel slewing at a constant rate produces. That is the
continuous-curvature Dubins problem (Fraichard & Scheuer). It is real work: the analytic solution is
substantially harder than plain Dubins, and the alternative is to solve the steering profile
numerically as a boundary-value problem — shoot a rate-limited command profile from the start pose
and Newton-iterate until it lands on the target pose, using the plain Dubins solution as the initial
guess. Either would give exact arrival poses that are drivable, which is the one capability the
current model lacks. The plain Dubins solver was deleted, but it is recoverable from git history at
`aad99e9` if that route is taken.

## 4. Following a drawn line — **abandoned, twice**

Treat the drawn curve as intent and steer the vehicle along it: feed-forward the wheel towards the
line's curvature a short distance ahead, correct the rest with heading and cross-track feedback,
report how far the vehicle strays.

**On smooth lines it is excellent.** PV mode A, after a straight lead-in:

| line | max deviation |
| --- | --- |
| straight, 50 m | 0.000 m |
| arc R = 40 m | 0.046 m |
| arc R = 15 m | 0.038 m |

**On anything with corners it is a disaster.** A drawn polyline has infinite curvature at each
vertex, so the feed-forward term saturates the wheel at every vertex and the route weaves across the
line it is meant to follow. Measured with pursuit-style anticipation instead, on raw polylines:

| PV mode A | right-angle corner | a four-corner shape |
| --- | --- | --- |
| best lookahead | 2.5 m off | 3.9 m off |
| typical | 8–11 m off | 14–15 m off |

BUS12 reached 76 m off. A tracking controller cannot anticipate a corner it has not reached, and no
lookahead setting fixes that — short lookaheads overshoot the corner, long ones cut it.

Smoothing the input first does not help either: interpolating a cubic through picked points with
uniform knots **overshoots outward before every corner**, so the vehicle swung wide entering each
turn. Centripetal knots reduce it but the smoothed line is still not the line that was drawn.

**Two implementation bugs found along the way**, both worth remembering because they produced
spectacular symptoms:

- The nearest-point search scanned the whole line ahead, so where a line looped back near itself the
  vehicle's place on it could jump tens of metres in one step. It then steered for a part of the
  route it had not driven to, or concluded it had finished and stopped. The place must only creep
  forward, at a little more than the distance actually travelled.
- Terminating on "the vehicle reached the last point" never fires: the carrot pins at the end and the
  vehicle orbits it forever. Terminate when the *projection* reaches the end.

**Could it work another way?** For a **smooth** drawn alignment, yes — the centimetre figures above
are real, and reporting deviation from an intended centreline is genuinely useful on a tight stretch.
Three things would have to change: feed-forward from curvature *averaged over the preview window*
rather than pointwise, so a vertex cannot spike it; a hard requirement that the input be
curvature-continuous, with polylines rejected or filleted first at a radius the vehicle can drive;
and the deviation reported prominently, because on any line worth checking it is the answer. This is
worth revisiting **only** as an analysis mode for pre-existing alignments — never as the authoring
model, where approach 1 is better on every measure.

## What a selected curve does now

It is sampled directly as the rear-axle path, and the checks report where the vehicle could not
actually follow it. A polyline therefore reports tangent discontinuities at its corners, which is
the honest answer: no vehicle turns a right angle. Draw the centreline smooth — `InterpCrv`, or
`Fillet` at a radius the vehicle can hold — and the report becomes meaningful.

## Telling the designer what to change

Reporting that an alignment is undrivable is only half an answer, and on its own it is a bad one:
the designer has no clothoid tool in Rhino and no way to act on it. The length of transition each
join needs is not a matter of judgement, so the tool computes it:

```
    length = speed x |atan(wheelbase / R2) - atan(wheelbase / R1)| / slewRate
```

On the customer's test road — 25 m radius corners — that is 2.64 m per arc end for PV, 4.37 m for
REN, 5.68 m for BUS 12, all in mode A, and under 1.25 m for any of them in mode B. Only one stretch
on that road fails: the 9.15 m tangent between two of the corners has to hold the run-out of one and
the run-in of the next, needing 11.36 m, and only for BUS 12 at 15 km/h.

That is an answer a designer can act on. It also shows why the check must be per stretch rather than
per join: the join is never the problem, the room either side of it is.

**Still unbuilt:** generating the transitioned alignment. The method is straightforward given the
above — sample the drawn alignment's curvature profile, slew-limit it in both directions so no
change exceeds what the wheel can do over the distance available, and integrate the result back into
a curve. The output is a drivable alignment differing from the drawn one by the classic clothoid
shift, which at these radii is a few centimetres. It is the natural next piece, and it would let the
tool answer "make this drivable" rather than only "this is not".

## Rules of thumb this leaves behind

1. **Model the wheel, not the curve.** Anything expressed as geometry alone will ask for curvature
   the vehicle cannot acquire in the distance available.
2. **Position is not a specification.** Asking a vehicle to reach a point says nothing about which
   way it faces there, and no solver fixes an under-specified request.
3. **Author from the vehicle's state, analyse against the drawing.** Authoring wants every step
   feasible by construction. Analysis wants the drawn line held fixed so the gap can be reported.
   The two want opposite things, and one mechanism cannot serve both.
