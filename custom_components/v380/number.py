"""Number platform (volume, PTZ speed) for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import V380Api
from .const import DOMAIN


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    api: V380Api = hass.data[DOMAIN][entry.entry_id]
    async_add_entities(
        [
            V380Number(api, entry, "speaker_vol", "Device volume", 0, 100),
            V380Number(api, entry, "ptz_h", "PTZ horizontal speed", 1, 9),
            V380Number(api, entry, "ptz_v", "PTZ vertical speed", 1, 9),
        ]
    )


class V380Number(NumberEntity):
    """XParam-backed number (read-modify-write on the camera)."""

    _attr_has_entity_name = True
    _attr_entity_category = EntityCategory.CONFIG
    _attr_mode = NumberMode.SLIDER

    def __init__(
        self, api: V380Api, entry: ConfigEntry, key: str, name: str,
        minimum: float, maximum: float,
    ) -> None:
        self._api = api
        self._key = key
        self._attr_unique_id = f"{entry.entry_id}_{key}"
        self._attr_translation_key = key
        self._attr_native_min_value = minimum
        self._attr_native_max_value = maximum
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    async def async_set_native_value(self, value: float) -> None:
        await self._api.async_set_config({self._key: str(int(value))})
