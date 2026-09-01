# RhinoRoad

RhinoRoad is an internal Rhino 8 plugin for architect-friendly rigid-vehicle access screening.
It analyzes a rear-axle midpoint route, creates swept and clearance envelopes, checks preliminary
road geometry, and reports steering and grade feasibility.

## Build and install

Requirements:

- Rhino 8.20 or later using the .NET 8 runtime;
- .NET SDK 10.0.301 (the repository is pinned by `global.json`);
- Windows for the current internal MVP.

Build and test:

```powershell
dotnet test RhinoRoad.sln
dotnet build src\RhinoRoad.Rhino\RhinoRoad.Rhino.csproj -c Release
```

The plugin is written to:

```text
src\RhinoRoad.Rhino\bin\Release\RhinoRoad.Rhino.rhp
```

In Rhino, open `Options > Plug-ins > Install`, select the `.rhp`, and restart Rhino if requested.
The adjacent `RhinoRoad.Core.dll` must remain beside the `.rhp`.

For a quick visual check, open `samples\RhinoRoad-vehicle-access.3dm`. To regenerate it from the
installed build, run `RRVehicleAccessSample` in an empty metric document and save the result.

## Reference certification

Run `RRReferenceCertify` in an empty metric document to repeat the official-DWG comparison matrix.
It writes `reference\certification\reference-certification.json`; the checked-in report is covered by
the unit tests. The current run passes 0 of 18 required cases, so all three presets deliberately remain
`SourceTranscribed`. See `reference-validation.md` for the measured ranges, extraction limitations,
and the path to a trustworthy rerun.

## Use `RRVehicleAccess`

1. Run `RRVehicleAccess`.
2. Configure `Source`, `Vehicle`, `Mode`, clearance, road-edge widths, and optional checks.
3. Press Enter.
4. For `ExistingCurve`, select a rear-axle midpoint curve. Its parameter direction is the travel
   direction; choose `Reverse` when the vehicle faces opposite that direction.
5. For `Interactive`, pick the rear-axle start and vehicle heading, then click successive previewed
   legs. Use `Reverse`, `Undo`, and Enter to finish. Requested turns beyond wheel lock are clamped.
6. Optionally select obstacle curves and closed allowed-area boundaries.
7. Review the command-line PASS/FAIL report and viewport preview; Enter bakes the result.

Generated objects are grouped and placed below `RhinoRoad` layers. Metadata records the source
GUID, analysis ID, vehicle/version, validation status, mode, clearance, and fixed widths. Re-running
with `ReplaceExisting=Yes` replaces only RhinoRoad objects linked to the same existing source curve.

## Meaning of the outputs

- **Rear/front axle tracks:** kinematic reference curves.
- **Body swept envelope:** theoretical occupied area from the sampled rigid-body poses.
- **Clearance envelope / minimum access footprint:** body envelope plus the configured allowance;
  the Vejregler default is 0.30 m.
- **Fixed-width road edges:** asymmetric offsets from the route. A failure means the clearance
  envelope does not fit between them.
- **Warning points:** steering, steering-rate, tangent, grade, obstacle, boundary, or road-fit issues.

These outputs are screening geometry, not construction-ready kerb design or certified AutoTURN
results. See `plan.md` and `reference-validation.md` for scope and validation status.
