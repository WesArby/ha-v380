"""Select platform for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.select import SelectEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity, DataUpdateCoordinator

from .api import V380Api
from .const import DOMAIN, LIGHT_MODES

LEVEL_TO_LABEL = {1: "low", 2: "medium", 3: "high"}
LABEL_TO_LEVEL = {"low": 1, "medium": 2, "high": 3}


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    data = hass.data[DOMAIN][entry.entry_id]
    api: V380Api = data["api"]
    coordinator: DataUpdateCoordinator[dict] = data["coordinator"]
    async_add_entities(
        [
            V380LightMode(api, entry),
            V380LevelSelect(api, coordinator, entry, "pir", "PIR sensitivity"),
            V380LevelSelect(api, coordinator, entry, "lamplight", "Lamplight sensitivity"),
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


class V380LevelSelect(CoordinatorEntity, SelectEntity):
    """XParam-backed Low/Medium/High select (pir, lamplight)."""

    _attr_has_entity_name = True
    _attr_entity_category = EntityCategory.CONFIG
    _attr_options = ["low", "medium", "high"]

    def __init__(
        self, api: V380Api, coordinator: DataUpdateCoordinator[dict],
        entry: ConfigEntry, key: str, _name: str,
    ) -> None:
        super().__init__(coordinator)
        self._api = api
        self._key = key
        self._attr_unique_id = f"{entry.entry_id}_{key}"
        self._attr_translation_key = key
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    @property
    def current_option(self) -> str | None:
        v = (self.coordinator.data or {}).get(self._key, -1)
        return LEVEL_TO_LABEL.get(v)

    async def async_select_option(self, option: str) -> None:
        await self._api.async_set_config({self._key: option})
        await self.coordinator.async_request_refresh()
