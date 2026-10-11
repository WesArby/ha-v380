"""Async client for the V380 decoder proxy (RTSP/ONVIF/API bridge).

The proxy (camera-v380decoder) speaks the proprietary V380 LAN protocol
to the camera and re-exposes it as RTSP + ONVIF + HTTP snapshot + REST.
This client only talks to the proxy's HTTP surface.
"""

from __future__ import annotations

import asyncio

import aiohttp

from .const import API_ALARM, API_CONFIG, API_LIGHT, API_PTZ, API_SNAPSHOT, API_STATUS


class V380Error(Exception):
    """Base error for V380 proxy communication."""


class V380AuthError(V380Error):
    """Proxy rejected the credentials / device pairing."""


class V380Api:
    """Minimal async wrapper around the decoder proxy HTTP API."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        host: str,
        http_port: int = 8080,
        rtsp_port: int = 8554,
    ) -> None:
        self._session = session
        self.host = host
        self.http_port = http_port
        self.rtsp_port = rtsp_port

    @property
    def base_url(self) -> str:
        return f"http://{self.host}:{self.http_port}"

    @property
    def rtsp_url(self) -> str:
        return f"rtsp://{self.host}:{self.rtsp_port}/live"

    @property
    def snapshot_url(self) -> str:
        return f"{self.base_url}{API_SNAPSHOT}"

    async def async_get_status(self) -> dict:
        """Return decoder status; raises V380Error when unreachable."""
        try:
            async with self._session.get(
                f"{self.base_url}{API_STATUS}",
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                if resp.status in (401, 403):
                    raise V380AuthError(f"HTTP {resp.status}")
                resp.raise_for_status()
                data = await resp.json(content_type=None)
                return data if isinstance(data, dict) else {"raw": data}
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"Cannot reach V380 proxy at {self.base_url}: {err}") from err

    async def async_get_snapshot(self) -> bytes:
        """Fetch the latest JPEG snapshot."""
        try:
            async with self._session.get(
                self.snapshot_url, timeout=aiohttp.ClientTimeout(total=15)
            ) as resp:
                resp.raise_for_status()
                return await resp.read()
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"Snapshot failed: {err}") from err

    async def async_ptz(self, direction: str) -> None:
        """Nudge PTZ; direction in up/down/left/right/stop."""
        try:
            async with self._session.post(
                f"{self.base_url}{API_PTZ.format(direction=direction)}",
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                resp.raise_for_status()
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"PTZ {direction} failed: {err}") from err

    async def async_set_light(self, mode: str) -> None:
        """Set floodlight mode; mode in on/off/auto."""
        try:
            async with self._session.post(
                f"{self.base_url}{API_LIGHT.format(mode=mode)}",
                timeout=aiohttp.ClientTimeout(total=10),
            ) as resp:
                resp.raise_for_status()
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"Light {mode} failed: {err}") from err

    async def async_set_alarm(self, armed: bool) -> None:
        """Arm/disarm the motion alarm master switch."""
        action = "on" if armed else "off"
        try:
            async with self._session.post(
                f"{self.base_url}{API_ALARM.format(action=action)}",
                timeout=aiohttp.ClientTimeout(total=15),
            ) as resp:
                resp.raise_for_status()
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"Alarm {action} failed: {err}") from err

    async def async_set_config(self, values: dict) -> dict:
        """Write batch-1 settings; keys: speaker_vol, pir, lamplight,
        ptz_h, ptz_v. Returns the applied mapping."""
        try:
            async with self._session.post(
                f"{self.base_url}{API_CONFIG}",
                json=values,
                timeout=aiohttp.ClientTimeout(total=20),
            ) as resp:
                resp.raise_for_status()
                data = await resp.json(content_type=None)
                return data if isinstance(data, dict) else {}
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"Config {values} failed: {err}") from err

    async def async_sync_time(self) -> str:
        """Sync the camera clock to now; returns the device time set."""
        try:
            async with self._session.post(
                f"{self.base_url}{API_TIME_SYNC}",
                timeout=aiohttp.ClientTimeout(total=20),
            ) as resp:
                resp.raise_for_status()
                data = await resp.json(content_type=None)
                return str((data or {}).get("device_time", ""))
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise V380Error(f"Time sync failed: {err}") from err
