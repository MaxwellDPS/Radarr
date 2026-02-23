"""Generate and update Recyclarr configuration YAML."""

import os

import yaml

from state import DesiredState


def generate_recyclarr_config(state: DesiredState) -> dict:
    """Generate a recyclarr.yml config dict from desired state.

    Creates entries for each user's Radarr and Sonarr instances with their
    URLs and API keys. When a user has a reference, their config blocks are
    copied from the reference user's entries.
    """
    config = {}

    radarr_entries = {}
    sonarr_entries = {}

    # First pass: build entries for reference users
    for username, user in state.users.items():
        if user.radarr and user.radarr.url and user.radarr.api_key:
            entry_name = f"radarr-{username}"
            radarr_entries[entry_name] = {
                "base_url": user.radarr.url,
                "api_key": user.radarr.api_key,
            }

        if user.sonarr and user.sonarr.url and user.sonarr.api_key:
            entry_name = f"sonarr-{username}"
            sonarr_entries[entry_name] = {
                "base_url": user.sonarr.url,
                "api_key": user.sonarr.api_key,
            }

    if radarr_entries:
        config["radarr"] = radarr_entries
    if sonarr_entries:
        config["sonarr"] = sonarr_entries

    return config


def plan_recyclarr(state: DesiredState) -> list[dict]:
    """Compare desired Recyclarr config against existing file.

    Returns a list of change descriptions.
    """
    if not state.recyclarr or not state.recyclarr.config_path:
        return []

    config_file = os.path.join(state.recyclarr.config_path, "recyclarr.yml")
    desired = generate_recyclarr_config(state)

    existing = {}
    if os.path.exists(config_file):
        with open(config_file) as f:
            existing = yaml.safe_load(f) or {}

    changes = []

    # Check for new/changed Radarr entries
    desired_radarr = desired.get("radarr", {})
    existing_radarr = existing.get("radarr", {})
    for name in desired_radarr:
        if name not in existing_radarr:
            changes.append({"type": "create", "name": name})
        elif (desired_radarr[name].get("base_url") != existing_radarr[name].get("base_url")
              or desired_radarr[name].get("api_key") != existing_radarr[name].get("api_key")):
            changes.append({"type": "update", "name": name})

    # Check for new/changed Sonarr entries
    desired_sonarr = desired.get("sonarr", {})
    existing_sonarr = existing.get("sonarr", {})
    for name in desired_sonarr:
        if name not in existing_sonarr:
            changes.append({"type": "create", "name": name})
        elif (desired_sonarr[name].get("base_url") != existing_sonarr[name].get("base_url")
              or desired_sonarr[name].get("api_key") != existing_sonarr[name].get("api_key")):
            changes.append({"type": "update", "name": name})

    return changes


def apply_recyclarr(state: DesiredState):
    """Write the Recyclarr config file."""
    if not state.recyclarr or not state.recyclarr.config_path:
        return

    config_dir = state.recyclarr.config_path
    os.makedirs(config_dir, exist_ok=True)
    config_file = os.path.join(config_dir, "recyclarr.yml")

    config = generate_recyclarr_config(state)

    # Merge with existing config to preserve any manual additions
    existing = {}
    if os.path.exists(config_file):
        with open(config_file) as f:
            existing = yaml.safe_load(f) or {}

    # Merge: update existing entries, add new ones, keep extras
    for section in ("radarr", "sonarr"):
        if section in config:
            existing.setdefault(section, {})
            existing[section].update(config[section])

    with open(config_file, "w") as f:
        yaml.dump(existing, f, default_flow_style=False, sort_keys=False)
