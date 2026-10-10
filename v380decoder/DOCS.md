# V380 Decoder add-on

Runs the V380 LAN-protocol proxy as a Home Assistant OS add-on. One
add-on instance fronts **one** camera (the camera takes a single `8800`
feed). For more cameras, duplicate this add-on directory with a
different `slug` (e.g. `v380decoder2`) and configure each for its camera.

## Install

0. Copy your **patched** decoder source (per-type reassembly,
   decrypt-from-16, wall-clock RTP, H265 passthrough, AAC audio, codec
   stickiness) into `v380decoder/decoder-src/` so the layout is:
   `v380decoder/{config.yaml,Dockerfile,run.sh,DOCS.md,decoder-src/{*.csproj,src/…}}`.
1. Copy this whole repo to GitHub (or any git host reachable by HA).
2. HA → Settings → Add-ons → Add-on Store → ⋮ → Repositories → add your repo URL.
3. Install **V380 Decoder**, open the **Configuration** tab:
   - `device_id`: numeric camera ID (sticker / V380 app / `--discover`)
   - `username` / `password`: camera credentials (`admin` + device password)
   - `camera_ip` / `camera_port`: usually `<camera-ip>` / `8800`
   - `quality`: `hd` (stacked dual composite) or `sd`
4. Start. Watch the Log tab for `[AUTH] success` + `[STREAM] login OK`.
5. In VLC: `rtsp://<ha-ip>:8554/live`. Web UI: `http://<ha-ip>:8080`.

## Wiring to the V380 integration

Add the **V380 Pro** custom integration (this repo's `custom_components`)
with host = your HA LAN IP, HTTP port `8080`, RTSP port `8554`. The
integration's camera/select/button entities then drive the add-on's
endpoints. The core **ONVIF** integration can additionally target
`http://<ha-ip>:8080/onvif/device_service`.

## Notes

- Keep the V380 phone app's live view closed while the proxy runs.
- Frame rate is firmware-fixed (~12fps HD on tested hardware); use `sd`
  for a lighter stream.
- Camera power-cycles / lockouts: the proxy retries; after a camera
  reboot give it ~60s before judging the log.
