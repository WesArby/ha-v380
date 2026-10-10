"""Switch platform (motion alarm arm/disarm) for the V380 Pro integration.

The decoder exposes POST /api/alarm/on|off (one-key alarm, cmd 144 /
DPID path depending on firmware). Optimistic: assumed state flips on
tap and reverts if the call fails.
"""

from __future__ import annotations

import aiohttp

from homeassistant.components.switch import SwitchEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import V380Api
from .const import DOMAIN


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    api: V380Api = hass.data[DOMAIN][entry.entry_id]
    async_add_entities([V380AlarmSwitch(api, entry)])


class V380AlarmSwitch(SwitchEntity):
    """Motion alarm master switch (optimistic)."""

    _attr_has_entity_name = True
    _attr_translation_key = "alarm"
    _attr_is_on = False

    def __init__(self, api: V380Api, entry: ConfigEntry) -> None:
        self._api = api
        self._attr_unique_id = f"{entry.entry_id}_alarm"
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    async def async_turn_on(self, **kwargs) -> None:
        await self._api.async_set_alarm(True)
        self._attr_is_on = True
        self.async_write_ha_state()

    async def async_turn_off(self, **kwargs) -> None:
        await self._api.async_set_alarm(False)
        self._attr_is_on = False
        self.async_write_ha_state()
