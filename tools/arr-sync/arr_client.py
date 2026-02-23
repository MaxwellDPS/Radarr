"""Unified API client for Radarr and Sonarr (/api/v3/)."""

import requests


class ArrClient:
    """Client for Radarr/Sonarr REST API v3.

    Both services share the same API shape for download clients, root folders,
    quality profiles, custom formats, naming, and media management.
    """

    def __init__(self, base_url: str, api_key: str, timeout: int = 30):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.timeout = timeout
        self.session = requests.Session()
        self.session.headers.update({
            "X-Api-Key": api_key,
            "Content-Type": "application/json",
        })

    def _url(self, path: str) -> str:
        return f"{self.base_url}/api/v3/{path.lstrip('/')}"

    def _get(self, path: str) -> dict | list:
        resp = self.session.get(self._url(path), timeout=self.timeout)
        resp.raise_for_status()
        return resp.json()

    def _post(self, path: str, data: dict) -> dict:
        resp = self.session.post(self._url(path), json=data, timeout=self.timeout)
        resp.raise_for_status()
        return resp.json()

    def _put(self, path: str, data: dict) -> dict | list:
        resp = self.session.put(self._url(path), json=data, timeout=self.timeout)
        resp.raise_for_status()
        return resp.json()

    # -- Status -----------------------------------------------------------

    def get_status(self) -> dict:
        return self._get("system/status")

    # -- Download clients -------------------------------------------------

    def get_download_clients(self) -> list[dict]:
        return self._get("downloadclient")

    def create_download_client(self, config: dict) -> dict:
        return self._post("downloadclient", config)

    def update_download_client(self, client_id: int, config: dict) -> dict:
        config["id"] = client_id
        return self._put(f"downloadclient/{client_id}", config)

    # -- Root folders -----------------------------------------------------

    def get_root_folders(self) -> list[dict]:
        return self._get("rootfolder")

    def create_root_folder(self, path: str) -> dict:
        return self._post("rootfolder", {"path": path})

    # -- Quality profiles -------------------------------------------------

    def get_quality_profiles(self) -> list[dict]:
        return self._get("qualityprofile")

    def create_quality_profile(self, profile: dict) -> dict:
        return self._post("qualityprofile", profile)

    def update_quality_profile(self, profile_id: int, profile: dict) -> dict:
        profile["id"] = profile_id
        return self._put(f"qualityprofile/{profile_id}", profile)

    # -- Custom formats ---------------------------------------------------

    def get_custom_formats(self) -> list[dict]:
        return self._get("customformat")

    def create_custom_format(self, cf: dict) -> dict:
        return self._post("customformat", cf)

    def update_custom_format(self, cf_id: int, cf: dict) -> dict:
        cf["id"] = cf_id
        return self._put(f"customformat/{cf_id}", cf)

    # -- Quality definitions ----------------------------------------------

    def get_quality_definitions(self) -> list[dict]:
        return self._get("qualitydefinition")

    def update_quality_definitions(self, defs: list[dict]) -> list[dict]:
        return self._put("qualitydefinition/update", defs)

    # -- Naming config ----------------------------------------------------

    def get_naming_config(self) -> dict:
        return self._get("config/naming")

    def update_naming_config(self, config: dict) -> dict:
        return self._put("config/naming", config)

    # -- Media management -------------------------------------------------

    def get_media_management(self) -> dict:
        return self._get("config/mediamanagement")

    def update_media_management(self, config: dict) -> dict:
        return self._put("config/mediamanagement", config)
