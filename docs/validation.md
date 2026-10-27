# Validation

What RhinoRoad has been checked against, what passed, what failed, and what is simply assumed. The
rule throughout: **a number is only reported when the geometry behind it is sound**, a failed
extraction never reports a deviation, and tolerances are never relaxed to obtain a pass.

No result is presented as certified AutoTURN output. The Directorate DWGs under
`reference/vejdirektoratet-koerekurver` are the authority; the legacy QGIS payload in
`Previous Attempts/` is useful for cross-checking but is never used to promote a preset.

## Status in one place

The stored official-DWG run (`reference/certification/reference-certification.json`, Rhino 8.34,
2026-09-02) contains the 18 required PV, REN and BUS 12 cases (mode A and B at 40, 100 and 180 gon).

- **Passed: 6 of 18.**
- **PV is `ReferenceValidated`.** All six PV cases sit inside both tolerances: maximum envelope
  deviation 0.037–0.054 m against the 0.10 m limit, maximum inward underprediction at most 0.045 m
  against the 0.05 m limit.
- **REN and BUS 12 remain `SourceTranscribed`.** None of their 12 cases passes.
- Required maximum envelope deviation: **0.10 m**. Required maximum inward underprediction: **0.05 m**.

Every other preset (LV 12, BUS 13,7, BUS 15, SVT, PVT, SK, MVT) is `SourceTranscribed` and absent from
the certification plan, because the matrix reconstructs a *rigid* body from paired wheel traces and
has no articulated case, and LV 12, BUS 13,7 and BUS 15 have no matrix entry yet. Presets loaded from
the user folder are `UserDefined`, which means not from the Vejregler source and not validated; the
loader forces that status whatever the file says.

The statuses are:

| Status | Meaning |
| --- | --- |
| `ReferenceValidated` | Compared with the official DWGs inside the tolerances above. |
| `SourceTranscribed` | Dimensions and locks typed in from *Grundlag for udformning af trafikarealer*, with source, figure, version and notes recorded; not (fully) compared with a drawn reference. |
| `UserDefined` | Supplied by the user. Nothing about it is vouched for. |

The status travels with the preset: it appears in the dialog, the baked access note, the inspector,
the report and the Grasshopper `Vehicle` component.

## The 12 cases that do not pass

Every failure records why:

| Cases | Cause |
| ----- | ----- |
| 4 | No pair of extracted wheel traces is consistent with the rigid-body chord |
| 3 | Red/yellow chains could not be joined into an envelope and wheel pair |
| 2 | Extracted reference envelope self-intersects |
| 2 | Measured, but outside tolerance |
| 1 | The DWG holds no extractable geometry for the annotated case |

Ten of the twelve are DWG extraction, not vehicle modelling: the route reconstruction itself is
exercised by unit tests and reproduces the official geometry wherever extraction succeeds. A null
deviation in the report means the extractor failed its sanity checks; it does not mean zero
deviation. The report also stores the exact official-source SHA-256 values, Rhino version, sampling
counts and failure notes for each case.

The official drawings show wheel-edge traces and body envelopes, not an explicit rear-axle midpoint
curve. For simple fans the harness infers the midpoint path from the inner rear-wheel trace. Several
REN and BUS drawings contain additional or fragmented paths that do not identify that trace
reliably, so the harness reports no number when the inferred track width, route length, joined
boundary or self-intersection checks fail.

The next validation step is to transcribe auditable wheel-centre/axle-track geometry from the
official vehicle diagrams (or obtain publisher-provided rear-axle paths) and rerun the same matrix.
The rectangular body outlines may also need to be replaced by the actual diagram polygons.

## Running the certification

Run `RRReferenceCertify` (hidden from autocomplete; type the full name) in an **empty metric
document**. The command refuses to run in a non-metric document and builds its swept envelopes at a
fixed geometry tolerance rather than the document's, so verdicts do not depend on document setup;
the report records that context under `environment`.

1. Build `src/RhinoRoad.Rhino/RhinoRoad.Rhino.csproj` in Release.
2. Load `src/RhinoRoad.Rhino/bin/Release/RhinoRoad.Rhino.rhp` in Rhino 8.
3. Run `RRReferenceCertify` in an empty metric document.
4. Run `dotnet test RhinoRoad.sln -c Release` after the report is updated.

The command imports each official DWG with Rhino's command-line importer, locates its named vehicle
fan, identifies red swept-envelope and yellow wheel-trace polylines, samples at 0.025 m, runs the
same rigid-body envelope builder as `RRRoad`, and writes the report. Imported document objects and
definitions are removed after each source is processed. The report tests require one unique record
for every vehicle/mode/angle combination and prevent a preset's status from advancing beyond the
stored evidence.

## Checking presets against the legacy curve library

`VejReferenceTemplateCatalog` parses the official Vejdirektoratet design envelopes embedded in the
legacy plugin — every preset, both modes, 40–180 gon, no Rhino needed. `LegacyCurveCheck` and
`LegacyCurveAgreementTests` use them to check the presets. The legacy templates cover PV and REN
modes A/B through 180 gon and BUS 12 mode A through 180 gon; BUS 12 mode B stops at 160 gon, and its
2.6138 m encoded source width differs from the handbook's 2.55 m body width.

**What is compared, and why it is not the whole envelope.** Reproducing a whole sheet needs the
manoeuvre it was drawn to — where the wheel starts moving and how fast — and that convention is not
published. Reconstructing it as "straight in, full lock, straight out" does not work: measured
against PV, the one preset independently validated against the official DWGs, that reconstruction
disagrees by up to 1.2 m at 100 gon and 7 m at 180 gon. The disagreement is the assumed manoeuvre,
not the preset. A comparison built on it would report every preset as broken, so there isn't one.

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
  report and the preset stay in step. See `ModeASheetsForLargeVehiclesAreDrawnAtThirtyDegrees`. The
  re-run is still outstanding.
- **PV's mode-A sheets are not drawn at a steering lock at all.** They imply about 18°. The source
  runs personbiler through junctions at 20 km/h against 15 for large vehicles, and at that speed the
  curve is set by comfort, not by the lock. See `PvModeASheetsAreNotDrawnAtTheSteeringLock`.

This is also what settled SVT's and PVT's mode-B lock as 38.7° — the exact conversion of the
published 43 gon — rather than the 38 the same table prints beside it: at 38° the prediction misses
the PVT sheet by 0.169 m, at 38.7° by 0.036 m. The same exact-gon convention gives LV 12 39.6°
(44 gon), BUS 13,7 41.4° (46 gon) and BUS 15 53.1° (59 gon). MVT is the exception: its own official
sheet states a 38.0° lock, so 38.0° is used there.

## Articulated presets and what corroborates them

The official-DWG matrix has no articulated case, so SVT, PVT, SK and MVT stay `SourceTranscribed`
and are absent from `certification-plan.json` rather than failing in it. What they are checked
against instead:

- **PVT**: the lorry matches the legacy curve library to 0.036 m (outer radius on the mode-B sheets
  at 140, 160 and 180 gon); the trailer is not corroborated, because the combination is still
  folding at 180 gon, the longest turn the library draws.
- **SVT**: not corroborated against any drawn reference. A semitrailer on this tractor has no steady
  turn at any lock either mode allows, and its two tightest mode-B sheets are structurally broken.
- **SK**: the tractor is partly corroborated by the official SKT B sheet (`SKT_B.dwg`), which holds a
  sustained 8.52 m outer arc about one centre over 4–6 m of its 120 and 140 gon envelopes, against
  8.52 m predicted for the sheet's own 2.50 m-wide vehicle and 8.54 m for this 2.55 m preset at
  45.0°. The semitrailer is not corroborated: at 45.0° the tractor's 4.8 m rear-axle radius is far
  below the 13.99 m a 14.0 m semitrailer on a 0.6 m kingpin needs for a steady state.
- **MVT**: the tractor is corroborated by the radii the official MVT sheet prints (at 30.0° an
  8.28 m front-corner and 6.44 m front-axle radius, at 38.0° 7.12 m and 5.23 m), all of which the
  preset's 3.22 m wheelbase, 1.42 m front overhang and 2.55 m width reproduce to 0.005 m — which
  also confirms the tandem centre as the tractor's effective axle. Neither trailer is corroborated:
  no steady turn exists at either lock.

## What is assumed rather than published

These are stated here so nobody mistakes them for transcribed fact. A research pass read the whole of
*Grundlag for udformning af trafikarealer* (Vejdirektoratet, September 2023, with the corrections of
2024-08-09 and 2025-12-18) for each of them.

- **Steering rate: `lockToLockSeconds` = 6.0 s for every preset is a RhinoRoad assumption.** The
  handbook publishes no steering rate for any vehicle. It sets how many metres of travel the wheel
  needs to reach its lock (12.5 m in mode A and 4.17 m in mode B, from centre), so it governs every
  transition length, standstill saving and steering-rate violation. The same value is
  used for every preset; it is not measured per vehicle.
- **Per-unit fold limit: 65°.** The source publishes none and it cannot be recovered from the
  presets (a semitrailer's body legitimately overlaps its tractor's in plan). 65° is the default
  `MaximumArticulationDegrees` on each towed unit; exceeding it is a violation, and the message says
  the limit is a RhinoRoad assumption. Separately, a fold past 90° is always rejected as a
  jackknife: that is a kinematic floor, not a vehicle property.
- **Mirrors.** The body widths exclude mirrors. The handbook gives mirrors a width of up to 0.30 m
  (p.37, repeated on p.44) but publishes neither their position along the body nor their height. The
  optional mirror allowance therefore uses the published 0.30 m width and a **1.0 m** length behind
  the lead unit's front that is a RhinoRoad assumption, chosen to cover any plausible mirror
  position near the cab.
- **Mode-A 29.7°** where the official sheets draw 30.0°: see above.
- **SK mode A.** The source publishes no SK mode-A curves, and Figures 6.3 and 6.5 exclude SK from the
  15 km/h rows (footnote 1: "Gælder ikke specialkøretøjer, da der ønskes en lav svinghastighed for
  disse"). The preset therefore uses **5 km/h** in mode A, with the general 33 gon rule (29.7°).
- **SK mode B lock.** Figure 6.7 prints "-" for SK with the footnote "Kørekurver findes for hjuldrejning
  11,1/16,7 og 50 gon" and does not say which lock belongs to which variant. **50 gon (45.0°)** is
  assigned to this 22 m vehicle because the official SKT B sheet, whose vehicle is this one, holds a
  sustained 8.52 m outer arc at 120 and 140 gon, which a 4.8 m wheelbase and 1.2 m front overhang
  produce only at that lock (the other two published locks would give about 20 m and 29 m). The 11.1
  and 16.7 gon locks are presumed to belong to the force-steered 26 m and 30 m variants and are not
  offered.
- **Unit splits and tails measured rather than dimensioned.** SVT's tractor rear overhang (1.00 m
  behind the drive axle) is assumed because the plan view draws the combination as one 16.5 m
  envelope. SK's tractor tail (1.705 m behind its axle reference) and trailer nose (1.44 m ahead of
  the kingpin) are measured from the side view of the vector figure in `SKT_B.dwg`, and sit inside
  the trailer footprint or ahead of the kingpin by under 1.5 m. MVT's whole unit split and every
  articulation point are taken from the vehicle definition in Vejdirektoratet's own MVT curve
  sheet (chain 1.42 + 3.19 + 8.48 + 7.78 + 4.31 = 25.18 m), because the published raster figure
  draws the link trailer like a separate dolly and three of its references sit a few centimetres
  apart.
- **Axle track and tyre width** are measured from the DWG vehicle block for PV, REN and BUS 12, and
  from the published plan view at its own scale for the others, with about 0.01 m of measurement
  uncertainty. PV's offsets (0.704 m rear-inner, 0.875 m front-outer) are the validated ones. The
  transcription of the **DWG axle tracks** for the remaining presets is still to do.
- **Front wheels** in the wheel-edge paths are placed across the vehicle axis, not rotated with the
  steering; the edge is at most a few centimetres off in a lock turn.

## Preset transcription notes

The notes below are the ones in `vehicles.json` that change how a result should be read.

- **BUS 13,7 and BUS 15** carry a *medløbsaksel*, a self-steering trailing axle that the published
  curves assume, so it carries no side force: the drive axle is the effective rear-axle reference and
  the distance to the trailing axle is folded into the rear overhang. The legacy curve library
  confirms that choice.
- **SK** is the 22 m variant with a fixed rear axle, the only one the source dimensions. Its
  dimension chain is drawn to a reference point in each axle group (the tractor's tandem axles sit
  0.614 m either side of the 4.8 m point, the trailer's tri-axle 1.2 m either side of the 13.4 m
  point), so each reference is its group's centre, which is also the effective-axle convention the
  SKT B sheet's own vehicle uses. The raster figure on PDF page 44 draws both groups ending at those
  points instead; the model is unaffected because it carries one effective axle per unit. The
  preset body is 2.55 m wide; the SKT B sheet's vehicle is drawn 2.50 m.
- **MVT** is the most space-demanding module combination the source names: a three-axle tractor, a
  link semitrailer whose own two axles carry a second fifth wheel, and a tri-axle semitrailer — a
  B-double, three units and two fifth-wheel joints, **taken from the official AutoTURN DWG
  definition** (`Koerekurve_MVT_vifte_11042019_VD.dwg`). Mode B uses **38.0°**, because Figure 6.7
  prints 43 gon and 38° and the official sheet's 5 km/h run states "STEERING LOCK ANGLE = 38.0 deg."
  PDF page 49 says no mode-B curves were drawn for MVT; the 5 km/h run sits in the mode-A folder of
  the curve download. The sheet's front-wheel radii put the tyre's outer edge flush with the body,
  0.05 m wider than this preset's measured 1.225 m.
- **SVT and PVT** mode B use 38.7° (the exact conversion of 43 gon), see above. PVT's drawbar is
  drawn from a coupling 0.15 m inside the lorry's rear face to the trailer's front axle, which is a
  turntable, so PVT is two joints; the drawbar carries no body outline and sweeps nothing.

## Test fixtures for DWG extraction

DWG extraction is the only per-vehicle-fragile stage of the certification, and it is the one stage
that cannot run outside Rhino. Each extractable case is therefore dumped to
`reference\certification\fixtures\<VEHICLE>_<MODE>_<ANGLE>.json`: the reference envelope, the chosen
wheel traces, and every candidate trace the extractor considered. The fixtures move the output across
the Rhino boundary, so the pure reconstruction can be replayed against real drawing geometry in
milliseconds, diffed in review, and regression-fenced by name. `CertificationFixtureTests` keeps a
ratchet list of the cases that reconstruct today; it must never shrink.

## Remaining validation work

- Transcribe the axle-track geometry from the official diagrams so REN and BUS 12 can pass the
  matrix (the DWG transcription of axle tracks, improvements item T5).
- Re-run the certification with mode A at 30.0° and apply it if PV still passes (T6).
- Corroborate the articulated trailers, which no legacy sheet holds in a steady turn.
- Replace rectangular body outlines with the diagram polygons where the sweep is governed by
  chamfered corners.
