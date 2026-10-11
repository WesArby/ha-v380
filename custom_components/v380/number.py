"""Number platform (volume, PTZ speed) for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity, DataUpdateCoordinator

from .api import V380Api
from .const import DOMAIN


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    data = hass.data[DOMAIN][entry.entry_id]
    api: V380Api = data["api"]
    coordinator: DataUpdateCoordinator[dict] = data["coordinator"]
    async_add_entities(
        [
            V380Number(api, coordinator, entry, "speaker_vol", 0, 100),
            V380Number(api, coordinator, entry, "ptz_h", 1, 9),
            V380Number(api, coordinator, entry, "ptz_v", 1, 9),
        ]
    )


class V380Number(CoordinatorEntity, NumberEntity):
    """XParam-backed number; state comes from the polled status."""

    _attr_has_entity_name = True
    _attr_entity_category = EntityCategory.CONFIG
    _attr_mode = NumberMode.SLIDER

    def __init__(
        self, api: V380Api, coordinator: DataUpdateCoordinator[dict],
        entry: ConfigEntry, key: str, minimum: float, maximum: float,
    ) -> None:
        super().__init__(coordinator)
        self._api = api
        self._key = key
        self._attr_unique_id = f"{entry.entry_id}_{key}"
        self._attr_translation_key = key
        self._attr_native_min_value = minimum
        self._attr_native_max_value = maximum
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})

    @property
    def native_value(self) -> float | None:
        v = (self.coordinator.data or {}).get(self._key, -1)
        return None if v is None or v < 0 else float(v)

    async def async_set_native_value(self, value: float) -> None:
        await self._api.async_set_config({self._key: str(int(value))})
        await self.coordinator.async_request_refresh()
