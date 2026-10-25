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
# WINDOWS LINE ENDING FIX FOR MCP (MONKEY-PATCH)
# The MCP SDK's stdio_server uses TextIOWrapper without newline='\n', causing
# Windows to output CRLF (\r\n) instead of LF (\n). This causes the error:
# "invalid trailing data at the end of stream"
# The patch must be installed before FastMCP is imported.
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
# Tools and diagnostics must never write to stdout: stdout carries the
# JSON-RPC stream. Point it at stderr for the imports below, then restore it.
# ============================================================================

_mcp_stdout = sys.stdout
sys.stdout = sys.stderr

import asyncio
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


PIPE_NAME_PREFIX = "Frosty_MCP_Bridge_v1_"


def _open_bridge_pipes():
    """Every bridge pipe the editor is currently listening on, by profile suffix."""
    pipe_root = "\\\\.\\pipe\\"
    try:
        names = os.listdir(pipe_root)
    except OSError:
        return []

    return sorted(n[len(PIPE_NAME_PREFIX):] for n in names if n.startswith(PIPE_NAME_PREFIX))


def _detect_running_pipe():
    """Find the open bridge pipe when no profile was requested.

    One editor is the normal case, so the only pipe wins. Several editors are
    listed instead of guessed at, because connecting to the wrong one edits the
    wrong game.
    """
    profiles = _open_bridge_pipes()
    if len(profiles) == 1:
        debug_log("using the only open bridge pipe: " + profiles[0])
        return PIPE_PREFIX + "_" + profiles[0]
    if len(profiles) > 1:
        debug_log(
            "several bridge pipes are open; call use_profile to choose one: "
            + ", ".join(profiles)
        )
    return None


def _resolve_pipe_name():
    explicit = os.environ.get("FROSTY_MCP_PIPE")
    if explicit:
        return explicit

    wanted = os.environ.get("FROSTY_MCP_PROFILE", "").strip()
    if not wanted:
        detected = _detect_running_pipe()
        if detected:
            return detected
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
# True when the pipe was not pinned by an environment variable. The name is then
# resolved again on every connect, so a server started before the editor still
# finds the pipe once the editor is up.
PIPE_NAME_IS_AUTO = (
    not os.environ.get("FROSTY_MCP_PIPE")
    and not os.environ.get("FROSTY_MCP_PROFILE", "").strip()
)
MCP_SERVER_NAME = "frosty"
MAX_RESPONSE_SIZE_BYTES = 32 * 1024 * 1024


def _parse_timeout_seconds(raw_value):
    if raw_value is None:
        return 120.0
    try:
        timeout = float(raw_value)
    except (TypeError, ValueError):
        return 120.0
    if not math.isfinite(timeout):
        return 120.0
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
        # Tools run in worker threads; two of them must not interleave
        # writes/reads on the same pipe handle.
        self._lock = threading.RLock()
        # Handles left behind by a timed-out request. They are closed by the
        # worker that owns the read, or by the next close() if the worker lost
        # the race, so a timed-out pipe never stays open for good.
        self._orphan_lock = threading.Lock()
        self._orphans = set()
        # Pipe chosen by use_profile, or None when the server follows whichever
        # editor is running.
        self.pinned_pipe = None
        if win32file is None or pywintypes is None:
            raise RuntimeError(
                "Named pipe transport requires Windows and pywin32. "
                "Install with: pip install pywin32"
            )

    @property
    def connected(self):
        return self.handle is not None

    def connect(self):
        global PIPE_NAME
        with self._lock:
            # Another worker may have connected while this one waited for the lock.
            if self.handle is not None:
                return True

            if self.pinned_pipe:
                PIPE_NAME = self.pinned_pipe
            elif PIPE_NAME_IS_AUTO:
                detected = _detect_running_pipe()
                if detected:
                    PIPE_NAME = detected
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
        with self._lock:
            return self._exchange_with_timeout_locked(req_json, method)

    def _exchange_with_timeout_locked(self, req_json, method):
        timeout = self.timeout_seconds
        if timeout is None:
            return self._exchange_once(req_json)

        result_holder = {}
        error_holder = {}
        state = {"handle": self.handle, "orphaned": False}

        def _worker():
            try:
                result_holder["response"] = self._exchange_once(req_json)
            except Exception as exc:
                error_holder["error"] = exc
            finally:
                # A timed-out request keeps the pipe until the editor answers.
                # Closing that handle here, rather than from the timing thread,
                # stops Windows from handing the same handle value to a new
                # connection while this read is still in flight.
                handle = state["handle"]
                with self._orphan_lock:
                    orphaned = state["orphaned"] or handle in self._orphans
                    self._orphans.discard(handle)
                if orphaned:
                    try:
                        win32file.CloseHandle(handle)
                    except Exception:
                        pass

        worker = threading.Thread(target=_worker, daemon=True)
        worker.start()
        worker.join(timeout)

        if worker.is_alive():
            with self._orphan_lock:
                state["orphaned"] = True
                # Keep the handle on the orphan list as well: if the worker is
                # already past its check, close() still closes it later.
                self._orphans.add(state["handle"])
            # Detach the handle so this client opens a fresh pipe next time; the
            # worker closes the old one when the editor finally replies.
            self.handle = None
            raise TimeoutError(
                f"Command '{method}' timed out after {timeout:g}s (FROSTY_MCP_TIMEOUT). "
                "The request may still be running in the editor."
            )

        if "error" in error_holder:
            raise error_holder["error"]

        return result_holder["response"]

    def send_command(self, method, params=None):
        # No automatic retry. A timeout or a broken connection does not prove
        # the editor did not run the command, so resending could duplicate a
        # create/duplicate/add. Reconnect happens on the next explicit call.
        if not self.connected:
            connected = False
            for attempt in range(3):
                if self.connect():
                    connected = True
                    break
                time.sleep(0.2 * (attempt + 1))
            if not connected:
                running = _open_bridge_pipes()
                if len(running) > 1:
                    raise ConnectionError(
                        "Several editors are listening (%s) and FROSTY_MCP_PROFILE is not set. "
                        "Call use_profile with one of them first."
                        % ", ".join(running)
                    )
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
        except (OSError, ConnectionError, TimeoutError):
            self.close()
            raise

        error = response.get("error") if isinstance(response, dict) else None
        if error is not None:
            if isinstance(error, dict):
                return {
                    "success": False,
                    "error": error.get("message", str(error)),
                    "error_code": "RPC_ERROR",
                    "rpc_code": error.get("code"),
                }
            return {"success": False, "error": str(error), "error_code": "RPC_ERROR"}
        if "result" in response:
            return response["result"]
        return response

    def set_pipe(self, pipe_name):
        """Point every later call at one pipe. None returns to auto detection."""
        global PIPE_NAME
        with self._lock:
            handle = self.handle
            self.handle = None
            self.pinned_pipe = pipe_name
            if pipe_name:
                PIPE_NAME = pipe_name
            else:
                PIPE_NAME = _detect_running_pipe() or PIPE_PREFIX
        if handle is not None:
            try:
                win32file.CloseHandle(handle)
            except Exception:
                pass

    def close(self):
        with self._lock:
            handle = self.handle
            self.handle = None
        with self._orphan_lock:
            orphans = list(self._orphans)
            self._orphans.clear()
        for h in ([handle] if handle else []) + orphans:
            try:
                win32file.CloseHandle(h)
            except Exception:
                pass


frosty_client = FrostyNamedPipeBridgeClient()

# ============================================================================
# MCP SERVER
# ============================================================================

mcp = FastMCP(
    MCP_SERVER_NAME,
    instructions=(
        "Frosty Editor MCP bridge v"
        + os.environ.get("FROSTY_MCP_VERSION", "2.2.2")
        + ". Tools that take a commit parameter are two-phase: the first call "
        "returns would_change + plan, and repeating the identical call with "
        "commit=true executes it. Tools without commit apply immediately. "
        "Run capabilities() first to see which optional plugins are loaded."
    ),
)


@mcp.tool()
async def list_profiles() -> str:
    """List the editors currently listening, and which one this server talks to.

    One bridge pipe exists per running editor, named after its profile. The
    active pipe is the one every other tool call goes to.
    """
    return format_result({
        "success": True,
        "active_pipe": PIPE_NAME,
        "pinned": frosty_client.pinned_pipe,
        "profiles": _open_bridge_pipes(),
    })


@mcp.tool()
async def use_profile(profile: str = "", pipe: str = "") -> str:
    """Point every later tool call at one running editor.

    profile is the suffix list_profiles reports, for example PVZBattleforNeighborville.
    pipe takes a full pipe path when the name is not enough. Passing neither returns
    to auto detection, which follows the only editor that is running and refuses to
    guess when there are several.
    """
    wanted = (pipe or "").strip()
    if not wanted:
        name = (profile or "").strip()
        if name:
            available = _open_bridge_pipes()
            if name not in available:
                return format_result({
                    "success": False,
                    "error": "No editor is listening on profile " + name,
                    "error_code": "PROFILE_NOT_RUNNING",
                    "profiles": available,
                })
            wanted = PIPE_PREFIX + "_" + name

    frosty_client.set_pipe(wanted or None)
    return format_result({
        "success": True,
        "active_pipe": PIPE_NAME,
        "pinned": frosty_client.pinned_pipe,
        "profiles": _open_bridge_pipes(),
    })


@mcp.tool()
async def ping() -> str:
    """Check connectivity with the Frosty Editor MCP bridge. Call this first."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "ping"))


@mcp.tool()
async def get_editor_info() -> str:
    """Get Frosty Editor profile, EBX count, dirty count, SDK version and selected asset."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_editor_info"))


@mcp.tool()
async def get_selected_asset() -> str:
    """Get the currently selected asset in Data Explorer and the opened editor asset."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_selected_asset"))


# ---------------------------------------------------------------------------
# BF1 DevPlugin helpers (registered early so clients always advertise them)
# ---------------------------------------------------------------------------

@mcp.tool()
async def set_root_flags(name: str = "", guid: str = "", flags: int = -1) -> str:
    """Set EBX root object Flags.

    flags is required; leaving it at -1 sends nothing and the bridge refuses the
    call, because a default here would rewrite the word on every asset type.
    """
    params = {"name": name, "guid": guid}
    if flags >= 0:
        params["flags"] = flags
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_root_flags", params))


@mcp.tool()
async def get_internal_pointer_ref(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    root: bool = False,
    component_guid: str = "",
) -> str:
    """Build External PointerRef (file_guid+class_guid) for NetworkRegistry.

    Pass root=True for asset root, or class_guid of an exported component.
    Never register MpSoldierAI (Giant's Shadow crash).
    """
    class_guid = class_guid or component_guid
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_internal_pointer_ref", {
        "name": name, "guid": guid, "class_guid": class_guid, "root": root or not class_guid,
    }))


@mcp.tool()
async def list_network_registry(
    registry: str = "",
    name: str = "",
    query: str = "",
    offset: int = 0,
    limit: int = 200,
) -> str:
    """List Objects in a NetworkRegistryAsset (*_networkregistry_Win32)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_network_registry", {
        "registry": registry or name, "name": name, "query": query,
        "offset": offset, "limit": limit,
    }))


@mcp.tool()
async def add_to_network_registry(
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
    except Exception as exc:
        return format_result({"success": False, "error": "refs_json is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_to_network_registry", params))


@mcp.tool()
async def remove_from_network_registry(
    registry: str = "",
    index: int = -1,
    file_guid: str = "",
    class_guid: str = "",
    refs_json: str = "[]",
    commit: bool = False,
) -> str:
    """Remove PointerRefs from NetworkRegistry by index or file_guid+class_guid / refs_json.

    First call returns would_change; call again with commit=True to execute.
    """
    import json as _json
    params = {"registry": registry, "file_guid": file_guid, "class_guid": class_guid}
    if index >= 0:
        params["index"] = index
    try:
        params["refs"] = _json.loads(refs_json) if refs_json else []
    except Exception as exc:
        return format_result({"success": False, "error": "refs_json is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_from_network_registry", params))


@mcp.tool()
async def safe_add_to_bundle(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Add EBX to bundle only if not already present; ModifyEbx so export is non-empty (BF1 tip)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "safe_add_to_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
async def add_to_bf1_map_bundles(name: str = "", guid: str = "", modify_ebx: bool = True) -> str:
    """Add asset to all BF1 MpSoldier-aligned map bundles (Core2 list, including */content paths)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_to_bf1_map_bundles", {
        "name": name, "guid": guid, "modify_ebx": modify_ebx,
    }))


@mcp.tool()
async def list_bf1_map_bundles() -> str:
    """List the 32+ BF1 map bundle names used by BF1DevPlugin Core2 (with resolve status)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_bf1_map_bundles", {}))


@mcp.tool()
async def open_asset(name: str = "", guid: str = "", create_default_editor: bool = True,
                     kind: str = "ebx") -> str:
    """Open an asset in Frosty Editor.

    Args:
        name: asset path/name (e.g. "levels/mp/mp_amiens/mp_amiens").
        guid: EBX file GUID, or RES rid / chunk id for those kinds.
        create_default_editor: Whether to open the default asset editor tab.
        kind: "ebx", "res" or "chunk".
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "open_asset", {
        "name": name,
        "guid": guid,
        "create_default_editor": create_default_editor,
        "kind": kind,
    }))


@mcp.tool()
async def search_ebx(
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
        name: Alias for query, used when query is empty.
        type: Frostbite type filter (subclass match), e.g. "TextureAsset".
        modified_only: Only return modified assets.
        offset: Pagination start index.
        limit: Max results (default 100, max 10000).

    Returns JSON with: success, total, offset, limit, returned, assets.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "search_ebx", {
        "query": query or name,
        "type": type,
        "modified_only": modified_only,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
async def get_ebx_entry(name: str = "", guid: str = "", linked_limit: int = 200) -> str:
    """Get EBX metadata, linked assets, and bundle membership.

    bundles is the effective list (vanilla minus removed, plus editor-added),
    matching the Frosty Bundles tab. Also returns original_bundles, added_bundles
    (new memberships like a duplicated MpSoldier2), and removed_bundles.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_ebx_entry", {
        "name": name,
        "guid": guid,
        "linked_limit": linked_limit,
    }))


@mcp.tool()
async def get_ebx_xml(name: str = "", guid: str = "", max_chars: int = 60000,
                summary: bool = False, filter: str = "", char_offset: int = 0) -> str:
    """Export an EBX asset as XML text (truncated if larger than max_chars).

    Args:
        name: EBX asset name/path.
        guid: EBX file GUID.
        max_chars: Maximum characters to return (default 60000).
        summary: return tag counts instead of the full text.
        filter: return only lines containing this text.
        char_offset: start reading at this character when continuing a truncated export.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_ebx_xml", {
        "name": name,
        "guid": guid,
        "max_chars": max_chars,
        "summary": summary,
        "filter": filter,
        "char_offset": char_offset,
    }))


@mcp.tool()
async def get_ebx_yaml(name: str = "", guid: str = "", max_chars: int = 60000,
                 summary: bool = False, filter: str = "", char_offset: int = 0) -> str:
    """Export an EBX asset as YAML text (truncated if larger than max_chars).

    Args:
        name: EBX asset name/path.
        guid: EBX file GUID.
        max_chars: Maximum characters to return (default 60000).
        summary: return type counts instead of the full text.
        filter: return only lines containing this text.
        char_offset: start reading at this character when continuing a truncated export.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_ebx_yaml", {
        "name": name,
        "guid": guid,
        "max_chars": max_chars,
        "summary": summary,
        "filter": filter,
        "char_offset": char_offset,
    }))


@mcp.tool()
async def list_modified_assets(kind: str = "all", offset: int = 0, limit: int = 100,
                               include_linked: bool = False) -> str:
    """List modified assets in the current Frosty project.

    Args:
        kind: One of "all", "ebx", "res", "chunk".
        offset: Pagination start index.
        limit: Max results.
        include_linked: Also list EBX that are only indirectly modified through a linked RES/chunk.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_modified_assets", {
        "kind": kind,
        "offset": offset,
        "limit": limit,
        "include_linked": include_linked,
    }))


@mcp.tool()
async def search_res(query: str = "", modified_only: bool = False, offset: int = 0, limit: int = 100) -> str:
    """Search RES assets by name substring."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "search_res", {
        "query": query,
        "modified_only": modified_only,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
async def get_res_entry(name: str = "", res_rid: str = "") -> str:
    """Get RES asset metadata by name or resource RID."""
    params = {"name": name}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_res_entry", params))


@mcp.tool()
async def search_chunks(query: str = "", modified_only: bool = False, offset: int = 0, limit: int = 100) -> str:
    """Search chunk assets by GUID/name substring."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "search_chunks", {
        "query": query,
        "modified_only": modified_only,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
async def get_chunk_entry(id: str = "", guid: str = "") -> str:
    """Get chunk asset metadata by chunk GUID/id."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_chunk_entry", {
        "id": id or guid,
        "guid": guid or id,
    }))


@mcp.tool()
async def list_bundles(query: str = "", offset: int = 0, limit: int = 100) -> str:
    """List bundles, optionally filtered by name substring."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_bundles", {
        "query": query,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
async def get_bundle(name: str, offset: int = 0, limit: int = 100) -> str:
    """Get a bundle and its contained EBX/RES/chunk assets (same page window for each list)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_bundle", {
        "name": name, "offset": offset, "limit": limit,
    }))


@mcp.tool()
async def get_asset_dependencies(name: str = "", guid: str = "", recursive: bool = False,
                                max_nodes: int = 0, compact: bool = False,
                                offset: int = 0, limit: int = 200) -> str:
    """List EBX dependencies / linked assets for an EBX asset.

    recursive=True walks the transitive graph; the walk stops at max_nodes
    (default 200 when recursive, 2000 otherwise). compact=True returns only
    name/type/depth per entry. Results are paged with offset/limit.
    """
    params = {"name": name, "guid": guid, "recursive": recursive, "compact": compact,
              "offset": offset, "limit": limit}
    if max_nodes > 0:
        params["max_nodes"] = max_nodes
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_asset_dependencies", params))


@mcp.tool()
async def find_pointer_references(name: str = "", file_guid: str = "", class_guid: str = "",
                                  query: str = "", max_scan: int = 3000,
                                  offset: int = 0, limit: int = 200) -> str:
    """Find every External PointerRef that points at an asset (and optionally one class GUID).

    The dependency graph only tracks file-level GUIDs; this walks the pointer
    fields so component-level references are visible. Bounded by max_scan.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "find_pointer_references", {
        "name": name, "file_guid": file_guid, "class_guid": class_guid, "query": query,
        "max_scan": max_scan, "offset": offset, "limit": limit,
    }))


@mcp.tool()
async def insert_ebx_item(name: str = "", guid: str = "", path: str = "", class_guid: str = "",
                          index: int = -1, source_index: int = -1) -> str:
    """Insert an element into an EBX array, mirroring the property grid's Insert item.

    With source_index the element at that position is reused; otherwise a default
    value is inserted (a null PointerRef for PointerRef lists).
    """
    params = {"name": name, "guid": guid, "path": path, "class_guid": class_guid}
    if index >= 0:
        params["index"] = index
    if source_index >= 0:
        params["source_index"] = source_index
    return format_result(await asyncio.to_thread(frosty_client.send_command, "insert_ebx_item", params))


@mcp.tool()
async def get_type_info(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_type_info", {
        "name": name,
        "declared_only": declared_only,
        "include_inherited": include_inherited,
        "expand_nested": expand_nested,
    }))


@mcp.tool()
async def search_types(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "search_types", {
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
async def get_type_field(name: str, field: str, expand_nested: int = 1) -> str:
    """Type Explorer: open a field's nested type (click-through on PointerRef/List/struct).

    field is a property name or dotted path (e.g. 'Soldier', 'Transform.trans').
    Returns the field meta plus the resolved SDK type's full definition.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_type_field", {
        "name": name,
        "field": field,
        "expand_nested": expand_nested,
    }))


@mcp.tool()
async def list_type_namespaces(query: str = "", offset: int = 0, limit: int = 100) -> str:
    """Type Explorer: list EBX modules/namespaces with type counts (class/struct/enum)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_type_namespaces", {
        "query": query,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
async def find_references(name: str = "", guid: str = "", offset: int = 0, limit: int = 100) -> str:
    """Find EBX assets that reference the given asset GUID."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "find_references", {
        "name": name,
        "guid": guid,
        "offset": offset,
        "limit": limit,
    }))


@mcp.tool()
async def get_ebx_property(name: str = "", guid: str = "", path: str = "") -> str:
    """Read an EBX root-object property by dotted path (e.g. 'Name', 'Transform.trans.x', 'Items[0]')."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_ebx_property", {
        "name": name,
        "guid": guid,
        "path": path,
    }))


@mcp.tool()
async def set_ebx_property(name: str = "", guid: str = "", path: str = "", value: str = "") -> str:
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_ebx_property", {
        "name": name,
        "guid": guid,
        "path": path,
        "value": parsed,
    }))


@mcp.tool()
async def export_ebx_bin(name: str = "", guid: str = "", path: str = "") -> str:
    """Export EBX asset to a .bin file on disk."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_ebx_bin", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
async def import_ebx_bin(name: str = "", guid: str = "", path: str = "", data_only: bool = True,
                        commit: bool = False, max_scan: int = 1500) -> str:
    """Import a .bin file into an existing EBX asset.

    data_only=True keeps the asset's file GUID and root class GUID; internal object
    GUIDs come from the file, and the reply lists the component GUIDs that changed
    plus the assets whose pointers still address the old ones. data_only=False adopts
    the file's GUID and requires a preview call first, then commit=True.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_ebx_bin", {
        "name": name, "guid": guid, "path": path, "data_only": data_only, "commit": commit,
        "max_scan": max_scan,
    }))


@mcp.tool()
async def export_asset(name: str = "", guid: str = "", path: str = "", format: str = "xml") -> str:
    """Export EBX via AssetDefinition (xml/yaml/bin/png/dds/fbx depending on asset type & plugins)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_asset", {
        "name": name, "guid": guid, "path": path, "format": format,
    }))


@mcp.tool()
async def duplicate_ebx(name: str = "", guid: str = "", new_name: str = "",
                        copy_linked: bool = False, copy_bundles: bool = False) -> str:
    """Duplicate an EBX asset to a new name.

    copy_bundles=True also copies the source's bundle memberships; leave it off
    when the duplicate gets its own bundle.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "duplicate_ebx", {
        "name": name, "guid": guid, "new_name": new_name,
        "copy_linked": copy_linked, "copy_bundles": copy_bundles,
    }))


@mcp.tool()
async def create_ebx(name: str = "", type: str = "", bundle: str = "") -> str:
    """Create a new EBX asset of the given Frostbite type name, optionally adding to a bundle."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_ebx", {
        "name": name, "type": type, "bundle": bundle,
    }))


@mcp.tool()
async def revert_asset(kind: str = "ebx", name: str = "", guid: str = "", id: str = "",
                 res_rid: str = "", data_only: bool = False, commit: bool = False) -> str:
    """Revert modifications on an ebx/res/chunk asset.

    First call returns would_change; call again with commit=True to execute.
    """
    params = {"kind": kind, "name": name, "guid": guid or id, "id": id, "data_only": data_only}
    if res_rid:
        params["res_rid"] = res_rid
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "revert_asset", params))


@mcp.tool()
async def export_res(name: str = "", res_rid: str = "", path: str = "",
                     format: str = "frosty_res") -> str:
    """Export a RES asset to a file.

    format="frosty_res" (default) writes the editor's [16-byte ResMeta][data] layout.
    format="raw" writes data only and cannot be exchanged with the editor's importer.
    """
    params = {"name": name, "path": path, "format": format}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_res", params))


@mcp.tool()
async def import_res(name: str = "", res_rid: str = "", path: str = "",
                     format: str = "frosty_res") -> str:
    """Import a RES file into an existing RES asset.

    format="frosty_res" (default) reads the editor's [16-byte ResMeta][data] layout
    and updates ResMeta. format="raw" treats the whole file as data and leaves the
    existing ResMeta untouched.
    """
    params = {"name": name, "path": path, "format": format}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_res", params))


@mcp.tool()
async def export_chunk(id: str = "", guid: str = "", path: str = "") -> str:
    """Export raw chunk bytes to a file."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_chunk", {
        "id": id or guid, "guid": guid or id, "path": path,
    }))


@mcp.tool()
async def import_chunk(id: str = "", guid: str = "", path: str = "") -> str:
    """Import raw bytes into an existing chunk."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_chunk", {
        "id": id or guid, "guid": guid or id, "path": path,
    }))


@mcp.tool()
async def duplicate_res(name: str = "", res_rid: str = "", new_name: str = "", res_type: str = "") -> str:
    """Duplicate a RES asset to a new name."""
    params = {"name": name, "new_name": new_name, "res_type": res_type}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(await asyncio.to_thread(frosty_client.send_command, "duplicate_res", params))


@mcp.tool()
async def duplicate_chunk(id: str = "", guid: str = "", texture_res: str = "") -> str:
    """Duplicate a chunk to a new GUID (keeps original bundle membership).

    Pass texture_res (a Texture RES name) when the chunk belongs to a texture, so the
    copy carries the texture metadata instead of a bare chunk.
    """
    params = {"id": id or guid, "guid": guid or id}
    if texture_res:
        params["texture_res"] = texture_res
    return format_result(await asyncio.to_thread(frosty_client.send_command, "duplicate_chunk", params))


@mcp.tool()
async def add_to_bundle(bundle: str, kind: str = "ebx", name: str = "", guid: str = "",
                  id: str = "", res_rid: str = "") -> str:
    """Add an ebx/res/chunk asset to a bundle."""
    params = {"bundle": bundle, "kind": kind, "name": name, "guid": guid, "id": id}
    if res_rid:
        params["res_rid"] = res_rid
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_to_bundle", params))


@mcp.tool()
async def remove_from_bundle(bundle: str, kind: str = "ebx", name: str = "", guid: str = "",
                       id: str = "", res_rid: str = "", commit: bool = False) -> str:
    """Remove an editor-added membership from a bundle.

    Native memberships cannot be removed in a .fbmod. First call returns
    would_change; call again with commit=True to execute.
    """
    params = {"bundle": bundle, "kind": kind, "name": name, "guid": guid, "id": id}
    if res_rid:
        params["res_rid"] = res_rid
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_from_bundle", params))


@mcp.tool()
async def create_bundle(name: str, type: str = "BlueprintBundle", super_bundle: str = "") -> str:
    """Create a new bundle (and optionally a super bundle)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_bundle", {
        "name": name, "type": type, "super_bundle": super_bundle,
    }))


@mcp.tool()
async def export_texture(name: str = "", guid: str = "", path: str = "", format: str = "dds") -> str:
    """Export a TextureAsset to png/tga/hdr/dds (requires TexturePlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_texture", {
        "name": name, "guid": guid, "path": path, "format": format,
    }))


@mcp.tool()
async def import_texture(name: str = "", guid: str = "", path: str = "",
                         raw_chunk: bool = False) -> str:
    """Import png/tga/hdr/dds into a TextureAsset.

    The default path validates the DDS type/format/size against the existing texture
    and rebuilds the Texture metadata. raw_chunk=true writes pixel data only and is
    refused for non-2D textures.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_texture", {
        "name": name, "guid": guid, "path": path, "raw_chunk": raw_chunk,
    }))


@mcp.tool()
async def export_mesh(
    name: str = "",
    guid: str = "",
    path: str = "",
    format: str = "fbx",
    skeleton: str = "",
    fbx_version: str = "FBX_2012",
    scale: str = "Centimeters",
    flatten_hierarchy: bool = False,
    single_lod: bool = False,
    export_non_renderable: bool = False,
) -> str:
    """Export a mesh asset to FBX/OBJ (requires MeshSetPlugin). No UI dialog."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_mesh", {
        "name": name,
        "guid": guid,
        "path": path,
        "format": format,
        "skeleton": skeleton,
        "fbx_version": fbx_version,
        "scale": scale,
        "flatten_hierarchy": flatten_hierarchy,
        "single_lod": single_lod,
        "export_non_renderable": export_non_renderable,
    }))


@mcp.tool()
async def import_mesh(name: str = "", guid: str = "", path: str = "", skeleton: str = "") -> str:
    """Import an FBX into a mesh asset (requires MeshSetPlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_mesh", {
        "name": name, "guid": guid, "path": path, "skeleton": skeleton,
    }))


# ---------------------------------------------------------------------------
# Blueprint connections
# ---------------------------------------------------------------------------

@mcp.tool()
async def list_connections(name: str = "", guid: str = "", kind: str = "all",
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_connections", {
        "name": name, "guid": guid, "kind": kind, "offset": offset, "limit": limit,
        "compact": compact, "component_guid": component_guid, "filter": filter,
    }))


@mcp.tool()
async def add_property_connection(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_property_connection", params))


@mcp.tool()
async def add_event_connection(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_event_connection", params))


@mcp.tool()
async def add_link_connection(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_link_connection", params))


@mcp.tool()
async def update_property_connection(
    name: str = "",
    guid: str = "",
    index: int = -1,
    source_guid: str = "",
    target_guid: str = "",
    source_field: str = "",
    target_field: str = "",
    realm: str = "",
    prop_type: str = "",
    source_cant_be_static: bool = None,
    flags: str = "",
) -> str:
    """Update an existing PropertyConnection by index (fields/flags optional).

    Omitted fields keep their current value; source_cant_be_static is only sent
    when explicitly given.
    """
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
    if source_cant_be_static is not None:
        params["source_cant_be_static"] = source_cant_be_static
    return format_result(await asyncio.to_thread(frosty_client.send_command, "update_property_connection", params))


@mcp.tool()
async def update_event_connection(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "update_event_connection", params))


@mcp.tool()
async def update_link_connection(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "update_link_connection", params))


@mcp.tool()
async def remove_connection(name: str = "", guid: str = "", kind: str = "property",
                            index: int = -1, commit: bool = False) -> str:
    """Remove a connection by kind (property|event|link) and index.

    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_connection", {
        "name": name, "guid": guid, "kind": kind, "index": index, "commit": commit,
    }))


@mcp.tool()
async def remove_connections_by_guid(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    kind: str = "all",
    commit: bool = False,
) -> str:
    """Remove all property/event/link connections touching a component class_guid (kind=all|property|event|link).

    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_connections_by_guid", {
        "name": name, "guid": guid, "class_guid": class_guid, "kind": kind, "commit": commit,
    }))


@mcp.tool()
async def decode_connection_flags(flags: str = "0") -> str:
    """Decode PropertyConnection flags into realm / prop_type / source_cant_be_static."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "decode_connection_flags", {"flags": flags}))


# ---------------------------------------------------------------------------
# Blueprint components
# ---------------------------------------------------------------------------

@mcp.tool()
async def list_components(name: str = "", guid: str = "", query: str = "", type: str = "",
                    offset: int = 0, limit: int = 200, compact: bool = True) -> str:
    """List internal EBX objects/components with class_guid, Flags, Realm.

    compact (default true) omits the object_flags breakdown; set false for it.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_components", {
        "name": name, "guid": guid, "query": query, "type": type,
        "offset": offset, "limit": limit, "compact": compact,
    }))


@mcp.tool()
async def get_component(name: str = "", guid: str = "", class_guid: str = "",
                  index: int = -1, component_type: str = "", type_index: int = 0,
                  include_properties: bool = True) -> str:
    """Get one component (by class_guid / index / component_type+type_index) including property dump."""
    params = {"name": name, "guid": guid, "class_guid": class_guid,
              "component_type": component_type, "type_index": type_index,
              "include_properties": include_properties}
    if index >= 0:
        params["index"] = index
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_component", params))


@mcp.tool()
async def create_component(
    name: str = "",
    guid: str = "",
    type: str = "",
    array: str = "",
    flag_preset: str = "",
    realm: str = "",
    properties_json: object = "",
    at_index: int = -1,
    array_host_guid: str = "",
) -> str:
    """Create a new internal component from Frostbite SDK type (e.g. DelayEntityData).

    Args:
        type: SDK class name.
        array: Root list to append PointerRef into (Objects or Components).
        flag_preset: client | server | client_and_server (optional).
        realm: e.g. ClientAndServer / Client / Server.
        properties_json: JSON object of initial property values (string or object).
        array_host_guid: class GUID of a nested host when the target array is not on the
            root object; available_arrays in the error reply lists valid names.
    """
    params = {"name": name, "guid": guid, "type": type}
    if array:
        params["array"] = array
    if array_host_guid:
        params["array_host_guid"] = array_host_guid
    if at_index >= 0:
        params["at_index"] = at_index
    if flag_preset:
        params["flag_preset"] = flag_preset
    if realm:
        params["realm"] = realm
    props = _json_arg(properties_json, "properties_json")
    if props is not None:
        params["properties"] = props
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_component", params))


@mcp.tool()
async def duplicate_component(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    array: str = "",
    properties_json: object = "",
    component_type: str = "",
    type_index: int = 0,
    at_index: int = -1,
) -> str:
    """Duplicate an internal component (new GUIDs via FrostyClipboard DeepCopy)."""
    params = {"name": name, "guid": guid, "class_guid": class_guid}
    if array:
        params["array"] = array
    if at_index >= 0:
        params["at_index"] = at_index
    if component_type:
        params["component_type"] = component_type
        params["type_index"] = type_index
    if index >= 0:
        params["index"] = index
    props = _json_arg(properties_json, "properties_json")
    if props is not None:
        params["properties"] = props
    return format_result(await asyncio.to_thread(frosty_client.send_command, "duplicate_component", params))


@mcp.tool()
async def set_component_flags(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_component_flags", params))


@mcp.tool()
async def get_component_property(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    path: str = "",
    component_type: str = "",
    type_index: int = 0,
) -> str:
    """Read a property on an internal component by dotted path (SDK fields)."""
    params = {"name": name, "guid": guid, "class_guid": class_guid, "path": path}
    if component_type:
        params["component_type"] = component_type
        params["type_index"] = type_index
    if index >= 0:
        params["index"] = index
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_component_property", params))


@mcp.tool()
async def set_component_property(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    path: str = "",
    value: str = "",
    component_type: str = "",
    type_index: int = 0,
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
    if component_type:
        params["component_type"] = component_type
        params["type_index"] = type_index
    if index >= 0:
        params["index"] = index
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_component_property", params))


@mcp.tool()
async def remove_component(
    name: str = "",
    guid: str = "",
    class_guid: str = "",
    index: int = -1,
    remove_object: bool = True,
    component_type: str = "",
    type_index: int = 0,
    commit: bool = False,
) -> str:
    """Remove a component: strips touching connections and array refs; optionally RemoveObject.

    First call returns would_change; call again with commit=True to execute.
    """
    params = {"name": name, "guid": guid, "class_guid": class_guid, "remove_object": remove_object}
    if component_type:
        params["component_type"] = component_type
        params["type_index"] = type_index
    if index >= 0:
        params["index"] = index
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_component", params))


@mcp.tool()
async def remove_ebx_item(
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
    commit: bool = False,
) -> str:
    """Frosty property-grid 'Remove item': delete an element from any EBX array.

    First call returns would_change; call again with commit=True to execute.

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
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_ebx_item", params))


@mcp.tool()
async def list_ebx_class_types(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_ebx_class_types", params))


@mcp.tool()
async def create_ebx_class(
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

    Mirrors FrostyPointerRefEditor: TypeLibrary.CreateObject, SetInstanceGuid,
    DataBusPeer Flags seed, asset.AddObject, then assign the PointerRef to host.path.

    Args:
        type: SDK class name to create (from list_ebx_class_types).
        path: Field on host component, e.g. 'Object', 'Components', 'Components[0]', 'Health'.
              A bare list property appends; a PointerRef or list[i] sets.
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_ebx_class", params))


# ---------------------------------------------------------------------------
# InterfaceDescriptorData (BF1DevPlugin Blueprint CreateFields / Events)
# ---------------------------------------------------------------------------

@mcp.tool()
async def list_interface(name: str = "", guid: str = "") -> str:
    """List Interface Fields / InputEvents / OutputEvents on a blueprint EBX."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_interface", {"name": name, "guid": guid}))


@mcp.tool()
async def ensure_interface(name: str = "", guid: str = "") -> str:
    """Ensure InterfaceDescriptorData exists (create if missing)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "ensure_interface", {"name": name, "guid": guid}))


@mcp.tool()
async def add_interface_field(
    name: str = "",
    guid: str = "",
    field_name: str = "",
    value: str = "",
    access_type: str = "FieldAccessType_Target",
) -> str:
    """Add an Interface DataField (BF1 CreateFields)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_interface_field", {
        "name": name, "guid": guid, "field_name": field_name,
        "value": value, "access_type": access_type,
    }))


@mcp.tool()
async def update_interface_field(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "update_interface_field", params))


@mcp.tool()
async def remove_interface_field(
    name: str = "",
    guid: str = "",
    index: int = -1,
    field_name: str = "",
    commit: bool = False,
) -> str:
    """Remove Interface field by index or field_name.

    First call returns would_change; call again with commit=True to execute.
    """
    params = {"name": name, "guid": guid, "field_name": field_name}
    if index >= 0:
        params["index"] = index
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_interface_field", params))


@mcp.tool()
async def add_interface_event(
    name: str = "",
    guid: str = "",
    event_name: str = "",
    kind: str = "input",
) -> str:
    """Add Interface InputEvent or OutputEvent (kind=input|output)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_interface_event", {
        "name": name, "guid": guid, "event_name": event_name, "kind": kind,
    }))


@mcp.tool()
async def remove_interface_event(
    name: str = "",
    guid: str = "",
    kind: str = "input",
    index: int = -1,
    event_name: str = "",
    commit: bool = False,
) -> str:
    """Remove Interface event by kind + index or event_name.

    First call returns would_change; call again with commit=True to execute.
    """
    params = {"name": name, "guid": guid, "kind": kind, "event_name": event_name}
    if index >= 0:
        params["index"] = index
    params["commit"] = commit
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_interface_event", params))


@mcp.tool()
async def search_ebx_fields(
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
    compact: bool = True,
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "search_ebx_fields", {
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
        "compact": compact,
    }))


@mcp.tool()
async def get_ebx_field(
    name: str = "",
    guid: str = "",
    path: str = "",
    class_guid: str = "",
    index: int = -1,
    component_type: str = "",
    type_index: int = 0,
) -> str:
    """Read one field inside an EBX component (or connection) by path from search_ebx_fields.

    Args:
        path: e.g. 'Delay', 'Fields[0].Name', or '$EventConnections[2].SourceEvent.Name'
        class_guid / index: identify the component (optional if path starts with '$')
    """
    params = {"name": name, "guid": guid, "path": path, "class_guid": class_guid}
    if component_type:
        params["component_type"] = component_type
        params["type_index"] = type_index
    if index >= 0:
        params["index"] = index
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_ebx_field", params))


@mcp.tool()
async def set_ebx_field(
    name: str = "",
    guid: str = "",
    path: str = "",
    value: str = "",
    class_guid: str = "",
    index: int = -1,
    component_type: str = "",
    type_index: int = 0,
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
    if component_type:
        params["component_type"] = component_type
        params["type_index"] = type_index
    if index >= 0:
        params["index"] = index
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_ebx_field", params))


@mcp.tool()
async def research_component_usage(
    type: str = "",
    name_query: str = "",
    asset_type: str = "",
    name: str = "",
    include_subclasses: bool = True,
    modified_only: bool = False,
    max_scan: int = 150,
    max_examples: int = 20,
    include_properties: bool = True,
    assets_json: str = "[]",
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "research_component_usage", {
        "type": type,
        "name_query": name_query,
        "asset_type": asset_type,
        "name": name,
        "include_subclasses": include_subclasses,
        "modified_only": modified_only,
        "max_scan": max_scan,
        "max_examples": max_examples,
        "include_properties": include_properties,
        "assets": json.loads(assets_json) if assets_json else [],
    }))


# ---------------------------------------------------------------------------
# Extra utilities (hash / localization / clipboard / inspect / smart bundle)
# ---------------------------------------------------------------------------

@mcp.tool()
async def hash_string(text: str = "", lowercase: bool = False, float_value: str = "",
                value: str = "") -> str:
    """Compute the FNV1 / localized hash, and optionally the float value as hex.

    text may be '0x...' to parse hex.

    value is accepted as an alias for text.
    """
    if not text:
        text = value
    params = {"text": text, "lowercase": lowercase}
    if float_value:
        params["float"] = float_value
    return format_result(await asyncio.to_thread(frosty_client.send_command, "hash_string", params))


@mcp.tool()
async def get_localized_string(id: str = "", hash: str = "") -> str:
    """Get a localized string by ID_... or hash (uint / 0xHEX)."""
    params = {}
    if id:
        params["id"] = id
    if hash:
        params["hash"] = hash
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_localized_string", params))


@mcp.tool()
async def set_localized_string(value: str = "", id: str = "", hash: str = "") -> str:
    """Set a localized string by ID_... or hash."""
    params = {"value": value}
    if id:
        params["id"] = id
    if hash:
        params["hash"] = hash
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_localized_string", params))


@mcp.tool()
async def revert_localized_string(hash: str = "", id: str = "") -> str:
    """Revert a localized string hash to original."""
    params = {}
    if hash:
        params["hash"] = hash
    if id:
        params["id"] = id
    return format_result(await asyncio.to_thread(frosty_client.send_command, "revert_localized_string", params))


@mcp.tool()
async def list_localized_strings(
    query: str = "",
    modified_only: bool = False,
    offset: int = 0,
    limit: int = 100,
) -> str:
    """Enumerate / search localized strings (optional modified_only filter)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_localized_strings", {
        "query": query, "modified_only": modified_only, "offset": offset, "limit": limit,
    }))


@mcp.tool()
async def clipboard_paste_object(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "clipboard_paste_object", params))


@mcp.tool()
async def create_struct(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_struct", params))


@mcp.tool()
async def get_texture_info(name: str = "", guid: str = "") -> str:
    """Inspect texture size/format/mips/chunk/res/bundles."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_texture_info", {"name": name, "guid": guid}))


@mcp.tool()
async def get_mesh_info(name: str = "", guid: str = "") -> str:
    """Inspect mesh LODs/sections/bones/materials/chunks (requires MeshSetPlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_mesh_info", {"name": name, "guid": guid}))


@mcp.tool()
async def add_to_bundle_smart(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Add EBX to bundle and linked RES/Chunk (Texture/Mesh/SoundWave) like BundleEditorPlugin."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "add_to_bundle_smart", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
async def remove_from_bundle_smart(name: str = "", guid: str = "", bundle: str = "",
                                  commit: bool = False) -> str:
    """Remove EBX and linked RES/Chunk from a bundle (inverse of add_to_bundle_smart).

    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "remove_from_bundle_smart", {
        "name": name, "guid": guid, "bundle": bundle, "commit": commit,
    }))


@mcp.tool()
async def batch_add_to_bundle(bundle: str = "", names_json: str = "[]", name: str = "", smart: bool = True) -> str:
    """Add multiple EBX assets to a bundle. names_json is a JSON string array of asset names."""
    import json as _json
    params = {"bundle": bundle, "smart": smart, "name": name}
    try:
        params["names"] = _json.loads(names_json) if names_json else []
    except Exception as exc:
        return format_result({"success": False, "error": "names_json is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    return format_result(await asyncio.to_thread(frosty_client.send_command, "batch_add_to_bundle", params))


@mcp.tool()
async def export_all_ebx(dir: str = "", format: str = "xml", skip_conversations: bool = False,
                         skip_virtual: bool = False, overwrite: bool = False,
                         query: str = "", type: str = "", limit: int = 0) -> str:
    """Export every EBX asset to XML/YAML files under dir, mirroring each asset's path."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_all_ebx", {
        "dir": dir, "format": format, "skip_conversations": skip_conversations,
        "skip_virtual": skip_virtual, "overwrite": overwrite, "query": query,
        "type": type, "limit": limit,
    }))


@mcp.tool()
async def export_ies(name: str = "", guid: str = "", path: str = "") -> str:
    """Export an IES profile resource as a DDS (R16_Float) file (requires IesResourcePlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_ies", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
async def can_be_loaded_by_bundle(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Ask PVZBundleManagerPlugin whether a bundle can load this asset (needs the plugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "can_be_loaded_by_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
async def manage_bundles(name: str = "", guid: str = "", commit: bool = False) -> str:
    """Run PVZBundleManagerPlugin's bundle hygiene pass for one asset.

    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "manage_bundles", {
        "name": name, "guid": guid, "commit": commit,
    }))


@mcp.tool()
async def get_ebx_guids(name: str = "", guid: str = "", list_objects: bool = False,
                        offset: int = 0, limit: int = 500) -> str:
    """Return file_guid + root_class_guid (for External PointerRef) and optionally all object class_guids."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_ebx_guids", {
        "name": name, "guid": guid, "list_objects": list_objects,
        "offset": offset, "limit": limit,
    }))


# ---------------------------------------------------------------------------
# Media / Lua / deep duplicate (LuaPlugin, Atlas, Svg, DuplicationPlugin)
# ---------------------------------------------------------------------------

@mcp.tool()
async def get_lua_source(name: str = "", guid: str = "") -> str:
    """Decompile LuaRunnerCompiledLua bytecode to source text (requires LuaPlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_lua_source", {"name": name, "guid": guid}))


@mcp.tool()
async def export_lua(name: str = "", guid: str = "", path: str = "") -> str:
    """Export Lua as .lua (decompiled) or .luares (raw SaveBytes)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_lua", {"name": name, "guid": guid, "path": path}))


@mcp.tool()
async def import_lua(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_lua", {
        "name": name, "guid": guid, "path": path, "source": source,
        "body": body, "guid_name": guid_name, "wrap_entrypoint": wrap_entrypoint,
    }))


@mcp.tool()
async def create_lua_asset(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_lua_asset", params))


@mcp.tool()
async def create_lua_runner_component(
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
    input_float_properties_json: str = "[]",
    output_float_properties_json: str = "[]",
    input_int_properties_json: str = "[]",
    output_int_properties_json: str = "[]",
    input_string_properties_json: str = "[]",
    output_string_properties_json: str = "[]",
    input_transform_properties_json: str = "[]",
    output_transform_properties_json: str = "[]",
    auto_start_per_frame: bool = False,
    auto_start_init: bool = False,
    run_on_property_change: bool = False,
) -> str:
    """Create LuaRunnerScriptEntityData in a blueprint and bind CompiledLua to a LuaRunnerCompiledLua asset."""
    import json as _json
    params = {
        "name": name, "guid": guid,
        "compiled_lua_name": compiled_lua_name, "compiled_lua_guid": compiled_lua_guid,
        "realm": realm, "script": script, "array": array,
        "auto_start_per_frame": auto_start_per_frame,
        "auto_start_init": auto_start_init,
        "run_on_property_change": run_on_property_change,
    }
    for key, raw in (
        ("input_events", input_events_json),
        ("output_events", output_events_json),
        ("input_bool_properties", input_bool_properties_json),
        ("output_bool_properties", output_bool_properties_json),
        ("input_float_properties", input_float_properties_json),
        ("output_float_properties", output_float_properties_json),
        ("input_int_properties", input_int_properties_json),
        ("output_int_properties", output_int_properties_json),
        ("input_string_properties", input_string_properties_json),
        ("output_string_properties", output_string_properties_json),
        ("input_transform_properties", input_transform_properties_json),
        ("output_transform_properties", output_transform_properties_json),
    ):
        try:
            params[key] = _json.loads(raw) if raw else []
        except Exception:
            params[key] = []
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_lua_runner_component", params))


@mcp.tool()
async def setup_lua(
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
    auto_start_per_frame: bool = False,
    host: str = "",
    new_name: str = "",
    template_guid: str = "",
    script: str = "",
    array: str = "",
) -> str:
    """Full Lua pipeline (BF1DevPlugin style):
    1) duplicate LuaRunnerCompiledLua EBX+RES
    2) create LuaRunnerScriptEntityData on blueprint, bind CompiledLua
    3) compile body/source with CompiledLua_{guid} entrypoint
    """
    import json as _json
    params = {
        "lua_name": lua_name or new_name, "blueprint": blueprint or host,
        "template": template, "realm": realm,
        "auto_start_init": auto_start_init,
        "auto_start_per_frame": auto_start_per_frame,
    }
    if template_guid:
        params["template_guid"] = template_guid
    if script:
        params["script"] = script
    if array:
        params["array"] = array
    if body:
        params["body"] = body
    if source:
        params["source"] = source
    if path:
        params["path"] = path
    try:
        params["input_events"] = _json.loads(input_events_json) if input_events_json else []
        params["output_events"] = _json.loads(output_events_json) if output_events_json else []
    except Exception as exc:
        return format_result({"success": False,
                              "error": "input_events_json / output_events_json is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    return format_result(await asyncio.to_thread(frosty_client.send_command, "setup_lua", params))


@mcp.tool()
async def get_lua_guid_name(name: str = "", guid: str = "", class_guid: str = "") -> str:
    """Compute BF1-style guid_name / entrypoint CompiledLua_{GUID_WITH_UNDERSCORES} from Lua asset or class_guid."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_lua_guid_name", {
        "name": name, "guid": guid, "class_guid": class_guid,
    }))


@mcp.tool()
async def export_atlas_texture(name: str = "", guid: str = "", path: str = "") -> str:
    """Export AtlasTextureAsset to DXT5 DDS (requires AtlasTexturePlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_atlas_texture", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
async def import_atlas_texture(name: str = "", guid: str = "", path: str = "") -> str:
    """Import DXT5/BC3 DDS into AtlasTextureAsset."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_atlas_texture", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
async def export_svg(name: str = "", guid: str = "", path: str = "") -> str:
    """Export SvgImage asset to .svg (requires SvgImagePlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_svg", {"name": name, "guid": guid, "path": path}))


@mcp.tool()
async def import_svg(name: str = "", guid: str = "", path: str = "") -> str:
    """Import an SVG file into an SvgImage asset (requires SvgImagePlugin)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_svg", {
        "name": name, "guid": guid, "path": path,
    }))


@mcp.tool()
async def get_sound_info(name: str = "", guid: str = "") -> str:
    """Inspect SoundWave chunks / RuntimeVariations / Segments."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_sound_info", {"name": name, "guid": guid}))


@mcp.tool()
async def export_sound_wav(name: str = "", guid: str = "", path: str = "", variation: int = 0) -> str:
    """Export SoundWave variation to WAV (Pcm16Big codec only)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_sound_wav", {
        "name": name, "guid": guid, "path": path, "variation": variation,
    }))


@mcp.tool()
async def import_sound_wav(name: str = "", guid: str = "", path: str = "", variation: int = 0) -> str:
    """Import PCM16 WAV into SoundWave variation as Pcm16Big (mono auto-upmixed to stereo)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_sound_wav", {
        "name": name, "guid": guid, "path": path, "variation": variation,
    }))


@mcp.tool()
async def duplicate_deep(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "duplicate_deep", params))


@mcp.tool()
async def create_schematic_channel_asset(
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
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_schematic_channel_asset", params))


@mcp.tool()
async def create_schematic_channel_entity(
    name: str = "",
    guid: str = "",
    channel_name: str = "",
    channel_guid: str = "",
    realm: str = "ClientAndServer",
    array: str = "Objects",
    input_properties_json: str = "[]",
    output_properties_json: str = "[]",
    input_ref_properties_json: str = "[]",
    output_ref_properties_json: str = "[]",
) -> str:
    """Create SchematicChannelEntityData on a blueprint and bind Channel to a SchematicChannelAsset."""
    import json as _json
    params = {
        "name": name, "guid": guid, "channel_name": channel_name,
        "channel_guid": channel_guid, "realm": realm, "array": array,
    }
    try:
        params["input_properties"] = _json.loads(input_properties_json) if input_properties_json else []
        params["output_properties"] = _json.loads(output_properties_json) if output_properties_json else []
        params["input_ref_properties"] = _json.loads(input_ref_properties_json) if input_ref_properties_json else []
        params["output_ref_properties"] = _json.loads(output_ref_properties_json) if output_ref_properties_json else []
    except Exception as exc:
        return format_result({"success": False,
                              "error": "schematic property list is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    try:
        params["input_ref_properties"] = _json.loads(input_ref_properties_json) if input_ref_properties_json else []
    except Exception:
        params["input_ref_properties"] = []
    try:
        params["output_ref_properties"] = _json.loads(output_ref_properties_json) if output_ref_properties_json else []
    except Exception:
        params["output_ref_properties"] = []
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_schematic_channel_entity", params))


# ---------------------------------------------------------------------------
# Advanced Bundle Editor + Blueprint utilities (v1.10)
# ---------------------------------------------------------------------------

@mcp.tool()
async def advanced_add_to_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    netreg: bool = False,
    mvdb: bool = False,
    unlock_table: bool = False,
    allow_fallback: bool = False,
) -> str:
    """Add EBX via AdvancedBundleEditorPlugin.BundleEditors (with RES/Chunk extensions).

    Optional: also add to NetworkRegistry / MeshVariationDb / UnlockIdTable.
    Without the plugin this returns PLUGIN_MISSING; allow_fallback=True switches
    to add_to_bundle_smart instead.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "advanced_add_to_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
        "netreg": netreg, "mvdb": mvdb, "unlock_table": unlock_table,
        "allow_fallback": allow_fallback,
    }))


@mcp.tool()
async def advanced_remove_from_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    netreg: bool = False,
    mvdb: bool = False,
    unlock_table: bool = False,
    allow_fallback: bool = False,
) -> str:
    """Remove EBX via AdvancedBundleEditorPlugin (optionally NetReg/MVDB/Unlock table).

    Without the plugin this returns PLUGIN_MISSING; allow_fallback=True switches
    to remove_from_bundle_smart instead.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "advanced_remove_from_bundle", {
        "name": name, "guid": guid, "bundle": bundle,
        "allow_fallback": allow_fallback,
        "netreg": netreg, "mvdb": mvdb, "unlock_table": unlock_table,
    }))


@mcp.tool()
async def completely_add_to_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    recursive: bool = True,
) -> str:
    """Completely add asset: Bundle + NetReg + MVDB (+ Unlock tables), optionally recurse dependencies.

    Mirrors BunpyApi.CompletelyAddAsset / RecAddToBundle+Reg+Mvdb. Requires AdvancedBundleEditorPlugin.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "completely_add_to_bundle", {
        "name": name, "guid": guid, "bundle": bundle, "recursive": recursive,
    }))


@mcp.tool()
async def completely_remove_from_bundle(
    name: str = "",
    guid: str = "",
    bundle: str = "",
    recursive: bool = True,
) -> str:
    """Completely remove from Bundle + NetReg + MVDB (+ Unlock), optionally recurse. Requires AdvancedBundleEditorPlugin."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "completely_remove_from_bundle", {
        "name": name, "guid": guid, "bundle": bundle, "recursive": recursive,
    }))


@mcp.tool()
async def validate_bundle_add(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Validate whether an asset can be (recursively) added / netreg / mvdb for a bundle."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "validate_bundle_add", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
async def create_advanced_bundle(
    bundle: str = "",
    super_bundle: str = "",
    bundle_type: str = "Shared",
    generate_blueprints: bool = True,
    blueprint_type: str = "BlueprintBundle",
) -> str:
    """Create a new bundle via BunpyApi.AddBundle (Blueprint + NetReg + MVDB scaffolding). Requires AdvancedBundleEditorPlugin."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_advanced_bundle", {
        "bundle": bundle, "super_bundle": super_bundle,
        "bundle_type": bundle_type, "generate_blueprints": generate_blueprints,
        "blueprint_type": blueprint_type,
    }))


@mcp.tool()
async def set_bundle_super(bundle: str = "", super_bundle: str = "", force: bool = False) -> str:
    """Repoint a bundle at a different super bundle.

    Only editor-added bundles reach a .fbmod, so a vanilla bundle is refused with
    NOT_EXPORTED unless force=True, in which case the editor changes and the mod
    does not.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_bundle_super", {
        "bundle": bundle, "super_bundle": super_bundle, "force": force,
    }))


@mcp.tool()
async def delete_bundle(bundle: str = "", clear_members: bool = True, commit: bool = False) -> str:
    """Delete an added bundle (Frosty's RevertBundle removes it). Optionally clears its id from every asset's added bundles.

    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "delete_bundle", {
        "bundle": bundle, "clear_members": clear_members, "commit": commit,
    }))


@mcp.tool()
async def list_super_bundles() -> str:
    """List super bundles with their bundle counts."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_super_bundles", {}))


@mcp.tool()
async def project_stats() -> str:
    """Project totals: modified/dirty counts, added vs modified EBX, added bundles/res/chunks."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "project_stats", {}))


# ---------------------------------------------------------------------------
# Project lifecycle (mirrors the editor File menu)
# ---------------------------------------------------------------------------

@mcp.tool()
async def create_asset(name: str = "", type: str = "", based_on: str = "", based_on_guid: str = "",
                      new_type: str = "", bundle: str = "", copy_bundles: bool = False,
                      commit: bool = False) -> str:
    """Create a resource-backed asset (EBX+RES+Chunk) via PVZBundleManagerPlugin handlers.

    The EBX, RES and Chunk are built and linked together, which a plain EBX
    create does not do.
    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "create_asset", {
        "name": name, "type": type, "based_on": based_on, "based_on_guid": based_on_guid,
        "new_type": new_type, "bundle": bundle, "copy_bundles": copy_bundles,
        "commit": commit,
    }))


@mcp.tool()
async def import_sound(name: str = "", guid: str = "", path: str = "", codec: int = 0,
                       channel_count: int = 0, seekable: bool = False, remix: bool = True,
                       chunk_index: int = 0, segment_index: int = -1,
                       variation_index: int = -1) -> str:
    """Re-encode an audio file into a SoundWave asset (requires SoundEditorPlugin).

    codec/channel_count are read from the existing chunk when left at 0.
    """
    params = {"name": name, "guid": guid, "path": path, "remix": remix,
              "chunk_index": chunk_index, "segment_index": segment_index,
              "variation_index": variation_index}
    if codec > 0:
        params["codec"] = codec
    if channel_count > 0:
        params["channel_count"] = channel_count
    if seekable:
        params["seekable"] = seekable
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_sound", params))


@mcp.tool()
async def import_project(path: str = "", overwrite_modified: bool = False, commit: bool = False) -> str:
    """Merge bundles/assets from another .fbproject into the open project.

    overwrite_modified=True replaces assets that already have unsaved edits.
    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "import_project", {
        "path": path, "overwrite_modified": overwrite_modified, "commit": commit,
    }))


@mcp.tool()
async def check_asset_ready(name: str = "", guid: str = "", bundle: str = "") -> str:
    """Report whether a bundle has everything the asset needs (deps and linked assets)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "check_asset_ready", {
        "name": name, "guid": guid, "bundle": bundle,
    }))


@mcp.tool()
async def refresh_mesh_variations() -> str:
    """Reload Frosty's MeshVariationDb, the same pass the Refresh Mesh Variations menu runs."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "refresh_mesh_variations", {}))


@mcp.tool()
async def capabilities() -> str:
    """Report bridge version, profile, whether a project is open, and which optional plugins are loaded."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "capabilities", {}))


@mcp.tool()
async def get_project_info() -> str:
    """Current .fbproject: display name, filename, profile, dirty flag, ModSettings."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_project_info", {}))


@mcp.tool()
async def save_project() -> str:
    """Save the current project to its existing filename."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "save_project", {}))


@mcp.tool()
async def save_project_as(path: str = "", overwrite: bool = False) -> str:
    """Save the current project under a new .fbproject path and switch to it."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "save_project_as", {
        "path": path, "overwrite": overwrite,
    }))


@mcp.tool()
async def save_project_copy(path: str = "", overwrite: bool = False) -> str:
    """Write a copy of the project to path without changing the open project or its dirty flag."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "save_project_copy", {
        "path": path, "overwrite": overwrite,
    }))


@mcp.tool()
async def open_project(path: str = "", save_first: bool = False, discard: bool = False) -> str:
    """Open another .fbproject.

    Refuses when the current project is dirty: pass save_first=True to save it
    first, or discard=True to drop the unsaved changes.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "open_project", {
        "path": path, "save_first": save_first, "discard": discard,
    }))


@mcp.tool()
async def new_project(save_first: bool = False, discard: bool = False) -> str:
    """Start a blank project.

    Refuses when the current project is dirty: pass save_first=True to save it
    first, or discard=True to drop the unsaved changes.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "new_project", {
        "save_first": save_first, "discard": discard,
    }))


@mcp.tool()
async def revert_all(discard: bool = False, commit: bool = False) -> str:
    """Revert every asset and remove added bundles. Requires discard=True when the project is dirty.

    First call returns would_change; call again with commit=True to execute.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "revert_all", {
        "discard": discard, "commit": commit,
    }))


@mcp.tool()
async def get_mod_settings() -> str:
    """Read the project's ModSettings (title/author/category/version/description/link/icon/screenshots)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_mod_settings", {}))


@mcp.tool()
async def set_mod_settings(title: str = "", author: str = "", category: str = "",
                           version: str = "", description: str = "", link: str = "",
                           selected_category: int = -1, icon_path: str = "",
                           screenshot_paths_json: str = "[]") -> str:
    """Update ModSettings fields.

    selected_category is an index into the editor's category list (0 = Custom);
    leave it at -1 to keep the current value. icon_path and screenshot_paths_json
    (up to 4 paths) are optional.
    """
    import json as _json
    params = {
        "title": title, "author": author, "category": category,
        "version": version, "description": description, "link": link,
    }
    if selected_category >= 0:
        params["selected_category"] = selected_category
    if icon_path:
        params["icon_path"] = icon_path
    try:
        params["screenshot_paths"] = _json.loads(screenshot_paths_json) if screenshot_paths_json else []
    except Exception as exc:
        return format_result({"success": False, "error": "screenshot_paths_json is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    return format_result(await asyncio.to_thread(frosty_client.send_command, "set_mod_settings", params))


@mcp.tool()
async def export_mod(path: str = "", overwrite: bool = False, title: str = "", author: str = "",
                     category: str = "", version: str = "", description: str = "",
                     link: str = "", icon_path: str = "", allow_empty: bool = False,
                     selected_category: int = -1) -> str:
    """Write a .fbmod from the current project. This never runs automatically.

    A project with no modifications is refused unless allow_empty=True. The
    metadata arguments override ModSettings for this export only; the project's
    settings are left unchanged.
    """
    params = {"path": path, "overwrite": overwrite, "allow_empty": allow_empty}
    for key, value in (("title", title), ("author", author), ("category", category),
                       ("version", version), ("description", description),
                       ("link", link), ("icon_path", icon_path)):
        if value:
            params[key] = value
    if selected_category >= 0:
        params["selected_category"] = selected_category
    return format_result(await asyncio.to_thread(frosty_client.send_command, "export_mod", params))


@mcp.tool()
async def get_log(max_chars: int = 20000) -> str:
    """Return the tail of the Frosty editor log."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "get_log", {
        "max_chars": max_chars,
    }))


@mcp.tool()
async def list_linked_assets(kind: str = "ebx", name: str = "", guid: str = "") -> str:
    """List LinkedAssets of an EBX/RES/Chunk entry."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_linked_assets", {
        "kind": kind, "name": name, "guid": guid,
    }))


@mcp.tool()
async def link_asset(kind: str = "ebx", name: str = "", guid: str = "",
                     linked_kind: str = "res", linked_name: str = "", linked_guid: str = "") -> str:
    """Add a LinkedAsset from one asset entry to another."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "link_asset", {
        "kind": kind, "name": name, "guid": guid,
        "linked_kind": linked_kind, "linked_name": linked_name, "linked_guid": linked_guid,
    }))


@mcp.tool()
async def unlink_asset(kind: str = "ebx", name: str = "", guid: str = "",
                       linked_kind: str = "res", linked_name: str = "", linked_guid: str = "") -> str:
    """Remove a LinkedAsset link between two asset entries."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "unlink_asset", {
        "kind": kind, "name": name, "guid": guid,
        "linked_kind": linked_kind, "linked_name": linked_name, "linked_guid": linked_guid,
    }))


@mcp.tool()
async def check_guid_invariants(limit: int = 100, max_scan: int = 5000, query: str = "",
                                added_only: bool = False, names_json: str = "[]") -> str:
    """Scan EBX entries for entry.Guid != asset.FileGuid and duplicate file GUIDs.

    The scan is bounded by max_scan (default 5000 assets); narrow it with query,
    added_only or names_json for a full project sweep in several calls.
    """
    import json as _json
    params = {"limit": limit, "max_scan": max_scan, "query": query, "added_only": added_only}
    try:
        params["names"] = _json.loads(names_json) if names_json else []
    except Exception as exc:
        return format_result({"success": False, "error": "names_json is not valid JSON: %s" % exc,
                              "error_code": "INVALID_PARAMS"})
    return format_result(await asyncio.to_thread(frosty_client.send_command, "check_guid_invariants", params))


@mcp.tool()
async def batch_set_field(items: list = []) -> str:
    """Apply many field writes in one call. items entries use the same params as set_ebx_field, or set_component_property when index/component_type is present."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "batch_set_field", {"items": items}))


@mcp.tool()
async def diff_ebx(name: str = "", guid: str = "", limit: int = 200) -> str:
    """Field-level YAML diff of a modified EBX against its unmodified (vanilla) data."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "diff_ebx", {
        "name": name, "guid": guid, "limit": limit,
    }))


@mcp.tool()
async def validate_refs(names: list = [], limit: int = 200) -> str:
    """Scan modified assets (or the given names) for dangling external/self/internal pointer refs."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "validate_refs", {"names": names, "limit": limit}))


@mcp.tool()
async def bundle_closure_check(bundle: str = "", include_outside: bool = False, limit: int = 200) -> str:
    """Check a bundle's EBX dependency closure: dangling deps and (optionally) deps that live outside the bundle."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "bundle_closure_check", {
        "bundle": bundle, "include_outside": include_outside, "limit": limit,
    }))


@mcp.tool()
async def registry_gap(name: str = "", names: list = [], registry: str = "", registries: list = [],
                       limit: int = 200, max_registries: int = 8) -> str:
    """Compare an asset's exported object GUIDs against NetworkRegistry entries and list what is missing.

    When no registry is named, up to max_registries discovered NetworkRegistryAsset
    names are checked; registries_skipped reports how many were left out.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "registry_gap", {
        "name": name, "names": names, "registry": registry, "registries": registries,
        "limit": limit, "max_registries": max_registries,
    }))


@mcp.tool()
async def list_bundle_contents(
    bundle: str = "",
    type: str = "",
    modified_only: bool = False,
    offset: int = 0,
    limit: int = 200,
    kind: str = "ebx",
) -> str:
    """List the members of a bundle.

    kind is ebx (default), res, chunk or all; type/modified_only only apply to EBX.
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_bundle_contents", {
        "bundle": bundle, "type": type, "modified_only": modified_only,
        "offset": offset, "limit": limit, "kind": kind,
    }))


@mcp.tool()
async def resolve_hash(hash: str = "", value: str = "") -> str:
    """Reverse FNV hash to string via Utils.GetString (same as Blueprint Editor hashing utils).

    Pass decimal or 0xHEX in hash (or value).
    """
    return format_result(await asyncio.to_thread(frosty_client.send_command, "resolve_hash", {
        "hash": hash or value, "value": value,
    }))


@mcp.tool()
async def encode_property_flags(
    realm: str = "ClientAndServer",
    prop_type: str = "Default",
    source_cant_be_static: bool = False,
) -> str:
    """Encode PropertyConnection Flags (mirrors BlueprintEditor PropertyFlagsHelper). Pair with decode_connection_flags."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "encode_property_flags", {
        "realm": realm, "prop_type": prop_type, "source_cant_be_static": source_cant_be_static,
    }))


@mcp.tool()
async def list_node_ports(type: str = "", component_type: str = "") -> str:
    """List Blueprint Editor node input/output ports for an EntityData type (EntityNode extension or SDK fallback)."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_node_ports", {
        "type": type or component_type, "component_type": component_type,
    }))


@mcp.tool()
async def list_registered_node_types(query: str = "", offset: int = 0, limit: int = 200) -> str:
    """List EntityNode / EntityMappingNode types registered by FrostyBlueprintEditor."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "list_registered_node_types", {
        "query": query, "offset": offset, "limit": limit,
    }))


@mcp.tool()
async def validate_connections(name: str = "", guid: str = "") -> str:
    """Scan Property/Event/Link connections for null pointers and empty field names."""
    return format_result(await asyncio.to_thread(frosty_client.send_command, "validate_connections", {
        "name": name, "guid": guid,
    }))


if __name__ == "__main__":
    try:
        debug_log("Starting FastMCP server for Frosty Editor...")
        mcp.run()
    except Exception as e:
        debug_log(f"Fatal Crash: {e}")
        traceback.print_exc(file=sys.stderr)
