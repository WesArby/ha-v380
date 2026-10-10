"""Config flow for the V380 Pro integration."""

from __future__ import annotations

from typing import Any

import aiohttp
import voluptuous as vol

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.const import CONF_HOST, CONF_NAME, CONF_PASSWORD, CONF_USERNAME
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import V380Api, V380AuthError, V380Error
from .const import (
    CONF_DEVICE_ID,
    CONF_HTTP_PORT,
    CONF_RTSP_PORT,
    DEFAULT_HTTP_PORT,
    DEFAULT_RTSP_PORT,
    DOMAIN,
)


class V380ConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle a config flow for V380 Pro (decoder proxy bridge)."""

    VERSION = 1

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            await self.async_set_unique_id(f"{host}:{user_input.get(CONF_HTTP_PORT, DEFAULT_HTTP_PORT)}")
            self._abort_if_unique_id_configured()

            api = V380Api(
                async_get_clientsession(self.hass),
                host,
                user_input.get(CONF_HTTP_PORT, DEFAULT_HTTP_PORT),
                user_input.get(CONF_RTSP_PORT, DEFAULT_RTSP_PORT),
            )
            try:
                await api.async_get_status()
            except V380AuthError:
                errors["base"] = "invalid_auth"
            except V380Error:
                errors["base"] = "cannot_connect"
            else:
                return self.async_create_entry(
                    title=user_input.get(CONF_NAME) or f"V380 {host}",
                    data=user_input,
                )

        schema = vol.Schema(
            {
                vol.Required(CONF_HOST): str,
                vol.Optional(CONF_NAME, default="V380 Camera"): str,
                vol.Optional(CONF_HTTP_PORT, default=DEFAULT_HTTP_PORT): int,
                vol.Optional(CONF_RTSP_PORT, default=DEFAULT_RTSP_PORT): int,
                vol.Optional(CONF_DEVICE_ID, default=""): str,
                vol.Optional(CONF_USERNAME, default="admin"): str,
                vol.Optional(CONF_PASSWORD, default=""): str,
            }
        )
        return self.async_show_form(step_id="user", data_schema=schema, errors=errors)
