import sys
import os
import json as _json


def _json_arg(value, name):
    """Accept either a JSON string or an already-parsed object."""
    if value is None or value == "":
        return None
    if isinstance(value, str):
        try:
            return _json.loads(value)
        except Exception as e:
            raise ValueError(name + " is not valid JSON: " + str(e))
    return value

# ============================================================================
# CRITICAL: WINDOWS LINE ENDING FIX FOR MCP (MONKEY-PATCH)
# The MCP SDK's stdio_server uses TextIOWrapper without newline='\n', causing
# Windows to output CRLF (\r\n) instead of LF (\n). This causes the error:
# "invalid trailing data at the end of stream"
# We MUST patch the MCP SDK BEFORE importing FastMCP.
# ============================================================================

if sys.platform == "win32":
    import msvcrt
    from io import TextIOWrapper
    from contextlib import asynccontextmanager

    msvcrt.setmode(sys.stdin.fileno(), os.O_BINARY)
    msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)

    import mcp.server.stdio as mcp_stdio
    import anyio
    import anyio.lowlevel
    from anyio.streams.memory import MemoryObjectReceiveStream, MemoryObjectSendStream
    import mcp.types as types
    from mcp.shared.message import SessionMessage

    @asynccontextmanager
    async def _patched_stdio_server(
        stdin: "anyio.AsyncFile[str] | None" = None,
        stdout: "anyio.AsyncFile[str] | None" = None,
    ):
        """Patched stdio_server with proper Windows newline handling."""
        if not stdin:
            stdin = anyio.wrap_file(TextIOWrapper(sys.stdin.buffer, encoding="utf-8", newline='\n'))
        if not stdout:
            stdout = anyio.wrap_file(TextIOWrapper(sys.stdout.buffer, encoding="utf-8", newline='\n'))

        read_stream_writer, read_stream = anyio.create_memory_object_stream(0)
        write_stream, write_stream_reader = anyio.create_memory_object_stream(0)

        async def stdin_reader():
            try:
                async with read_stream_writer:
                    async for line in stdin:
                        try:
                            message = types.JSONRPCMessage.model_validate_json(line)
                        except Exception as exc:
                            await read_stream_writer.send(exc)
                            continue
                        session_message = SessionMessage(message)
                        await read_stream_writer.send(session_message)
            except anyio.ClosedResourceError:
                await anyio.lowlevel.checkpoint()

        async def stdout_writer():
            try:
                async with write_stream_reader:
                    async for session_message in write_stream_reader:
                        json = session_message.message.model_dump_json(by_alias=True, exclude_none=True)
                        await stdout.write(json + "\n")
                        await stdout.flush()
            except anyio.ClosedResourceError:
                await anyio.lowlevel.checkpoint()

        async with anyio.create_task_group() as tg:
            tg.start_soon(stdin_reader)
            tg.start_soon(stdout_writer)
            yield read_stream, write_stream

    mcp_stdio.stdio_server = _patched_stdio_server

# ============================================================================
# STDOUT PROTECTION FOR MCP
# ============================================================================

_mcp_stdout = sys.stdout
sys.stdout = sys.stderr

import json
import struct
import time
import math
import threading
import traceback

try:
    from mcp.server.fastmcp import FastMCP

    if sys.platform == "win32":
        import mcp.server.fastmcp.server as fastmcp_server
        fastmcp_server.stdio_server = _patched_stdio_server

except ImportError as e:
    print(f"[MCP Frosty] Import Error: {e}", file=sys.stderr, flush=True)
    sys.exit(1)

try:
    import win32file
    import pywintypes
except ImportError:
    win32file = None
    pywintypes = None

sys.stdout = _mcp_stdout


def debug_log(msg):
    print(f"[MCP Frosty] {msg}", file=sys.stderr, flush=True)


def format_result(result):
    """Format Frosty Bridge result as a proper JSON string for AI consumption."""
    if isinstance(result, dict):
        return json.dumps(result, indent=None, ensure_ascii=False)
    if isinstance(result, str):
        return result
    return json.dumps(result)


# ============================================================================
# CONFIGURATION
# ============================================================================

PIPE_PREFIX = r"\\.\pipe\Frosty_MCP_Bridge_v1"
PIPE_PROFILES_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "pipe_profiles.json"
)


def _load_pipe_profiles():
    """Read the generated profile table written by gen_pipe_profiles.py."""
    try:
        with open(PIPE_PROFILES_PATH, "r", encoding="utf-8") as fh:
            payload = json.load(fh)
    except OSError:
        return None
    profiles = payload.get("profiles")
    if not isinstance(profiles, list):
        return None
    return profiles


def _resolve_pipe_name():
    explicit = os.environ.get("FROSTY_MCP_PIPE")
    if explicit:
        return explicit

    wanted = os.environ.get("FROSTY_MCP_PROFILE", "").strip()
    if not wanted:
        return PIPE_PREFIX

    profiles = _load_pipe_profiles()
    if profiles is None:
        debug_log("pipe_profiles.json is missing; using the raw profile name")
        return PIPE_PREFIX + "_" + wanted

    folded = wanted.casefold()
    for entry in profiles:
        if not isinstance(entry, dict):
            continue
        name = entry.get("name")
        display_name = entry.get("display_name")
        if isinstance(name, str) and name.casefold() == folded:
            return PIPE_PREFIX + "_" + name
        if isinstance(display_name, str) and display_name.casefold() == folded:
            return PIPE_PREFIX + "_" + name

    # Plugin-supplied profiles are not in Frosty's database. The plugin uses
    # the raw profile name as the suffix too, so this stays consistent.
    debug_log(
        'profile "%s" is not in pipe_profiles.json; using it as the pipe suffix'
        % wanted
    )
    return PIPE_PREFIX + "_" + wanted


PIPE_NAME = _resolve_pipe_name()
MCP_SERVER_NAME = "frosty"
MAX_RESPONSE_SIZE_BYTES = 32 * 1024 * 1024


def _parse_timeout_seconds(raw_value):
    if raw_value is None:
        return 30.0
    try:
        timeout = float(raw_value)
    except (TypeError, ValueError):
        return 30.0
    if not math.isfinite(timeout):
        return 30.0
    if timeout <= 0:
        return None
    return timeout


FROSTY_MCP_TIMEOUT_SECONDS = _parse_timeout_seconds(os.environ.get("FROSTY_MCP_TIMEOUT"))


# ============================================================================
# BRIDGE CLIENT
# ============================================================================

class FrostyNamedPipeBridgeClient:
    def __init__(self):
        self.handle = None
        self.timeout_seconds = FROSTY_MCP_TIMEOUT_SECONDS
        if win32file is None or pywintypes is None:
            raise RuntimeError(
                "Named pipe transport requires Windows and pywin32. "
                "Install with: pip install pywin32"
            )

    @property
    def connected(self):
        return self.handle is not None

    def connect(self):
        try:
            self.handle = win32file.CreateFile(
                PIPE_NAME,
                win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                0,
                None,
                win32file.OPEN_EXISTING,
                0,
                None,
            )
            return True
        except pywintypes.error:
            return False

    def _read_exact(self, size):
        chunks = []
        remaining = size
        while remaining > 0:
            try:
                chunk = win32file.ReadFile(self.handle, remaining)[1]
            except pywintypes.error as exc:
                raise ConnectionError(f"Pipe communication failed: {exc}") from exc
            if not chunk:
                raise ConnectionError("Connection closed while reading from Frosty pipe.")
            chunks.append(chunk)
            remaining -= len(chunk)
        return b"".join(chunks)

    def _exchange_once(self, req_json):
        header = struct.pack("<I", len(req_json))
        try:
            win32file.WriteFile(self.handle, header)
            win32file.WriteFile(self.handle, req_json)
        except pywintypes.error as exc:
            raise ConnectionError(f"Pipe communication failed: {exc}") from exc

        resp_header_buffer = self._read_exact(4)
        resp_len = struct.unpack("<I", resp_header_buffer)[0]
        if resp_len > MAX_RESPONSE_SIZE_BYTES:
            raise ConnectionError(f"Response too large: {resp_len} bytes")
        resp_body_buffer = self._read_exact(resp_len)
        try:
            return json.loads(resp_body_buffer.decode("utf-8"))
        except json.JSONDecodeError as exc:
            raise ConnectionError("Invalid JSON received from Frosty") from exc

    def _exchange_with_timeout(self, req_json, method):
        timeout = self.timeout_seconds
        if timeout is None:
            return self._exchange_once(req_json)

        result_holder = {}
        error_holder = {}

        def _worker():
            try:
                result_holder["response"] = self._exchange_once(req_json)
            except Exception as exc:
                error_holder["error"] = exc

        worker = threading.Thread(target=_worker, daemon=True)
        worker.start()
        worker.join(timeout)

        if worker.is_alive():
            self.close()
            raise TimeoutError(
                f"Command '{method}' timed out after {timeout:g}s (FROSTY_MCP_TIMEOUT)."
            )

        if "error" in error_holder:
            raise error_holder["error"]

        return result_holder["response"]

    def send_command(self, method, params=None):
        max_retries = 2
        last_error = None

        for attempt in range(max_retries):
            if not self.connected:
                if not self.connect():
                    raise ConnectionError(
                        "Frosty MCP Bridge is not reachable at %s. "
                        "Start Frosty Editor with FrostyMcpPlugin loaded first."
                        % PIPE_NAME
                    )

            request = {
                "jsonrpc": "2.0",
                "method": method,
                "params": params or {},
                "id": int(time.time() * 1000),
            }

            try:
                req_json = json.dumps(request).encode("utf-8")
                response = self._exchange_with_timeout(req_json, method)

                if "error" in response:
                    return {"success": False, "error": str(response["error"])}
                if "result" in response:
                    return response["result"]
                return response

            except (OSError, ConnectionError, TimeoutError) as e:
                self.close()
                last_error = e
                if attempt < max_retries - 1:
                    continue

        if last_error:
            raise last_error
        raise ConnectionError("Unknown communication error")

    def close(self):
        if self.handle:
            try:
                win32file.CloseHandle(self.handle)
            except Exception:
                pass
            self.handle = None


frosty_client = FrostyNamedPipeBridgeClient()

# ============================================================================
# MCP SERVER
# ============================================================================

mcp = FastMCP(
    MCP_SERVER_NAME,
    instructions=(
        "Frosty Editor MCP bridge v"
        + os.environ.get("FROSTY_MCP_VERSION", "2.0.1")
        + ". Includes Type Explorer (search_types, get_type_info, get_type_field, "
        "list_type_namespaces) and BF1 helpers: set_root_flags, add_to_network_registry, "
        "get_internal_pointer_ref, list_network_registry, safe_add_to_bundle, add_to_bf1_map_bundles."
    ),
)


@mcp.tool()
def ping() -> str:
    """Check connectivity with the Frosty Editor MCP bridge. Call this first."""
    return format_result(frosty_client.send_command("ping"))


@mcp.tool()
def get_editor_info() -> str:
    """Get Frosty Editor profile, EBX count, dirty count, SDK version and selected asset."""
    return format_result(frosty_client.send_command("get_editor_info"))


@mcp.tool()
def get_selected_asset() -> str:
    """Get the currently selected asset in Data Explorer and the opened editor asset."""
    return format_result(frosty_client.send_command("get_selected_asset"))


# ---------------------------------------------------------------------------
# BF1 DevPlugin helpers (registered early so clients always advertise them)
# ---------------------------------------------------------------------------

@mcp.tool()
def set_root_flags(name: str = "", guid: str = "", flags: int = 15) -> str:
    """Set EBX root object Flags. BF1 SpatialPrefabBlueprint root must be 15."""
    return format_result(frosty_client.send_command("set_root_flags", {
        "name": name, "guid": guid, "flags": flags,
    }))


@mcp.tool()
def get_internal_pointer_ref(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    root: bool = False,
) -> str:
    """Build External PointerRef (file_guid+class_guid) for NetworkRegistry.

    Pass root=True for asset root, or class_guid of an exported component.
    Never register MpSoldierAI (Giant's Shadow crash).
    """
    return format_result(frosty_client.send_command("get_internal_pointer_ref", {
        "name": name, "guid": guid, "class_guid": class_guid, "root": root or not class_guid,
    }))


@mcp.tool()
def list_network_registry(
    registry: str = "",
    name: str = "",
    query: str = "",
    offset: int = 0,
    limit: int = 200,
) -> str:
    """List Objects in a NetworkRegistryAsset (*_networkregistry_Win32)."""
    return format_result(frosty_client.send_command("list_network_registry", {
        "registry": registry or name, "name": name, "query": query,
        "offset": offset, "limit": limit,
    }))


@mcp.tool()
def add_to_network_registry(
    registry: str = "",
    asset: str = "",
    asset_guid: str = "",
    asset_class_guid: str = "",
    file_guid: str = "",
    class_guid: str = "",
    refs_json: str = "[]",
) -> str:
    """Add External PointerRefs to NetworkRegistry Objects (skip duplicates).

    Use asset= (optional asset_class_guid), or file_guid+class_guid, or refs_json.
    Never register MpSoldierAI.
    """
    import json as _json
    params = {
        "registry": registry, "asset": asset, "asset_guid": asset_guid,
        "asset_class_guid": asset_class_guid,
        "file_guid": file_guid, "class_guid": class_guid,
    }
    try:
        params["refs"] = _json.loads(refs_json) if refs_json else []
    except Exception:
        params["refs"] = []
    return format_result(frosty_client.send_command("add_to_network_registry", params))


@mcp.tool()
def remove_from_network_registry(
    registry: str = "",
    index: int = -1,
    file_guid: str = "",
    class_guid: str = "",
    refs_json: str = "[]",
) -> str:
    """Remove PointerRefs from NetworkRegistry by index or file_guid+class_guid / refs_json."""
    import json as _json
    params = {"registry": registry, "file_guid": file_guid, "class_guid": class_guid}
    if index >= 0:
        params["index"] = index
    try:
        params["refs"] = _json.loads(refs_json) if refs_json else []
    except Exception:
        params["refs"] = []
    return format_result(frosty_client.send_command("remove_from_network_registry", params))


@mcp.tool()
def safe_add_to_bundle(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Add EBX to bundle only if not already present; ModifyEbx so export is non-empty (BF1 tip)."""
    return format_result(frosty_client.send_command("safe_add_to_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
def add_to_bf1_map_bundles(name: str = "", guid: str = "", modify_ebx: bool = True) -> str:
    """Add asset to all BF1 MpSoldier-aligned map bundles (Core2 list, including */content paths)."""
    return format_result(frosty_client.send_command("add_to_bf1_map_bundles", {
        "name": name, "guid": guid, "modify_ebx": modify_ebx,
    }))


@mcp.tool()
def list_bf1_map_bundles() -> str:
    """List the 32+ BF1 map bundle names used by BF1DevPlugin Core2 (with resolve status)."""
    return format_result(frosty_client.send_command("list_bf1_map_bundles", {}))


@mcp.tool()
def open_asset(name: str = "", guid: str = "", create_default_editor: bool = True) -> str:
    """Open an EBX asset in Frosty Editor.

    Args:
        name: EBX asset path/name (e.g. "levels/mp/mp_amiens/mp_amiens").
        guid: EBX file GUID (optional alternative to name).
        create_default_editor: Whether to open the default asset editor tab.
    """
    return format_result(frosty_client.send_command("open_asset", {
        "name": name,
        "guid": guid,
        "create_default_editor": create_default_editor,
    }))


@mcp.tool()
def search_ebx(
    query: str = "",
    type: str = "",
    modified_only: bool = False,
    offset: int = 0,
    limit: int = 100,
    name: str = "",
) -> str:
    """Search EBX assets by name substring and optional type filter.

    Args:
        query: Case-insensitive substring matched against name/type.
        type: Frostbite type filter (subclass match), e.g. "TextureAsset".
        modified_only: Only return modified assets.
        offset: Pagination start index.
        limit: Max results (default 100, max 10000).

    Returns JSON with: success, total, offset, limit, returned, assets.
    """
    return format_result(frosty_client.send_command("search_ebx", {
        "query": query or name,
        "type": type,
        "modified_only": modified_only,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_ebx_entry(name: str = "", guid: str = "") -> str:
    """Get EBX metadata, linked assets, and bundle membership.

    `bundles` is the effective list (vanilla minus removed, plus editor-added),
    matching the Frosty Bundles tab. Also returns original_bundles, added_bundles
    (new memberships like a duplicated MpSoldier2), and removed_bundles.
    """
    return format_result(frosty_client.send_command("get_ebx_entry", {
        "name": name,
        "guid": guid,
    }))


@mcp.tool()
def get_ebx_xml(name: str = "", guid: str = "", max_chars: int = 200000,
                summary: bool = False, filter: str = "") -> str:
    """Export an EBX asset as XML text (truncated if larger than max_chars).

    Args:
        name: EBX asset name/path.
        guid: EBX file GUID.
        max_chars: Maximum characters to return (default 200000).
        summary: return tag counts instead of the full text.
        filter: return only lines containing this text.
    """
    return format_result(frosty_client.send_command("get_ebx_xml", {
        "name": name,
        "guid": guid,
        "max_chars": max_chars,
        "summary": summary,
        "filter": filter,
    }))


@mcp.tool()
def get_ebx_yaml(name: str = "", guid: str = "", max_chars: int = 200000,
                 summary: bool = False, filter: str = "") -> str:
    """Export an EBX asset as YAML text (truncated if larger than max_chars).

    Args:
        name: EBX asset name/path.
        guid: EBX file GUID.
        max_chars: Maximum characters to return (default 200000).
        summary: return tag counts instead of the full text.
        filter: return only lines containing this text.
    """
    return format_result(frosty_client.send_command("get_ebx_yaml", {
        "name": name,
        "guid": guid,
        "max_chars": max_chars,
        "summary": summary,
        "filter": filter,
    }))


@mcp.tool()
def list_modified_assets(kind: str = "all", offset: int = 0, limit: int = 100) -> str:
    """List modified assets in the current Frosty project.

    Args:
        kind: One of "all", "ebx", "res", "chunk".
        offset: Pagination start index.
        limit: Max results.
    """
    return format_result(frosty_client.send_command("list_modified_assets", {
        "kind": kind,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def search_res(query: str = "", modified_only: bool = False, offset: int = 0, limit: int = 100) -> str:
    """Search RES assets by name substring."""
    return format_result(frosty_client.send_command("search_res", {
        "query": query,
        "modified_only": modified_only,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_res_entry(name: str = "", res_rid: str = "") -> str:
    """Get RES asset metadata by name or resource RID."""
    params = {"name": name}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("get_res_entry", params))


@mcp.tool()
def search_chunks(query: str = "", modified_only: bool = False, offset: int = 0, limit: int = 100) -> str:
    """Search chunk assets by GUID/name substring."""
    return format_result(frosty_client.send_command("search_chunks", {
        "query": query,
        "modified_only": modified_only,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_chunk_entry(id: str = "", guid: str = "") -> str:
    """Get chunk asset metadata by chunk GUID/id."""
    return format_result(frosty_client.send_command("get_chunk_entry", {
        "id": id or guid,
        "guid": guid or id,
    }))


@mcp.tool()
def list_bundles(query: str = "", offset: int = 0, limit: int = 100) -> str:
    """List bundles, optionally filtered by name substring."""
    return format_result(frosty_client.send_command("list_bundles", {
        "query": query,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_bundle(name: str) -> str:
    """Get a bundle and its contained EBX/RES/chunk assets (first page of each)."""
    return format_result(frosty_client.send_command("get_bundle", {"name": name}))


@mcp.tool()
def get_asset_dependencies(name: str = "", guid: str = "") -> str:
    """List EBX dependencies / linked assets for an EBX asset."""
    return format_result(frosty_client.send_command("get_asset_dependencies", {
        "name": name,
        "guid": guid,
    }))


@mcp.tool()
def get_type_info(
    name: str,
    declared_only: bool = True,
    include_inherited: bool = False,
    expand_nested: int = 0,
) -> str:
    """Type Explorer: full SDK type detail (kind, ebx namespace, inheritance, fields).

    Matches the Frosty Type Explorer pane. name can be 'BFUISoldierInfoEntityData'
    or 'TunguskaShared.BFUISoldierInfoEntityData'. Enums return members with values.
    Fields use EBX names (Float32, PointerRef<T>, List<...>). Set expand_nested=1..3
    to inline nested SDK struct/class/enum definitions. include_inherited=True lists
    base-class fields as well (Type Explorer default is declared-only).
    """
    return format_result(frosty_client.send_command("get_type_info", {
        "name": name,
        "declared_only": declared_only,
        "include_inherited": include_inherited,
        "expand_nested": expand_nested,
    }))


@mcp.tool()
def search_types(
    query: str = "",
    base_type: str = "",
    namespace: str = "",
    kind: str = "all",
    hide_empty: bool = False,
    match_subclass: bool = True,
    compact: bool = True,
    offset: int = 0,
    limit: int = 100,
) -> str:
    """Type Explorer: search SDK types (same list as the Type Explorer plugin).

    query matches type name, Module.Name, or (if match_subclass) subclasses of query.
    namespace filters EbxClassMeta module (e.g. 'Audio', 'TunguskaShared').
    kind is 'all' | 'class' | 'struct' | 'enum'. hide_empty hides types with no fields.
    compact (default true) returns name/kind/namespace only; set false for full details.
    """
    return format_result(frosty_client.send_command("search_types", {
        "query": query,
        "base_type": base_type,
        "namespace": namespace,
        "kind": kind,
        "hide_empty": hide_empty,
        "compact": compact,
        "match_subclass": match_subclass,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_type_field(name: str, field: str, expand_nested: int = 1) -> str:
    """Type Explorer: open a field's nested type (click-through on PointerRef/List/struct).

    field is a property name or dotted path (e.g. 'Soldier', 'Transform.trans').
    Returns the field meta plus the resolved SDK type's full definition.
    """
    return format_result(frosty_client.send_command("get_type_field", {
        "name": name,
        "field": field,
        "expand_nested": expand_nested,
    }))


@mcp.tool()
def list_type_namespaces(query: str = "", offset: int = 0, limit: int = 100) -> str:
    """Type Explorer: list EBX modules/namespaces with type counts (class/struct/enum)."""
    return format_result(frosty_client.send_command("list_type_namespaces", {
        "query": query,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def find_references(name: str = "", guid: str = "", offset: int = 0, limit: int = 100) -> str:
    """Find EBX assets that reference the given asset GUID."""
    return format_result(frosty_client.send_command("find_references", {
        "name": name,
        "guid": guid,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_ebx_property(name: str = "", guid: str = "", path: str = "") -> str:
    """Read an EBX root-object property by dotted path (e.g. 'Name', 'Transform.trans.x', 'Items[0]')."""
    return format_result(frosty_client.send_command("get_ebx_property", {
        "name": name,
        "guid": guid,
        "path": path,
    }))


@mcp.tool()
def set_ebx_property(name: str = "", guid: str = "", path: str = "", value: str = "") -> str:
    """Set an EBX root-object property by dotted path.

    Args:
        name: EBX asset name.
        guid: EBX GUID.
        path: Property path (e.g. 'Name', 'Flags', 'Items[0].Value').
        value: JSON value encoded as string. Examples: '"Hello"', '1.5', 'true',
               '{"asset":"path/to/other"}' for PointerRef,
               '{"x":1,"y":2,"z":3}' for vectors.
    """
    import json as _json
    try:
        parsed = _json.loads(value)
    except Exception:
        parsed = value
    return format_result(frosty_client.send_command("set_ebx_property", {
        "name": name,
        "guid": guid,
        "path": path,
        "value": parsed,
    }))


@mcp.tool()
def export_ebx_bin(name: str = "", guid: str = "", path: str = "") -> str:
    """Export EBX asset to a .bin file on disk."""
    return format_result(frosty_client.send_command("export_ebx_bin", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
def import_ebx_bin(name: str = "", guid: str = "", path: str = "", data_only: bool = True) -> str:
    """Import a .bin file into an existing EBX asset (keeps GUIDs when data_only=True)."""
    return format_result(frosty_client.send_command("import_ebx_bin", {
        "name": name, "guid": guid, "path": path, "data_only": data_only,
    }))


@mcp.tool()
def export_asset(name: str = "", guid: str = "", path: str = "", format: str = "xml") -> str:
    """Export EBX via AssetDefinition (xml/yaml/bin/png/dds/fbx depending on asset type & plugins)."""
    return format_result(frosty_client.send_command("export_asset", {
        "name": name, "guid": guid, "path": path, "format": format,
    }))


@mcp.tool()
def duplicate_ebx(name: str = "", guid: str = "", new_name: str = "", copy_linked: bool = False) -> str:
    """Duplicate an EBX asset to a new name (optionally duplicate linked texture res/chunk)."""
    return format_result(frosty_client.send_command("duplicate_ebx", {
        "name": name, "guid": guid, "new_name": new_name, "copy_linked": copy_linked,
    }))


@mcp.tool()
def create_ebx(name: str = "", type: str = "", bundle: str = "") -> str:
    """Create a new EBX asset of the given Frostbite type name, optionally adding to a bundle."""
    return format_result(frosty_client.send_command("create_ebx", {
        "name": name, "type": type, "bundle": bundle,
    }))


@mcp.tool()
def revert_asset(kind: str = "ebx", name: str = "", guid: str = "", id: str = "",
                 res_rid: str = "", data_only: bool = False) -> str:
    """Revert modifications on an ebx/res/chunk asset."""
    params = {"kind": kind, "name": name, "guid": guid or id, "id": id, "data_only": data_only}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("revert_asset", params))


@mcp.tool()
def export_res(name: str = "", res_rid: str = "", path: str = "") -> str:
    """Export raw RES bytes to a file."""
    params = {"name": name, "path": path}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("export_res", params))


@mcp.tool()
def import_res(name: str = "", res_rid: str = "", path: str = "") -> str:
    """Import raw bytes into an existing RES asset."""
    params = {"name": name, "path": path}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("import_res", params))


@mcp.tool()
def export_chunk(id: str = "", guid: str = "", path: str = "") -> str:
    """Export raw chunk bytes to a file."""
    return format_result(frosty_client.send_command("export_chunk", {
        "id": id or guid, "guid": guid or id, "path": path,
    }))


@mcp.tool()
def import_chunk(id: str = "", guid: str = "", path: str = "") -> str:
    """Import raw bytes into an existing chunk."""
    return format_result(frosty_client.send_command("import_chunk", {
        "id": id or guid, "guid": guid or id, "path": path,
    }))


@mcp.tool()
def duplicate_res(name: str = "", res_rid: str = "", new_name: str = "", res_type: str = "") -> str:
    """Duplicate a RES asset to a new name."""
    params = {"name": name, "new_name": new_name, "res_type": res_type}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("duplicate_res", params))


@mcp.tool()
def duplicate_chunk(id: str = "", guid: str = "") -> str:
    """Duplicate a chunk to a new GUID (keeps original bundle membership)."""
    return format_result(frosty_client.send_command("duplicate_chunk", {
        "id": id or guid, "guid": guid or id,
    }))


@mcp.tool()
def add_to_bundle(bundle: str, kind: str = "ebx", name: str = "", guid: str = "",
                  id: str = "", res_rid: str = "") -> str:
    """Add an ebx/res/chunk asset to a bundle."""
    params = {"bundle": bundle, "kind": kind, "name": name, "guid": guid, "id": id}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("add_to_bundle", params))


@mcp.tool()
def remove_from_bundle(bundle: str, kind: str = "ebx", name: str = "", guid: str = "",
                       id: str = "", res_rid: str = "") -> str:
    """Remove an ebx/res/chunk asset from a bundle."""
    params = {"bundle": bundle, "kind": kind, "name": name, "guid": guid, "id": id}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(frosty_client.send_command("remove_from_bundle", params))


@mcp.tool()
def create_bundle(name: str, type: str = "BlueprintBundle", super_bundle: str = "") -> str:
    """Create a new bundle (and optionally a super bundle)."""
    return format_result(frosty_client.send_command("create_bundle", {
        "name": name, "type": type, "super_bundle": super_bundle,
    }))


@mcp.tool()
def export_texture(name: str = "", guid: str = "", path: str = "", format: str = "dds") -> str:
    """Export a TextureAsset to png/tga/hdr/dds (requires TexturePlugin)."""
    return format_result(frosty_client.send_command("export_texture", {
        "name": name, "guid": guid, "path": path, "format": format,
    }))


@mcp.tool()
def import_texture(name: str = "", guid: str = "", path: str = "") -> str:
    """Import png/tga/hdr/dds into a TextureAsset (requires TexturePlugin for non-DDS)."""
    return format_result(frosty_client.send_command("import_texture", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
def export_mesh(
    name: str = "",
    guid: str = "",
    path: str = "",
    format: str = "fbx",
    skeleton: str = "",
    fbx_version: str = "FBX_2012",
    scale: str = "Centimeters",
    flatten_hierarchy: bool = False,
    single_lod: bool = False,
) -> str:
    """Export a mesh asset to FBX/OBJ (requires MeshSetPlugin). No UI dialog."""
    return format_result(frosty_client.send_command("export_mesh", {
        "name": name,
        "guid": guid,
        "path": path,
        "format": format,
        "skeleton": skeleton,
        "fbx_version": fbx_version,
        "scale": scale,
        "flatten_hierarchy": flatten_hierarchy,
        "single_lod": single_lod,
    }))


@mcp.tool()
def import_mesh(name: str = "", guid: str = "", path: str = "", skeleton: str = "") -> str:
    """Import an FBX into a mesh asset (requires MeshSetPlugin)."""
    return format_result(frosty_client.send_command("import_mesh", {
        "name": name, "guid": guid, "path": path, "skeleton": skeleton,
    }))


# ---------------------------------------------------------------------------
# Blueprint connections
# ---------------------------------------------------------------------------

@mcp.tool()
def list_connections(name: str = "", guid: str = "", kind: str = "all",
                     offset: int = 0, limit: int = 200,
                     compact: bool = True, component_guid: str = "",
                     filter: str = "") -> str:
    """List Property / Event / Link connections on a blueprint EBX.

    Args:
        kind: all | property | event | link
        compact: one-line source/target instead of full pointer objects (default true)
        component_guid: only connections touching this component GUID
        filter: only connections whose serialized fields contain this text
    """
    return format_result(frosty_client.send_command("list_connections", {
        "name": name, "guid": guid, "kind": kind, "offset": offset, "limit": limit,
        "compact": compact, "component_guid": component_guid, "filter": filter,
    }))


@mcp.tool()
def add_property_connection(
    name: str = "",
    guid: str = "",
    source_guid: str = "",
    target_guid: str = "",
    source_field: str = "",
    target_field: str = "",
    realm: str = "ClientAndServer",
    prop_type: str = "Default",
    source_cant_be_static: bool = False,
    source_asset: str = "",
    target_asset: str = "",
    flags: str = "",
) -> str:
    """Add a PropertyConnection. realm: Invalid|ClientAndServer|Client|Server|NetworkedClient|NetworkedClientAndServer.
    prop_type: Default|Interface|Exposed|Invalid. Prefer source_guid/target_guid for internal components.
    """
    params = {
        "name": name, "guid": guid,
        "source_guid": source_guid, "target_guid": target_guid,
        "source_field": source_field, "target_field": target_field,
        "realm": realm, "prop_type": prop_type,
        "source_cant_be_static": source_cant_be_static,
    }
    if source_asset:
        params["source_asset"] = source_asset
    if target_asset:
        params["target_asset"] = target_asset
    if flags:
        params["flags"] = flags
    return format_result(frosty_client.send_command("add_property_connection", params))


@mcp.tool()
def add_event_connection(
    name: str = "",
    guid: str = "",
    source_guid: str = "",
    target_guid: str = "",
    source_event: str = "",
    target_event: str = "",
    target_type: str = "EventConnectionTargetType_ClientAndServer",
    source_asset: str = "",
    target_asset: str = "",
) -> str:
    """Add an EventConnection. target_type examples: EventConnectionTargetType_Client / _Server / _ClientAndServer."""
    params = {
        "name": name, "guid": guid,
        "source_guid": source_guid, "target_guid": target_guid,
        "source_event": source_event, "target_event": target_event,
        "target_type": target_type,
    }
    if source_asset:
        params["source_asset"] = source_asset
    if target_asset:
        params["target_asset"] = target_asset
    return format_result(frosty_client.send_command("add_event_connection", params))


@mcp.tool()
def add_link_connection(
    name: str = "",
    guid: str = "",
    source_guid: str = "",
    target_guid: str = "",
    source_field: str = "",
    target_field: str = "",
    source_asset: str = "",
    target_asset: str = "",
) -> str:
    """Add a LinkConnection between components/assets."""
    params = {
        "name": name, "guid": guid,
        "source_guid": source_guid, "target_guid": target_guid,
        "source_field": source_field, "target_field": target_field,
    }
    if source_asset:
        params["source_asset"] = source_asset
    if target_asset:
        params["target_asset"] = target_asset
    return format_result(frosty_client.send_command("add_link_connection", params))


@mcp.tool()
def update_property_connection(
    name: str = "",
    guid: str = "",
    index: int = -1,
    source_guid: str = "",
    target_guid: str = "",
    source_field: str = "",
    target_field: str = "",
    realm: str = "",
    prop_type: str = "",
    source_cant_be_static: bool = False,
    flags: str = "",
) -> str:
    """Update an existing PropertyConnection by index (fields/flags optional)."""
    params = {"name": name, "guid": guid, "index": index}
    if source_guid:
        params["source_guid"] = source_guid
    if target_guid:
        params["target_guid"] = target_guid
    if source_field != "":
        params["source_field"] = source_field
    if target_field != "":
        params["target_field"] = target_field
    if realm:
        params["realm"] = realm
    if prop_type:
        params["prop_type"] = prop_type
    if flags:
        params["flags"] = flags
    params["source_cant_be_static"] = source_cant_be_static
    return format_result(frosty_client.send_command("update_property_connection", params))


@mcp.tool()
def update_event_connection(
    name: str = "",
    guid: str = "",
    index: int = -1,
    source_guid: str = "",
    target_guid: str = "",
    source_event: str = "",
    target_event: str = "",
    target_type: str = "",
) -> str:
    """Update an existing EventConnection by index."""
    params = {"name": name, "guid": guid, "index": index}
    if source_guid:
        params["source_guid"] = source_guid
    if target_guid:
        params["target_guid"] = target_guid
    if source_event:
        params["source_event"] = source_event
    if target_event:
        params["target_event"] = target_event
    if target_type:
        params["target_type"] = target_type
    return format_result(frosty_client.send_command("update_event_connection", params))


@mcp.tool()
def update_link_connection(
    name: str = "",
    guid: str = "",
    index: int = -1,
    source_guid: str = "",
    target_guid: str = "",
    source_field: str = "",
    target_field: str = "",
) -> str:
    """Update an existing LinkConnection by index."""
    params = {"name": name, "guid": guid, "index": index}
    if source_guid:
        params["source_guid"] = source_guid
    if target_guid:
        params["target_guid"] = target_guid
    if source_field:
        params["source_field"] = source_field
    if target_field:
        params["target_field"] = target_field
    return format_result(frosty_client.send_command("update_link_connection", params))


@mcp.tool()
def remove_connection(name: str = "", guid: str = "", kind: str = "property", index: int = -1) -> str:
    """Remove a connection by kind (property|event|link) and index."""
    return format_result(frosty_client.send_command("remove_connection", {
        "name": name, "guid": guid, "kind": kind, "index": index,
    }))


@mcp.tool()
def remove_connections_by_guid(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    kind: str = "all",
) -> str:
    """Remove all property/event/link connections touching a component class_guid (kind=all|property|event|link)."""
    return format_result(frosty_client.send_command("remove_connections_by_guid", {
        "name": name, "guid": guid, "class_guid": class_guid, "kind": kind,
    }))


@mcp.tool()
def decode_connection_flags(flags: str = "0") -> str:
    """Decode PropertyConnection flags into realm / prop_type / source_cant_be_static."""
    return format_result(frosty_client.send_command("decode_connection_flags", {"flags": flags}))


# ---------------------------------------------------------------------------
# Blueprint components
# ---------------------------------------------------------------------------

@mcp.tool()
def list_components(name: str = "", guid: str = "", query: str = "", type: str = "",
                    offset: int = 0, limit: int = 200, compact: bool = True) -> str:
    """List internal EBX objects/components with class_guid, Flags, Realm.

    compact (default true) omits the object_flags breakdown; set false for it.
    """
    return format_result(frosty_client.send_command("list_components", {
        "name": name, "guid": guid, "query": query, "type": type,
        "offset": offset, "limit": limit, "compact": compact,
    }))


@mcp.tool()
def get_component(name: str = "", guid: str = "", class_guid: str = "",
                  index: int = -1, component_type: str = "", type_index: int = 0) -> str:
    """Get one component (by class_guid / index / component_type+type_index) including property dump."""
    params = {"name": name, "guid": guid, "class_guid": class_guid,
              "component_type": component_type, "type_index": type_index}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("get_component", params))


@mcp.tool()
def create_component(
    name: str = "",
    guid: str = "",
    type: str = "",
    array: str = "Objects",
    flag_preset: str = "",
    realm: str = "",
    properties_json: object = "",
) -> str:
    """Create a new internal component from Frostbite SDK type (e.g. DelayEntityData).

    Args:
        type: SDK class name.
        array: Root list to append PointerRef into (Objects or Components).
        flag_preset: client | server | client_and_server (optional).
        realm: e.g. ClientAndServer / Client / Server.
        properties_json: JSON object of initial property values (string or object).
    """
    params = {"name": name, "guid": guid, "type": type, "array": array}
    if flag_preset:
        params["flag_preset"] = flag_preset
    if realm:
        params["realm"] = realm
    props = _json_arg(properties_json, "properties_json")
    if props is not None:
        params["properties"] = props
    return format_result(frosty_client.send_command("create_component", params))


@mcp.tool()
def duplicate_component(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    array: str = "Objects",
    properties_json: object = "",
) -> str:
    """Duplicate an internal component (new GUIDs via FrostyClipboard DeepCopy)."""
    params = {"name": name, "guid": guid, "class_guid": class_guid, "array": array}
    if index >= 0:
        params["index"] = index
    props = _json_arg(properties_json, "properties_json")
    if props is not None:
        params["properties"] = props
    return format_result(frosty_client.send_command("duplicate_component", params))


@mcp.tool()
def set_component_flags(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    flag_preset: str = "",
    realm: str = "",
    flags_json: str = "",
) -> str:
    """Set component Object Flags and optional Realm.

    Args:
        flag_preset: client | server | client_and_server
        flags_json: optional JSON object overriding individual bits, e.g.
          '{"client_event":true,"server_event":true,"client_property":true,"server_property":true,"unknown":true}'
        realm: ClientAndServer | Client | Server | ...
    """
    import json as _json
    params = {"name": name, "guid": guid, "class_guid": class_guid}
    if index >= 0:
        params["index"] = index
    if flag_preset:
        params["flag_preset"] = flag_preset
    if realm:
        params["realm"] = realm
    if flags_json:
        try:
            params.update(_json.loads(flags_json))
        except Exception:
            pass
    return format_result(frosty_client.send_command("set_component_flags", params))


@mcp.tool()
def get_component_property(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    path: str = "",
) -> str:
    """Read a property on an internal component by dotted path (SDK fields)."""
    params = {"name": name, "guid": guid, "class_guid": class_guid, "path": path}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("get_component_property", params))


@mcp.tool()
def set_component_property(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    path: str = "",
    value: str = "",
) -> str:
    """Set a property on an internal component. value is JSON-encoded string.

    Examples: value='1.5', value='true', value='"OnDelay"', value='{"class_guid":"..."}' for PointerRef.
    """
    import json as _json
    try:
        parsed = _json.loads(value)
    except Exception:
        parsed = value
    params = {"name": name, "guid": guid, "class_guid": class_guid, "path": path, "value": parsed}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("set_component_property", params))


@mcp.tool()
def remove_component(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    remove_object: bool = True,
) -> str:
    """Remove a component: strips touching connections and array refs; optionally RemoveObject."""
    params = {"name": name, "guid": guid, "class_guid": class_guid, "remove_object": remove_object}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("remove_component", params))


@mcp.tool()
def remove_ebx_item(
    name: str = "",
    guid: str = "",
    path: str = "",
    class_guid: str = "",
    index: int = -1,
    indices: str = "",
    remove_all: bool = False,
    remove_object: bool = True,
    cleanup_connections: bool = True,
    component_type: str = "",
    type_index: int = 0,
) -> str:
    """Frosty property-grid 'Remove item': delete an element from any EBX array.

    Works anywhere Frosty shows Remove item: Components, Objects, Children,
    MemberDatas, Property/Event/LinkConnections, Interface Fields/Events, nested
    lists on a host object (e.g. StaticModelGroupEntityData.Components).

    Path examples:
      - Components[1]
      - Objects[0]
      - Object.Components[1]   (walks PointerRef Internal)
      - $PropertyConnections[3]
      - $EventConnections[0]
      - Fields[0]  (with class_guid of InterfaceDescriptorData)
      - MemberDatas[2]  (with class_guid of the host)

    indices: JSON array string like '[2,0]' (removed high-to-low).
    all=True empties the list. remove_object/cleanup_connections apply only
    to Internal PointerRef objects (not connection structs).
    """
    params = {
        "name": name,
        "guid": guid,
        "path": path,
        "class_guid": class_guid,
        "remove_object": remove_object,
        "cleanup_connections": cleanup_connections,
        "all": remove_all,
        "type_index": type_index,
    }
    if index >= 0:
        params["index"] = index
    if component_type:
        params["component_type"] = component_type
    raw = (indices or "").strip()
    if raw:
        params["indices"] = json.loads(raw)
    return format_result(frosty_client.send_command("remove_ebx_item", params))


@mcp.tool()
def list_ebx_class_types(
    name: str = "",
    guid: str = "",
    path: str = "",
    base_type: str = "",
    class_guid: str = "",
    index: int = -1,
    query: str = "",
    offset: int = 0,
    limit: int = 200,
) -> str:
    """List SDK types allowed for Frosty 'Create Class' on a PointerRef field.

    Provide either:
      - path (+ optional class_guid/index of host component) to read EbxFieldMeta BaseType, or
      - base_type directly (e.g. 'GameObjectData', 'ComponentData').
    """
    params = {
        "name": name, "guid": guid, "path": path, "base_type": base_type,
        "class_guid": class_guid, "query": query, "offset": offset, "limit": limit,
    }
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("list_ebx_class_types", params))


@mcp.tool()
def create_ebx_class(
    name: str = "",
    guid: str = "",
    type: str = "",
    path: str = "",
    class_guid: str = "",
    index: int = -1,
    mode: str = "",
    reuse_guid: bool = True,
    create_only: bool = False,
    flag_preset: str = "",
    realm: str = "",
    properties_json: object = "",
) -> str:
    """Create a new class inside an EBX (Frosty property-grid 'Create Class' on PointerRef).

    Mirrors FrostyPointerRefEditor: TypeLibrary.CreateObject → SetInstanceGuid →
    DataBusPeer Flags seed → asset.AddObject → assign PointerRef to host.path.

    Args:
        type: SDK class name to create (from list_ebx_class_types).
        path: Field on host component, e.g. 'Object', 'Components', 'Components[0]', 'Health'.
              Bare list property → append; PointerRef / list[i] → set.
        class_guid / index: Host component (omit = root object).
        mode: 'set' | 'append' (default auto-inferred from path).
        reuse_guid: When replacing an existing Internal PointerRef, keep its GUID (Frosty default).
        create_only: Only AddObject; do not assign to a path.
        flag_preset / realm / properties_json: Same as create_component.
    """
    params = {
        "name": name, "guid": guid, "type": type, "path": path,
        "class_guid": class_guid, "mode": mode,
        "reuse_guid": reuse_guid, "create_only": create_only,
    }
    if index >= 0:
        params["index"] = index
    if flag_preset:
        params["flag_preset"] = flag_preset
    if realm:
        params["realm"] = realm
    props = _json_arg(properties_json, "properties_json")
    if props is not None:
        params["properties"] = props
    return format_result(frosty_client.send_command("create_ebx_class", params))


# ---------------------------------------------------------------------------
# InterfaceDescriptorData (BF1DevPlugin Blueprint CreateFields / Events)
# ---------------------------------------------------------------------------

@mcp.tool()
def list_interface(name: str = "", guid: str = "") -> str:
    """List Interface Fields / InputEvents / OutputEvents on a blueprint EBX."""
    return format_result(frosty_client.send_command("list_interface", {"name": name, "guid": guid}))


@mcp.tool()
def ensure_interface(name: str = "", guid: str = "") -> str:
    """Ensure InterfaceDescriptorData exists (create if missing)."""
    return format_result(frosty_client.send_command("ensure_interface", {"name": name, "guid": guid}))


@mcp.tool()
def add_interface_field(
    name: str = "",
    guid: str = "",
    field_name: str = "",
    value: str = "",
    access_type: str = "FieldAccessType_Target",
) -> str:
    """Add an Interface DataField (BF1 CreateFields)."""
    return format_result(frosty_client.send_command("add_interface_field", {
        "name": name, "guid": guid, "field_name": field_name,
        "value": value, "access_type": access_type,
    }))


@mcp.tool()
def update_interface_field(
    name: str = "",
    guid: str = "",
    index: int = -1,
    field_name: str = "",
    new_name: str = "",
    value: str = None,
    access_type: str = "",
) -> str:
    """Update Interface field by index or field_name. Use new_name to rename; pass value to set/clear."""
    params = {"name": name, "guid": guid, "field_name": field_name}
    if index >= 0:
        params["index"] = index
    if new_name:
        params["new_name"] = new_name
    if value is not None:
        params["value"] = value
    if access_type:
        params["access_type"] = access_type
    return format_result(frosty_client.send_command("update_interface_field", params))


@mcp.tool()
def remove_interface_field(
    name: str = "",
    guid: str = "",
    index: int = -1,
    field_name: str = "",
) -> str:
    """Remove Interface field by index or field_name."""
    params = {"name": name, "guid": guid, "field_name": field_name}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("remove_interface_field", params))


@mcp.tool()
def add_interface_event(
    name: str = "",
    guid: str = "",
    event_name: str = "",
    kind: str = "input",
) -> str:
    """Add Interface InputEvent or OutputEvent (kind=input|output)."""
    return format_result(frosty_client.send_command("add_interface_event", {
        "name": name, "guid": guid, "event_name": event_name, "kind": kind,
    }))


@mcp.tool()
def remove_interface_event(
    name: str = "",
    guid: str = "",
    kind: str = "input",
    index: int = -1,
    event_name: str = "",
) -> str:
    """Remove Interface event by kind + index or event_name."""
    params = {"name": name, "guid": guid, "kind": kind, "event_name": event_name}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("remove_interface_event", params))


@mcp.tool()
def search_ebx_fields(
    name: str = "",
    guid: str = "",
    query: str = "",
    scope: str = "both",
    type: str = "",
    class_guid: str = "",
    case_insensitive: bool = True,
    include_connections: bool = True,
    max_depth: int = 8,
    offset: int = 0,
    limit: int = 200,
) -> str:
    """Search field names and/or values inside all components of an EBX asset (词条搜索).

    Args:
        query: Substring to find (e.g. 'OnDelay', 'ID_UI_', a number, enum name).
        scope: 'name' (property names only), 'value' (field values only), or 'both'.
        type: Optional component type filter (e.g. DelayEntityData).
        class_guid: Optional restrict to one component.
        include_connections: Also search Property/Event/Link connection field names.

    Each match includes object_index, class_guid, path (use with get/set_ebx_field), value, match_in.
    Connection matches use paths like '$PropertyConnections[0].SourceField'.
    """
    return format_result(frosty_client.send_command("search_ebx_fields", {
        "name": name,
        "guid": guid,
        "query": query,
        "scope": scope,
        "type": type,
        "class_guid": class_guid,
        "case_insensitive": case_insensitive,
        "include_connections": include_connections,
        "max_depth": max_depth,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
def get_ebx_field(
    name: str = "",
    guid: str = "",
    path: str = "",
    class_guid: str = "",
    index: int = -1,
) -> str:
    """Read one field inside an EBX component (or connection) by path from search_ebx_fields.

    Args:
        path: e.g. 'Delay', 'Fields[0].Name', or '$EventConnections[2].SourceEvent.Name'
        class_guid / index: identify the component (optional if path starts with '$')
    """
    params = {"name": name, "guid": guid, "path": path, "class_guid": class_guid}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("get_ebx_field", params))


@mcp.tool()
def set_ebx_field(
    name: str = "",
    guid: str = "",
    path: str = "",
    value: str = "",
    class_guid: str = "",
    index: int = -1,
) -> str:
    """Edit one field inside an EBX component (or connection). value is JSON-encoded.

    Examples:
      set_ebx_field(..., class_guid='...', path='Delay', value='1.5')
      set_ebx_field(..., class_guid='...', path='Fields[0].Name', value='"ID_New"')
      set_ebx_field(..., path='$PropertyConnections[0].SourceField', value='"Out"')
    """
    import json as _json
    try:
        parsed = _json.loads(value)
    except Exception:
        parsed = value
    params = {"name": name, "guid": guid, "path": path, "class_guid": class_guid, "value": parsed}
    if index >= 0:
        params["index"] = index
    return format_result(frosty_client.send_command("set_ebx_field", params))


@mcp.tool()
def research_component_usage(
    type: str = "",
    name_query: str = "",
    asset_type: str = "",
    name: str = "",
    include_subclasses: bool = True,
    modified_only: bool = False,
    max_scan: int = 150,
    max_examples: int = 20,
    include_properties: bool = True,
) -> str:
    """Research how an SDK component type (e.g. DelayEntityData) is used across EBX assets.

    Scans blueprints for instances of the type, then aggregates:
      - property/event/link field names used when the component is source or target
      - common peer component types on the other end of connections
      - concrete examples with properties + related connections

    Args:
        type: SDK class name (required), e.g. 'DelayEntityData', 'EventGateEntityData'.
        name_query: Optional EBX path substring filter (narrows scan; recommended).
        asset_type: Optional root EBX type filter (e.g. 'LogicPrefabBlueprint').
        name: Optional single EBX to study (skips global scan).
        max_scan: Max assets to load (default 150). Raise for broader research.
        max_examples: Max detailed instance examples to return.
    """
    return format_result(frosty_client.send_command("research_component_usage", {
        "type": type,
        "name_query": name_query,
        "asset_type": asset_type,
        "name": name,
        "include_subclasses": include_subclasses,
        "modified_only": modified_only,
        "max_scan": max_scan,
        "max_examples": max_examples,
        "include_properties": include_properties,
    }))


# ---------------------------------------------------------------------------
# Extra utilities (hash / localization / clipboard / inspect / smart bundle)
# ---------------------------------------------------------------------------

@mcp.tool()
def hash_string(text: str = "", lowercase: bool = False, float_value: str = "",
                value: str = "") -> str:
    """Compute FNV1 / localized hash (and optional float→hex). text may be '0x...' to parse hex.

    value is accepted as an alias for text.
    """
    if not text:
        text = value
    params = {"text": text, "lowercase": lowercase}
    if float_value:
        params["float"] = float_value
    return format_result(frosty_client.send_command("hash_string", params))


@mcp.tool()
def get_localized_string(id: str = "", hash: str = "") -> str:
    """Get a localized string by ID_... or hash (uint / 0xHEX)."""
    params = {}
    if id:
        params["id"] = id
    if hash:
        params["hash"] = hash
    return format_result(frosty_client.send_command("get_localized_string", params))


@mcp.tool()
def set_localized_string(value: str = "", id: str = "", hash: str = "") -> str:
    """Set a localized string by ID_... or hash."""
    params = {"value": value}
    if id:
        params["id"] = id
    if hash:
        params["hash"] = hash
    return format_result(frosty_client.send_command("set_localized_string", params))


@mcp.tool()
def revert_localized_string(hash: str = "", id: str = "") -> str:
    """Revert a localized string hash to original."""
    params = {}
    if hash:
        params["hash"] = hash
    if id:
        params["id"] = id
    return format_result(frosty_client.send_command("revert_localized_string", params))


@mcp.tool()
def list_localized_strings(
    query: str = "",
    modified_only: bool = False,
    offset: int = 0,
    limit: int = 100,
) -> str:
    """Enumerate / search localized strings (optional modified_only filter)."""
    return format_result(frosty_client.send_command("list_localized_strings", {
        "query": query, "modified_only": modified_only, "offset": offset, "limit": limit,
    }))


@mcp.tool()
def clipboard_paste_object(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    source_name: str = "",
    source_guid: str = "",
    source_index: int = -1,
    array: str = "Objects",
) -> str:
    """Paste an object into target EBX via FrostyClipboard (regenerates GUIDs). Same as BF1 GetFrostyClipboardPasteData."""
    params = {
        "name": name, "guid": guid, "class_guid": class_guid,
        "source_name": source_name, "source_guid": source_guid, "array": array,
    }
    if source_index >= 0:
        params["source_index"] = source_index
    return format_result(frosty_client.send_command("clipboard_paste_object", params))


@mcp.tool()
def create_struct(
    type: str = "",
    name: str = "",
    guid: str = "",
    path: str = "",
    class_guid: str = "",
    properties_json: object = "",
) -> str:
    """Create a Frostbite struct (DataField, LinearTransform, ...). Optionally append to EBX path list."""
    params = {"type": type, "name": name, "guid": guid, "path": path, "class_guid": class_guid}
    props = _json_arg(properties_json, "properties_json")
    if props is not None:
        params["properties"] = props
    return format_result(frosty_client.send_command("create_struct", params))


@mcp.tool()
def get_texture_info(name: str = "", guid: str = "") -> str:
    """Inspect texture size/format/mips/chunk/res/bundles."""
    return format_result(frosty_client.send_command("get_texture_info", {"name": name, "guid": guid}))


@mcp.tool()
def get_mesh_info(name: str = "", guid: str = "") -> str:
    """Inspect mesh LODs/sections/bones/materials/chunks (requires MeshSetPlugin)."""
    return format_result(frosty_client.send_command("get_mesh_info", {"name": name, "guid": guid}))


@mcp.tool()
def add_to_bundle_smart(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Add EBX to bundle and linked RES/Chunk (Texture/Mesh/SoundWave) like BundleEditorPlugin."""
    return format_result(frosty_client.send_command("add_to_bundle_smart", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
def remove_from_bundle_smart(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Remove EBX and linked RES/Chunk from a bundle (inverse of add_to_bundle_smart)."""
    return format_result(frosty_client.send_command("remove_from_bundle_smart", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
def batch_add_to_bundle(bundle: str = "", names_json: str = "[]", name: str = "", smart: bool = True) -> str:
    """Add multiple EBX assets to a bundle. names_json is a JSON string array of asset names."""
    import json as _json
    params = {"bundle": bundle, "smart": smart, "name": name}
    try:
        params["names"] = _json.loads(names_json) if names_json else []
    except Exception:
        params["names"] = []
    return format_result(frosty_client.send_command("batch_add_to_bundle", params))


@mcp.tool()
def get_ebx_guids(name: str = "", guid: str = "", list_objects: bool = False) -> str:
    """Return file_guid + root_class_guid (for External PointerRef) and optionally all object class_guids."""
    return format_result(frosty_client.send_command("get_ebx_guids", {
        "name": name, "guid": guid, "list_objects": list_objects,
    }))


# ---------------------------------------------------------------------------
# Media / Lua / deep duplicate (LuaPlugin, Atlas, Svg, DuplicationPlugin)
# ---------------------------------------------------------------------------

@mcp.tool()
def get_lua_source(name: str = "", guid: str = "") -> str:
    """Decompile LuaRunnerCompiledLua bytecode to source text (requires LuaPlugin)."""
    return format_result(frosty_client.send_command("get_lua_source", {"name": name, "guid": guid}))


@mcp.tool()
def export_lua(name: str = "", guid: str = "", path: str = "") -> str:
    """Export Lua as .lua (decompiled) or .luares (raw SaveBytes)."""
    return format_result(frosty_client.send_command("export_lua", {"name": name, "guid": guid, "path": path}))


@mcp.tool()
def import_lua(
    name: str = "",
    guid: str = "",
    path: str = "",
    source: str = "",
    body: str = "",
    guid_name: str = "",
    wrap_entrypoint: bool = True,
) -> str:
    """Compile Lua into CompiledLuaResource.

    Prefer body= (function body only): auto-wraps as
      function CompiledLua_{GUID}(self, event, vars, sharedvars, globalvars, deltaTime) ... end
    which BF1 requires. Full source= also works; set wrap_entrypoint=False if already wrapped.
    Needs thirdparty/luacmp.dll.
    """
    return format_result(frosty_client.send_command("import_lua", {
        "name": name, "guid": guid, "path": path, "source": source,
        "body": body, "guid_name": guid_name, "wrap_entrypoint": wrap_entrypoint,
    }))


@mcp.tool()
def create_lua_asset(
    new_name: str = "",
    template: str = "",
    body: str = "",
    source: str = "",
    path: str = "",
) -> str:
    """Create LuaRunnerCompiledLua EBX + CompiledLua RES by duplicating a template (cannot create empty).

    Args:
        new_name: e.g. 'Lua/LuaRunnerCompiledLua_MyScript_Win32'
        template: optional source LuaRunnerCompiledLua; auto-picks one if omitted
        body/source/path: optional immediate compile after create
    """
    params = {"new_name": new_name, "template": template, "wrap_entrypoint": True}
    if body:
        params["body"] = body
    if source:
        params["source"] = source
    if path:
        params["path"] = path
    return format_result(frosty_client.send_command("create_lua_asset", params))


@mcp.tool()
def create_lua_runner_component(
    name: str = "",
    guid: str = "",
    compiled_lua_name: str = "",
    compiled_lua_guid: str = "",
    realm: str = "ClientAndServer",
    script: str = "",
    array: str = "Objects",
    input_events_json: str = "[]",
    output_events_json: str = "[]",
    input_bool_properties_json: str = "[]",
    output_bool_properties_json: str = "[]",
    auto_start_per_frame: bool = False,
    auto_start_init: bool = False,
) -> str:
    """Create LuaRunnerScriptEntityData in a blueprint and bind CompiledLua to a LuaRunnerCompiledLua asset."""
    import json as _json
    params = {
        "name": name, "guid": guid,
        "compiled_lua_name": compiled_lua_name, "compiled_lua_guid": compiled_lua_guid,
        "realm": realm, "script": script, "array": array,
        "auto_start_per_frame": auto_start_per_frame,
        "auto_start_init": auto_start_init,
    }
    for key, raw in (
        ("input_events", input_events_json),
        ("output_events", output_events_json),
        ("input_bool_properties", input_bool_properties_json),
        ("output_bool_properties", output_bool_properties_json),
    ):
        try:
            params[key] = _json.loads(raw) if raw else []
        except Exception:
            params[key] = []
    return format_result(frosty_client.send_command("create_lua_runner_component", params))


@mcp.tool()
def setup_lua(
    lua_name: str = "",
    blueprint: str = "",
    template: str = "",
    body: str = "",
    source: str = "",
    path: str = "",
    realm: str = "ClientAndServer",
    input_events_json: str = "[]",
    output_events_json: str = "[]",
    auto_start_init: bool = False,
) -> str:
    """Full Lua pipeline (BF1DevPlugin style):
    1) duplicate LuaRunnerCompiledLua EBX+RES
    2) create LuaRunnerScriptEntityData on blueprint, bind CompiledLua
    3) compile body/source with CompiledLua_{guid} entrypoint
    """
    import json as _json
    params = {
        "lua_name": lua_name, "blueprint": blueprint, "template": template,
        "realm": realm, "auto_start_init": auto_start_init,
    }
    if body:
        params["body"] = body
    if source:
        params["source"] = source
    if path:
        params["path"] = path
    try:
        params["input_events"] = _json.loads(input_events_json) if input_events_json else []
    except Exception:
        params["input_events"] = []
    try:
        params["output_events"] = _json.loads(output_events_json) if output_events_json else []
    except Exception:
        params["output_events"] = []
    return format_result(frosty_client.send_command("setup_lua", params))


@mcp.tool()
def get_lua_guid_name(name: str = "", guid: str = "", class_guid: str = "") -> str:
    """Compute BF1-style guid_name / entrypoint CompiledLua_{GUID_WITH_UNDERSCORES} from Lua asset or class_guid."""
    return format_result(frosty_client.send_command("get_lua_guid_name", {
        "name": name, "guid": guid, "class_guid": class_guid,
    }))


@mcp.tool()
def export_atlas_texture(name: str = "", guid: str = "", path: str = "") -> str:
    """Export AtlasTextureAsset to DXT5 DDS (requires AtlasTexturePlugin)."""
    return format_result(frosty_client.send_command("export_atlas_texture", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
def import_atlas_texture(name: str = "", guid: str = "", path: str = "") -> str:
    """Import DXT5/BC3 DDS into AtlasTextureAsset."""
    return format_result(frosty_client.send_command("import_atlas_texture", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
def export_svg(name: str = "", guid: str = "", path: str = "") -> str:
    """Export SvgImage asset to .svg (requires SvgImagePlugin)."""
    return format_result(frosty_client.send_command("export_svg", {"name": name, "guid": guid, "path": path}))


@mcp.tool()
def get_sound_info(name: str = "", guid: str = "") -> str:
    """Inspect SoundWave chunks / RuntimeVariations / Segments."""
    return format_result(frosty_client.send_command("get_sound_info", {"name": name, "guid": guid}))


@mcp.tool()
def export_sound_wav(name: str = "", guid: str = "", path: str = "", variation: int = 0) -> str:
    """Export SoundWave variation to WAV (Pcm16Big codec only)."""
    return format_result(frosty_client.send_command("export_sound_wav", {
        "name": name, "guid": guid, "path": path, "variation": variation,
    }))


@mcp.tool()
def import_sound_wav(name: str = "", guid: str = "", path: str = "", variation: int = 0) -> str:
    """Import PCM16 WAV into SoundWave variation as Pcm16Big (mono auto-upmixed to stereo)."""
    return format_result(frosty_client.send_command("import_sound_wav", {
        "name": name, "guid": guid, "path": path, "variation": variation,
    }))


@mcp.tool()
def duplicate_deep(
    name: str = "",
    guid: str = "",
    new_name: str = "",
    new_type: str = "",
) -> str:
    """Deep-duplicate EBX via DuplicationPlugin (Mesh/Texture/Atlas/SVG/Sound/Lua extensions + linked res/chunk).

    Args:
        new_name: Full new asset path (e.g. 'mod/my_mesh').
        new_type: Optional create-as different SDK type (rare).
    """
    params = {"name": name, "guid": guid, "new_name": new_name}
    if new_type:
        params["new_type"] = new_type
    return format_result(frosty_client.send_command("duplicate_deep", params))


@mcp.tool()
def create_schematic_channel_asset(
    new_name: str = "",
    template: str = "",
    clear: bool = True,
    events_json: str = "[]",
    links_json: str = "[]",
    properties_json: str = "[]",
) -> str:
    """Create SchematicChannelAsset by duplicating a template, then fill Events/Links/Properties.

    events_json: ["ClientAndServer", ...]
    links_json: [{"realm":"ClientAndServer","id":"Name","link_type":"Type"}, ...]
    properties_json: [{"realm":"ClientAndServer","id":"Name","field_type":"Float"}, ...]
    id/link_type/field_type are FNV-hashed like BF1 BPStruct.
    """
    import json as _json
    params = {"new_name": new_name, "template": template, "clear": clear}
    for key, raw in (("events", events_json), ("links", links_json), ("properties", properties_json)):
        try:
            params[key] = _json.loads(raw) if raw else []
        except Exception:
            params[key] = []
    return format_result(frosty_client.send_command("create_schematic_channel_asset", params))


@mcp.tool()
def create_schematic_channel_entity(
    name: str = "",
    guid: str = "",
    channel_name: str = "",
    channel_guid: str = "",
    realm: str = "ClientAndServer",
    array: str = "Objects",
    input_properties_json: str = "[]",
    output_properties_json: str = "[]",
) -> str:
    """Create SchematicChannelEntityData on a blueprint and bind Channel to a SchematicChannelAsset."""
    import json as _json
    params = {
        "name": name, "guid": guid, "channel_name": channel_name,
        "channel_guid": channel_guid, "realm": realm, "array": array,
    }
    try:
        params["input_properties"] = _json.loads(input_properties_json) if input_properties_json else []
    except Exception:
        params["input_properties"] = []
    try:
        params["output_properties"] = _json.loads(output_properties_json) if output_properties_json else []
    except Exception:
        params["output_properties"] = []
    return format_result(frosty_client.send_command("create_schematic_channel_entity", params))


# ---------------------------------------------------------------------------
# Advanced Bundle Editor + Blueprint utilities (v1.10)
# ---------------------------------------------------------------------------

@mcp.tool()
def advanced_add_to_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    netreg: bool = False,
    mvdb: bool = False,
    unlock_table: bool = False,
) -> str:
    """Add EBX via AdvancedBundleEditorPlugin.BundleEditors (with RES/Chunk extensions).

    Optional: also add to NetworkRegistry / MeshVariationDb / UnlockIdTable.
    Falls back to add_to_bundle_smart if Advanced Bundle plugin is not loaded.
    """
    return format_result(frosty_client.send_command("advanced_add_to_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
        "netreg": netreg, "mvdb": mvdb, "unlock_table": unlock_table,
    }))


@mcp.tool()
def advanced_remove_from_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    netreg: bool = False,
    mvdb: bool = False,
    unlock_table: bool = False,
) -> str:
    """Remove EBX via AdvancedBundleEditorPlugin (optionally NetReg/MVDB/Unlock table)."""
    return format_result(frosty_client.send_command("advanced_remove_from_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
        "netreg": netreg, "mvdb": mvdb, "unlock_table": unlock_table,
    }))


@mcp.tool()
def completely_add_to_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    recursive: bool = True,
) -> str:
    """Completely add asset: Bundle + NetReg + MVDB (+ Unlock tables), optionally recurse dependencies.

    Mirrors BunpyApi.CompletelyAddAsset / RecAddToBundle+Reg+Mvdb. Requires AdvancedBundleEditorPlugin.
    """
    return format_result(frosty_client.send_command("completely_add_to_bundle", {
        "name": name, "guid": guid, "bundle": bundle, "recursive": recursive,
    }))


@mcp.tool()
def completely_remove_from_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    recursive: bool = True,
) -> str:
    """Completely remove from Bundle + NetReg + MVDB (+ Unlock), optionally recurse. Requires AdvancedBundleEditorPlugin."""
    return format_result(frosty_client.send_command("completely_remove_from_bundle", {
        "name": name, "guid": guid, "bundle": bundle, "recursive": recursive,
    }))


@mcp.tool()
def validate_bundle_add(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Validate whether an asset can be (recursively) added / netreg / mvdb for a bundle."""
    return format_result(frosty_client.send_command("validate_bundle_add", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
def create_advanced_bundle(
    bundle: str = "",
    super_bundle: str = "",
    bundle_type: str = "Shared",
    generate_blueprints: bool = True,
    blueprint_type: str = "BlueprintBundle",
) -> str:
    """Create a new bundle via BunpyApi.AddBundle (Blueprint + NetReg + MVDB scaffolding). Requires AdvancedBundleEditorPlugin."""
    return format_result(frosty_client.send_command("create_advanced_bundle", {
        "bundle": bundle, "super_bundle": super_bundle,
        "bundle_type": bundle_type, "generate_blueprints": generate_blueprints,
        "blueprint_type": blueprint_type,
    }))


@mcp.tool()
def set_bundle_super(bundle: str = "", super_bundle: str = "") -> str:
    """Repoint an existing (added) bundle to a different super bundle. Frosty's AddBundle returns the existing bundle unchanged, so this is the only way to fix a bundle's super bundle after creation."""
    return format_result(frosty_client.send_command("set_bundle_super", {
        "bundle": bundle, "super_bundle": super_bundle,
    }))


@mcp.tool()
def delete_bundle(bundle: str = "", clear_members: bool = True) -> str:
    """Delete an added bundle (Frosty's RevertBundle removes it). Optionally clears its id from every asset's added bundles."""
    return format_result(frosty_client.send_command("delete_bundle", {
        "bundle": bundle, "clear_members": clear_members,
    }))


@mcp.tool()
def list_super_bundles() -> str:
    """List super bundles with their bundle counts."""
    return format_result(frosty_client.send_command("list_super_bundles", {}))


@mcp.tool()
def project_stats() -> str:
    """Project totals: modified/dirty counts, added vs modified EBX, added bundles/res/chunks."""
    return format_result(frosty_client.send_command("project_stats", {}))


@mcp.tool()
def batch_set_field(items: list = []) -> str:
    """Apply many field writes in one call. items entries use the same params as set_ebx_field, or set_component_property when index/component_type is present."""
    return format_result(frosty_client.send_command("batch_set_field", {"items": items}))


@mcp.tool()
def diff_ebx(name: str = "", guid: str = "", limit: int = 200) -> str:
    """Field-level YAML diff of a modified EBX against its unmodified (vanilla) data."""
    return format_result(frosty_client.send_command("diff_ebx", {
        "name": name, "guid": guid, "limit": limit,
    }))


@mcp.tool()
def validate_refs(names: list = [], limit: int = 200) -> str:
    """Scan modified assets (or the given names) for dangling external/self/internal pointer refs."""
    return format_result(frosty_client.send_command("validate_refs", {"names": names, "limit": limit}))


@mcp.tool()
def bundle_closure_check(bundle: str = "", include_outside: bool = False, limit: int = 200) -> str:
    """Check a bundle's EBX dependency closure: dangling deps and (optionally) deps that live outside the bundle."""
    return format_result(frosty_client.send_command("bundle_closure_check", {
        "bundle": bundle, "include_outside": include_outside, "limit": limit,
    }))


@mcp.tool()
def registry_gap(name: str = "", names: list = [], registry: str = "", registries: list = [], limit: int = 200) -> str:
    """Compare an asset's exported object GUIDs against NetworkRegistry entries and list what is missing."""
    return format_result(frosty_client.send_command("registry_gap", {
        "name": name, "names": names, "registry": registry, "registries": registries, "limit": limit,
    }))


@mcp.tool()
def list_bundle_contents(
    bundle: str = "",
    type: str = "",
    modified_only: bool = False,
    offset: int = 0,
    limit: int = 200,
) -> str:
    """List EBX assets inside a bundle (optional type filter / modified_only)."""
    return format_result(frosty_client.send_command("list_bundle_contents", {
        "bundle": bundle, "type": type, "modified_only": modified_only,
        "offset": offset, "limit": limit,
    }))


@mcp.tool()
def resolve_hash(hash: str = "", value: str = "") -> str:
    """Reverse FNV hash to string via Utils.GetString (same as Blueprint Editor hashing utils).

    Pass decimal or 0xHEX in hash (or value).
    """
    return format_result(frosty_client.send_command("resolve_hash", {
        "hash": hash or value, "value": value,
    }))


@mcp.tool()
def encode_property_flags(
    realm: str = "ClientAndServer",
    prop_type: str = "Default",
    source_cant_be_static: bool = False,
) -> str:
    """Encode PropertyConnection Flags (mirrors BlueprintEditor PropertyFlagsHelper). Pair with decode_connection_flags."""
    return format_result(frosty_client.send_command("encode_property_flags", {
        "realm": realm, "prop_type": prop_type, "source_cant_be_static": source_cant_be_static,
    }))


@mcp.tool()
def list_node_ports(type: str = "", component_type: str = "") -> str:
    """List Blueprint Editor node input/output ports for an EntityData type (EntityNode extension or SDK fallback)."""
    return format_result(frosty_client.send_command("list_node_ports", {
        "type": type or component_type, "component_type": component_type,
    }))


@mcp.tool()
def list_registered_node_types(query: str = "", offset: int = 0, limit: int = 200) -> str:
    """List EntityNode / EntityMappingNode types registered by FrostyBlueprintEditor."""
    return format_result(frosty_client.send_command("list_registered_node_types", {
        "query": query, "offset": offset, "limit": limit,
    }))


@mcp.tool()
def validate_connections(name: str = "", guid: str = "") -> str:
    """Scan Property/Event/Link connections for null pointers and empty field names."""
    return format_result(frosty_client.send_command("validate_connections", {
        "name": name, "guid": guid,
    }))


if __name__ == "__main__":
    try:
        debug_log("Starting FastMCP server for Frosty Editor...")
        mcp.run()
    except Exception as e:
        debug_log(f"Fatal Crash: {e}")
        traceback.print_exc(file=sys.stderr)
