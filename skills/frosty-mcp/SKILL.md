---
name: frosty-mcp
description: Use the Frosty Editor MCP bridge to inspect and edit Frostbite EBX assets, components and blueprint connections, bundles, network registries, project lifecycle and exports. Load this whenever a task involves the frosty-* MCP tools, Frosty Editor, .fbproject/.fbmod work, EBX fields or bundle membership.
---

# Frosty Editor MCP

Wraps a running Frosty Editor through a named pipe. 160 tools.

## Servers and prerequisites

The plugin registers one server per Frosty profile it supports, named `frosty-<profile>`.
Tool names and arguments are identical across them; only the editor behind the pipe
differs. Use the server that matches the profile you are editing.

The bridge lives inside the editor process. Nothing works until Frosty Editor is running
with a profile loaded; the pipe name is derived from that profile at startup, so it cannot
be switched without restarting the editor. Profiles are chosen by hand in the editor's
launcher window; no tool can do it.

## Before anything else

```
capabilities()
```

Returns the bridge version, pipe name, profile, whether a project is open, the
localization database in use, and which optional plugins are loaded. Optional plugins gate
whole families of tools; check before calling one and report `PLUGIN_MISSING` rather than
trying a workaround.

```
ping()               # is the pipe reachable at all
get_editor_info()    # profile, EBX count, dirty count, SDK version
get_project_info()   # .fbproject path, dirty flag, ModSettings
get_log(max_chars)   # tail of the editor log; first stop for unexplained failures
```

## Two-phase commit

16 tools never act on the first call. The first call resolves the targets, stores the plan,
and returns **`success: false`** with `error_code: "PREVIEW_ONLY"`, `would_change: true`
and a `plan` object. Nothing has been applied at that point.

Calling the *identical* tool again with `commit: true` executes the stored plan. The second
call must be the **immediately next** call, must be the **same tool**, and must have
**deep-equal parameters** (only `commit` may differ). Any other call in between, a client
disconnect, or 10 minutes of idle time discards the plan and yields `COMMIT_REQUIRED`.

Two-phase tools: `delete_bundle`, `revert_asset`, `revert_all`, `remove_from_bundle`,
`remove_from_bundle_smart`, `remove_from_network_registry`, `remove_component`,
`remove_ebx_item`, `remove_connection`, `remove_connections_by_guid`,
`remove_interface_field`, `remove_interface_event`, `manage_bundles`, `import_project`,
`create_asset`, and `import_ebx_bin` when `data_only: false`.

Read `plan` before committing: it lists the resolved members, indices, counts and GUIDs.

## Tool catalogue

### Connectivity and editor state
- `ping`: reachability, version, pipe name.
- `capabilities`: profile, project-open flag, plugin availability, localization DB.
- `get_editor_info`: profile, packs, EBX count, dirty count, SDK version, selection.
- `get_selected_asset`: the Data Explorer selection and the opened editor tab.
- `get_log`: tail of the editor log (`max_chars`, `total_length`, `truncated`).

### Project lifecycle
- `get_project_info`: display name, filename, profile, dirty flag, ModSettings summary.
- `save_project`: save to the current filename; also refreshes explorers and title.
- `save_project_as`: save under a new `.fbproject` path and switch to it.
- `save_project_copy`: write a copy, leaving the open project and its dirty flag alone.
- `open_project`: open another project; `save_first` or `discard` handles unsaved work.
- `new_project`: start a blank project; same dirty handling.
- `revert_all`: revert every asset and drop added bundles (`discard`, two-phase).
- `project_stats`: added/modified EBX, RES and chunk counts that reconcile with each other.

### Searching and inspecting
- `search_ebx`: by name substring and/or type; `modified_only`, paged.
- `get_ebx_entry`: metadata, effective bundle membership, linked assets (capped).
- `search_res` / `get_res_entry`: RES by name or resource RID.
- `search_chunks` / `get_chunk_entry`: chunks by id or name substring.
- `list_bundles`: name, bundle id, type, added flag, blueprint, super bundle.
- `get_bundle`: members of one bundle, paged per list, with true counts.
- `list_bundle_contents`: members of a bundle filtered by `kind` (ebx/res/chunk/all).
- `list_super_bundles`: super bundles with their bundle counts.
- `get_asset_dependencies`: EBX dependencies and linked assets; `recursive` walks the
  graph (compact rows, paged, bounded by `max_nodes`).
- `find_references`: assets that reference a file GUID (file-level only).
- `find_pointer_references`: every External PointerRef aimed at a file GUID, optionally
  narrowed to one class GUID; the only component-level view (`max_scan`, paged).
- `list_modified_assets`: what the open project has changed; `include_linked` also shows
  assets that only look modified through a linked RES or chunk.

### Text export
- `get_ebx_xml` / `get_ebx_yaml`: one asset as text; `summary`, `filter`, `max_chars`,
  `char_offset` for continuation.
- `export_all_ebx`: every EBX to XML/YAML files under a directory, mirroring paths.

### EBX properties and fields
- `get_ebx_property` / `set_ebx_property`: root object by dotted path
  (`Transform.trans.x`, `Items[0]`); `Components` and `Objects` are interchangeable.
- `search_ebx_fields`: find field names or values inside any component or connection.
- `get_ebx_field` / `set_ebx_field`: one field, addressed by `class_guid`, `index`, or
  `component_type` + `type_index`; `$PropertyConnections[0].SourceField` style paths reach
  connections.
- `batch_set_field`: many field writes in one call, each item shaped like the single call.
- `get_ebx_guids`: file GUID, root class GUID, and optionally every object's class GUID.
- `get_internal_pointer_ref`: build an External PointerRef for one asset or component.
- `set_root_flags`: the root object `Flags` word. `flags` is required; some profiles
  require 15 on spatial prefab roots and others use a different value.
- `research_component_usage`: how often one component type appears, with the field names
  and peer types it is wired to; `assets_json` studies an explicit asset list instead of
  scanning.

### Creating, copying and reverting EBX
- `create_ebx`: a bare EBX of a type; only suitable for pure-data types.
- `create_asset`: a resource-backed asset (EBX + RES + chunk) through the PVZ asset
  handlers; use this for texture, mesh, sound and atlas assets.
- `duplicate_ebx`: a copy with fresh object GUIDs; `copy_bundles`, `copy_linked`.
- `duplicate_deep`: copy through DuplicationPlugin's per-type extensions, which also copy
  the linked RES and chunks.
- `revert_asset`: undo edits on one EBX/RES/chunk.
- `export_asset`: export through the editor's asset definition (format depends on type).
- `export_ebx_bin` / `import_ebx_bin`: binary EBX round trip.
- `diff_ebx`: line-level diff of an asset against its vanilla data.
- `check_guid_invariants`: find assets whose entry GUID and file GUID diverged.

### RES and chunks
- `export_res` / `import_res`: editor-compatible `.res` files by default
  (`format: "frosty_res"`), `raw` for bare bytes.
- `duplicate_res` / `duplicate_chunk`: copies that keep bundle membership;
  `duplicate_chunk` accepts `texture_res` to keep texture metadata.
- `export_chunk` / `import_chunk`: raw chunk bytes, no metadata handling.

### Bundles
- `add_to_bundle`: one membership row only; warns when the type normally carries
  resources.
- `add_to_bundle_smart`: the membership plus the linked RES/chunk, like the editor.
- `batch_add_to_bundle`: several assets, same options as smart or raw.
- `safe_add_to_bundle`: adds only when absent, for minimal-diff edits.
- `remove_from_bundle` / `remove_from_bundle_smart`: drop an editor-added membership (and
  the resources it brought); native membership is `NOT_EXPORTABLE`.
- `add_to_bf1_map_bundles` / `list_bf1_map_bundles`: the hard-coded Battlefield 1 map
  bundle set. It names assets that exist only in that profile, so every other profile
  returns `NOT_FOUND`.
- `create_bundle`: new bundle in a super bundle; reports `existing`, `created`,
  `type_matches`, `super_matches`.
- `set_bundle_super`: repoint an editor-added bundle at another super bundle.
- `delete_bundle`: remove the newest added bundle (two-phase).
- `bundle_closure_check`: dependencies and linked members missing from a bundle.
- `check_asset_ready`: the same question for one asset in one bundle.
- `can_be_loaded_by_bundle`: ask PVZBundleManagerPlugin (parent/prev bundle aware).
- `manage_bundles`: PVZBundleManagerPlugin's hygiene pass for one asset (two-phase, only
  on the profiles that plugin supports, otherwise `NOT_SUPPORTED_PROFILE`).
- `validate_bundle_add`: the plugin's recursive add checks for an asset and bundle.
- `advanced_add_to_bundle` / `advanced_remove_from_bundle`: AdvancedBundleEditorPlugin
  paths, optional NetReg/MVDB/unlock-table edits; `allow_fallback` degrades to the smart
  variants when the plugin is absent.
- `completely_add_to_bundle` / `completely_remove_from_bundle`: bundle plus NetReg, MVDB
  and unlock tables, optionally recursive; requires the same plugin.
- `create_advanced_bundle`: bundle with blueprint/NetReg/MVDB scaffolding; same plugin.

### Network registry
- `list_network_registry`: entries of a NetworkRegistryAsset, paged.
- `add_to_network_registry`: register one asset root, one component, or explicit refs.
- `remove_from_network_registry`: by index, GUID pair or refs (two-phase).
- `registry_gap`: which exported objects an asset is missing from the registries.

### Components and arrays
- `list_components`: every internal object with class GUID, flags and realm.
- `get_component`: one component, property dump optional (`include_properties`).
- `create_component`: new EntityData on a blueprint; `at_index` inserts, `array_host_guid`
  targets an array on a nested host, and a missing array is an error rather than an orphan.
- `duplicate_component`: copy one component with fresh GUIDs.
- `remove_component`: drop a component and the arrays/connections touching it; reports
  `cleared_paths`.
- `set_component_flags`: object flags and realm.
- `get_component_property` / `set_component_property`: SDK properties by dotted path.
- `remove_ebx_item`: delete elements from any EBX array (property-grid Remove item).
- `insert_ebx_item`: insert into any array (property-grid Insert item).
- `create_ebx_class` / `list_ebx_class_types`: create a class into a PointerRef field.
- `create_struct`: new struct value, optionally appended to a list.
- `clipboard_paste_object`: paste a copied object graph with regenerated GUIDs.

### Blueprint connections
- `list_connections`: property, event and link connections, paged, compact by default.
- `add_property_connection` / `add_event_connection` / `add_link_connection`.
- `update_property_connection` / `update_event_connection` / `update_link_connection` , 
  partial updates; omitted fields keep their current value.
- `remove_connection` / `remove_connections_by_guid`: remove one, or all touching a
  component (two-phase).
- `decode_connection_flags` / `encode_property_flags`: the flag bit layout.
- `validate_connections`: null pointers, empty fields, invalid realm or prop_type,
  duplicate connections, interface fields the source cannot serve.

### Interface descriptor
- `list_interface`, `ensure_interface`.
- `add_interface_field`, `update_interface_field`, `remove_interface_field`.
- `add_interface_event`, `remove_interface_event`.

### Type explorer
- `search_types`: SDK types by name, subclass, namespace or kind.
- `get_type_info`: full detail with fields, inheritance, EBX metadata.
- `get_type_field`: follow one field into its nested type.
- `list_type_namespaces`: namespaces with type counts per kind.
- `list_node_ports` / `list_registered_node_types`: Blueprint Editor node metadata.

### Media
- `get_texture_info`, `export_texture`, `import_texture`: textures; import validates the
  DDS against the existing texture and rebuilds its metadata.
- `get_mesh_info`, `export_mesh`, `import_mesh`: meshes via MeshSetPlugin; import runs
  through the editor's progress window.
- `export_atlas_texture` / `import_atlas_texture`: DXT5 atlas textures.
- `export_svg` / `import_svg`: SVG images, point for point as the editor parses them.
- `get_sound_info`: SoundWave chunks, variations and segments.
- `export_sound_wav` / `import_sound_wav`: PCM16 WAV only, no re-encoding.
- `import_sound`: re-encode any audio file with SoundEditorPlugin's tool, updating the
  chunk and the segment and variation tables.
- `export_ies`: IES profile as a DDS.
- `refresh_mesh_variations`: reload MeshVariationDb.

### Lua
- `get_lua_source`, `export_lua`, `import_lua`: decompile, export, compile.
- `create_lua_asset`: duplicate a LuaRunnerCompiledLua template (cannot start empty).
- `create_lua_runner_component`: add LuaRunnerScriptEntityData and bind it.
- `setup_lua`: full pipeline: create the asset, add the component, compile the body.
- `get_lua_guid_name`: BF1-style entrypoint name for a Lua asset.

### Schematic channels
- `create_schematic_channel_asset`: duplicate a channel template, then fill it.
- `create_schematic_channel_entity`: add SchematicChannelEntityData to a blueprint.

### Localization
- `get_localized_string`, `set_localized_string`, `revert_localized_string`,
  `list_localized_strings`. These need a database plugin; the built-in default is a no-op
  and the tools refuse it with `PLUGIN_MISSING`.

### Hashing
- `hash_string`: FNV1, localized hash, float bit pattern.
- `resolve_hash`: reverse an FNV hash; `known` is false when nothing is registered.

### Links
- `list_linked_assets`, `link_asset`, `unlink_asset`: the LinkedAssets list of any
  EBX/RES/chunk entry.

### Validation and merging
- `validate_refs`: dangling external, self or internal pointers in modified assets.
- `import_project`: merge another `.fbproject` (two-phase, `overwrite_modified`).

## Error codes

Every failure is `{"success": false, "error": "...", "error_code": "..."}`. Branch on the
code; messages change.

| Code | Meaning |
| --- | --- |
| `NOT_READY` | Asset manager, project window or plugin state is not available yet |
| `NOT_FOUND` | Named asset, bundle, registry, field, type member or index does not exist |
| `BUNDLE_NOT_FOUND` | The bundle named in a create or add call does not exist |
| `INVALID_PARAMS` | Bad or missing argument, invalid format string or JSON |
| `INVALID_STATE` | The object exists but the operation does not apply in its current state |
| `ALREADY_EXISTS` / `EXISTS` | Target name or path is taken |
| `NOT_MEMBER` | Asset is not an editor-added member of that bundle or registry |
| `NOT_LINKED` | The two entries are not linked |
| `NOT_EXPORTABLE` | Removal cannot be expressed in a `.fbmod` (native bundle membership) |
| `NOT_EXPORTED` | The edit would not reach a `.fbmod` (vanilla bundle metadata) |
| `NOT_LAST_BUNDLE` | Only the newest added bundle can be deleted |
| `NOT_SUPPORTED_PROFILE` | Tool only works on a specific game profile |
| `NOT_IMPLEMENTED` | This editor build has no such entry point |
| `NOT_BROWSABLE` / `NO_BASE_TYPE` | A type field cannot be followed any further |
| `NOT_A_BLUEPRINT` / `NOT_A_SOUND` | Asset type does not carry the arrays or scans asked for |
| `PREVIEW_ONLY` | A two-phase tool was called without `commit: true` |
| `COMMIT_REQUIRED` | `commit: true` without a matching immediately preceding preview |
| `DIRTY_PROJECT` | Operation refused while the project has unsaved changes |
| `ASSETS_LOST` | `open_project` failed after the editor had already unloaded the old project |
| `NOTHING_TO_EXPORT` | `export_mod` on a project with no modifications |
| `PLUGIN_MISSING` / `PLUGIN_FAILED` | An optional Frosty plugin is absent or threw |
| `UNSUPPORTED_TYPE` | Type uses a custom handler; the binary path refuses it |
| `UNSUPPORTED_VERSION` / `UNSUPPORTED_FORMAT` / `UNSUPPORTED_CODEC` / `UNSUPPORTED_LOGGER` | Input or environment outside what the tool handles |
| `TYPE_NOT_FOUND` / `TYPE_MISMATCH` / `INVALID_TYPE` | SDK type missing, or the asset is another kind |
| `GUID_MISMATCH` / `GUID_INCONSISTENT` | Entry GUID and file GUID diverged; the write was refused |
| `VERIFY_FAILED` | RES round-trip byte comparison failed; previous data restored |
| `SCAN_INCOMPLETE` | A validation scan failed part way; the asset was not fully checked |
| `ITEM_DROPPED` | `insert_ebx_item` added the element but `Update()` discarded it |
| `CODEC_UNKNOWN` / `CHANNELS_UNKNOWN` | `import_sound` could not read the existing sample header; pass `codec` / `channel_count` |
| `ARRAY_NOT_FOUND` | No array accepted the new object; nothing was created |
| `FORBIDDEN` | Attempt to remove the root object |
| `AMBIGUOUS_COMPONENT` | class GUID matches several components; pass `index` |
| `NO_VANILLA_DATA` / `EXPORT_EMPTY` / `INVALID_DDS` / `FORMAT_MISMATCH` | Export or import precondition failed |
| `*_FAILED` | Operation-specific failure: `ADD_FAILED`, `REMOVE_FAILED`, `CREATE_FAILED`, `DUPLICATE_FAILED`, `EXPORT_FAILED`, `IMPORT_FAILED`, `SAVE_FAILED`, `SET_FAILED`, `GET_FAILED`, `LOAD_FAILED`, `OPEN_FAILED`, `REVERT_FAILED`, `DIFF_FAILED`, `DEPS_FAILED`, `LIST_FAILED`, `INFO_FAILED`, `VALIDATE_FAILED`, `PASTE_FAILED`, `RESOLVE_FAILED`, `REFRESH_FAILED`, `COMPILE_FAILED`, `DECOMPILE_FAILED`, `NEW_PROJECT_FAILED`, `MANAGE_FAILED`, `SET_FLAGS_FAILED`, `ENSURE_FAILED`, `PATH_FAILED`, `UPDATE_FAILED` |

Transport-level failures (pipe closed, timeout) come back as `error_code: "RPC_ERROR"`
with the JSON-RPC number in `rpc_code`.

## Easy to get wrong

These are not look-alike tools; each one is a way to get a wrong result that still
reports success. Check the named field before believing the reply.

**A bundle membership alone does not ship a resource-backed asset.** `add_to_bundle` adds
the EBX and nothing else, so a texture or mesh loads in the editor and fails in the game.
Use `add_to_bundle_smart` or `completely_add_to_bundle`, and check `warnings`.

**One bundle is often not enough.** An asset belongs in every bundle its consumers load
from, which is not always the bundle being edited. `bundle_closure_check` and
`check_asset_ready` report dependencies and linked assets that live outside the bundle; a
clean membership row for the asset itself does not cover them.

**A preview expires and a stray call cancels it.** The pending preview is dropped when the
next call is anything other than the same tool with the same parameters, and after ten
minutes. `COMMIT_REQUIRED` means nothing ran. Tools without a `commit` parameter apply
immediately: there is no preview to inspect first.

**`import_ebx_bin(data_only=true)` renumbers internal objects.** Pointers that other assets
hold into those components break silently. Read `changed_class_guids`, then
`affected_assets`, and check its `truncated` flag before concluding nothing else refers to
the asset.

**Shared linked assets are edited in two places.** `duplicate_ebx` without `copy_linked`
gives the copy the same RES and chunks as the source; `linked_assets_shared` says so. Editing
either asset's texture changes both.

**Two assets in one bundle must not share internal object GUIDs.** That combination crashes
the game on load. Use `duplicate_ebx` or `duplicate_deep` rather than copying objects by
hand, and run `check_guid_invariants` after any bulk copy.

**Native bundle membership cannot be removed.** A mod can add memberships and drop the ones
it added; it cannot take an asset out of the bundle it ships in. `remove_from_bundle`
returns `NOT_EXPORTABLE` there, and `native_skipped` counts what it left alone.

**`set_bundle_super` on a vanilla bundle is editor-only.** It returns `NOT_EXPORTED` unless
`force`, and with `force` the mod still ships without the change.

**`create_bundle` can return an existing bundle.** `existing: true` means nothing was
created. A false `type_matches` means the name is taken by a bundle of another type.

**`limit: 0` means zero rows, not the default.** It is the way to read `total` alone. A
page-length field means the array was capped; compare it with the paired count.

**`search_ebx` matches asset names and types, not field values.** Looking for who uses a component type or a GUID returns nothing useful from it. `search_ebx_fields` searches field names and values, `find_pointer_references` answers which assets point at one object, and `find_references` answers the same for a whole file. There is no whole-database scan, so scope every search with `query` or a type.

**A bounded scan that finished early is not a clean result.** `check_guid_invariants`,
`find_pointer_references` and `registry_gap` stop at a limit and say so in `scan_truncated`,
`candidate_count` or `registries_skipped`. Page or narrow the query until the flag is false
before reporting "no problems found".

**An empty `issues` list from `validate_connections` can mean the scan broke.** Check
`scan_error_count` and `SCAN_INCOMPLETE`. Interface field checks only run on property
connections whose target actually carries an interface descriptor; `interface_ports_checked`
says how many were tested.

**`insert_ebx_item` verifies its own insert.** `asset.Update()` drops elements it cannot
re-serialise, so the tool reads the list back and returns `ITEM_DROPPED` with the before and
after counts. A `success: true` from the older path did not prove the element survived.

**`import_sound` reads the codec and channel count from the existing sample header.** When
that header cannot be read the call fails with `CODEC_UNKNOWN` or `CHANNELS_UNKNOWN` instead
of guessing, because a wrong guess re-encodes the asset in the wrong format and still
reports success. `segment_index` and `variation_index` default to 0; on a multi-segment
asset that is a guess, so check `segment_count` and `variation_count`.

**Importing into a streamed sound appends to the existing chunk data.** Re-importing without
reverting first grows the chunk each time. Revert the asset between attempts.

**Connection fields need the `$` path, and flags have three parts.** `set_ebx_field` with
`$PropertyConnections[3].SourceField` addresses the connection struct; a bare name addresses
the component. Writing `flags` wholesale can flip bit 3 (source must not be static) and bits
4 and 5 (property type) along with the realm in bits 0 to 2, which changes whether the
client or the server sees the property.

**Internal GUIDs are reassigned between editor sessions.** A `class_guid` or `object_index`
captured before a restart, a revert or a duplicate may address a different object. Re-read
`list_components` instead of reusing it.

**`export_mod` needs a saved project and actual modifications.** It refuses with
`NOTHING_TO_EXPORT` when `GetModifiedCount()` is zero, and metadata arguments apply to that
export only, not to the project's ModSettings.

**`import_project` overwrites by default.** Vanilla entries that were never modified are
protected and skipped unless `allow_overwrite`; read `skipped_protected` rather than assuming
the whole file was applied.

**`copy_linked: true` is best effort and every way it can fail is silent.** `DuplicateEbx` fills `shared_linked_assets` only when `copy_linked` is false (`McpEditHandlers.cs` lines 313 to 321), while the actual copying runs through `TryDuplicateLinkedTextureRes`, which returns quietly when the root object has no `Resource` property, when the old RES is missing, when a RES with the destination name already exists, and on any exception, because both the inner and the outer body are empty `catch` blocks (lines 1630 to 1685). For a mesh, a sound, a non-texture asset, or any failure along the way, the duplicate keeps pointing at the source RES or chunk while the reply says `copy_linked: true` with `shared_linked_assets: []` and no `linked_assets_shared` key. The tell is that empty list next to a non-empty `linked_assets` in `get_ebx_entry` for the source: when the source has linked resources and `shared_linked_assets` is empty with no warning, assume the copy shares them.

**A failed `import_ebx_bin` can still have imported.** `def.Import` has already run at line 187 when the follow-up reload is checked at lines 194 to 197, and a null reload returns `IMPORT_FAILED` without reverting; the revert path exists only in the `GUID_MISMATCH` branch (lines 214 to 225). Any exception thrown after the import, for example while computing the after-GUID set, also lands in the outer `catch` and returns `IMPORT_FAILED`. After an `IMPORT_FAILED` from this tool, treat the asset as possibly imported and check `get_ebx_entry` `is_modified` and `diff_ebx` before retrying, because a retry against an already imported asset compounds the change.

**`raw_chunk: true` writes whatever bytes you hand it into the texture chunk.** The only checks are that the existing texture is `TT_2d` and, if the file starts with the DDS magic, whether a DX10 header makes the header size 148 instead of 128 (lines 1770 to 1787). A non-DDS file, a PNG or a JPEG, sets `headerSize = 0` and the whole file is copied into the chunk as pixel data, returning `success: true` with `metadata_changed: false` and a warning that only mentions metadata. Check `bytes` against the file length and `metadata_changed`: the tool never compares the written size with the existing chunk or the expected mip chain, so the reply looks exactly like a correct import.

**A `guid` that disagrees with `name` silently wins, and RES resolves in the opposite order.** `ResolveEbxEntry` tries the GUID first and returns it if it resolves, falling back to the name with no warning when the GUID is malformed or names nothing (lines 806 to 819), while `ResolveRes` tries the name first and only then `res_rid` (lines 1235 to 1255). Every handler that takes both, including `duplicate_ebx`, `import_ebx_bin`, `set_ebx_property` and `revert_asset`, therefore targets one of the two without saying which won. The reply always names the asset it actually resolved, so compare reply `name` or `asset.name` with what you sent; a mismatch means a stale GUID from an earlier session sent the edit to the wrong asset.

**`import_res` verifies payload bytes and nothing else, and its failure message overstates the recovery.** `BuildResMeta` copies at most 16 bytes of the entry's `ResMeta` (lines 623 to 629) and import writes exactly those 16 bytes back, so a longer metadata blob is silently truncated by one export and import round trip. With `format: "raw"` the call passes a null meta to `ModifyRes` yet reports `meta_updated: false` with a warning that `ResMeta` was left unchanged, and nothing reads the metadata back to prove it. When the payload comparison fails, the previous data was restored message is emitted even when `previousData` could not be read and no restore happened (lines 583 to 601), so the success of the rollback is not a field you can rely on here.

**A JSON object written into a struct silently drops keys it does not recognise.** `ConvertTo` treats any `JObject` aimed at a value type as a bag of fields and wraps each individual `SetMember` call in `catch { }` (lines 1490 to 1499), so `{"x": 1, "z": 3}` written to a two-field vector keeps `x`, drops `z`, leaves the second field at its default, and returns `success: true`. The tool does echo the resolved value afterwards, so compare its keys with the keys you sent; an echoed struct that is missing a key means a typo, not a type that lacked the field.

**`changed_class_guids` is empty when the before-scan failed, not when nothing changed.** `ExportedClassGuids` swallows a load failure and returns an empty set (lines 1186 to 1207), so if the asset could not be read before the import the comparison at line 242 finds no removed GUIDs, and `affected_assets` is never computed. The reply then looks identical to a clean import with no renumbering, which is exactly the case the caller was told to check. Look at `changed_class_guid_count` together with the import having actually succeeded, and re-read `get_ebx_guids` separately when the asset was in a doubtful state before the call.

**`create_bundle` invents the super bundle when the name does not exist.** `ResolveSuperBundleId` returning -1 is not an error: the handler calls `AddSuperBundle(sbName)` and resolves again (lines 911 to 916), so a typo in `super_bundle` silently creates a new empty super bundle instead of failing. The reply then reports `super_matches: true` and the requested name, because the comparison is against the newly created entry. Cross-check the typed name against `list_super_bundles`, and treat any super bundle whose only member is the bundle you just made as the result of a typo.

**`duplicate_res` accepts an unparsable `res_type` and keeps the source type.** Line 784 runs `Enum.TryParse` and leaves `resType` at the source value when parsing fails, so a misspelled or nonexistent resource type produces a normal duplicate, `success: true`, and no warning. The reply's `asset.res_type` is the real value, which will differ from what you passed; compare those two fields rather than assuming the override applied.

**`add_to_bundle` suppresses its own warning precisely when the asset carries linked resources.** The warning is added only when `IsResourceBackedType(entry.Type)` is true and `entry.LinkedAssets` is empty (lines 829 to 830), so a texture or mesh that already has its RES and chunk linked, which is the case where a raw membership add leaves those resources unshipped, produces `warnings: []`. An empty warning list therefore proves nothing; run `check_asset_ready` or `bundle_closure_check` instead, and treat `warnings: []` on a resource-backed asset as the expected output, not as confirmation.

**`get_ebx_property` prints a type name where a compound value should be.** In `SerializeValue` any member that is not primitive, string, enum, GUID, CString or PointerRef, and is not a value type from a `FrostySdk` namespace, is returned as `v.GetType().Name` (lines 1615 to 1620). For a field that holds an object or an array of objects, the reply's `value` contains a string like a struct name, with no flag saying it was substituted. Read `type` alongside `value`, and if the type is a class or a list while the value is a plain string, treat it as an unresolved reference rather than a value.

**`create_bundle` turns an unknown `type` into `BlueprintBundle` without saying so.** `Enum.TryParse` failure falls through to `bundleType = BundleType.BlueprintBundle` (lines 903 to 905), and `requested_type` in the reply echoes that fallback rather than the string you sent, so `type` and `requested_type` always agree. The bundle is created with the wrong type, which matters because bundle type drives the loader. Compare the reply `type` against the literal you passed, not against `requested_type`, and prefer the exact enum spelling.

**`add_to_bundle` and `remove_from_bundle` echo your lowercased string as `bundle`, not the canonical name.** Both handlers lowercase the input at line 813 and line 850 before resolving and then return the lowercased string in the `bundle` field, so a bundle indexed as `Mod/Foo` comes back as `mod/foo`. Because the input has already been lowercased, the exact-case fast path in `ResolveBundleId` (lines 772 to 791) can never hit, and when two bundles differ only in case the loop returns the first case-insensitive match, which may not be the one you meant. Use `list_bundles` to get the real spelling and check `bundle_id` rather than the echoed name.

**`max_chars: 0` means 60000, not zero characters.** Lines 221 to 223 replace any non-positive `max_chars` with `DefaultXmlMaxChars`, unlike `limit: 0`, which genuinely returns no rows. A caller who passes 0 expecting an empty body, or expecting no cap, gets a 60000-character page with `truncated: true` and a `next_offset`, and there is no way to ask for the whole document in one call. Page with `next_offset` until `truncated` is false, and never infer no limit from a zero.

**`summary: true` caps `elements` at 40 type names and ignores the other text parameters.** The summary path counts object types, keeps only the top 40 by count (line 268), and returns no flag saying more types existed, while `filter`, `char_offset` and `max_chars` are never consulted on that branch (lines 257 to 282). A caller who asks for a summary plus a filter gets a whole-asset histogram that looks like it answered the filtered question. Check `object_count` against the sum of the `elements` values: any difference means types were dropped.

**`export_mesh` echoes your `format` but only writes OBJ for the exact string `obj`.** The exporter receives `format == "obj" ? "obj" : "binary"` (line 2113), so `fbx`, `ascii`, `FBX` and anything else all produce a binary FBX, while the reply repeats the string you typed in `format` (line 1059). The handler also never checks that a file appeared at `path`, so the reply proves only that the reflection call returned. Check the file on disk and read the caller's intent, not the echoed `format`, when the exported file matters.

**`remove_ebx_item` silently retargets to the root when the host GUID does not resolve.** In the non-`$` branch the tool parses `class_guid` and calls `FindObjectByClassGuid`, but the assignment is guarded by `if (found != null) host = found;`, so a miss leaves `host` at `asset.RootObject`; the `component_type` path behaves the same way when `ResolveComponent` returns null. A stale GUID plus `path: "Objects[3]"` therefore deletes element 3 from the root object and still returns `success: true`. The only clue is `host_type` in the reply, which will name the root class instead of the component you meant.

**`create_ebx_class` with the default `reuse_guid: true` leaves the replaced object in the asset under the same class GUID.** When the target field already holds an internal pointer, `guid = ((dynamic)existingPr.Internal).GetInstanceGuid()` copies that instance GUID onto the new object, and nothing in the method ever calls `asset.RemoveObject` on the old one, which remains in `asset.Objects`. Two objects then answer to one `class_guid`, so later GUID addressing is ambiguous and `remove_connections_by_guid`, which sweeps every pointer carrying that GUID, hits both. Pass `reuse_guid: false` unless the identity swap is what you want.

**Reference cleanup after `remove_component` and `remove_ebx_item` reads properties only, never fields, and swallows every failure.** `RemoveReferencesToObject` iterates `host.GetType().GetProperties(...)` with no `GetFields` pass, even though `GetListMember` elsewhere in the same file exists because some Frostbite classes expose arrays as fields. Each `SetValue`, `RemoveAt` and `GetValue` in that loop sits inside a bare `catch { }`. A pointer held in a field therefore survives the removal, the call returns `success: true`, and `cleared_paths` simply does not mention it.

**`remove_component` disables its ambiguity guard whenever `index` is present, and a bad index falls through to another selector.** The guard is `if (p["index"] == null && !IsClassGuidUnique(...))`. `ResolveComponent` returns early only for an in-range index; an out-of-range or stale index drops through to `class_guid` and then to `component_type` plus `type_index`, so the call removes the first object of that type instead of failing. Note also that this type match is exact (`obj.GetType().Name.Equals`), unlike `list_components`, which also accepts subclasses.

**`add_property_connection` writes flags 0, which is realm Invalid, and no validator reports it.** `ResolvePropertyConnectionFlags(p)` starts from `existing = 0` and, with no `flags`, `realm`, `prop_type` or `source_cant_be_static`, returns 0 unchanged. `decode_connection_flags` names that value realm Invalid, yet `CheckFlagBits` only flags realms outside 0 through 5, so 0 passes and `validate_connections` stays clean. The connection is added with `success: true` even though neither client nor server should see the property; pass `realm` explicitly and read `realm` back from `list_connections` with `compact: false`.

**Numeric or misspelled `realm` and `prop_type` are applied without masking and silently default.** `ParseRealm` and `ParsePropType` accept any integer through `int.TryParse`, and `ResolvePropertyConnectionFlags` then does `(flags & ~0x7u) | realm` and `(flags & ~0x30u) | (propType << 4)`. A `realm` of 9 also sets bit 3, which is `source_cant_be_static`, so the connection reads back as ClientAndServer with the static rule flipped; a `prop_type` of 5 bleeds into bit 6 the same way. Any unrecognized name, a typo such as ClientSever for instance, falls through to `return 1` (ClientAndServer) with no warning.

**`validate_connections` only inspects property connections deeply.** Empty-field, flag-bit, duplicate and interface checks all live inside the `PropertyConnections` loop; the event and link loops call `CheckPointer` and nothing else, so an event connection with an empty `SourceEvent.Name` or `TargetEvent.Name`, or a link connection with empty field names, reports a clean asset. `CheckPointer` only tests `pr.Type == PointerRefType.Null`, so an internal pointer whose `Internal` is null is not counted either. Read `issue_count` together with `property_count`, `event_count` and `link_count`, and do not treat zero as full coverage.

**`advanced_add_to_bundle` and `advanced_remove_from_bundle` return `success: true` whatever the plugin returned.** The add path stores the plugin result in `added` but never branches on it, so `added: false` arrives alongside `success: true`; the remove path discards the return value of `RemoveAssetFromBundle` entirely and exposes no per-target result. With `allow_fallback: true` and the plugin absent, the add degrades to `AddToBundleSmart`, which only calls `AddToBundleExportable` and `LinkAsset`, so requested `netreg`, `mvdb` and `unlock_table` work is silently dropped. Read `added`, and treat an absent `netreg` key as not done.

**`completely_add_to_bundle` reports validation results where you expect confirmation.** The `netreg` and `mvdb` fields are the outcomes of `AssetAddNetworkValid` and `AssetAddMeshVariationValid`, not of `AddAssetToNetRegs` and `AddAssetToMvdBs`, which run only when those checks pass and whose own results are dropped. `AddAssetToTables` is wrapped in `try { } catch { }` in both branches, so an unlock-table failure leaves no trace, and dependencies whose `Type` is `ShaderGraph` are skipped before they are recorded in `touched`. `touched` is also capped by `Take(200)` while `touched_count` stays true.

**The `allow_fallback` path of `advanced_remove_from_bundle` can never be committed.** The fallback calls `McpMoreHandlers.RemoveFromBundleSmart`, which registers its preview with `McpCommit.Require("remove_from_bundle_smart", ...)`, while `BeginCall` recorded the incoming method name `advanced_remove_from_bundle`. On the next call `BeginCall` sees a different tool name and discards the pending plan, so repeating with `commit: true` always ends in `COMMIT_REQUIRED`. Re-issue the request as `remove_from_bundle_smart` with identical parameters instead.

**`set_bundle_super` creates the super bundle it cannot find.** When `GetSuperBundleId(superName)` returns negative the tool calls `App.AssetManager.AddSuperBundle(superName)` and proceeds, so a typo in `super_bundle` yields `success: true`, a new empty super bundle in the project, and `super_bundle` echoing the misspelled name. Nothing in the reply distinguishes repointing to an existing super bundle from creating a fresh one. Run `list_super_bundles` first, and read `added_bundle` and `game_effect` before assuming the change reaches a `.fbmod`.

**`set_component_flags` reports success when it changed nothing.** `ApplyObjectFlags` starts from the current word because `preserveGuidBits` is true and applies a key only when `p[key] != null`, so a misspelled flag name is ignored. An unrecognized `flag_preset`, meaning anything other than `client`, `server`, `client_and_server` or `both`, falls into the toggle branch with no keys present and rewrites the same value. `realm: ""` is swallowed by `SetEnumProperty`, which returns immediately on whitespace. Compare the `flags` value in the reply against the value before the call.

**A failed section in `list_connections` looks exactly like an asset with no connections.** `AppendPropertyConnections`, `AppendEventConnections` and `AppendLinkConnections` each wrap their loop in a bare `catch`, and the reply has no `scan_errors` equivalent, so `success: true` with `total: 0` covers both nothing exists and the loop threw. With the default `compact: true` the serialiser also omits `flags_hex` and `source_cant_be_static`, hiding the bit 3 state. The `filter` argument is a case-insensitive substring over the whole serialised row, so `filter: "client"` matches realm text and `filter: "property"` matches the `kind` field.

**The array a new component lands in is not always the one you named.** In `create_component`, supplying `at_index` calls `TryInsertPointerToArray` directly, which has no fallback to `Components` or `Children` and clamps an out-of-range index down to `list.Count`; without `at_index` the same call walks `Objects`, `Components`, `Children` and reports whichever accepted it in `added_to_array`. The `index` inside `component` comes from `IndexOfObject` and is the position in `asset.Objects`, not the array position you asked for. `duplicate_component` never reads `array_host_guid` and always inserts on `asset.RootObject`, and it copies none of the source connections.

**`type` and `modified_only` in `list_bundle_contents` only filter EBX rows.** Both conditions sit inside the `EnumerateEbx` loop, after the RES and chunk loops have already appended their rows, so with `kind: "all"` a `type` filter is applied to part of the result only. The reply echoes `kind` and `total` without saying the filter was partly ignored. The three kinds are also concatenated in a fixed order (res, then chunk, then ebx), so paging with `offset` and `limit` crosses sections.

**Interface helpers create the descriptor as a side effect and default to the wrong event list.** `add_interface_field` and `add_interface_event` call `TryGetInterfaceInternal(..., createIfMissing: true)`, which builds a new `InterfaceDescriptorData`, assigns it a fresh class GUID, adds it to `asset.Objects` and writes it into the root `Interface` pointer before saving; the reply carries no `created` flag, unlike `ensure_interface`. `add_interface_event` defaults `kind` to `input`, so omitting it appends to `InputEvents` rather than `OutputEvents`. `list_interface` answers `success: true` with `has_interface: false` and empty arrays when no descriptor exists, so check that flag before reading empty `fields` as "none defined".

**`import_sound_wav` overwrites a chunk that other variations may share.** The tool takes the chunk from `root.Chunks[variation.ChunkIndex]` and calls `ModifyChunk` on it, then rewrites only the selected variation and its first segment, so every other variation that points at the same chunk index now decodes the new audio through its old offsets. It also has no `variation_index` parameter: it reads `variation` or `index`, so a caller copying the `import_sound` spelling silently edits variation 0. The selected segment's `SegmentLength` is never updated, so even the targeted variation keeps a stale duration. Check the reply's `variation` value and compare chunk indexes with `get_sound_info` before trusting the result.

**`import_sound_wav` always replaces the payload with Pcm16Big.** `EncodePcm16Big(samples, channels, sampleRate)` is the only encoder on this path, and the reply has no `codec` field, only a free-text `note` that names Pcm16Big. Importing into an asset that was ealayer3, xas, or opus therefore changes its codec while reporting success, and the variation is forced to one segment with `SeekTableOffset` set to `4294967295`. Read the existing sample header first, and treat `bytes` and `sample_rate` in the reply as the only structured evidence of what was written.

**An explicit `import_sound` codec outside the known table becomes multistreamopus.** `CodecName` maps 2 through 7, 9, and 11 through 15, and returns `"multistreamopus"` for everything else, including 1, 8, 10, or any larger number. The re-encode succeeds and the reply reports `"codec": "multistreamopus"`, which looks like a chosen codec rather than a fallback. Codec 0 cannot be forced either, because 0 is the sentinel that makes the handler read the nibble from the existing sample header. Compare the reply `codec` with the value you sent; a mismatch is the only warning you get.

**`export_sound_wav` trusts raw segment offsets and lengths.** It uses `segment.SamplesOffset` without masking, while `import_sound` masks the same field with `& ~3u` and writes flag bits into its low two bits, so audio written by the importer is read one to three bytes late. When the variation has more than one segment, or the next segment's offset is not greater, it reads from the offset to the end of the chunk, which pulls later segments or other variations into the WAV. Check `variation_count`, `segment_count`, and whether the offset is a multiple of four before treating the export as one clip.

**`import_atlas_texture` validates the pixel format and nothing else.** The only checks are DXT5 or BC3, a 16384 pixel limit, and a readable header; width, height, mip count, and the number of bytes after the header are never compared with the existing atlas or with a valid mip chain. It then writes the remaining bytes into the existing chunk and calls `SetData` with the DDS dimensions, so a DDS cut from the wrong mip level is accepted and the reply reports the new `width`, `height`, and `bytes` as if they were correct. Compare those fields with `get_texture_info` taken before the import, and check that `bytes` equals the expected sum of mip sizes.

**`import_texture` with `raw_chunk=true` writes bytes with no validation at all.** The path only rejects non-2D textures; it strips a DDS header when the magic is present and otherwise sets `headerSize` to 0, so a file that is not really DDS is written whole as pixel data. Dimensions, mip count, and total size are never checked, and the reply carries `"metadata_changed": false` plus a warning instead of failing. The owning texture keeps its old `width`, `height`, `pixel_format`, and `mip_count`, so anything that reads the chunk using those values sees mismatched data.

**`import_chunk` accepts any payload and reports success from a boolean.** After `File.ReadAllBytes` it calls `ModifyChunk(entry.Id, data)` and only checks the returned `bool`; it never compares the length with the chunk entry's previous size or with the consuming asset's declared dimensions, segments, or LODs. A wrong file, a truncated file, or a chunk copied from a different asset is accepted and the reply shows the new serialized chunk entry. Re-read `asset.size` and the owning texture or mesh metadata before assuming the replacement is coherent.

**`export_mesh` has only two output formats and echoes what you asked for.** The code treats `format == "obj"` as OBJ and everything else as `"binary"`, so `format: "ascii"` or `"fbx_ascii"` silently produces a binary FBX while the reply repeats the requested `format` string. `fbx_version` is passed through after the `FBX_` prefix is stripped, and the output file is never reopened to confirm that it exists or is nonempty. Check the file's first bytes for the binary FBX magic rather than the reply.

**`import_lua` forces the entrypoint name away from the function you wrote.** A source that begins with `function Name(` is treated as already wrapped, compiled successfully, and then `EntrypointName` is overwritten with `CompiledLua_{guid}` regardless of `Name`; the reply shows `"wrapped": false` with the forced `entrypoint`. The bytecode length is nonzero, so the empty-bytecode check passes, but the resource now advertises an entrypoint that does not match the only function the source defines. Cross-check the function name in the source against the reply `entrypoint`, or pass a bare body so the wrapper creates the matching name.

**`create_lua_runner_component` can report success with the component unattached.** It calls `hostAsset.AddObject(newObject)` and then tries `Objects` and, only if that fails, `Components`; when both `TryAddPointer` calls return false the handler still updates the asset and returns `"success": true` with `"added_to_array": null`. The object exists in the asset's object table but is not reachable from the root, so the script never runs. Check `added_to_array` and confirm the component appears under the root array with `list_components` before proceeding.

**`import_svg` ignores the coordinate system that matters.** It recurses into `<g>` but never reads a `transform` attribute, and from `viewBox` it uses only `values[2]` and `values[3]`, dropping a nonzero min-x or min-y. Neither case emits a warning: `warnings` receives only unsupported element names and the `A` or `a` arc command. A file that renders correctly in a browser can import with shifted or unrotated shapes while `warning_count` stays at 0.

**SVG colors do not survive a round trip, and the export is not in standard SVG color order.** `export_svg` writes `"#" + sc.ToString("x8")` from an integer packed as `(a << 24) | (b << 16) | (g << 8) | r`, producing eight hex digits in AABBGGRR order, while `import_svg` parses eight digits as RRGGBBAA and repacks them the same way, so re-importing permutes the channels. Separately, `SvgColor` returns 0 for any name that does not start with `#`, and the caller treats a zero color as no paint. Round-trip a known color and inspect both the file and the re-imported shape flags.

**`import_mesh` reports success without checking that anything changed.** The handler calls `ImportFBX` and immediately returns `success`, `name`, `path`, and the serialized EBX entry; it never re-reads the mesh to confirm LOD count, section count, or chunk IDs. The `skeleton` argument is applied only if `FrostyMeshImportSettings` was found and the `SkeletonAsset` member exists, and the set is silent when either is missing. Take `get_mesh_info` before and after and compare `lod_count` and each `chunk_id`.

**The `frosty_res` container can silently truncate a resource's metadata.** `BuildResMeta` allocates 16 bytes and copies at most 16 from `entry.ResMeta`, and `import_res` always takes exactly the first 16 bytes as metadata; the round-trip verification compares only payload bytes. If the original `ResMeta` is longer than 16 bytes, the export loses the tail and the import writes the shortened metadata while returning success. The reply always shows `"meta_size": 16`, and `SerializeResEntry` does not expose the metadata, so no field in the response reveals the loss.

**`duplicate_res` ignores a bad `res_type` instead of rejecting it.** `Enum.TryParse(typeOverride, true, out ResourceType parsed)` updates the type only on success, so a typo or a name from another game leaves the source type in place and the call still returns success. The reply's `source` and `asset` entries both include `res_type`, but nothing marks it as a fallback. Compare `asset.res_type` with what you sent, and note that `new_name` is lowercased before the duplicate is created.

**`export_ies` does not produce an IES file.** It reads the resource's sample grid and writes a DDS header with `dxgiFormat = R16_Float`, one mip, and `size` by `size` pixels, so the output is a binary texture-like container; the path check even asks for `.dds`. There is no `import_ies` in the bridge, so the export cannot be reversed or edited as text. The reply's `size` is the grid dimension from the resource, and the file length is not reported at all.

**`import_project` with `overwrite_modified: false` protects only RES entries.** Before the payload pass, the dummy EBX pass sets `entry.IsDirty = true` on every EBX name in the file, so the protection test `!allowOverwrite && !entry.IsDirty && entry.IsModified` can never fire for EBX and those assets are overwritten regardless of the flag. Chunks have no protection test at all, so an existing chunk is given a fresh `ModifiedEntry` too. Only the RES pass runs the test, which means `skipped_protected` counts RES entries alone and `success: true` with `skipped_protected: 0` is exactly what an EBX overwrite looks like. Read `ebx_merged` and `chunks_merged` before trusting the flag.

**`link_asset` and `unlink_asset` report a change that `save_project` never writes.** Both handlers only set `entry.IsDirty = true`; they create no `ModifiedEntry`, `AddedBundles` or `RemBundles`, and `AssetEntry.IsDirectlyModified` is defined as exactly those three. `FrostyProject.Save` iterates `EnumerateEbx(modifiedOnly: true)`, which filters on `IsModified`, so a link or unlink against otherwise vanilla assets is skipped and the saved `.fbproject` keeps the old link list. The reply still says `success: true` with `added: true`, and the next `save_project` reply shows `is_dirty: true`. Link to an asset that is already modified if the link must survive a reload.

**`discard: true` silently overrides `save_first: true`.** In both `open_project` and `new_project` the dirty branch starts with `if (project.IsDirty && discard) saveFirst = false;`, so a caller that passes both flags to be safe drops the edits instead of saving them. Nothing in the reply mentions that the save was skipped, because the reply only describes the newly opened project. Pass exactly one of the two, and only pass `discard` after confirming with `get_project_info` that `is_dirty` is work you are willing to lose.

**A failed `open_project` can destroy the current project while returning `LOAD_FAILED`.** The editor resets the asset manager before parsing the new file, and the handler selects `ASSETS_LOST` only when `assets_after < assets_before`; if the reset left an equal or larger count, the same destruction is reported as `LOAD_FAILED`. Branching on `ASSETS_LOST` alone to decide whether the session was lost is therefore unsafe. Treat any failure from `open_project` as a possible loss and check `assets_before`, `assets_after` and whether `get_project_info` still names the old file; on success compare the returned `filename` with the path you asked for, because success is decided only by whether the window's `Project` object changed identity.

**Importing the same project twice duplicates rows instead of merging them.** The EBX, RES and chunk passes all call `entry.LinkedAssets.AddRange(...)` and `entry.AddedBundles.AddRange(...)` with no membership check, so a second import appends the same linked assets and bundle ids again. The counters cannot show this because `ebx_merged`, `res_merged` and `chunks_merged` count assets touched, not rows appended, and `success` stays true. Revert with `revert_all` or start from a fresh session before re-importing.

**`revert_all` can return `success: true` with `dirty_count` still above zero.** It calls `App.AssetManager.Reset()`, and `Reset` delegates to `RevertAsset`, which returns immediately when `!entry.IsModified`; `IsDirty` is a separate bit that handlers set directly, as `link_asset` does, so a dirty but not modified entry survives. The commit reply prints `dirty_count` and `is_dirty` after the reset, and `FrostyProject.IsDirty` is computed from `GetDirtyCount()`, so the project stays dirty and the next `open_project` or `new_project` fails with `DIRTY_PROJECT`. Read `dirty_count` in the revert reply rather than the `success` flag.

**The commit gate compares raw JSON, so restating a default breaks the commit.** `Require` normalizes by deep cloning the parameters and removing only the `commit` key, then requires `JToken.DeepEquals` against the stored tree. Adding a parameter that the tool would have defaulted anyway, such as sending `overwrite_modified: false` on the commit when the preview omitted it, fails the comparison just as a real change would, and a number written as `100.0` instead of `100` also fails. The result is `COMMIT_REQUIRED`, which reads the same as having no preview at all; the correct retry is to send the preview again and then repeat it with identical text plus `commit: true`, not to patch the failed commit call.

**`export_mod` neither saves the project nor checks that it is dirty.** The handler gates on `App.AssetManager.GetModifiedCount() == 0` and then calls `project.WriteToMod`, which reads the in-memory managers; `project.Save` is never called. A `.fbmod` can therefore be produced from a session whose `.fbproject` on disk is many edits old, and nothing in the export reply compares the two. Run `save_project` first and use its `is_dirty` field as confirmation.

**The `import_project` preview tells you nothing about the merge.** Its plan contains only `path` and `overwrite_modified`, and the file is not opened until the execute closure calls `Merge`, so `LEGACY_PROJECT` and `UNSUPPORTED_VERSION` appear only after the commit call has already consumed the preview. `McpCommit` also hardcodes `would_change: true` for every preview, so that flag is not evidence that anything will change. There is no way to learn `ebx_merged` or `skipped` without merging, so take a `save_project_copy` snapshot first if the merge may need to be undone.

**`revert_all` leaves the mod metadata in place.** The commit calls `App.AssetManager.Reset()` and `project.GetModSettings()?.ClearDirtyFlag()` only; title, author, category, version, description, link, icon and screenshots are untouched, and `FrostyProject.Save` writes all of them from ModSettings. After a revert, a later save or export still carries the metadata set earlier with `set_mod_settings`. Check `get_project_info` `mod_settings` after the revert if the previous metadata was supposed to come back.

**Per-asset merge failures return `success: false` without `partial_changes_possible`.** That flag is set only in the catch that wraps the whole `Merge` call; an exception inside one asset loop is caught locally and pushed into `failures`, and the final reply returns `success = errors.Count == 0` with no partial flag. A caller that treats a missing `partial_changes_possible` as all or nothing will misjudge a merge that applied everything except the failing assets. Read `ebx_merged`, `res_merged`, `chunks_merged` and `failed_count` together.

**Import drops linked assets and bundle memberships without a counter.** `FrostyProject.LoadLinkedAssets` adds a linked entry only when its name resolves in the current project, and `ReadBundleList` keeps a bundle name only when `GetBundleId` is not -1, so links and memberships to things this profile does not have vanish silently. The bundle dummy pass also reuses any existing bundle with the same name without comparing `BundleType` or super bundle, unlike `create_bundle`, so `bundles_existing` can hide a name collision with the wrong type. Verify with `list_linked_assets` and `get_bundle` after a merge instead of reading the counters.

**`selected_category` rewrites `category`.** When `selected_category` is 1 to 8 and no `category` text is supplied, `set_mod_settings` assigns `ModCategories[selectedCategory]` into `settings.Category`, replacing whatever custom string was there. Because `selected_category` persists to neither file and `category` does, the only lasting effect of that call can be the category rewrite. Read `settings.category` in the reply before saving.

**`set_mod_settings` can half apply and cannot clear images.** All validation runs before any assignment, but the assignments themselves are sequential inside one try, so a `SET_FAILED` from a later setter leaves the earlier fields changed. Paths beyond index 3 in `screenshot_paths` are dropped by the `i < 4` loop with no warning, an empty string keeps the old screenshot instead of removing it, and no argument clears an icon. Confirm with the returned `settings` object and its `screenshots` count.

**`get_log` only ever returns the tail.** The handler takes `text.Substring(totalLength - maxChars)` and offers no offset, so with `truncated: true` the earlier lines, which often hold the cause, cannot be retrieved in that call or later once the log has grown. A `max_chars` below 1 is silently replaced by 20000 instead of meaning zero. Capture `total_length` and call again promptly if you need the window that contains the failure.

**An oversized request gets no error at all.** `McpBridgeServer` reads a 4 byte length and breaks the loop when `length > MaxRequestBytes` (32 MB) without writing a response, and the client's finally block calls `McpCommit.ClearPending`. A large `import_ebx_bin` or `refs_json` therefore surfaces as a transport failure or a timeout, and any preview taken earlier is gone. Keep bulk payloads under the cap and re-preview after any pipe error.

**A pointer search driven by an asset name compares entry GUIDs to file GUIDs.** When you pass `name` instead of `file_guid`, `find_pointer_references` takes `target.Guid` from the asset entry and compares it against every `PointerRef.External.FileGuid`. If the entry GUID and the file GUID of that asset have diverged, the walk matches nothing and still returns `success: true`, `total: 0`, `returned: 0`, `scan_truncated: false` and `load_failures: 0`, which is indistinguishable from "nothing points at this asset". Cross-check with `get_internal_pointer_ref`, which reports `file_guid` and `asset_file_guid` separately plus `guid_invariant_ok`. When that flag is false, rerun the search with `file_guid` set to `asset_file_guid`.

**A clean reference validation result is weaker than it looks.** Names in `names` that do not resolve are skipped silently, so a call where every name is a typo returns `success: true`, `scanned_assets: 0`, `issue_count: 0` and `truncated: false`. Even when it does scan, an external pointer is only checked with `GetEbxEntry(fileGuid) == null`; the target class GUID inside that file is never looked up, so a component that no longer exists in the target file still passes. Pointers whose file GUID is empty are skipped outright, and the recursive walk stops at depth 4 with no counter. Read `scanned_assets`, `unscanned_assets` and the resolved names before quoting `issue_count: 0`.

**A clipboard paste reports success even when the object never landed in an array.** After `targetAsset.Update()` the handler returns `success: true` whether or not `TryAddPointer` succeeded for `Objects` and for the `Components` fallback; the only tell is `added_to_array: null`. The pasted object was already registered with `targetAsset.AddObject(copy)`, so the asset keeps an extra object that no array points at. Treat a null `added_to_array` as a failed paste and revert the asset.

**A smart bundle add reports success when it added only the membership.** Each resource branch resolves the RES or chunk and, when the lookup returns null, adds nothing and records nothing in `errors`; with the EBX membership added, the `NOT_MEMBER` guard does not fire, so the reply is `success: true` with `linked_count: 0`. This reply has no `warnings` key at all: sub-step failures arrive as `errors` and `error_count` with `success: false` and `error_code: "PARTIAL"`. Check `linked` against what the asset type needs (a texture needs its RES and an imported chunk) rather than trusting `success`.

**A readiness check judges linked assets by direct bundle membership only.** Dependencies go through the bundle manager when its parent and previous bundle table is loaded, but the `LinkedAssets` loop always calls `EnumerateBundles().Contains(bid)`, so a linked RES that loads from a parent bundle is reported under `linked_assets_outside_bundle`. A dependency test that throws falls back to direct membership with no per-item flag, so the dependency half can be partly guessed while the linked half is never asked. `dependency_check` says which mode the dependency half used, and `note` warns that linked assets only appear once the editor has loaded them, so `ready: true` can mean "nothing was visible to check".

**A network registry add drops bad input instead of refusing it.** An `asset` name that does not resolve leaves the pointer list empty and the call returns the generic "Provide refs_json / file_guid+class_guid / asset name(+class_guid)" `INVALID_PARAMS`, which points at a missing argument rather than a bad name. Inside `refs_json`, a malformed array is swallowed entirely and any entry missing either GUID is skipped one at a time, so `added_count` is the only evidence that part of the list vanished. The bare `file_guid` plus `class_guid` shape also skips both the existence check and the `GUID_INCONSISTENT` check that the `asset` shape performs. Prefer the `asset` shape, where a mismatch is refused.

**A loadability answer can be a direct membership guess with no degraded flag.** The `degraded: true` path is reserved for a loaded plugin whose `m_bundleAndParents` table is empty; when the plugin is missing or `CanBeLoadedByBundle` is not found by reflection, the tool falls through to the dependency loop and answers `can_be_loaded: outside.Count == 0` with `engine: "direct_dependencies_only"`. A dependency satisfied by a parent or previous bundle then reads as `false`, and `true` only means every direct dependency sits in this bundle. Always read `engine` before quoting the boolean.

**The array insert tool is two phase although the documented two phase list omits it.** `insert_ebx_item` calls `McpCommit.Require`, so the first call returns `success: false` with `error_code: "PREVIEW_ONLY"` and a plan, and nothing is inserted; the insert and the `ITEM_DROPPED` readback only happen on the repeat with `commit: true`. The plan echoes your raw `index` (default -1) while the result reports the index actually used. An out of range `index` is not an error: anything below 0 or above `list.Count` inserts at the end.

**The bulk text export has an uncounted file limit and counts nothing.** `limit` defaults to 0 (no cap), but any positive value stops the collection loop after that many entries and the reply carries no `total` and no `truncated`, only `exported_count`. `success` is merely `failures.Count == 0`, so a call that exported nothing (a `query` that matches no name, or every target already on disk with the default `overwrite: false`) returns `success: true` with `exported_count: 0` and a large `skipped_existing`. Compare `exported_count` with the set you asked for, and pass `overwrite: true` on re-runs.

**A registry gap can be zero for an asset that was never found.** An unresolvable name becomes a row with `"error": "not found"` inside `assets` while the envelope stays `success: true`, `asset_count` still counts the name, and `missing_total` ignores it. `missing_total` counts object and registry pairs, so one unregistered object checked against eight registries contributes eight. The match key is built from `entry.Guid` paired with the object GUID, so an asset whose entry GUID and file GUID diverged looks unregistered in every registry.

**The type detail tool ignores one inheritance parameter unless another is cleared.** `declared_only` defaults to true and `include_inherited` to false, and the inherited walk only runs in the `includeInherited && !declaredOnly` branch, so passing `include_inherited: true` alone silently returns declared fields only. The reply has `field_count` and `fields` with no marker for what was left out. Properties whose names start with an underscore are also filtered from `fields`, so a member can be absent rather than reported as zero. To see inherited fields, pass both `declared_only: false` and `include_inherited: true`.

**The type search answers a bad filter with an empty list.** An unknown `base_type` raises nothing because `TypeLibrary.GetTypes` simply matches no type, so `total` comes back 0 with `success: true`; an unknown `kind` behaves the same way. The reply never echoes `query`, `base_type`, `kind` or `namespace`, so a typo is indistinguishable from "no such types". The namespace filter also requires a non-empty EBX namespace, which means the `(none)` group that `list_type_namespaces` reports can never be found through `namespace`. Verify a filter by dropping it and comparing `total`.

**The struct creator discards properties it cannot place.** In the `properties` loop, a name that matches no writable property is skipped, so a typo yields a struct full of defaults and `success: true`. A `class_guid` that fails to parse or matches no object leaves the host at the asset root, and the reply still reports `assigned_to` and `path` as though the component had been used. Read the field back with `get_ebx_field` to see which object really changed. A call with no `name` and `path` writes nothing and says so only in `note`.

**A digit only or hex prefixed string is read as a number, not a name.** `get_localized_string`, `set_localized_string` and `revert_localized_string` treat any `id` that `uint.TryParse` accepts, or that starts with `0x`, as a hash; the reply then carries `hash` and no `id`, and a set writes to that hash. Pass such identifiers through `hash` deliberately, or pick a name that cannot parse as a number. `hash_string` has the mirror rule: a `text` starting with `0x` is parsed as a hex value instead of being hashed, and `fnv1` is a signed int, so compare against `fnv1_uint`.

**The BF1 map bundle helper truncates its skip list and returns the asset as modified anyway.** The reply caps `skipped` at 50 entries while `skipped_count` is the true total, so compare the two before concluding what was already present. `modify_ebx` defaults to true and the `ModifyEbx` call runs after the loop regardless of `added_count`, so an asset that matched no map bundle at all, for example every name missing on a profile that does not ship BF1 levels, is still marked modified and then shows up in `project_stats` and in the next `.fbmod`. Pass `modify_ebx: false` when you only want to probe.

**Every tool failure comes back as a successful JSON-RPC response.** `Dispatch` returns each handler result, including `success: false` and `METHOD_NOT_FOUND`, as the `result` member of an ordinary response; only an exception inside a handler becomes a JSON-RPC error (-32603, with no `error_code`). A client that only checks the transport for errors reads a refused write as a completed call. `METHOD_NOT_FOUND` is also absent from the documented error table and is what any name outside the router gets, including a name that belongs to a different build. The router map ignores case, but the commit gate compares the request name with the canonical tool name using `StringComparison.Ordinal`, so a preview and a commit sent with different case cannot pair.

**`insert_ebx_item` can never be committed through the MCP tool.** The C# handler wraps its work in `McpCommit.Require("insert_ebx_item", ...)`, so a call without `commit` stores the plan and returns `success: false` with `error_code: "PREVIEW_ONLY"`. The Python wrapper has parameters `name`, `guid`, `path`, `class_guid`, `index` and `source_index` only, with no `commit`, and the payload it builds contains no `commit` key, so the commit branch is unreachable from MCP. Every call therefore reports a preview and changes nothing, no matter how many times it is repeated; the reply's `tool` field says `insert_ebx_item` and `would_change` is true while the array stays untouched.

**`set_mod_settings` blanks every text field you leave out.** The Python tool always puts `title`, `author`, `category`, `version`, `description` and `link` into the payload, defaulting each to the empty string. The C# loop skips a field only when `p.Value<string>(field) == null`, and `""` is not null, so it assigns an empty author, category, version, description and link for the five fields you did not mention. A call such as `set_mod_settings(title="New")` therefore erases those five while returning `success: true`. To change one field safely, read `get_mod_settings` first and re-send the current values of the others. Only `selected_category = -1` truly means keep current, because the wrapper omits that key when it is negative.

**`import_sound` fails on its own defaults because `-1` is sent instead of omitted.** The Python payload always includes `segment_index` and `variation_index`, defaulting to `-1`. The C# handler treats a present key as an explicit request and range-checks it, returning `INVALID_PARAMS` with `segment_index must be 0..N` as soon as the value is negative, before its own absent-means-zero fallback runs. So a normal call with a path and nothing else fails on any sound that has segments, and you must pass `segment_index=0` and `variation_index=0` yourself. In the other direction, `codec: 0`, `channel_count: 0` and `seekable: False` are dropped from the payload, so they mean read the sample header and keep the asset's IsSeekable rather than force that value.

**`set_root_flags` writes one word, and 15 is not a universal value.** The tool needs `flags` explicitly; called without it the bridge refuses with INVALID_PARAMS rather than picking a default, because a wrong value silently changes how the asset is treated at load time. Read the current word first and change only the bits you mean, then confirm with `get_ebx_property` on `Flags`.

**`set_component_flags` swallows bad `flags_json` and lets its keys overwrite the target identity.** The wrapper parses `flags_json` and merges the result straight into the top level of the payload with `params.update(...)` inside a bare `except: pass`. Invalid JSON is silently ignored, so the remaining `flag_preset` and `realm` arguments are applied and the reply still says `success: true`. A valid object is merged at the same level as `name`, `guid`, `class_guid` and `index`, so a key such as `"name"` or `"class_guid"` replaces the asset or component you meant to edit, and a misspelled bit name such as `"cleint_event"` is ignored by the C# bit reader while the call still reports success.

**An omitted `value` writes an empty string instead of erroring.** `set_ebx_field`, `set_ebx_property`, `set_component_property` and `set_localized_string` all default `value` to `""`, and the three field setters fall back to the raw string whenever `json.loads` fails. The C# side only checks that the `value` key exists, so a forgotten argument becomes a real empty-string write and returns `success: true` with `value: ""`. The same fallback means a malformed literal such as `{x:1}` is stored verbatim on string-typed targets; the only clue is the echoed `value` and `value_type` in the reply. Pass the quoted empty string deliberately if you really want to clear a field, and read the reply's `value` before assuming the intended value landed.

**`export_texture` defaults `format` to `dds` and overrides extension detection.** The Python default is `format: str = "dds"`, and it is always sent. The C# handler would otherwise derive the format from the output path extension when `format` is absent, so `export_texture(name=..., path="out.png")` writes DDS bytes into a file ending in `.png` and returns `success: true` with `format: "dds"`. Compare the reply's `format` against the path extension before using the file. Pass `format="png"` or `format="tga"` explicitly when the extension is meaningful.

**`get_internal_pointer_ref` silently turns a component request into the root request when `root` is also true.** The wrapper computes `"root": root or not class_guid`, so an explicit `root=True` wins even when a valid `class_guid` or `component_guid` was supplied. The C# handler then takes `useRoot` and returns `asset.RootInstanceGuid`, ignoring the component GUID, with `success: true` and a plausible looking `pointer`. The giveaway is that the returned `class_guid` equals the asset's root instance GUID rather than the component you passed. Never set `root=True` together with `class_guid`; omit `root` and let the wrapper decide from `class_guid`.

**`add_to_network_registry` adds every shape you supply, not one of them.** The Python payload always carries `asset`, `asset_guid`, `asset_class_guid`, `file_guid`, `class_guid` and `refs`, and the C# parser walks each shape independently: the `refs` array, then the `file_guid` plus `class_guid` pair, then the asset entry. Supplying both `asset` and `file_guid` plus `class_guid` therefore registers two pointers, or the same pointer twice with the second reported under `skipped` as `already_present`; `added_count` is the field that exposes it. Note also that `class_guid` doubles as the asset's class GUID when `asset` is given, so the same argument changes meaning depending on which other shape is present. Send exactly one shape per call.

**Transport failures are Python exceptions, not `RPC_ERROR` payloads.** `send_command` raises `TimeoutError` after the configured timeout and `ConnectionError` when the pipe cannot be opened, and the tool functions call it without a try block, so those reach the client as a failed tool call rather than a JSON body with `error_code`; only a JSON-RPC error object inside an otherwise delivered response becomes `RPC_ERROR`. Opening a pipe gets just three attempts with sleeps of 0.2, 0.4 and 0.6 seconds, roughly 1.2 seconds in total, so an early call after launch can report the bridge unreachable while the editor is merely still loading. The timeout message states that the request may still be running in the editor, and there is no automatic retry by design, so a timed out create or add may already have happened; check state before resending. The timeout is 120 seconds unless `FROSTY_MCP_TIMEOUT` overrides it, and a value of 0 or less removes the timeout entirely, leaving a hanging read with no deadline.

**One pipe and one lock serialize every tool call, and the timeout clock starts after the lock is acquired.** `_exchange_with_timeout` holds a per-client `RLock` for the whole write and read, and the worker join with the timeout happens inside that lock. Concurrent MCP calls queue behind a long export or scan, and the queued call's own 120 second budget begins only once it obtains the lock, so a busy editor can make an unrelated call appear slow without any individual request exceeding its limit. After a timeout the handle is detached and the next call opens a fresh pipe while the orphaned handle is closed by the worker that still owns the read.

**The three `update_*_connection` tools cannot clear a field to empty.** Each wrapper adds a key only when the string is non-empty, for example `if source_field != "": params["source_field"] = source_field`. The C# handler would write an empty string for any present key, but the empty string can never be sent, so `update_link_connection(source_field="")` returns `success: true` with the old value still in place. `update_interface_field` is the exception: it declares `value: str = None` and sends the key whenever it is not None, so passing `value=""` really does clear that field. When a connection field must be emptied, set it through `set_ebx_field` with a `$` path or through `set_component_property` instead.

**`export_all_ebx` treats `limit: 0` as no limit.** The Python default is `limit: int = 0` and it is always sent, and the C# loop breaks only when `limit > 0 && entries.Count >= limit`, so the default caps nothing. Unlike the paged list tools, a zero here does not mean zero rows, so a habit carried over will export the entire asset database to disk in one call. Use a positive `limit` for a trial run, and narrow the set with `query` or `type` before writing thousands of files.

**`get_asset_dependencies` sends `compact: false` and defeats the recursive default.** The Python wrapper defaults `compact` to `False` and always includes it, while the C# handler computes `compact = p.Value<bool?>("compact") ?? recursive`. With `recursive=True` and no `compact` argument the tool would return compact name, type and depth rows; through the wrapper it returns full serialized asset entries instead, which is larger, slower and not what the docstring describes. Set `compact=True` explicitly for graph walks. The `max_nodes` handling is the intended pattern: the wrapper omits the key when it is 0, so the C# default of 200 recursive and 2000 otherwise still applies.

**`batch_set_field` forwards items raw, so routing and value encoding differ from the single tools.** Each item is passed through untouched, and the C# side picks `SetComponentProperty` whenever the item contains an `index` or `component_type` key at all. An item that carries the single tool's sentinel `"index": -1` is therefore routed to the component setter, which has no root fallback and no `$` connection path support, and fails where `set_ebx_field` would have succeeded. Values are also not JSON-decoded the way the single tools decode them, so quotes you add to protect a string survive into string-typed properties. The top-level `success` is true only when `failed_count` is zero, and the per-item rows carry only `name`, `path`, `success` and an optional `error`, so inspect `ok_count` and `results` after any partial failure.

**Python-only aliases silently pick one argument and discard the other.** Several wrappers merge parameters before sending: `search_ebx` sends `query or name`, `list_network_registry` sends `registry or name`, `get_chunk_entry`, `export_chunk` and `import_chunk` send both `id` and `guid` cross-filled, `setup_lua` sends `lua_name or new_name` and `blueprint or host`, and `hash_string` falls back to `value` only when `text` is empty. If both arguments are supplied with different values, the C# side reads them in its own order, which is `query` before `name`, `registry` before `name`, and `id` before `guid`, so the first one wins with no warning. The reply echoes the name or id that was actually used; compare it against what you meant before trusting the result.

**`bundles` in `get_ebx_entry` is the effective list, not the vanilla one.** The field holds vanilla membership minus removed rows plus editor-added rows, which is what the mod writer will emit. `original_bundles`, `added_bundles` and `removed_bundles` are the raw views; reasoning about "is this in its original bundle" from `bundles` alone gets the answer wrong as soon as anything was added or removed.

**A new networked object also has to be registered.** Adding a Synced component or a new networked entity data object to a blueprint is not enough: the server keeps and replicates state for objects it finds in a NetworkRegistry asset. Without a matching registry entry the change is visible on the host and silently missing on clients. `registry_gap` compares an asset's exported object GUIDs against the registries and lists what is missing.

**`diff_ebx` compares against vanilla, and object numbering drifts after a revert.** Diff rows are keyed on object GUIDs, and those are reassigned when the asset is reverted or the editor restarts, so the same object can appear as removed and added. Read the diff by object name and position, not by GUID.

**To learn what the mod really changes, run the round trip.** Call `export_ebx_bin`, then `revert_asset`, then export again and compare the two files. A clean asset round-trips byte for byte, so anything that differs is yours. Align the two exports by object name and order rather than by internal GUID, because the revert reassigns them.

**A new asset of a runtime-registered type can crash the game while looking perfectly correct.** In Battle for Neighborville a freshly created BuffData crashes at character spawn inside the asset registration path, whether it came from `create_ebx` or `duplicate_ebx`, with a matching class table, bundle hash and pointers. Reusing a vanilla asset of the same type that nothing else references, and editing that, works. For gameplay-critical types, treat "the editor accepted it" as no evidence and test in game before building on it.

**An edit writes the whole asset, not the field.** Any tool that marks an asset modified causes the entire asset to be serialised into the `.fbmod`. Fields changed earlier for convenience, or defaults changed while experimenting, ship with it. `diff_ebx` shows exactly what will be written; use it before packaging, especially on an asset that other work already touched.

**A second client on the same pipe blocks the first.** Each editor profile has exactly one bridge pipe, and the bridge serves one request at a time. Starting a script that connects directly while an MCP server session is attached leaves one of them waiting until the timeout, and a timed-out request may still run in the editor. Disconnect the other client first, and treat a timeout as "may have executed", never as "did not run".

**Do not experiment in the project that is under test or already packaged.** Test edits land in the same `.fbproject` that produces the shipped `.fbmod`, so a forgotten trial asset or a half-reverted asset ends up in the release. Start a fresh project for each experiment and copy only the finished asset in.

**A failed import can leave added chunks behind.** The encoder and the chunk write happen before the segment and variation tables are updated, so a failure part way through reports `partial_changes_possible` and the project then holds an orphan chunk. Check `list_modified_assets(kind="chunk")` after a failed media import and revert the asset before retrying.

**`save_project` on a project with no filename fails instead of prompting.** A project created with `new_project` or opened without a path has nothing to write to, and the tool returns `INVALID_STATE`; there is no save dialog behind it. Use `save_project_as` first, then `save_project` for later saves, and remember that `export_mod` reads the saved file.

## Easy to confuse

**Six ways to put an asset in a bundle.** `add_to_bundle` writes one membership row.
`safe_add_to_bundle` does the same but skips when already present, for minimal diffs.
`add_to_bundle_smart` also adds the RES/chunk the asset needs (preferred for anything
resource-backed). `batch_add_to_bundle` applies either to a list. `advanced_add_to_bundle`
and `completely_add_to_bundle` need AdvancedBundleEditorPlugin and also touch
NetworkRegistry, MeshVariationDb and unlock tables. On a build without that plugin only the
first three work.

**Removing has the same shape.** `remove_from_bundle` drops one editor-added row;
`remove_from_bundle_smart` also unlinks the resources it brought and lists what it left
alone in `native_skipped`. Native membership cannot be removed at all (`NOT_EXPORTABLE`),
because a `.fbmod` can only add memberships.

**Four ways to copy.** `duplicate_component` copies one object inside an asset.
`duplicate_ebx` copies one asset with fresh object GUIDs but shares linked resources unless
`copy_linked` is set, and the reply flags `linked_assets_shared`. `duplicate_deep` runs
DuplicationPlugin's per-type extension, which copies the RES and chunks too.
`clipboard_paste_object` pastes a copied object graph into a different asset.

**Three ways to write a field.** `set_ebx_property` walks the root object by path and
understands the `Components` and `Objects` aliases. `set_component_property` addresses one
component's SDK property. `set_ebx_field` addresses any field including connections
(`$PropertyConnections[3].TargetField`) and accepts `component_type` plus `type_index`.
They are not interchangeable, and only `set_ebx_field` reaches connection fields.

**Three flag helpers.** `encode_property_flags` builds a value, `decode_connection_flags`
reads one back, and `set_component_flags` sets object flags, which is a different bit layout
on a different object. The connection layout is
`flags = realm | (prop_type << 4) | (source_cant_be_static ? 8 : 0)`, with realm
`ClientAndServer=1, Client=2, Server=3, NetworkedClient=4, NetworkedClientAndServer=5` and
prop_type `Default=0, Interface=1, Exposed=2, Invalid=3`.

**Four ways to look for references.** `find_references` uses the file-level dependency
graph. `find_pointer_references` walks actual PointerRefs and can filter by class GUID.
`get_asset_dependencies` walks one asset's graph outward. `validate_refs` checks that the
pointers an asset holds actually resolve.

**Four ways to inspect a bundle.** `list_bundles` lists bundles. `get_bundle` lists the
members of one with counts and paging. `list_bundle_contents` lists members filtered by
`kind`. `bundle_closure_check` reports what a member needs that the bundle lacks, and
`check_asset_ready` asks the same for one asset.

**Four registry tools.** `list_network_registry` reads a registry,
`add_to_network_registry` and `remove_from_network_registry` change it, and `registry_gap`
reports which of an asset's exported objects are missing. Registration is per object, so an
asset's root and each of its components are separate entries.

**Sound has four entry points.** `get_sound_info` is read-only. `export_sound_wav` and
`import_sound_wav` handle PCM16 WAV only and never re-encode. `import_sound` re-encodes any
file with SoundEditorPlugin's tool and rewrites the chunk, seek table and segment and
variation tables.

**Export comes in four flavours.** `get_ebx_xml` and `get_ebx_yaml` return text you can
read. `export_asset` writes a file through the editor's asset definition, whose format
depends on the asset type. `export_ebx_bin` is the lossless binary format that
`import_ebx_bin` reads back. `export_all_ebx` is the bulk text version.

**Project writes versus mod packaging.** `save_project`, `save_project_as` and
`save_project_copy` write `.fbproject` files. `export_mod` writes a `.fbmod`. Neither
implies the other: save first, export separately.

**Reverting one asset is not reverting the project.** `revert_asset` undoes one asset;
`revert_all` also drops added bundles and clears the ModSettings dirty flag.

**`data_only` is two different operations.** `data_only: true` (default, single call) keeps
the asset's file GUID and root class GUID, but **internal object GUIDs come from the
file**; the reply lists the replaced component GUIDs in `changed_class_guids` and the assets
still pointing at the old ones in `affected_assets`. `data_only: false` adopts the file's
file GUID, so it is two-phase and refuses with `GUID_MISMATCH` if the identity would split.

**Bundle names are case-insensitive on input but exact on lookup.** Every bundle tool
resolves the name (exact first, then case-insensitive) and returns the canonical `bundle`
name; report that one, not what you typed.

**`create_bundle` may not have created anything.** `AddBundle` returns the existing entry
when the name is taken, so read `existing`, `created`, `type_matches` and `super_matches`.
A false `type_matches` means the bundle already existed with a different type.

**`set_bundle_super` only works on editor-added bundles.** Repointing a vanilla bundle
changes the editor view and nothing else, so it returns `NOT_EXPORTED` unless `force`.

**`add_to_network_registry` takes three mutually exclusive shapes**: `asset` (plus
`asset_class_guid`), `file_guid` plus `class_guid`, or `refs_json`. A `class_guid` given
with `asset` selects a non-root object and is validated. A successful add may carry a
`warning` about an object type that crashes the game; honour it.

## Reply conventions

- List tools return `total` (true match count), `offset`, `limit` and `returned`. Page until
  `offset + returned >= total`.
- `limit: 0` returns no rows, which is the way to read only the totals.
- Paired `*_count` (true) and `*_returned` (page length) fields mean the array was capped; a
  `truncated` boolean says whether more exist.
- Scan tools (`validate_connections`, `get_sound_info`) report `scan_errors` instead of
  treating a failed scan as "nothing there", and fail with `SCAN_INCOMPLETE`.
- `get_type_info` returns `fields` only; there is no duplicate `properties` key.

## Bounded scans

Some tools walk the whole asset database. They are bounded and say so:

- `check_guid_invariants` scans at most `max_scan` assets (default 5000) and reports
  `candidate_count`, `scan_truncated` and `load_failures`. Narrow with `query`, `added_only`
  or `names_json`, and sweep a large project in several calls.
- `find_pointer_references` scans at most `max_scan` assets (default 3000), paged.
- `registry_gap` checks at most `max_registries` discovered registries (default 8). A
  large profile can hold thousands of `NetworkRegistryAsset` entries, so
  `registries_checked` lists what was compared and `registries_skipped` says how many were
  left out.
- `get_asset_dependencies(recursive=true)` caps the walk at `max_nodes` (default 200) and
  returns compact rows (name, type, depth); results are paged.
- `get_ebx_xml` and `get_ebx_yaml` default to 60000 characters; when `truncated`, pass
  `next_offset` back as `char_offset`.

## Internal objects and class_guid

`list_components` and `get_component` report `class_guid` for every object, but objects
Frosty does not export (most child EntityData) have no real class GUID. For those the value
is synthesised from `InternalId`, so it looks like
`00000000-0000-0000-0000-000000000001` and up. Two consequences:

- It addresses the object within this session (`set_ebx_field`, `remove_component` and
  friends resolve it), but it is **not** a GUID the game knows and must never be written
  into an External PointerRef.
- `InternalId` is reassigned when the asset reloads, so a synthesised GUID captured earlier
  can point at a different object later. Resolve it again from the current listing rather
  than storing it.

## Workflows

Read a component field:
```
search_ebx_fields(name=A, query=<value or field name>, type=<ComponentType>)
get_ebx_field(name=A, path=<from match>, class_guid=<from match>)
set_ebx_field(name=A, class_guid=..., path=..., value='"text"')
```

Duplicate a blueprint and give it its own bundle:
```
duplicate_ebx(name=src, new_name=dst)          # copy_bundles defaults false
create_bundle(name=..., super_bundle=...)
add_to_bundle_smart(name=dst, bundle=...)
add_to_network_registry(registry=..., asset=dst)
registry_gap(names=[dst])                      # expect missing_total: 0
```

Add a component and wire it:
```
create_component(name=A, type=DelayEntityData, at_index=0)
add_event_connection(name=A, source_guid=..., source_event=..., target_guid=..., target_event=...)
validate_connections(name=A)
```

Delete safely:
```
remove_component(name=A, class_guid=..., commit=false)   # preview and plan
remove_component(name=A, class_guid=..., commit=true)    # same call, applies
```

Check a bundle before saving:
```
bundle_closure_check(bundle=B)
check_asset_ready(name=A, bundle=B)
find_pointer_references(name=A)
check_guid_invariants(query="mod/", max_scan=2000)
```

## Saving and exporting

- The first save of a project belongs to the user: they choose the filename and the mod
  metadata. After that the tools can save on their own.
- `save_project_as` switches the open project to the new path; `save_project_copy` does not.
- `open_project` and `new_project` refuse a dirty project; pass `save_first` or `discard`.
- A failed `open_project` returns `ASSETS_LOST` with `assets_before` and `assets_after`: the
  editor unloads the current project before parsing the new file.
- `export_mod` refuses an unmodified project unless `allow_empty`, builds into
  `<path>.tmp` and moves it into place, and copies the icon and screenshots from ModSettings
  unless `icon_path` is given.
- `get_mod_settings` / `set_mod_settings` read and write the mod metadata; the icon and up
  to four screenshots come from file paths.
- ModSettings: `link` only reaches a `.fbmod` (`link_persists_to: "fbmod"`) and
  `selected_category` reaches neither file (`selected_category_persists_to: "none"`).
  `selected_category` is 0..8 and, when non-zero, fills `category` from the editor's list.

## Plugin-dependent tools

Tools that need a plugin refuse with `PLUGIN_MISSING` when it is absent. Confirm with
`capabilities()` first.

- TexturePlugin: `export_texture`, `import_texture`, non-DDS image conversion.
- MeshSetPlugin: `export_mesh`, `import_mesh`, `get_mesh_info`.
- SvgImagePlugin: `export_svg`, `import_svg`.
- SoundEditorPlugin: `import_sound`, `get_sound_info`, the WAV helpers.
- AtlasTexturePlugin, IesResourcePlugin, DuplicationPlugin, LuaPlugin: their own tools.
- PVZBundleManagerPlugin: `can_be_loaded_by_bundle`, `manage_bundles`, `create_asset`.
  `can_be_loaded_by_bundle` returns `degraded: true` with `can_be_loaded: null` when the
  plugin has not built its parent and prev bundle table.
- AdvancedBundleEditorPlugin: `advanced_*`, `completely_*`, `create_advanced_bundle`,
  `validate_bundle_add`. The `advanced_*` pair accepts `allow_fallback` to degrade to the
  smart variants; the rest simply fail.

## Cautions

- Everything except `open_asset`, `get_selected_asset` and the `InvokeOnUi` paths runs on
  the pipe thread and mutates the in-memory project. Save when the result matters.
- `remove_component` and `remove_ebx_item` report `cleared_paths`: the property paths whose
  pointers were dropped. Check them when the object was referenced from more than one place.
- `import_sound` writes the chunk before updating the tables; a failure returns
  `partial_changes_possible`.
- `import_project` applies changes as it reads, so a failure returns
  `partial_changes_possible` too.
- `delete_bundle` accepts only the newest added bundle, because bundle ids are list indices
  and removing any other would renumber every later one.
- `get_log` is the fastest way to see why a plugin-backed tool failed without saying so.
