# Vehicle access: Rhino acceptance checks

The automated core tests cover the multi-turn REN journey, preview/final sweep agreement, retained
geometry invalidation, disconnected footprints, islands, section intersections, body versus
clearance conflicts, concave site boundaries, and movement-fit wording. Existing replay tests cover
reverse, Finish, and editable control reconciliation.

The following require a running Rhino 8 session. They have **not been verified** in this change:
the isolated in-process Rhino startup attempt failed with COM `E_FAIL` before a document opened.

1. In an empty metric document, run `RRRoad`, select REN mode B, and drive a hairpin,
   straight exit, right-angle turn, and small bend. Check live sweep responsiveness, the retained
   outline, dotted reshaped tail, and steering-limit message. Finish and inspect the baked result.
2. Repeat with a closed site boundary and obstacle curves, including an open line inside the body
   sweep, a clearance-only obstruction, and an obstacle entirely within a circulation island.
   Confirm fit wording, collision locations, and the individual inspector checks agree.
3. Edit source grips, starting heading, starting direction, and a numbered control's direction.
   Exercise Undo and final Aim/Finish. Confirm position edits wait for Update and accepted intent
   edits regenerate the same saved journey without leaving old output behind.
   Use **Edit points** and drag through small adjustments with native dragging, Move, and Gumball.
   Confirm the path/sweep responds before releasing the point, old output is temporarily suppressed,
   and the previous path is dotted. During rapid motion the last completed sweep should fade and
   be labelled as the previous position, then catch up when the cursor stops. Cancel with Esc and
   confirm original output returns. Drop a point and check the preview remains until Update.
   Repeat in a millimetre document and with two documents open; close a document during computation.
   Confirm no stale worker result appears after cancellation, configuration changes, or closing.
4. Draw a second required movement through the site and run `RRCombineRoads`. Confirm the
   union includes both movements and retains islands/disconnected areas. Inspect each movement's
   result and the combined result's separate-movement label.
5. Run `RRMeasureRoad` across one journey, then across the combined result. Check ordinary
   Rhino dimensions, separate occupied intervals, and rejection of clipped section endpoints.
   Move the section endpoints and Update. Repeat in a millimetre document.
6. Move a source or referenced obstacle. Check linked sizing objects turn orange and read Out of
   date. Update the journey and verify linked dimensions/footprints refresh. If another journey is
   stale, confirm its dependent output remains intact until that journey is updated.
7. Save, close, and reopen the document; inspect and update journeys and sizing outputs. Delete a
   required source/reference and confirm updates stop while old output survives. Restore it and
   update again. Check that locked sizing objects cannot be partially replaced.
8. Reopen an existing version-1 analysis and use ExistingCurve/Configure/Update. Confirm its original
   rear-axle-path interpretation and saved settings are retained.
