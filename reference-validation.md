# Reference certification status

The MVP presets remain `SourceTranscribed`. The stored Rhino 8.34 run contains all 18 required
PV, REN, and BUS 12 mode A/B cases at 40, 100, and 180 gon, but none currently meets the promotion
policy.

## Stored result

- Passed: **0 of 18**.
- Measured tolerance failures: **11**.
- DWG path-extraction failures with no deviation claimed: **7**.
- Required maximum envelope deviation: **0.10 m**.
- Required maximum inward underprediction: **0.05 m**.

PV produced measurements for all six cases. Its maximum envelope deviations range from 0.254 m to
0.397 m and inward underprediction ranges from 0.203 m to 0.308 m, so PV cannot be labelled
`ReferenceValidated`. REN and BUS 12 also have measured failures, while some of their DWG fans
cannot yet be converted unambiguously into the rear-axle-midpoint input required by RhinoRoad.

The complete per-case result, exact official-source SHA-256 values, Rhino version, sampling counts,
and failure notes are stored in `reference/certification/reference-certification.json`. A null
deviation means the extractor failed its sanity checks; it does not mean zero deviation.

## Repeat the run

1. Build `src/RhinoRoad.Rhino/RhinoRoad.Rhino.csproj` in Release.
2. Load `src/RhinoRoad.Rhino/bin/Release/RhinoRoad.Rhino.rhp` in Rhino 8.
3. Run `RRReferenceCertify` in an empty metric document.
4. Run `dotnet test RhinoRoad.sln -c Release` after the report is updated.

The command imports each official DWG with Rhino's command-line importer, locates its named vehicle
fan, identifies red swept-envelope and yellow wheel-trace polylines, samples at 0.025 m, runs the
same rigid-body envelope builder as `RRVehicleAccess`, and writes the report. Imported document
objects and definitions are removed after each source is processed. The report tests require one
unique record for every vehicle/mode/angle combination and prevent preset status from advancing
beyond the stored evidence.

## Why some cases are extraction failures

The official drawings show wheel-edge traces and body envelopes, not an explicit rear-axle midpoint
curve. For simple fans the harness infers the midpoint path from the inner rear-wheel trace. Several
REN/BUS drawings contain additional or fragmented paths that do not identify that trace reliably.
The harness therefore reports no number when the inferred track width, route length, joined boundary,
or self-intersection checks fail.

The next validation step is to transcribe auditable wheel-centre/axle-track geometry from the official
vehicle diagrams (or obtain publisher-provided rear-axle paths), then rerun the same matrix. The
rectangular MVP body outlines may also need to be replaced by the actual diagram polygons. Tolerances
must not be relaxed to obtain a pass.

The Directorate DWGs under `reference/vejdirektoratet-koerekurver` are authoritative. The legacy
QGIS payload remains useful for cross-checking coverage, but it is not used to promote a preset and
BUS 12 B still lacks its 180-gon legacy record.
