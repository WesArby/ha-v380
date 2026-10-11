"""Constants for the V380 Pro integration."""

DOMAIN = "v380"

CONF_HTTP_PORT = "http_port"
CONF_RTSP_PORT = "rtsp_port"
CONF_DEVICE_ID = "device_id"

DEFAULT_HTTP_PORT = 8080
DEFAULT_RTSP_PORT = 8554

PLATFORMS = ["camera", "select", "button", "sensor", "switch", "number"]

API_STATUS = "/api/status"
API_SNAPSHOT = "/snapshot"
API_PTZ = "/api/ptz/{direction}"
API_LIGHT = "/api/light/{mode}"
API_ALARM = "/api/alarm/{action}"
API_CONFIG = "/api/config"

LIGHT_MODES = ["on", "off", "auto"]
PTZ_DIRECTIONS = ["up", "down", "left", "right"]
