"""Sensor platform (device telemetry) for the V380 Pro integration."""

from __future__ import annotations

from homeassistant.components.sensor import SensorEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import DOMAIN

POWER_SOURCES = {0: "unknown", 1: "battery", 2: "mains"}


async def async_setup_entry(
    hass: HomeAssistant, entry: ConfigEntry, async_add_entities: AddEntitiesCallback
) -> None:
    coordinator = hass.data[DOMAIN][entry.entry_id]["coordinator"]
    async_add_entities(
        [
            V380PowerSource(coordinator, entry),
            V380WifiInfo(coordinator, entry),
            V380BatteryInfo(coordinator, entry),
        ]
    )


class _Base(CoordinatorEntity, SensorEntity):
    _attr_has_entity_name = True
    _attr_entity_category = EntityCategory.DIAGNOSTIC

    def __init__(self, coordinator, entry: ConfigEntry, key: str) -> None:
        super().__init__(coordinator)
        self._attr_unique_id = f"{entry.entry_id}_{key}"
        self._attr_device_info = DeviceInfo(identifiers={(DOMAIN, entry.entry_id)})


class V380PowerSource(_Base):
    _attr_translation_key = "power_source"

    def __init__(self, coordinator, entry):
        super().__init__(coordinator, entry, "power")

    @property
    def native_value(self) -> str | None:
        v = (self.coordinator.data or {}).get("power_source", -1)
        return None if v is None or v < 0 else POWER_SOURCES.get(v, f"mode_{v}")


class V380WifiInfo(_Base):
    _attr_translation_key = "wifi_info"

    def __init__(self, coordinator, entry):
        super().__init__(coordinator, entry, "wifi")

    @property
    def native_value(self) -> str | None:
        v = (self.coordinator.data or {}).get("wifi") or ""
        return v if v else None


class V380BatteryInfo(_Base):
    _attr_translation_key = "battery_info"

    def __init__(self, coordinator, entry):
        super().__init__(coordinator, entry, "battery")

    @property
    def native_value(self) -> str | None:
        v = (self.coordinator.data or {}).get("battery") or ""
        return v if v else None
