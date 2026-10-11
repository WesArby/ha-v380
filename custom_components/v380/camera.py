"""Camera platform for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.camera import Camera
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_NAME
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import V380Api
from .const import DOMAIN


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    api: V380Api = hass.data[DOMAIN][entry.entry_id]["api"]
    async_add_entities([V380Camera(api, entry)])


class V380Camera(Camera):
    """Live H.265 camera via the V380 decoder proxy RTSP feed."""

    _attr_has_entity_name = True
    _attr_name = None

    def __init__(self, api: V380Api, entry: ConfigEntry) -> None:
        super().__init__()
        self._api = api
        self._attr_unique_id = f"{entry.entry_id}_live"
        self._attr_device_info = DeviceInfo(
            identifiers={(DOMAIN, entry.entry_id)},
            name=entry.data.get(CONF_NAME, "V380 Camera"),
            manufacturer="V380 (via decoder proxy)",
            model="V380 Pro dual-lens",
        )

    @property
    def stream_source(self) -> str:
        return self._api.rtsp_url

    @property
    def use_stream_for_stills(self) -> bool:
        return False

    async def async_camera_image(
        self, width: int | None = None, height: int | None = None
    ) -> bytes | None:
        try:
            return await self._api.async_get_snapshot()
        except Exception:  # never break the frontend on a missed frame
            return None
