"""Compare desired state vs actual, produce change plan, execute actions."""

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Callable

from arr_client import ArrClient
from prowlarr_client import ProwlarrClient
from state import DesiredState, ServiceConfig, SeedrConfig, UserState


class ActionType(Enum):
    CREATE = "+"
    UPDATE = "~"
    NOOP = "="
    WARNING = "!"


@dataclass
class Action:
    type: ActionType
    category: str  # e.g. "Download client", "Root folder", "Quality profile"
    name: str  # e.g. "Seedr", "/movies"
    detail: str = ""  # e.g. "email changed", "will be created"
    execute: Callable | None = None  # callable to apply the change
    children: list["Action"] = field(default_factory=list)


@dataclass
class ServicePlan:
    service_type: str  # "Radarr" or "Sonarr"
    url: str
    actions: list[Action] = field(default_factory=list)


@dataclass
class UserPlan:
    username: str
    services: list[ServicePlan] = field(default_factory=list)


@dataclass
class ProwlarrPlan:
    url: str
    actions: list[Action] = field(default_factory=list)


@dataclass
class RecyclarrPlan:
    config_path: str
    actions: list[Action] = field(default_factory=list)


@dataclass
class ChangePlan:
    prowlarr: ProwlarrPlan | None = None
    users: list[UserPlan] = field(default_factory=list)
    recyclarr: RecyclarrPlan | None = None

    @property
    def all_actions(self) -> list[Action]:
        actions = []
        if self.prowlarr:
            actions.extend(self.prowlarr.actions)
        for user in self.users:
            for svc in user.services:
                actions.extend(svc.actions)
        if self.recyclarr:
            actions.extend(self.recyclarr.actions)
        return actions

    @property
    def summary(self) -> tuple[int, int, int]:
        """Return (create_count, update_count, noop_count)."""
        creates = updates = noops = 0
        for action in self.all_actions:
            if action.type == ActionType.CREATE:
                creates += 1
            elif action.type == ActionType.UPDATE:
                updates += 1
            elif action.type == ActionType.NOOP:
                noops += 1
        return creates, updates, noops


def _build_seedr_payload(seedr: SeedrConfig) -> dict:
    """Build the download client payload for Seedr."""
    return {
        "name": "Seedr",
        "implementation": "Seedr",
        "configContract": "SeedrSettings",
        "enable": True,
        "protocol": "torrent",
        "priority": 1,
        "removeCompletedDownloads": True,
        "removeFailedDownloads": True,
        "fields": [
            {"name": "email", "value": seedr.email},
            {"name": "password", "value": seedr.password},
            {"name": "downloadDirectory", "value": seedr.download_directory},
            {"name": "deleteFromCloud", "value": seedr.delete_from_cloud},
        ],
    }


def _get_field_value(fields: list[dict], name: str) -> Any:
    """Extract a field value from a provider fields list."""
    for f in fields:
        if f.get("name") == name:
            return f.get("value")
    return None


def _seedr_fields_match(existing: dict, desired: SeedrConfig) -> bool:
    """Check if an existing Seedr download client matches desired config."""
    fields = existing.get("fields", [])
    return (
        _get_field_value(fields, "email") == desired.email
        and _get_field_value(fields, "password") == desired.password
        and _get_field_value(fields, "downloadDirectory") == desired.download_directory
        and _get_field_value(fields, "deleteFromCloud") == desired.delete_from_cloud
    )


def _seedr_diff_detail(existing: dict, desired: SeedrConfig) -> str:
    """Describe what changed between existing and desired Seedr config."""
    fields = existing.get("fields", [])
    changes = []
    if _get_field_value(fields, "email") != desired.email:
        changes.append("email")
    if _get_field_value(fields, "password") != desired.password:
        changes.append("password")
    if _get_field_value(fields, "downloadDirectory") != desired.download_directory:
        changes.append("downloadDirectory")
    if _get_field_value(fields, "deleteFromCloud") != desired.delete_from_cloud:
        changes.append("deleteFromCloud")
    return f"{', '.join(changes)} changed"


def plan_download_client(
    client: ArrClient, seedr: SeedrConfig
) -> Action:
    """Plan Seedr download client sync."""
    existing_clients = client.get_download_clients()
    existing_seedr = next(
        (c for c in existing_clients if c.get("implementation") == "Seedr"),
        None,
    )

    payload = _build_seedr_payload(seedr)

    if existing_seedr is None:
        return Action(
            type=ActionType.CREATE,
            category="Download client",
            name="Seedr",
            detail="will be created",
            execute=lambda: client.create_download_client(payload),
        )

    if _seedr_fields_match(existing_seedr, seedr):
        return Action(
            type=ActionType.NOOP,
            category="Download client",
            name="Seedr",
            detail="up to date",
        )

    detail = _seedr_diff_detail(existing_seedr, seedr)
    existing_id = existing_seedr["id"]
    return Action(
        type=ActionType.UPDATE,
        category="Download client",
        name="Seedr",
        detail=detail,
        execute=lambda: client.update_download_client(existing_id, payload),
    )


def plan_root_folders(
    client: ArrClient, desired_paths: list[str]
) -> list[Action]:
    """Plan root folder sync."""
    existing = client.get_root_folders()
    existing_paths = {rf["path"] for rf in existing}
    actions = []

    for path in desired_paths:
        if path in existing_paths:
            actions.append(Action(
                type=ActionType.NOOP,
                category="Root folder",
                name=f'"{path}"',
                detail="exists",
            ))
        else:
            p = path  # capture for lambda
            actions.append(Action(
                type=ActionType.CREATE,
                category="Root folder",
                name=f'"{path}"',
                detail="will be created",
                execute=lambda p=p: client.create_root_folder(p),
            ))

    # Warn about extra root folders not in YAML
    for path in existing_paths:
        if path not in desired_paths:
            actions.append(Action(
                type=ActionType.WARNING,
                category="Root folder",
                name=f'"{path}"',
                detail="exists in API but not in YAML (not removing)",
            ))

    return actions


def plan_reference_sync(
    target_client: ArrClient,
    ref_client: ArrClient,
    ref_username: str,
    target_svc: ServiceConfig,
) -> list[Action]:
    """Plan syncing quality profiles, custom formats, naming, media management from reference."""
    actions = []

    # -- Custom Formats --
    ref_cfs = ref_client.get_custom_formats()
    target_cfs = target_client.get_custom_formats()
    target_cf_names = {cf["name"]: cf for cf in target_cfs}

    cf_creates = 0
    cf_existing = 0
    cf_id_map = {}  # ref_id -> target_id

    for ref_cf in ref_cfs:
        if ref_cf["name"] in target_cf_names:
            cf_existing += 1
            cf_id_map[ref_cf["id"]] = target_cf_names[ref_cf["name"]]["id"]
        else:
            cf_creates += 1

    if cf_creates > 0:
        def _sync_custom_formats():
            nonlocal cf_id_map
            t_cfs = target_client.get_custom_formats()
            t_names = {cf["name"]: cf for cf in t_cfs}
            for ref_cf in ref_cfs:
                if ref_cf["name"] not in t_names:
                    payload = {k: v for k, v in ref_cf.items() if k != "id"}
                    created = target_client.create_custom_format(payload)
                    cf_id_map[ref_cf["id"]] = created["id"]
                else:
                    cf_id_map[ref_cf["id"]] = t_names[ref_cf["name"]]["id"]

        actions.append(Action(
            type=ActionType.CREATE,
            category="Custom formats",
            name=f"{cf_creates} from {ref_username}",
            detail=f"{cf_creates} to create, {cf_existing} existing",
            execute=_sync_custom_formats,
        ))
    else:
        # Still build the ID map
        for ref_cf in ref_cfs:
            if ref_cf["name"] in target_cf_names:
                cf_id_map[ref_cf["id"]] = target_cf_names[ref_cf["name"]]["id"]

        actions.append(Action(
            type=ActionType.NOOP,
            category="Custom formats",
            name=f"{cf_existing} synced",
            detail=f"from {ref_username}",
        ))

    # -- Quality Profiles --
    ref_profiles = ref_client.get_quality_profiles()
    target_profiles = target_client.get_quality_profiles()
    target_profile_names = {p["name"]: p for p in target_profiles}

    profile_creates = 0
    profile_updates = 0
    profile_noops = 0

    for ref_profile in ref_profiles:
        if ref_profile["name"] not in target_profile_names:
            profile_creates += 1
        else:
            profile_noops += 1

    total_profiles = len(ref_profiles)
    if profile_creates > 0:
        def _sync_quality_profiles():
            t_profiles = target_client.get_quality_profiles()
            t_names = {p["name"]: p for p in t_profiles}
            for ref_p in ref_profiles:
                payload = {k: v for k, v in ref_p.items() if k != "id"}
                # Remap custom format IDs
                for fi in payload.get("formatItems", []):
                    old_id = fi.get("format")
                    if old_id in cf_id_map:
                        fi["format"] = cf_id_map[old_id]
                if ref_p["name"] in t_names:
                    target_client.update_quality_profile(t_names[ref_p["name"]]["id"], payload)
                else:
                    target_client.create_quality_profile(payload)

        actions.append(Action(
            type=ActionType.CREATE,
            category="Quality profiles",
            name=f"{profile_creates} from {ref_username}",
            detail=f"{profile_creates} to create, {profile_noops} existing",
            execute=_sync_quality_profiles,
        ))
    else:
        actions.append(Action(
            type=ActionType.NOOP,
            category="Quality profiles",
            name=f"{total_profiles} synced",
            detail=f"from {ref_username}",
        ))

    # -- Quality Definitions --
    ref_defs = ref_client.get_quality_definitions()
    target_defs = target_client.get_quality_definitions()

    defs_differ = False
    if len(ref_defs) == len(target_defs):
        for rd, td in zip(ref_defs, target_defs):
            if (rd.get("minSize") != td.get("minSize")
                    or rd.get("maxSize") != td.get("maxSize")
                    or rd.get("preferredSize") != td.get("preferredSize")):
                defs_differ = True
                break
    else:
        defs_differ = True

    if defs_differ:
        actions.append(Action(
            type=ActionType.UPDATE,
            category="Quality definitions",
            name="bulk update",
            detail=f"from {ref_username}",
            execute=lambda: target_client.update_quality_definitions(ref_defs),
        ))
    else:
        actions.append(Action(
            type=ActionType.NOOP,
            category="Quality definitions",
            name="up to date",
            detail=f"from {ref_username}",
        ))

    # -- Naming Config --
    ref_naming = ref_client.get_naming_config()
    target_naming = target_client.get_naming_config()

    # Apply explicit YAML overrides if any
    yaml_naming = target_svc.naming
    desired_naming = dict(ref_naming)
    if yaml_naming:
        field_map = {
            "rename_movies": "renameMovies",
            "standard_movie_format": "standardMovieFormat",
            "movie_folder_format": "movieFolderFormat",
        }
        for yaml_key, api_key in field_map.items():
            if yaml_key in yaml_naming:
                desired_naming[api_key] = yaml_naming[yaml_key]

    # Compare relevant naming fields
    naming_keys = [
        "renameMovies", "standardMovieFormat", "movieFolderFormat",
        "renameEpisodes", "standardEpisodeFormat", "seasonFolderFormat",
        "seriesFolderFormat", "dailyEpisodeFormat", "animeEpisodeFormat",
    ]
    naming_changed = any(
        desired_naming.get(k) != target_naming.get(k)
        for k in naming_keys
        if k in desired_naming
    )

    if naming_changed:
        # Preserve target's id
        desired_naming["id"] = target_naming.get("id", 1)
        actions.append(Action(
            type=ActionType.UPDATE,
            category="Naming config",
            name="will be updated",
            detail=f"from {ref_username}",
            execute=lambda: target_client.update_naming_config(desired_naming),
        ))
    else:
        actions.append(Action(
            type=ActionType.NOOP,
            category="Naming config",
            name="up to date",
            detail=f"from {ref_username}",
        ))

    # -- Media Management --
    ref_mm = ref_client.get_media_management()
    target_mm = target_client.get_media_management()

    yaml_mm = target_svc.media_management
    desired_mm = dict(ref_mm)
    if yaml_mm:
        field_map = {
            "recycle_bin": "recycleBin",
            "delete_empty_folders": "deleteEmptyFolders",
        }
        for yaml_key, api_key in field_map.items():
            if yaml_key in yaml_mm:
                desired_mm[api_key] = yaml_mm[yaml_key]

    mm_keys = [
        "recycleBin", "recycleBinCleanupDays", "deleteEmptyFolders",
        "autoUnmonitorPreviouslyDownloadedMovies",
    ]
    mm_changed = any(
        desired_mm.get(k) != target_mm.get(k)
        for k in mm_keys
        if k in desired_mm
    )

    if mm_changed:
        desired_mm["id"] = target_mm.get("id", 1)
        actions.append(Action(
            type=ActionType.UPDATE,
            category="Media management",
            name="will be updated",
            detail=f"from {ref_username}",
            execute=lambda: target_client.update_media_management(desired_mm),
        ))
    else:
        actions.append(Action(
            type=ActionType.NOOP,
            category="Media management",
            name="up to date",
            detail=f"from {ref_username}",
        ))

    return actions


def plan_prowlarr_apps(
    prowlarr: ProwlarrClient,
    prowlarr_url: str,
    state: DesiredState,
) -> list[Action]:
    """Plan Prowlarr application sync for all users."""
    existing_apps = prowlarr.get_applications()
    existing_by_name = {a["name"]: a for a in existing_apps}
    actions = []

    for username, user in state.users.items():
        for svc_name, impl in [("radarr", "Radarr"), ("sonarr", "Sonarr")]:
            svc = getattr(user, svc_name)
            if svc is None or not svc.url or not svc.api_key:
                continue

            app_name = f"{impl} ({username})"

            if app_name in existing_by_name:
                existing = existing_by_name[app_name]
                existing_fields = existing.get("fields", [])
                url_match = _get_field_value(existing_fields, "baseUrl") == svc.url
                key_match = _get_field_value(existing_fields, "apiKey") == svc.api_key

                if url_match and key_match:
                    actions.append(Action(
                        type=ActionType.NOOP,
                        category="App",
                        name=f'"{app_name}"',
                        detail="up to date",
                    ))
                else:
                    changes = []
                    if not url_match:
                        changes.append("baseUrl")
                    if not key_match:
                        changes.append("apiKey")
                    detail = f"{', '.join(changes)} changed"
                    app_id = existing["id"]

                    def _update_app(
                        _name=app_name, _impl=impl, _url=svc.url,
                        _key=svc.api_key, _id=app_id
                    ):
                        payload = prowlarr.build_app_payload(
                            _name, _impl, prowlarr_url, _url, _key,
                        )
                        prowlarr.update_application(_id, payload)

                    actions.append(Action(
                        type=ActionType.UPDATE,
                        category="App",
                        name=f'"{app_name}"',
                        detail=detail,
                        execute=_update_app,
                    ))
            else:
                def _create_app(
                    _name=app_name, _impl=impl, _url=svc.url,
                    _key=svc.api_key,
                ):
                    payload = prowlarr.build_app_payload(
                        _name, _impl, prowlarr_url, _url, _key,
                    )
                    prowlarr.create_application(payload)

                actions.append(Action(
                    type=ActionType.CREATE,
                    category="App",
                    name=f'"{app_name}"',
                    detail="will be created",
                    execute=_create_app,
                ))

    return actions


def plan_service(
    service_type: str,
    svc: ServiceConfig,
    seedr: SeedrConfig | None,
    user: UserState,
    state: DesiredState,
) -> ServicePlan:
    """Build the full plan for a single Radarr/Sonarr instance."""
    client = ArrClient(svc.url, svc.api_key)
    plan = ServicePlan(service_type=service_type, url=svc.url)

    # Download client
    if seedr:
        plan.actions.append(plan_download_client(client, seedr))

    # Root folders
    if svc.root_folders:
        plan.actions.extend(plan_root_folders(client, svc.root_folders))

    # Reference sync
    if user.reference:
        ref_user = state.users[user.reference]
        ref_svc = getattr(ref_user, service_type.lower())
        if ref_svc and ref_svc.url and ref_svc.api_key:
            ref_client = ArrClient(ref_svc.url, ref_svc.api_key)
            plan.actions.extend(
                plan_reference_sync(client, ref_client, user.reference, svc)
            )

    return plan


def compute_plan(state: DesiredState, target_user: str | None = None) -> ChangePlan:
    """Compute the full change plan for all (or one) users."""
    change_plan = ChangePlan()

    # Prowlarr
    if state.prowlarr and state.prowlarr.url and state.prowlarr.api_key:
        prowlarr = ProwlarrClient(state.prowlarr.url, state.prowlarr.api_key)

        # Filter state for target user if specified
        if target_user:
            filtered_state = DesiredState(
                prowlarr=state.prowlarr,
                users={target_user: state.users[target_user]},
            )
        else:
            filtered_state = state

        prowlarr_actions = plan_prowlarr_apps(
            prowlarr, state.prowlarr.url, filtered_state
        )
        if prowlarr_actions:
            change_plan.prowlarr = ProwlarrPlan(
                url=state.prowlarr.url, actions=prowlarr_actions
            )

    # Per-user services
    users_to_process = (
        {target_user: state.users[target_user]}
        if target_user
        else state.users
    )

    for username, user in users_to_process.items():
        user_plan = UserPlan(username=username)

        for svc_name, svc_label in [("radarr", "Radarr"), ("sonarr", "Sonarr")]:
            svc = getattr(user, svc_name)
            if svc and svc.url and svc.api_key:
                service_plan = plan_service(
                    svc_label, svc, user.seedr, user, state
                )
                user_plan.services.append(service_plan)

        if user_plan.services:
            change_plan.users.append(user_plan)

    return change_plan


def display_plan(plan: ChangePlan):
    """Print the plan in the diff output format."""
    print()

    if plan.prowlarr:
        print(f"Prowlarr ({plan.prowlarr.url}):")
        for action in plan.prowlarr.actions:
            print(f"  {action.type.value} {action.category} {action.name}: {action.detail}")
        print()

    for user_plan in plan.users:
        print(f"User: {user_plan.username}")
        for svc in user_plan.services:
            print(f"  {svc.service_type} ({svc.url}):")
            for action in svc.actions:
                print(f"    {action.type.value} {action.category} {action.name}: {action.detail}")
        print()

    if plan.recyclarr:
        print(f"Recyclarr ({plan.recyclarr.config_path}):")
        for action in plan.recyclarr.actions:
            print(f"  {action.type.value} {action.category} {action.name}: {action.detail}")
        print()

    creates, updates, noops = plan.summary
    print(f"Plan: {creates} to create, {updates} to update, {noops} unchanged")


def apply_plan(plan: ChangePlan):
    """Execute all actions in the plan."""
    all_actions = plan.all_actions
    applied = 0
    errors = 0

    for action in all_actions:
        if action.execute is None:
            continue
        try:
            action.execute()
            applied += 1
            print(f"  {action.type.value} {action.category} {action.name}: done")
        except Exception as e:
            errors += 1
            print(f"  ! {action.category} {action.name}: FAILED - {e}")

    # Trigger Prowlarr sync if we created/updated any apps
    if plan.prowlarr:
        prowlarr_had_changes = any(
            a.type in (ActionType.CREATE, ActionType.UPDATE)
            for a in plan.prowlarr.actions
        )
        if prowlarr_had_changes and applied > 0:
            try:
                from prowlarr_client import ProwlarrClient
                from state import DesiredState
                # Re-create client from plan url (prowlarr info is on the state)
                # We need to trigger sync - but we don't have credentials here
                # The sync trigger is handled by the CLI layer
                pass
            except Exception:
                pass

    print(f"\nApplied: {applied} changes, {errors} errors")
    return errors == 0
