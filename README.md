# V380 Pro for Home Assistant (custom integration)
Bridges a V380 / V380 Pro Wi-Fi camera into Home Assistant through a
running [camera-v380decoder](https://github.com/felipemarques/camera-v380decoder)
proxy. The proxy speaks the proprietary V380 LAN protocol to the camera
and re-exposes it as RTSP + ONVIF + HTTP snapshot + REST — this
integration consumes those endpoints. It exists because locked-down
V380 firmware (e.g. `AppKN_VACL3_*`, `Hw_HsAkQQNC_*`) exposes no native
RTSP/ONVIF, which the core Generic Camera and ONVIF integrations need.

> No native V380 integration exists in HA core or HACS today; the
> community threads all end at generic-camera/ONVIF workarounds that
> fail on exactly this firmware generation.

## What you get

| Entity | Source |
|---|---|
| `camera.*` live H.265 feed | `rtsp://<proxy>:8554/live` via HA `stream` |
| Camera stills | `http://<proxy>:8080/snapshot` |
| `select.*_floodlight_mode` (on/off/auto) | `POST /api/light/{mode}` |
| `button.*_ptz_{up,down,left,right}` | `POST /api/ptz/{direction}` |

Tip: HA's core **ONVIF** integration can *also* point at the proxy's
`http://<proxy>:8080/onvif/device_service` if you want its PTZ service
and entities alongside these.

## Prerequisites

1. A reachable decoder proxy. Minimal run (same LAN as camera):
   ```bash
   dotnet run --project V380Decoder.csproj -c Release -- \
     --id 104796856 --username admin --password 'XXXX' \
     --ip 10.0.0.11 --enable-api --enable-onvif
   ```
   Verify: `http://<proxy>:8080/api/status` → 200, VLC → `rtsp://<proxy>:8554/live`.
2. Home Assistant with `ffmpeg` + `stream` (default on HA OS).

## Install

1. Copy `custom_components/v380/` into `/config/custom_components/v380/`
   (or ship this repo via HACS as a custom repository).
2. Restart Home Assistant.
3. Settings → Devices & Services → Add Integration → **V380 Pro**.
4. Enter proxy host/ports (+ camera ID/user/pass for reference).
5. Add a Picture Glance card; enable WebRTC/`camera_view: live` for low latency.

## HA OS add-on (bundled)

`v380decoder/` in this repo is a ready-to-build Supervisor add-on that
runs the patched proxy (see its `DOCS.md`). Add this repo as a custom
add-on repository, install it, point the integration above at your HA
LAN IP. One instance per camera.

## Notes / limits

- One proxy instance fronts one camera (single `8800` feed per camera).
  Run one proxy per camera, add one integration entry each.
- Floodlight select is optimistic (assumed state).
- Frame rate is firmware-fixed (~12fps HD on tested hardware); for a
  smoother/cheaper feed use the proxy's `--quality sd`.
- Credentials live in the config entry (local-only integration).

## Releasing (version numbers)

Both consumers key off versions, so they move together:

- `custom_components/v380/manifest.json` → `version` (what HACS shows)
- `v380decoder/config.yaml` → `version` (what triggers Supervisor add-on updates)

To cut a release (example `0.3.0`):

```bash
# 1. bump both version fields, commit
# 2. tag + push
git tag -a v0.3.0 -m "Batch 2: ..."
git push origin main --tags
# 3. GitHub → Releases → Draft a new release from the tag
```

HACS picks up the new manifest version; Supervisor offers the add-on
update on next store refresh. Never push a version bump without the
matching code — a Supervisor update prompt with no changes trains
users to ignore updates.
