"""Button platform (PTZ nudges) for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.button import ButtonEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import V380Api
from .const import DOMAIN, PTZ_DIRECTIONS


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    api: V380Api = hass.data[DOMAIN][entry.entry_id]["api"]
    async_add_entities(
        [V380PtzButton(api, entry, direction) for direction in PTZ_DIRECTIONS]
        + [V380SyncClock(api, entry)]
    )


class V380PtzButton(ButtonEntity):
    """Momentary PTZ nudge button."""

    _attr_has_entity_name = True

    def __init__(self, api: V380Api, entry: ConfigEntry, direction: str) -> None:
        self._api = api
        self._direction = direction
        self._attr_unique_id = f"{entry.entry_id}_ptz_{direction}"
        self._attr_translation_key = f"ptz_{direction}"
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    async def async_press(self) -> None:
        await self._api.async_ptz(self._direction)


class V380SyncClock(ButtonEntity):
    """Sync the camera clock to now (preserves its timezone)."""

    _attr_has_entity_name = True

    def __init__(self, api: V380Api, entry: ConfigEntry) -> None:
        self._api = api
        self._attr_unique_id = f"{entry.entry_id}_sync_clock"
        self._attr_translation_key = "sync_clock"
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    async def async_press(self) -> None:
        await self._api.async_sync_time()
