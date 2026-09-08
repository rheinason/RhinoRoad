# RhinoRoad MVP — Architect-Friendly Vehicle Access

RhinoRoad is a Rhino 8 vehicle-access screening plugin for architects and early-stage site design.
It is deliberately narrower than a general road-design or Civil 3D replacement.

> Drive a complete vehicle journey, rigid or articulated, check it against the site, and measure the
> space required through consecutive bends and junctions.

## Implemented MVP workflow

The `Road` command supports:

- an existing open or closed 3D Rhino curve, interpreted as the rear-axle midpoint path;
- an interactive, rate-limited forward/reverse leg authoring mode with straightening, where each leg
  starts from the vehicle's actual state and is therefore drivable by construction;
- Danish PV, REN, LV 12, BUS 12, BUS 13,7, BUS 15, SVT and PVT source-transcribed vehicle
  definitions, the last two articulated;
- Vejregler driving modes A and B;
- wheel-angle, steering-rate, tangent-discontinuity, and optional grade-limit checks;
- swept body and 0.30 m default clearance envelopes;
- planar obstacle and allowed-area checks;
- minimum access footprints and asymmetric fixed-width road edges;
- transient preview, static grouped output, layers, metadata, and explicit replacement.

Interactive driving is the default, with live body and clearance sweeps. Saved journeys support
position and driving-intent edits. Section measurements and combined journey footprints support
bend and junction sizing without generating road or kerb designs. Linked sizing results become stale
when their inputs change and refresh through explicit Update. See README.md for the commands.

Plan geometry is evaluated in World XY. A selected path retains its Z values for longitudinal grade
reporting, but crossfall, terrain draping, vertical clearance, and 3D road surfaces are not part of
this MVP.

## Architecture

- `RhinoRoad.Core`: SI-unit vehicle definitions, kinematic analysis, rate-limited trajectory generation,
  geometry primitives, and legacy Vej/QGIS reference-template loading.
- `RhinoRoad.Rhino`: Rhino curve sampling, Boolean swept envelopes, offsets, feasibility checks,
  interactive input, preview, layers, grouping, metadata, and the `RRReferenceCertify` DWG harness.
- `RhinoRoad.Core.Tests`: deterministic geometry, vehicle, steering, grade, reverse, reference-asset,
  and stored-certification report tests.

The core contains no Rhino dependency. Rhino model units are converted at the adapter boundary.
Generated Rhino objects remain ordinary curves, points, dimensions, and text dots.

## Vehicle-source and validation policy

Dimensions and driving-mode limits come from *Grundlag for udformning af trafikarealer* and are
stored with source, date, figure, version, validation status, and notes. Existing AutoTURN `.veh`
payloads are not reverse-engineered.

The legacy QGIS-derived templates from `Previous Attempts/VehicleTurn_VD_v12.py` are loadable by
the test suite. They cover PV and REN modes A/B through 180 gon, and BUS 12 mode A through 180 gon.
BUS 12 mode B stops at 160 gon and its 2.6138 m encoded source width differs from the handbook's
2.55 m body width. The current stored official-DWG run passes all six PV cases; PV is
`ReferenceValidated`. REN and BUS12 remain `SourceTranscribed`. No result is presented as certified
AutoTURN output. See `reference-validation.md` and `reference/certification/reference-certification.json`.

## Deferred

- articulated vehicles, trailers, rear-steered vehicles, bicycles;
- persistent custom road objects or automatic history updates;
- terrain, crossfall, cut/fill, kerbs, markings, and highway intersection design;
- automatic route finding, layout resizing, and simultaneous vehicle passing;
- Yak/public distribution and any certification claim.
