# Log4YM

**Modern amateur radio logging** — because Log4OM is for Old Men.

> **Alpha Software** — Log4YM is under active development. Features may change and bugs are expected.

<img width="3438" height="1409" alt="image" src="https://github.com/user-attachments/assets/8103d81f-dfac-407f-82ae-b01bd225232f" />


Log4YM is an amateur radio logger for Windows, macOS, and Linux with drag-and-drop panels, real-time DX cluster, interactive maps, AI talk points, and cloud sync via MongoDB.

## What this fork adds

This fork (devildoc5/Log4YM-2-) adds the logging features that are still missing upstream. Uploading to QRZ and LoTW stays manual: you choose what to send.

| Feature | Where to find it |
|---------|------------------|
| **Special-event logbooks**: keep events in their own log, each with its own on-air callsign and TQSL station location | Log Entry panel header (switcher and *New logbook…*), **Settings → Logbooks**, logbook filter in Log History |
| **Worked before**: call, grid and DXCC entity, each shown overall, on this band, on this mode and on both, worked vs confirmed, plus bands worked, last QSO, distance and bearing | Appears under the callsign in the Log Entry panel |
| **WSJT-X / JTDX / MSHV auto-logging**: QSOs you log there are saved here automatically, duplicates skipped | **Settings → WSJT-X** |
| **Upload selected QSOs** to QRZ or LoTW | Tick rows in Log History, then *Upload to QRZ* / *Upload to LOTW* |
| **Download confirmations** from LoTW and QRZ | *LOTW QSLs* and *QRZ QSLs* buttons in Log History; the **QSL** column shows **L** and **Q** status |

### Setting it up

**WSJT-X auto-logging.** In Log4YM, open **Settings → WSJT-X**, turn on *Log QSOs from WSJT-X*, and save. In WSJT-X, open **File → Settings → Reporting** and set *UDP Server* to `127.0.0.1` and *UDP Server port* to `2237`. Then turn on *Prompt me to log QSO* or *Log automatically*. To share the stream with GridTracker or JTAlert, use a multicast address such as `224.0.0.73` in every program, or list them under *Forward to*.

**Special-event logbooks.** Pick *New logbook…* from the switcher on the Log Entry panel, or add logbooks in **Settings → Logbooks**. New QSOs go into the active logbook. QSOs logged before this feature belong to **Main**. A logbook's TQSL station location is used when its QSOs are uploaded to LoTW. The logbook name is saved in exported ADIF (`APP_LOG4YM_LOGBOOK`), so re-importing a file puts each QSO back in its logbook.

**LoTW confirmations.** In **Settings → LOTW**, enter your lotw.arrl.org website username and password. Uploading still goes through TQSL and doesn't use them. *LOTW QSLs* asks only for confirmations since the last download. Shift+click it to download everything again.

**QRZ confirmations.** These need your QRZ logbook API key (**Settings → QRZ**). QRZ only allows downloads with an XML Logbook Data subscription.

**How confirmations are matched.** A downloaded confirmation is matched to a QSO with the same callsign and band, the same mode group (CW, phone or data), and a start time within 30 minutes. This is the same rule LoTW uses. Confirmations that match nothing are listed after the download so you can check them.

### Notes

- Row selection in Log History covers one page (50 QSOs) at a time.
- These features have unit tests but haven't been tested against live LoTW, QRZ or WSJT-X traffic yet. Please report anything odd.

## [Read the Wiki](https://github.com/brianbruff/Log4YM/wiki)

Everything you need — quick start, feature guides, hardware setup, and developer docs — lives in the **[Wiki](https://github.com/brianbruff/Log4YM/wiki)**.

## Download

Grab the latest release from the **[Releases page](https://github.com/brianbruff/Log4YM/releases/latest)**.

> Upstream releases don't include this fork's features. To use them, build from source: see the development commands in [CLAUDE.md](CLAUDE.md), or run `npm run package:linux` in `src/Log4YM.Desktop` for an AppImage.

| Platform | File | Notes |
|----------|------| ------ |
| Windows | `.exe` installer ||
| macOS (Apple Silicon) | `.dmg` (arm64) | see quick start wiki |
| macOS (Intel) | `.dmg` (x64) | see quick start wiki |
| Linux | `.AppImage` ||

### macOS — Removing Gatekeeper Warning

Log4YM is not signed with an Apple Developer certificate, so macOS will block it on first launch. To fix this, open Terminal and run:

```bash
xattr -cr /Applications/Log4YM.app
```

If you still see a security warning, go to **System Settings → Privacy & Security** and click **Open Anyway**.

## License

MIT

---

*73 de EI6LF*
