#!/usr/bin/env python3
"""arr-sync: Declarative configuration tool for the *arr media stack.

Like Terraform for Radarr, Sonarr, Prowlarr, and Recyclarr.
Reads a single YAML file declaring desired state, diffs against running
services, and applies changes via their REST APIs.
"""

import sys

import click
import yaml

from arr_client import ArrClient
from diff import (
    ActionType,
    ChangePlan,
    RecyclarrPlan,
    Action,
    apply_plan,
    compute_plan,
    display_plan,
)
from prowlarr_client import ProwlarrClient
from recyclarr_config import apply_recyclarr, plan_recyclarr
from seedr_validator import validate_credentials
from state import resolve_state


def _add_recyclarr_to_plan(plan: ChangePlan, state):
    """Add Recyclarr changes to the change plan."""
    if not state.recyclarr or not state.recyclarr.config_path:
        return

    changes = plan_recyclarr(state)
    if changes:
        actions = []
        for change in changes:
            actions.append(Action(
                type=ActionType.CREATE if change["type"] == "create" else ActionType.UPDATE,
                category=change["name"],
                name="",
                detail="will be added" if change["type"] == "create" else "will be updated",
                execute=lambda: apply_recyclarr(state),
            ))
        plan.recyclarr = RecyclarrPlan(
            config_path=state.recyclarr.config_path,
            actions=actions,
        )


def _trigger_prowlarr_sync(state):
    """Trigger Prowlarr AppIndexerSync after changes."""
    if state.prowlarr and state.prowlarr.url and state.prowlarr.api_key:
        try:
            prowlarr = ProwlarrClient(state.prowlarr.url, state.prowlarr.api_key)
            prowlarr.trigger_app_indexer_sync()
            click.echo("  Triggered Prowlarr AppIndexerSync")
        except Exception as e:
            click.echo(f"  ! Failed to trigger Prowlarr sync: {e}", err=True)


@click.group()
@click.option(
    "--config", "-c",
    default="arr-state.yml",
    help="Path to state YAML file (default: arr-state.yml)",
    type=click.Path(),
)
@click.pass_context
def cli(ctx, config):
    """arr-sync: Declarative *arr stack configuration."""
    ctx.ensure_object(dict)
    ctx.obj["config"] = config


@cli.command()
@click.option("--user", "-u", default=None, help="Only plan for a specific user")
@click.pass_context
def plan(ctx, user):
    """Show what changes would be made (dry run)."""
    config_path = ctx.obj["config"]

    try:
        state = resolve_state(config_path)
    except Exception as e:
        click.echo(f"Error loading config: {e}", err=True)
        sys.exit(1)

    if user and user not in state.users:
        click.echo(f"User '{user}' not found in config", err=True)
        sys.exit(1)

    # Validate Seedr credentials
    users_to_check = {user: state.users[user]} if user else state.users
    for username, u in users_to_check.items():
        if u.seedr:
            try:
                validate_credentials(u.seedr.email, u.seedr.password)
                click.echo(f"  Seedr credentials valid for {username}")
            except RuntimeError as e:
                click.echo(f"  ! Seedr validation failed for {username}: {e}", err=True)

    try:
        change_plan = compute_plan(state, target_user=user)
        _add_recyclarr_to_plan(change_plan, state)
        display_plan(change_plan)
    except Exception as e:
        click.echo(f"Error computing plan: {e}", err=True)
        sys.exit(1)


@cli.command()
@click.option("--user", "-u", default=None, help="Only apply for a specific user")
@click.option("--dry-run", is_flag=True, help="Alias for plan command")
@click.pass_context
def apply(ctx, user, dry_run):
    """Apply changes to converge services to desired state."""
    if dry_run:
        ctx.invoke(plan, user=user)
        return

    config_path = ctx.obj["config"]

    try:
        state = resolve_state(config_path)
    except Exception as e:
        click.echo(f"Error loading config: {e}", err=True)
        sys.exit(1)

    if user and user not in state.users:
        click.echo(f"User '{user}' not found in config", err=True)
        sys.exit(1)

    # Validate Seedr credentials
    users_to_check = {user: state.users[user]} if user else state.users
    for username, u in users_to_check.items():
        if u.seedr:
            try:
                validate_credentials(u.seedr.email, u.seedr.password)
                click.echo(f"  Seedr credentials valid for {username}")
            except RuntimeError as e:
                click.echo(f"  ! Seedr validation failed for {username}: {e}", err=True)
                sys.exit(1)

    try:
        change_plan = compute_plan(state, target_user=user)
        _add_recyclarr_to_plan(change_plan, state)
    except Exception as e:
        click.echo(f"Error computing plan: {e}", err=True)
        sys.exit(1)

    # Display plan first
    display_plan(change_plan)

    creates, updates, noops = change_plan.summary
    if creates == 0 and updates == 0:
        click.echo("\nNothing to do.")
        return

    click.echo()
    if not click.confirm("Apply these changes?"):
        click.echo("Aborted.")
        return

    click.echo("\nApplying changes...")
    success = apply_plan(change_plan)

    # Apply Recyclarr config
    if change_plan.recyclarr and change_plan.recyclarr.actions:
        try:
            apply_recyclarr(state)
            click.echo("  Recyclarr config updated")
        except Exception as e:
            click.echo(f"  ! Recyclarr config failed: {e}", err=True)

    # Trigger Prowlarr sync
    _trigger_prowlarr_sync(state)

    if not success:
        sys.exit(1)


@cli.command()
@click.option("--user", "-u", default=None, help="Only show status for a specific user")
@click.pass_context
def status(ctx, user):
    """Show current state vs desired state."""
    config_path = ctx.obj["config"]

    try:
        state = resolve_state(config_path)
    except Exception as e:
        click.echo(f"Error loading config: {e}", err=True)
        sys.exit(1)

    if user and user not in state.users:
        click.echo(f"User '{user}' not found in config", err=True)
        sys.exit(1)

    # Shared services status
    if state.prowlarr and state.prowlarr.url:
        try:
            prowlarr = ProwlarrClient(state.prowlarr.url, state.prowlarr.api_key)
            prowlarr_status = prowlarr.get_status()
            click.echo(f"Prowlarr ({state.prowlarr.url}): v{prowlarr_status.get('version', '?')}")
        except Exception as e:
            click.echo(f"Prowlarr ({state.prowlarr.url}): UNREACHABLE - {e}")

    # Per-user status
    users_to_check = {user: state.users[user]} if user else state.users
    for username, u in users_to_check.items():
        click.echo(f"\nUser: {username}")

        for svc_name, svc_label in [("radarr", "Radarr"), ("sonarr", "Sonarr")]:
            svc = getattr(u, svc_name)
            if svc and svc.url and svc.api_key:
                try:
                    client = ArrClient(svc.url, svc.api_key)
                    svc_status = client.get_status()
                    click.echo(f"  {svc_label} ({svc.url}): v{svc_status.get('version', '?')}")

                    # Show download clients
                    dcs = client.get_download_clients()
                    seedr_clients = [d for d in dcs if d.get("implementation") == "Seedr"]
                    if seedr_clients:
                        click.echo(f"    Seedr download client: configured")
                    else:
                        click.echo(f"    Seedr download client: NOT configured")

                    # Show root folders
                    rfs = client.get_root_folders()
                    for rf in rfs:
                        click.echo(f"    Root folder: {rf['path']}")

                    # Show quality profiles
                    qps = client.get_quality_profiles()
                    click.echo(f"    Quality profiles: {len(qps)}")

                except Exception as e:
                    click.echo(f"  {svc_label} ({svc.url}): UNREACHABLE - {e}")

        if u.seedr:
            try:
                user_info = validate_credentials(u.seedr.email, u.seedr.password)
                bandwidth = user_info.get("bandwidth_used", 0)
                space = user_info.get("space_used", 0)
                click.echo(f"  Seedr ({u.seedr.email}): OK (space: {space}, bandwidth: {bandwidth})")
            except RuntimeError as e:
                click.echo(f"  Seedr ({u.seedr.email}): {e}")


@cli.command(name="import")
@click.argument("username")
@click.pass_context
def import_config(ctx, username):
    """Import existing config from a running instance into the YAML."""
    config_path = ctx.obj["config"]

    try:
        state = resolve_state(config_path)
    except FileNotFoundError:
        click.echo(f"Config file '{config_path}' not found. Creating a new one.", err=True)
        state = None
    except Exception as e:
        click.echo(f"Error loading config: {e}", err=True)
        sys.exit(1)

    if state and username not in state.users:
        click.echo(
            f"User '{username}' not found in config. "
            f"Add a minimal entry with radarr/sonarr URLs and containers first.",
            err=True,
        )
        sys.exit(1)

    user = state.users[username]
    imported = {"users": {username: {}}}

    for svc_name, svc_label in [("radarr", "Radarr"), ("sonarr", "Sonarr")]:
        svc = getattr(user, svc_name)
        if not svc or not svc.url or not svc.api_key:
            continue

        click.echo(f"Importing from {svc_label} ({svc.url})...")
        client = ArrClient(svc.url, svc.api_key)

        svc_config = {
            "url": svc.url,
            "api_key": svc.api_key,
            "container": svc.container,
        }

        # Root folders
        rfs = client.get_root_folders()
        svc_config["root_folders"] = [rf["path"] for rf in rfs]

        # Naming
        naming = client.get_naming_config()
        naming_export = {}
        for key in ["renameMovies", "standardMovieFormat", "movieFolderFormat",
                     "renameEpisodes", "standardEpisodeFormat", "seasonFolderFormat",
                     "seriesFolderFormat"]:
            if key in naming:
                # Convert camelCase to snake_case for YAML
                snake = "".join(
                    f"_{c.lower()}" if c.isupper() else c for c in key
                ).lstrip("_")
                naming_export[snake] = naming[key]
        svc_config["naming"] = naming_export

        # Media management
        mm = client.get_media_management()
        mm_export = {}
        for key in ["recycleBin", "recycleBinCleanupDays", "deleteEmptyFolders"]:
            if key in mm:
                snake = "".join(
                    f"_{c.lower()}" if c.isupper() else c for c in key
                ).lstrip("_")
                mm_export[snake] = mm[key]
        svc_config["media_management"] = mm_export

        # Download clients - check for Seedr
        dcs = client.get_download_clients()
        for dc in dcs:
            if dc.get("implementation") == "Seedr":
                fields = dc.get("fields", [])
                seedr_config = {}
                for f in fields:
                    name = f.get("name", "")
                    if name == "email":
                        seedr_config["email"] = f.get("value", "")
                    elif name == "password":
                        seedr_config["password"] = f.get("value", "")
                    elif name == "downloadDirectory":
                        seedr_config["download_directory"] = f.get("value", "")
                    elif name == "deleteFromCloud":
                        seedr_config["delete_from_cloud"] = f.get("value", True)
                imported["users"][username]["seedr"] = seedr_config

        imported["users"][username][svc_name] = svc_config
        click.echo(f"  {svc_label}: {len(rfs)} root folders, {len(client.get_quality_profiles())} quality profiles")

    # Output as YAML
    click.echo(f"\n--- Imported config for '{username}' ---\n")
    click.echo(yaml.dump(imported, default_flow_style=False, sort_keys=False))
    click.echo("Paste the relevant sections into your arr-state.yml")


if __name__ == "__main__":
    cli()
