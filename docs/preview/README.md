# DeckCapture interface preview

This is the selected console-style interface study, with **Console** and **Multiview** alternatives. All activity is simulated. It is a design reference for the eventual application frontend.

- `index.html` is the browser-openable export. It includes its own display and state bridge and does not require Codex to run. Optional icons and tooltip helpers load from pinned CDN URLs.
- `deckcapture.fragment.html` is the editable interface source. It contains product styling, simulated state, and the interaction logic for both layouts.

## Try the workflow

Channel 2 starts with a prepared 90-minute job. Start it, advance simulated time, and stop/save it to add a delivery package. Use **Edit job details** to change metadata, duration, and destination.

**Settings → Recording fields** controls individual metadata fields. **Settings → Detection** contains the default-off experimental end-of-tape switch. **Transfers** demonstrates the time window, rate limits, and overrides. The simulation controls change the selected input or advance time.

Preview settings are independent for each layout. Browser storage availability determines whether changes persist between visits. The configuration export is a preview-specific subset; `../settings.design-example.json` retains the original proposal and `../settings.example.json` shows the current runtime configuration.

## Edit and rebuild

Edit `deckcapture.fragment.html`, then run this from the repository root:

```powershell
python tools/update_preview.py
```

The script updates the embedded fragment inside `index.html` while preserving the standalone wrapper. It uses only the Python standard library. Keep the source boundary comments intact and commit both files together after a change.

The standalone export was created through the visualization export workflow. Host-only design controls are optional; the recording and delivery interface works without a live Codex session. No real credentials, DeckLink SDK binaries, production configuration, or captured media are included.
