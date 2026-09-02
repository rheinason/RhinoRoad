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

`Straighten` originally took a *distance* — run the wheel out over this far — which left the
finishing direction to fall out of the arithmetic, so it overshot in its own right. It now takes the
**direction to end up travelling in**, which is solvable exactly because the heading still to come
from unwinding the wheel has a closed form:

```
    Δψ = direction · (speed / (wheelbase · slewRate)) · −ln(cos δ)
```

Hold the lock until that equals the turn still required, then unwind: the vehicle arrives on the
chosen heading with the wheel centred, and cannot overshoot. Where the wheel is already turned
further than the chosen direction needs, the same comparison calls for counter-steer through centre
first. Measured over 50 combinations of starting wheel angle and target direction across both modes,
forwards and reversing, every leg lands on its direction to within 0.01° with the wheel centred.

The one subtlety is that the moment to stop turning in almost never falls on a step boundary;
stopping at the next one overshoots by a whole step's worth of turn, which at full lock is most of a
degree. The step that crosses that moment has its length bisected so the turn ends exactly on it.

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

## Rules of thumb this leaves behind

1. **Model the wheel, not the curve.** Anything expressed as geometry alone will ask for curvature
   the vehicle cannot acquire in the distance available.
2. **Position is not a specification.** Asking a vehicle to reach a point says nothing about which
   way it faces there, and no solver fixes an under-specified request.
3. **Author from the vehicle's state, analyse against the drawing.** Authoring wants every step
   feasible by construction. Analysis wants the drawn line held fixed so the gap can be reported.
   The two want opposite things, and one mechanism cannot serve both.
