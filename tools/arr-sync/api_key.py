"""Extract API keys from running *arr containers."""

import os
import re
import subprocess

import yaml

CACHE_FILE = ".arr-sync-cache.yml"


def _load_cache(config_dir: str) -> dict:
    path = os.path.join(config_dir, CACHE_FILE)
    if os.path.exists(path):
        with open(path) as f:
            return yaml.safe_load(f) or {}
    return {}


def _save_cache(config_dir: str, cache: dict):
    path = os.path.join(config_dir, CACHE_FILE)
    with open(path, "w") as f:
        yaml.dump(cache, f, default_flow_style=False)


def extract_api_key(container: str, config_dir: str = ".") -> str:
    """Extract API key from a running container's config.xml.

    Caches results in .arr-sync-cache.yml to avoid repeated docker exec calls.
    """
    cache = _load_cache(config_dir)
    cached = cache.get("api_keys", {}).get(container)
    if cached:
        return cached

    key = _docker_extract(container)
    if key:
        cache.setdefault("api_keys", {})[container] = key
        _save_cache(config_dir, cache)
    return key


def _docker_extract(container: str) -> str:
    """Run docker exec to read config.xml and parse the ApiKey element."""
    try:
        result = subprocess.run(
            ["docker", "exec", container, "cat", "/config/config.xml"],
            capture_output=True,
            text=True,
            timeout=10,
        )
        if result.returncode != 0:
            raise RuntimeError(
                f"Failed to read config.xml from container '{container}': "
                f"{result.stderr.strip()}"
            )
        match = re.search(r"<ApiKey>([^<]+)</ApiKey>", result.stdout)
        if not match:
            raise RuntimeError(
                f"No <ApiKey> element found in config.xml from container '{container}'"
            )
        return match.group(1)
    except subprocess.TimeoutExpired:
        raise RuntimeError(
            f"Timed out reading config.xml from container '{container}'"
        )
    except FileNotFoundError:
        raise RuntimeError("Docker CLI not found. Is Docker installed?")


def invalidate_cache(container: str, config_dir: str = "."):
    """Remove a cached API key so next call re-extracts from container."""
    cache = _load_cache(config_dir)
    cache.get("api_keys", {}).pop(container, None)
    _save_cache(config_dir, cache)
