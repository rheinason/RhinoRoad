# RhinoRoad: Rhino acceptance checklist

The automated Core tests cover the geometry, replay, planners, reports and catalogue rules (see
[docs/developer.md](../docs/developer.md)). Everything below needs a running Rhino 8 session and is
checked by hand, using a disposable session as described in the live-testing section of that guide.
Record pass or fail per release in the table at the end.

Use an empty **metric** document unless a step says otherwise. Build Debug first and load the exact
`RhinoRoad.Rhino.rhp` (check the loaded assembly location). Wait for each prompt before acting again.

## 1. Install and commands

1. Load the plugin. The **RhinoRoad toolbar** appears with Road, Edit, Update, Inspect, Measure, Combine,
   Compare, Report, Import vehicle and Vehicles buttons, and each runs its command.
2. Run `RRVehicles`: ten presets are listed (PV, REN, LV 12, BUS 12, BUS 13,7, BUS 15, SVT, PVT, SK, MVT)
   with their status; only PV is `ReferenceValidated`. The user vehicles folder is printed.
3. Open the Grasshopper components (`Vehicle`, `Analyse Path`, `Drive Manoeuvre`); `Vehicle` for `SK`
   reports its status and a minimum radius.

## 2. Road dialog and settings

1. Run `RRRoad`. Changing mode updates the Clearance field to the mode default (shown as "default
   0.30 m") unless you have edited it. The Source field is locked when a saved road is selected first.
2. Tick options under **Advanced geometry and output**: wheel-edge paths (on), mirror allowance (off),
   terrain draping, grade limit. Continue, finish a road, close Rhino, reopen, run `RRRoad`: the last
   vehicle, mode, clearance and options are restored.

## 3. Interactive driving (REN, mode B)

1. Drive a hairpin, a straight exit, a right-angle turn and a small bend. Check live sweep responsiveness
   (the cursor stays smooth), the retained outline, the dotted reshaped tail, forward/reverse colours,
   and the steering-limit message. Finish and inspect the baked result.
2. `Turn` (or `Ctrl`) and click abeam: a 180° U-turn. Type `180`: exactly 180°. `Shift` squares the leg
   to the ortho angle on a rotated CPlane.
3. `Stop`, `Reverse`, then a forward leg: a three-point turn; check it is narrower than the U-turn.
4. `Speed`: choose `Crawl`, drive a leg, then type `30` while in mode A: the command line says the
   leg is driven at the mode's speed. After choosing a speed the command line reports the travel the wheel needs from centre to lock.
5. **Esc** with legs driven asks *Keep the N legs driven so far?*: Enter keeps them, `Discard` abandons,
   `Continue` carries on. Esc with no legs cancels.
6. `Finish` and a direction: the vehicle ends on that heading, and **no further click is accepted** after
   the Finish leg commits.
7. At *Place vehicle* choose `TangentToCurve` with a drawn curve: the journey starts at the curve's end
   nearer the pick and heads on past it; Esc from the curve pick returns to the start prompt.

## 4. Site checks and live clash preview

1. Tick obstacle curves and allowed-area boundaries. `RRRoad` asks for them **before** driving. Drive
   so a leg crosses an obstacle and leaves the yard: the preview sweep turns **red** where it clashes,
   before the click.
2. Repeat with an open line inside the body sweep, a clearance-only obstruction, an obstacle on a
   circulation island, and a bollard completely inside the body. Confirm the fit wording, conflict
   hatches (area shown), one marker per problem range with its station range, and the inspector checks
   agree; none is reported at station 0.
3. A note sits below the envelope with vehicle, mode, clearance, validation status and date. With the
   mirror allowance on it also states the mirror assumption.

## 5. Reversing into a bay, every vehicle

For each of PV, LV 12, BUS 12, SVT, PVT, SK and MVT, in mode A and mode B:

1. Drive parallel to a bay, turn away, `Reverse`, `Direction`, pick the **rear of the vehicle or trailer**
   (the prompt names the right body) and point the reversing direction. The docked body is drawn as you
   point. Commit: the body finishes within about 0.25 m and 2° of the bay.
2. `PlanReverse` before reversing: pick the bay; the review shows forward blue and reverse purple legs;
   Enter accepts, and the result is ordinary editable controls.
3. SVT repeatable check (mode B): rear axle `0,0` heading `1,0`; picks `25,0`, `35,5`; `Reverse`;
   `Direction` with the trailer rear at `6.8,-4` and direction `180`. Controls saved: `(25,0)` Forward,
   `(35,5)` Forward, `(10,-4)` Reverse with exit heading 180°.
4. Open the saved rigid reversing road created in an older version, if available, and `RRUpdateRoad`:
   it now replays as a docking reverse (see the changelog).
5. Fold: reverse PVT or SVT too steeply; the run stops where it stops being drivable and a fold over 65°
   is reported as a violation (not a silent pass). The inspector shows a fold check and fold profile.

## 6. Editing, undo and the palette

1. `RREditRoad`: drag control points with native drag, Move and Gumball; the path and sweep respond
   before release, the old output is suppressed, the previous path is dotted. During rapid motion the
   last sweep fades and is labelled as the previous position, then catches up. Esc cancels the move.
2. In the palette change the starting heading, starting direction, a control's direction, its Aim/Turn/
   Finish kind and its **speed** (dropdown). Use `…` to **Insert control before/after** and **Delete
   control**. Nothing is committed to the document until **Save**.
3. **Settings…**: change the clearance or vehicle; the status says it is part of the draft; **Discard**
   restores the road with no leftover output from the discarded draft.
4. **Save**, then Ctrl+Z restores the previous result; do the same for **Discard**.
5. Repeat in a millimetre document and with two documents open; close a document during computation. No
   stale worker result may appear after cancellation, configuration changes or closing.
6. For a saved road that ends reversing into a bay: the **Clear-area search** group is present
   (collapsed) for a dockable vehicle and absent for a road that does not end in a bay. Pick yard and
   obstacles, set the bay, search, and apply a suggestion; Save stores yard and obstacles, Discard
   restores the original. (Test with a rigid vehicle as well as SVT.)

## 7. Inspector

1. `RRInspectRoad`: overall status, checks, measurements, grouped problem ranges, profiles (steering,
   steering rate, clearance, grade, and fold angle for an articulated road). Hover the profile: a marker,
   label and the vehicle pose appear on the road.
2. Use the scrub slider, `◀ 1 m` / `1 m ▶`, arrow keys, **Play** at each speed, and **Show swept trail**.
   Click a failed check or problem area to zoom to it.
3. Move a source or referenced obstacle: the inspector reads **Out of date**; nothing recomputes until
   Update.

## 8. Sizing, compare, report

1. Draw a second movement; `RRCombineRoads` keeps islands and disconnected areas. `RRMeasureRoad` across
   a journey and the combined result gives ordinary dimensions with separate intervals and rejects
   clipped endpoints; move the endpoints and Update. Repeat in a millimetre document.
2. `RRCompareRoads` on two journeys (for example BUS 12 and BUS 15): the areas only A, only B, both and the
   net difference are printed and hatched on `RhinoRoad::Comparison`.
3. `RRReportRoad` for one and for two roads: the `.html` opens and reads correctly, the `.csv` has one
   row per station with the road name first, a flat route shows no grade figures, a draped route says so.
4. Move a source or referenced obstacle: linked sizing objects turn orange and read Out of date; Update
   the journey and the linked dimensions refresh; if another journey is stale its dependent output stays
   intact until it is updated.

## 9. Terrain, wheel edges, mirrors

1. Drape onto a terrain surface, polysurface, SubD and mesh in turn: the grade profile appears for a driven
   route; a route partly off the terrain reports the missed stations.
2. Wheel-edge paths bake on `RhinoRoad::WheelTracks` for the lead unit and each towed axle. Turn the mirror
   allowance on: the envelope is wider at the front by about 0.30 m a side; the note says so.

## 10. User vehicles

1. Copy `samples/user-vehicle-example.json` and change it. `RRImportVehicle` accepts it; `RRVehicles` lists
   it as `UserDefined`; it appears in the dialog and the palette and can be driven and docked.
2. Import a file whose id is `PV`, then one with an invalid dimension: each is refused with a reason and
   nothing is added.

## 11. Saved documents and schema

1. Save, close and reopen the document; inspect and update journeys and sizing outputs. Delete a
   required source or reference: updates stop while old output survives. Restore it and update again.
   Locked sizing objects cannot be partially replaced.
2. Reopen an existing version-1 analysis and use ExistingCurve/Configure/Update. Its original rear-axle
   path interpretation and saved settings are retained. A document with a road saved by a newer
   RhinoRoad produces the "saved by a newer RhinoRoad; update the plugin" message and is not altered.

## Results

| Release | Date | Tester | Sections run | Failures / notes |
| --- | --- | --- | --- | --- |
| | | | | |
