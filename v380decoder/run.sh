#!/bin/bash
# V380 Decoder add-on entrypoint. Reads Supervisor options and execs the proxy.
set -e
OPT=/data/options.json

ID="$(jq -r '.device_id' "$OPT")"
USER="$(jq -r '.username' "$OPT")"
PASS="$(jq -r '.password' "$OPT")"
IP="$(jq -r '.camera_ip' "$OPT")"
PORT="$(jq -r '.camera_port' "$OPT")"
QUALITY="$(jq -r '.quality' "$OPT")"

ARGS=(--id "$ID" --username "$USER" --password "$PASS" --ip "$IP" --port "$PORT" --quality "$QUALITY")
[ "$(jq -r '.enable_api' "$OPT")" = "true" ] && ARGS+=(--enable-api)
[ "$(jq -r '.enable_onvif' "$OPT")" = "true" ] && ARGS+=(--enable-onvif)

echo "Starting V380Decoder for device $ID at $IP:$PORT ($QUALITY)..."
exec /app/V380Decoder "${ARGS[@]}"
