"""Load YAML config, resolve references, and produce desired state."""

import copy
import os
from dataclasses import dataclass, field

import yaml

from api_key import extract_api_key


@dataclass
class SeedrConfig:
    email: str
    password: str
    download_directory: str = "/downloads"
    delete_from_cloud: bool = True


@dataclass
class ServiceConfig:
    url: str
    api_key: str
    container: str = ""
    root_folders: list[str] = field(default_factory=list)
    naming: dict = field(default_factory=dict)
    media_management: dict = field(default_factory=dict)


@dataclass
class UserState:
    name: str
    seedr: SeedrConfig | None = None
    radarr: ServiceConfig | None = None
    sonarr: ServiceConfig | None = None
    overseerr_url: str = ""
    is_reference: bool = False
    reference: str = ""


@dataclass
class ProwlarrConfig:
    url: str
    api_key: str


@dataclass
class RecyclarrConfig:
    config_path: str


@dataclass
class FlareSolverrConfig:
    url: str


@dataclass
class DesiredState:
    prowlarr: ProwlarrConfig | None = None
    recyclarr: RecyclarrConfig | None = None
    flaresolverr: FlareSolverrConfig | None = None
    users: dict[str, UserState] = field(default_factory=dict)


def load_yaml(config_path: str) -> dict:
    """Load and return raw YAML config."""
    with open(config_path) as f:
        return yaml.safe_load(f) or {}


def _parse_seedr(data: dict) -> SeedrConfig | None:
    if not data:
        return None
    return SeedrConfig(
        email=data["email"],
        password=data["password"],
        download_directory=data.get("download_directory", "/downloads"),
        delete_from_cloud=data.get("delete_from_cloud", True),
    )


def _parse_service(data: dict) -> ServiceConfig | None:
    if not data:
        return None
    return ServiceConfig(
        url=data.get("url", ""),
        api_key=data.get("api_key", ""),
        container=data.get("container", ""),
        root_folders=data.get("root_folders", []),
        naming=data.get("naming", {}),
        media_management=data.get("media_management", {}),
    )


def _resolve_api_key(service: ServiceConfig | None, config_dir: str) -> None:
    """If API key is empty and container is set, extract from container."""
    if service and not service.api_key and service.container:
        service.api_key = extract_api_key(service.container, config_dir)


def _apply_reference(user: UserState, ref_user: UserState):
    """Copy config from reference user where the target user hasn't overridden."""
    for svc_name in ("radarr", "sonarr"):
        target_svc = getattr(user, svc_name)
        ref_svc = getattr(ref_user, svc_name)
        if target_svc is None or ref_svc is None:
            continue

        # Copy naming if target didn't specify (empty dict means "use reference")
        if not target_svc.naming:
            target_svc.naming = copy.deepcopy(ref_svc.naming)

        # Copy media_management if target didn't specify
        if not target_svc.media_management:
            target_svc.media_management = copy.deepcopy(ref_svc.media_management)


def resolve_state(config_path: str) -> DesiredState:
    """Load YAML and produce a fully-resolved DesiredState."""
    raw = load_yaml(config_path)
    config_dir = os.path.dirname(os.path.abspath(config_path))

    state = DesiredState()

    # Shared services
    if "prowlarr" in raw:
        p = raw["prowlarr"]
        prowlarr_api_key = p.get("api_key", "")
        if not prowlarr_api_key and p.get("container"):
            prowlarr_api_key = extract_api_key(p["container"], config_dir)
        state.prowlarr = ProwlarrConfig(
            url=p.get("url", ""),
            api_key=prowlarr_api_key,
        )

    if "recyclarr" in raw:
        state.recyclarr = RecyclarrConfig(
            config_path=raw["recyclarr"].get("config_path", ""),
        )

    if "flaresolverr" in raw:
        state.flaresolverr = FlareSolverrConfig(
            url=raw["flaresolverr"].get("url", ""),
        )

    # Users
    users_raw = raw.get("users", {})
    for username, udata in users_raw.items():
        user = UserState(
            name=username,
            seedr=_parse_seedr(udata.get("seedr")),
            radarr=_parse_service(udata.get("radarr")),
            sonarr=_parse_service(udata.get("sonarr")),
            overseerr_url=udata.get("overseerr", {}).get("url", "") if udata.get("overseerr") else "",
            is_reference=udata.get("is_reference", False),
            reference=udata.get("reference", ""),
        )

        # Resolve API keys
        _resolve_api_key(user.radarr, config_dir)
        _resolve_api_key(user.sonarr, config_dir)

        state.users[username] = user

    # Resolve references (second pass after all users parsed)
    for username, user in state.users.items():
        if user.reference:
            ref_user = state.users.get(user.reference)
            if ref_user is None:
                raise ValueError(
                    f"User '{username}' references '{user.reference}' "
                    f"which is not defined"
                )
            _apply_reference(user, ref_user)

    return state
