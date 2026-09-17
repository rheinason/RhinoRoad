# Live testing RhinoRoad in Rhino 8

Use a disposable Rhino session for release checks. Build before starting Rhino: a running instance holds the `.rhp` and adjacent `RhinoRoad.Core.dll` open, so a later build can fail or leave you testing an older binary.

## Start a clean session

1. From the repository root, run `dotnet build RhinoRoad.sln -c Debug` and `dotnet test RhinoRoad.sln -c Debug`.
2. Start the Rhino-MCP live client from `TopoTest/tools/rhino-live-client.py` in a shell that can launch Rhino. Send `{"tool":"spawn_slot","args":{"version":"8"}}` and record the returned slot ID, PID, and `adopted` flag. Use this slot ID on every later call. Never use the user's existing Rhino window as a test session.
3. Use `run_csharp` to confirm the slot responds: `Console.WriteLine(__rhino_doc__.Objects.Count);`.
4. Load the exact Debug plugin at `src/RhinoRoad.Rhino/bin/Debug/RhinoRoad.Rhino.rhp` with `Rhino.PlugIns.PlugIn.LoadPlugIn`. Check the loaded plugin's assembly location. In a fresh slot, load its adjacent `RhinoRoad.Core.dll` with `Assembly.LoadFrom` first if Rhino would otherwise resolve an installed release copy. Do not infer the loaded version from the `LoadPlugIn` success result alone.

The TopoTest guide, `docs/rhino-live-testing.md` in that checkout, describes the router and its fallback client. Paths to TopoTest depend on the developer's checkout; on the current test machine it is under `C:\Users\hbxma\Dropbox\TopoTest`.

## Test the actual interaction

Open `RRRoad` in **interactive** mode so the Road modal appears. Select the vehicle, driving mode, and `Drive the vehicle interactively` in the modal; inspect the displayed vehicle and mode before clicking **Continue**. `-RRRoad` exercises a different command-line settings path and does not verify the modal.

In the Rhino viewport and command prompt, complete each point or option and wait until the next prompt appears before acting again. Rapid input can be processed after a delayed redraw and accidentally add the same point twice. Read command history after each step, and use **Undo** if an extra control was committed. Test the on-screen **Reverse**, **Direction**, **Undo**, and **Finish** options, plus the live sweep preview. Watch for orange steering, reach, or articulation warnings before finishing.

For a repeatable SVT loading-bay check in a metric document:

| Step | UI action or point |
| --- | --- |
| Modal | SVT, driving mode B, interactive source, Forward |
| Place vehicle | Rear axle `0,0`; forward heading through `1,0` |
| Drive parallel | Pick `25,0` |
| Turn away | Pick `35,5` |
| Reverse | Select **Reverse** |
| Set bay direction | Select **Direction**, pick the trailer rear at `6.8,-4`, then point/type `180` degrees (the SVT trailer rear is 3.2 m behind its axle, so the stored axle is `10,-4`) |
| Complete | Inspect the reverse preview and press Enter to finish |

Coordinates may be typed into Rhino's active `GetPoint` prompt for a measured regression; also make at least one physical viewport pick when testing pointer usability and snapping. If automating the desktop, use the computer-use tool's native window actions for modal and pointer interaction. In the current Sky-based setup, send coordinate characters with individual `press_key` calls after focusing Rhino's prompt; `type_text` invokes Rhino Paste and is not a reliable coordinate entry method. The desktop may contain unrelated windows, so check the Rhino window title and prompt before clicking.

## Check the saved result

After finishing, confirm Rhino reports objects created and that the viewport shows the baked path, body sweep, clearance, and wheel tracks. Inspect the source object's `RhinoRoad.AccessDefinition` user string (or use `RRInspectRoad`) and verify the vehicle, mode, and ordered controls. For the SVT example there should be three controls: `(25,0)` Forward, `(35,5)` Forward, and `(10,-4)` Reverse with an exit heading of 180 degrees. Core regression tests in `TrailerReverseTests` also check the trailer's final position, squareness, articulation, and exact replay. A successful bake alone does not establish that the trailer reached the bay.

Close only the owned test slot with `close_slot`. If it times out, inspect the recorded PID and its command line before stopping that exact process. Then quit the client, rebuild once if any source changed during the test, and check `git status` for temporary files. Do not save test documents into the repository unless they are intentional fixtures.
