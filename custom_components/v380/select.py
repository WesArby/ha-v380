"""Select platform (floodlight mode) for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.select import SelectEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .api import V380Api
from .const import DOMAIN, LIGHT_MODES


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    api: V380Api = hass.data[DOMAIN][entry.entry_id]
    async_add_entities(
        [
            V380LightMode(api, entry),
            V380ConfigSelect(api, entry, "pir", "PIR sensitivity"),
            V380ConfigSelect(api, entry, "lamplight", "Lamplight sensitivity"),
        ]
    )


class V380LightMode(SelectEntity):
    """Floodlight mode: on / off / auto (optimistic)."""

    _attr_has_entity_name = True
    _attr_translation_key = "light_mode"
    _attr_options = LIGHT_MODES
    _attr_current_option = "auto"

    def __init__(self, api: V380Api, entry: ConfigEntry) -> None:
        self._api = api
        self._attr_unique_id = f"{entry.entry_id}_light_mode"
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    async def async_select_option(self, option: str) -> None:
        await self._api.async_set_light(option)
        self._attr_current_option = option
        self.async_write_ha_state()


class V380ConfigSelect(SelectEntity):
    """XParam-backed Low/Medium/High select (pir, lamplight)."""

    _attr_has_entity_name = True
    _attr_entity_category = EntityCategory.CONFIG
    _attr_options = ["low", "medium", "high"]

    def __init__(self, api: V380Api, entry: ConfigEntry, key: str, _name: str) -> None:
        self._api = api
        self._key = key
        self._attr_unique_id = f"{entry.entry_id}_{key}"
        self._attr_translation_key = key
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    async def async_select_option(self, option: str) -> None:
        await self._api.async_set_config({self._key: option})
        self._attr_current_option = option
        self.async_write_ha_state()
