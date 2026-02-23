"""Prowlarr API client (/api/v1/)."""

import requests


# Default torrent categories for Radarr and Sonarr in Prowlarr
RADARR_SYNC_CATEGORIES = [2000, 2010, 2020, 2030, 2040, 2045, 2050, 2060, 2070, 2080]
SONARR_SYNC_CATEGORIES = [5000, 5010, 5020, 5030, 5040, 5045, 5050, 5060, 5070, 5080]


class ProwlarrClient:
    """Client for Prowlarr REST API v1."""

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
        return f"{self.base_url}/api/v1/{path.lstrip('/')}"

    def _get(self, path: str) -> dict | list:
        resp = self.session.get(self._url(path), timeout=self.timeout)
        resp.raise_for_status()
        return resp.json()

    def _post(self, path: str, data: dict) -> dict:
        resp = self.session.post(self._url(path), json=data, timeout=self.timeout)
        resp.raise_for_status()
        return resp.json()

    def _put(self, path: str, data: dict) -> dict:
        resp = self.session.put(self._url(path), json=data, timeout=self.timeout)
        resp.raise_for_status()
        return resp.json()

    # -- Status -----------------------------------------------------------

    def get_status(self) -> dict:
        return self._get("system/status")

    # -- Applications -----------------------------------------------------

    def get_applications(self) -> list[dict]:
        return self._get("applications")

    def get_application_schema(self) -> list[dict]:
        return self._get("applications/schema")

    def create_application(self, config: dict) -> dict:
        return self._post("applications", config)

    def update_application(self, app_id: int, config: dict) -> dict:
        config["id"] = app_id
        return self._put(f"applications/{app_id}", config)

    # -- Commands ---------------------------------------------------------

    def trigger_app_indexer_sync(self) -> dict:
        return self._post("command", {"name": "AppIndexerSync"})

    # -- Helpers ----------------------------------------------------------

    def build_app_payload(
        self,
        name: str,
        implementation: str,
        prowlarr_url: str,
        base_url: str,
        api_key: str,
        sync_categories: list[int] | None = None,
    ) -> dict:
        """Build an application payload from a schema template.

        Fetches the schema for the given implementation, fills in the fields,
        and returns a ready-to-POST payload.
        """
        schemas = self.get_application_schema()
        schema = next(
            (s for s in schemas if s["implementation"] == implementation),
            None,
        )
        if schema is None:
            raise ValueError(
                f"No schema found for implementation '{implementation}' in Prowlarr"
            )

        if sync_categories is None:
            if implementation == "Radarr":
                sync_categories = RADARR_SYNC_CATEGORIES
            elif implementation == "Sonarr":
                sync_categories = SONARR_SYNC_CATEGORIES
            else:
                sync_categories = []

        field_map = {
            "prowlarrUrl": prowlarr_url,
            "baseUrl": base_url,
            "apiKey": api_key,
            "syncCategories": sync_categories,
        }

        fields = []
        for field in schema.get("fields", []):
            f = dict(field)
            if f["name"] in field_map:
                f["value"] = field_map[f["name"]]
            fields.append(f)

        return {
            "name": name,
            "implementation": implementation,
            "configContract": schema.get("configContract", f"{implementation}Settings"),
            "syncLevel": "fullSync",
            "fields": fields,
            "tags": [],
        }
