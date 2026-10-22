# FrostyMcpPlugin-AdaptVirjoinga

> **Adapted for [Virjoinga/FrostyToolsuite-1](https://github.com/Virjoinga/FrostyToolsuite-1)** —
> the Frosty Tool Suite **1.0.7** fork reworked for *Plants vs. Zombies: Battle for Neighborville*.
> Build against that tree, not the official CadeEvs one.

A plugin for [Frosty Editor](https://github.com/CadeEvs/FrostyToolsuite) that exposes the currently loaded game project to any MCP client (Cursor, Claude Desktop, Claude Code, OpenAI Codex, and others) over the [Model Context Protocol](https://modelcontextprotocol.io/).

It lets an agent search and edit EBX, manage bundles, inspect Frostbite SDK types (Type Explorer), and drive blueprint connections without clicking through the editor UI.

This plugin talks to **whatever profile Frosty has loaded**. Here it is driven against a *Plants vs. Zombies: Battle for Neighborville* profile.

## What works

- Named-pipe JSON-RPC bridge started with Frosty (`Tools → MCP Bridge Status`)
- Query / open / export EBX, RES, chunks, and bundles
- Property, event, and link connections; create / duplicate / remove blueprint objects
- Type Explorer: search SDK types, inspect fields, walk nested `PointerRef` / `List` / struct types
- NetworkRegistry objects: list, add and remove external pointer refs
- Texture, mesh, Lua, sound, atlas, and schematic helpers (when the matching Frosty plugins are loaded)

## Architecture

```
Cursor / Claude / Codex  --stdio-->  mcp_frosty.py  --Named Pipe-->  FrostyMcpPlugin (inside Frosty Editor)
```

Wire protocol: `uint32_le(payload_length)` + UTF-8 JSON-RPC (same layout as the Cheat Engine MCP bridge).

Pipe name: `\\.\pipe\Frosty_MCP_Bridge_v1_<profile>` (falls back to `Frosty_MCP_Bridge_v1` when no profile is loaded)

Plugin version: **1.13.1**

The `<profile>` suffix comes from Frosty's own profile database (`FrostySdk/Profiles.bin`). The table is generated at build time; no game names are hardcoded in the plugin or the Python server.

# Usage

## Requirements

- [Frosty Editor](https://github.com/CadeEvs/FrostyToolsuite) (this tree is built against a FrostyToolsuite checkout)
- Windows (named pipes + `pywin32`)
- Python 3.10+ with `mcp>=1.0.0,<2.0.0` (mcp 2.x removed `FastMCP`)
- Any MCP client that can spawn a local stdio server (Cursor, Claude Desktop, Claude Code, Codex, …)

## 1. Build and install the Frosty plugin

Build `FrostyMcpPlugin.sln` as **Developer - Debug | x64**. A PostBuild step copies `FrostyMcpPlugin.dll` into `FrostyEditor\<OutDir>\Plugins\`.

Start Frosty, load a profile, and check the editor log:

```
[MCP v1.13.1] Listening on: Frosty_MCP_Bridge_v1_<profile>
```

Menu: **Tools → MCP Bridge Status**

If Frosty was already running during the build, restart the editor so it loads the new DLL.

## 2. Python side

```bash
pip install -r MCP_Server/requirements.txt
```

## 3. Connect an MCP client

The Python process is a **stdio** MCP server. Point every client at the same `command` / `args` (use an **absolute** path to `mcp_frosty.py`). Prefer the `python.exe` you used for `pip install` if `python` is not on PATH.

Shared stdio launch:

| Field | Value |
|-------|--------|
| `command` | `python` (or `C:/Users/You/AppData/Local/Python/pythoncore-3.14-64/python.exe`) |
| `args` | `["D:/path/to/FrostyMcpPlugin/MCP_Server/mcp_frosty.py"]` |
| `env` | `FROSTY_MCP_VERSION=1.13.1` (optional; bump it when tool schemas change so clients reload) |
| `env` | `FROSTY_MCP_PROFILE=<profile name>` (optional; internal name or display name, used to build the pipe suffix) |
| `env` | `FROSTY_MCP_PIPE=\\.\pipe\...` (optional; full pipe path override, wins over `FROSTY_MCP_PROFILE`) |

After changing tools, bump `FROSTY_MCP_VERSION` and restart the MCP server in that client. Call `ping` first; it should return `profile_loaded: true` once Frosty has a profile open.

### Multiple editor profiles

Every Frosty editor instance listens on `Frosty_MCP_Bridge_v1_<profile>`, so several editors can run at the same time. Add one server entry per profile and set `FROSTY_MCP_PROFILE` to the internal profile name or the display name shown in Frosty (for example `PVZBattleforNeighborville` or `Plants vs Zombies: Battle for Neighborville™`). `mcp_frosty.py` resolves it through `MCP_Server/pipe_profiles.json`; an unknown profile is used verbatim as the suffix, which matches the plugin.

### Cursor

User-level: `%USERPROFILE%\.cursor\mcp.json`  
Project-level: `.cursor/mcp.json`  
Or: Cursor Settings → MCP.

```json
{
  "mcpServers": {
    "frosty": {
      "command": "python",
      "args": ["D:/path/to/FrostyMcpPlugin/MCP_Server/mcp_frosty.py"],
      "env": { "FROSTY_MCP_VERSION": "1.13.1" }
    }
  }
}
```

Disable/enable the `frosty` server (or reload MCP) after editing this file.

### Claude Desktop

Windows: `%APPDATA%\Claude\claude_desktop_config.json`  
macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

Same `mcpServers` JSON as Cursor. Fully quit and reopen Claude Desktop after saving. This plugin is Windows-only (named pipes), so Claude Desktop must be running on the same Windows machine as Frosty.

### Claude Code

User-level: `%USERPROFILE%\.claude.json` (Windows) or `~/.claude.json`  
Project-level: `.mcp.json` in the project root (same `mcpServers` object).

Do **not** put MCP servers in `~/.claude/settings.json` — Claude Code does not load them from there.

CLI equivalent:

```bash
claude mcp add frosty --scope user -- python D:/path/to/FrostyMcpPlugin/MCP_Server/mcp_frosty.py
```

If you already configured Claude Desktop, you can import with `claude mcp add-from-claude-desktop`. Then `claude mcp list` and restart Claude Code.

### OpenAI Codex (CLI, IDE extension, ChatGPT desktop Codex)

Codex does **not** use Cursor/Claude JSON. Edit `%USERPROFILE%\.codex\config.toml` (or a trusted project's `.codex/config.toml`). The table name must be `mcp_servers` (underscore).

```toml
[mcp_servers.frosty]
command = "python"
args = ["D:/path/to/FrostyMcpPlugin/MCP_Server/mcp_frosty.py"]
startup_timeout_sec = 20
tool_timeout_sec = 120

[mcp_servers.frosty.env]
FROSTY_MCP_VERSION = "1.13.1"
```

CLI equivalent:

```bash
codex mcp add frosty -- python D:/path/to/FrostyMcpPlugin/MCP_Server/mcp_frosty.py
```

`codex mcp list` should show `frosty`. The CLI, IDE extension, and ChatGPT desktop Codex host share this `config.toml`.

Pasting a JSON `mcpServers` block into `config.toml` will be ignored.

## Typical workflow

1. Open Frosty and load the game profile / project.
2. Confirm the MCP log line above.
3. In your MCP client, `ping` → `get_editor_info`.
4. `search_ebx` / `open_asset` / `list_components` / `list_connections` before editing.
5. Mutate with `set_ebx_field`, `create_component`, `add_property_connection`, `safe_add_to_bundle`, etc.

Example (blueprint delay node):

```
list_components(name="levels/.../logic")
create_component(name="...", type="DelayEntityData", flag_preset="client_and_server",
                 realm="ClientAndServer", properties_json='{"Delay":1.5,"AutoStart":true}')
add_property_connection(name="...", source_guid="<A>", target_guid="<B>",
                        source_field="Out", target_field="In", realm="ClientAndServer")
```

Example (Type Explorer):

```
search_types(query="ServerSpawn", match_subclass=false)
get_type_info(name="SpawnReferenceObjectData")
get_type_field(name="SpawnReferenceObjectData", field="Blueprint")
```

# For developers

If you want to contribute, please read this first.

## Build instructions

This plugin is meant to live next to a FrostyToolsuite tree, the same way [FrostyBlueprintEditor](https://github.com/MagixGames/FrostyBlueprintEditor) does.

Default layout:

```
FrostyToolsuite/
  FrostyEditor/
  FrostySdk/
  FrostyPlugin/
  FrostyControls/
  FrostyHash/
  Plugins/
    FrostyMcpPlugin/     <-- this repository
```

If your checkout is not in that layout:

1. Open `FrostyMcpPlugin.csproj`.
2. Find the `FrostyRoot` property (commented as Frosty Toolsuite root).
3. Point it at the folder that contains `FrostyEditor` and `FrostySdk`, **or** pass `/p:FrostyRoot=C:\path\to\FrostyToolsuite\` when building.

PostBuild copies the DLL into `$(FrostyRoot)FrostyEditor\$(OutDir)Plugins\`. Change `FrostyRoot` if that copy lands in the wrong editor.

To launch from Visual Studio, set the debug start program to your `FrostyEditor.exe`.

```bat
msbuild FrostyMcpPlugin.csproj /p:Configuration="Developer - Debug" /p:Platform=x64
```

## Pipe profile database

`FrostySdk/Profiles.bin` is Frosty's profile database. `MCP_Server/gen_pipe_profiles.py` reads it and regenerates the two checked-in tables:

- `Bridge/PipeProfiles.g.cs` - compiled into the plugin
- `MCP_Server/pipe_profiles.json` - read by the Python server

Re-run it after updating Frosty:

```bash
python MCP_Server/gen_pipe_profiles.py --profiles-bin /path/to/FrostyToolsuite/FrostySdk/Profiles.bin
```

Do not edit the generated files by hand. A profile that is not in the table still works; the plugin and the Python server both fall back to the raw profile name as the pipe suffix.

## Optional Frosty plugins

Some MCP tools call into other editor plugins by reflection. They no-op or return a clear error if those plugins are missing:

| Frosty plugin | MCP tools |
|---------------|-----------|
| TexturePlugin | `export_texture` / `import_texture` |
| MeshSetPlugin | `export_mesh` / `import_mesh` |
| Lua plugin | `get_lua_source` / `import_lua` / `setup_lua` |
| DuplicationPlugin | `duplicate_deep` |
| AdvancedBundleEditor | `advanced_add_to_bundle`, `completely_add_to_bundle`, … |
| FrostyBlueprintEditor | `list_node_ports`, `list_registered_node_types`, `validate_connections` |

## Tool groups

| Area | Examples |
|------|----------|
| Query | `ping`, `get_editor_info`, `search_ebx`, `get_ebx_xml` / `yaml`, `list_modified_assets` |
| SDK types | `search_types`, `get_type_info`, `get_type_field`, `list_type_namespaces` |
| EBX edit | `get/set_ebx_property`, `get/set_ebx_field`, `create_ebx`, `duplicate_ebx`, `revert_asset`, `set_root_flags` |
| Bundles | `add_to_bundle`, `safe_add_to_bundle`, `add_to_bundle_smart`, `list_bundles` |
| Blueprint | `list/create/duplicate/remove_component`, `remove_ebx_item`, `add_*_connection`, `list_interface` |
| NetworkRegistry | `list_network_registry`, `add_to_network_registry`, `remove_from_network_registry` |
| Media | texture, mesh, lua, wav, atlas, svg |

`get_ebx_entry` reports bundle membership in four fields. `bundles` is the **effective** set —
vanilla, minus `removed_bundles`, plus `added_bundles` — and the three parts are reported
separately too. Reach for `original_bundles` when you want the untouched vanilla list.

The Python file `MCP_Server/mcp_frosty.py` is the source of truth for tool names and arguments.

## Contribution guidelines

- Keep the **core bridge** (pipe server, generic EBX / bundle / connection tools) game-agnostic. If you add something that only makes sense for one title, say so in the tool's docstring.
- Do not hardcode game or profile names. The known profiles live in the generated table (`Bridge/PipeProfiles.g.cs` and `MCP_Server/pipe_profiles.json`); regenerate it instead of editing source lists.
- Document new MCP tools in this README and in the `@mcp.tool()` docstring. Bump `McpBridgeServer.Version`, `[assembly: PluginVersion]`, and `FROSTY_MCP_VERSION` together so MCP clients reload the schema.
- Prefer comments on new handlers: reflection against Frosty APIs is brittle, and the next person needs to know *why* an optional argument is padded.

## License

Use and modify as you need for Frosty Toolsuite plugin development. No third-party license is bundled with this repository.
