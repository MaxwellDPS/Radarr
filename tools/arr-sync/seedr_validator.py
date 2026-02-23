"""Validate Seedr.cc credentials."""

import requests


SEEDR_API_URL = "https://www.seedr.cc/rest"


def validate_credentials(email: str, password: str, timeout: int = 10) -> dict:
    """Validate Seedr credentials by calling the user endpoint.

    Returns the user info dict on success.
    Raises RuntimeError on failure.
    """
    try:
        resp = requests.get(
            f"{SEEDR_API_URL}/user",
            auth=(email, password),
            timeout=timeout,
        )
    except requests.RequestException as e:
        raise RuntimeError(f"Failed to connect to Seedr API: {e}")

    if resp.status_code == 401:
        raise RuntimeError(f"Seedr authentication failed for '{email}'")
    resp.raise_for_status()

    data = resp.json()
    if "error" in data:
        raise RuntimeError(f"Seedr API error for '{email}': {data['error']}")

    return data
