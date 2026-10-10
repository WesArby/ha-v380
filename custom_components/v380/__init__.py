"""The V380 Pro integration."""

from __future__ import annotations

import aiohttp

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_HOST, CONF_PASSWORD, CONF_USERNAME
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryNotReady
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import V380Api, V380Error
from .const import CONF_HTTP_PORT, CONF_RTSP_PORT, DEFAULT_HTTP_PORT, DEFAULT_RTSP_PORT, DOMAIN, PLATFORMS


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Set up V380 Pro from a config entry."""
    session = async_get_clientsession(hass)
    api = V380Api(
        session,
        entry.data[CONF_HOST],
        entry.data.get(CONF_HTTP_PORT, DEFAULT_HTTP_PORT),
        entry.data.get(CONF_RTSP_PORT, DEFAULT_RTSP_PORT),
    )
    try:
        await api.async_get_status()
    except V380Error as err:
        raise ConfigEntryNotReady(f"V380 proxy not ready: {err}") from err

    hass.data.setdefault(DOMAIN, {})[entry.entry_id] = api
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Unload a config entry."""
    if unload_ok := await hass.config_entries.async_unload_platforms(entry, PLATFORMS):
        hass.data[DOMAIN].pop(entry.entry_id, None)
    return unload_ok


async def async_migrate_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Migrate old entries (currently a no-op, version 1)."""
    return entry.version == 1
