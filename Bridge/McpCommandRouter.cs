using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace FrostyMcpPlugin.Bridge
{
    internal static class McpCommandRouter
    {
        private static readonly Dictionary<string, Func<JObject, object>> Handlers =
            new Dictionary<string, Func<JObject, object>>(StringComparer.OrdinalIgnoreCase)
            {
                // Query / navigation
                ["ping"] = McpHandlers.Ping,
                ["get_editor_info"] = McpHandlers.GetEditorInfo,
                ["get_selected_asset"] = McpHandlers.GetSelectedAsset,
                ["open_asset"] = McpHandlers.OpenAsset,
                ["search_ebx"] = McpHandlers.SearchEbx,
                ["get_ebx_entry"] = McpHandlers.GetEbxEntry,
                ["get_ebx_xml"] = McpHandlers.GetEbxXml,
                ["get_ebx_yaml"] = McpHandlers.GetEbxYaml,
                ["list_modified_assets"] = McpHandlers.ListModifiedAssets,
                ["search_res"] = McpHandlers.SearchRes,
                ["get_res_entry"] = McpHandlers.GetResEntry,
                ["search_chunks"] = McpHandlers.SearchChunks,
                ["get_chunk_entry"] = McpHandlers.GetChunkEntry,
                ["list_bundles"] = McpHandlers.ListBundles,
                ["get_bundle"] = McpHandlers.GetBundle,
                ["get_asset_dependencies"] = McpHandlers.GetAssetDependencies,
                ["get_type_info"] = McpTypeExplorerHandlers.GetTypeInfo,
                ["search_types"] = McpTypeExplorerHandlers.SearchTypes,
                ["get_type_field"] = McpTypeExplorerHandlers.GetTypeField,
                ["list_type_namespaces"] = McpTypeExplorerHandlers.ListTypeNamespaces,
                ["find_references"] = McpEditHandlers.FindReferences,
                ["find_pointer_references"] = McpRefHandlers.FindPointerReferences,
                ["insert_ebx_item"] = McpRefHandlers.InsertEbxItem,

                // EBX edit
                ["get_ebx_property"] = McpEditHandlers.GetEbxProperty,
                ["set_ebx_property"] = McpEditHandlers.SetEbxProperty,
                ["export_ebx_bin"] = McpEditHandlers.ExportEbxBin,
                ["import_ebx_bin"] = McpEditHandlers.ImportEbxBin,
                ["duplicate_ebx"] = McpEditHandlers.DuplicateEbx,
                ["create_ebx"] = McpEditHandlers.CreateEbx,
                ["revert_asset"] = McpEditHandlers.RevertAsset,
                ["export_asset"] = McpEditHandlers.ExportAsset,

                // RES / Chunk
                ["export_res"] = McpEditHandlers.ExportRes,
                ["import_res"] = McpEditHandlers.ImportRes,
                ["export_chunk"] = McpEditHandlers.ExportChunk,
                ["import_chunk"] = McpEditHandlers.ImportChunk,
                ["duplicate_res"] = McpEditHandlers.DuplicateRes,
                ["duplicate_chunk"] = McpEditHandlers.DuplicateChunk,

                // Bundles
                ["add_to_bundle"] = McpEditHandlers.AddToBundle,
                ["remove_from_bundle"] = McpEditHandlers.RemoveFromBundle,
                ["create_bundle"] = McpEditHandlers.CreateBundle,

                // Texture / Mesh
                ["export_texture"] = McpEditHandlers.ExportTexture,
                ["import_texture"] = McpEditHandlers.ImportTexture,
                ["export_mesh"] = McpEditHandlers.ExportMesh,
                ["import_mesh"] = McpEditHandlers.ImportMesh,

                // Blueprint connections
                ["list_connections"] = McpBlueprintHandlers.ListConnections,
                ["add_property_connection"] = McpBlueprintHandlers.AddPropertyConnection,
                ["add_event_connection"] = McpBlueprintHandlers.AddEventConnection,
                ["add_link_connection"] = McpBlueprintHandlers.AddLinkConnection,
                ["update_property_connection"] = McpBlueprintHandlers.UpdatePropertyConnection,
                ["update_event_connection"] = McpBlueprintHandlers.UpdateEventConnection,
                ["update_link_connection"] = McpBlueprintHandlers.UpdateLinkConnection,
                ["remove_connection"] = McpBlueprintHandlers.RemoveConnection,
                ["remove_connections_by_guid"] = McpBlueprintHandlers.RemoveConnectionsByGuid,
                ["decode_connection_flags"] = McpBlueprintHandlers.DecodeConnectionFlags,

                // Blueprint components
                ["list_components"] = McpBlueprintHandlers.ListComponents,
                ["get_component"] = McpBlueprintHandlers.GetComponent,
                ["create_component"] = McpBlueprintHandlers.CreateComponent,
                ["duplicate_component"] = McpBlueprintHandlers.DuplicateComponent,
                ["set_component_flags"] = McpBlueprintHandlers.SetComponentFlags,
                ["get_component_property"] = McpBlueprintHandlers.GetComponentProperty,
                ["set_component_property"] = McpBlueprintHandlers.SetComponentProperty,
                ["remove_component"] = McpBlueprintHandlers.RemoveComponent,
                ["remove_ebx_item"] = McpBlueprintHandlers.RemoveEbxItem,
                ["list_ebx_class_types"] = McpBlueprintHandlers.ListEbxClassTypes,
                ["create_ebx_class"] = McpBlueprintHandlers.CreateEbxClass,

                // Interface descriptor
                ["list_interface"] = McpBlueprintHandlers.ListInterface,
                ["ensure_interface"] = McpBlueprintHandlers.EnsureInterface,
                ["add_interface_field"] = McpBlueprintHandlers.AddInterfaceField,
                ["update_interface_field"] = McpBlueprintHandlers.UpdateInterfaceField,
                ["remove_interface_field"] = McpBlueprintHandlers.RemoveInterfaceField,
                ["add_interface_event"] = McpBlueprintHandlers.AddInterfaceEvent,
                ["remove_interface_event"] = McpBlueprintHandlers.RemoveInterfaceEvent,

                // Field search / edit inside EBX objects
                ["search_ebx_fields"] = McpBlueprintHandlers.SearchEbxFields,
                ["get_ebx_field"] = McpBlueprintHandlers.GetEbxField,
                ["set_ebx_field"] = McpBlueprintHandlers.SetEbxField,
                ["research_component_usage"] = McpBlueprintHandlers.ResearchComponentUsage,

                // Extra utilities (hash / localization / clipboard / inspect / smart bundle)
                ["hash_string"] = McpExtraHandlers.HashString,
                ["get_localized_string"] = McpExtraHandlers.GetLocalizedString,
                ["set_localized_string"] = McpExtraHandlers.SetLocalizedString,
                ["revert_localized_string"] = McpExtraHandlers.RevertLocalizedString,
                ["list_localized_strings"] = McpExtraHandlers.ListLocalizedStrings,
                ["clipboard_paste_object"] = McpExtraHandlers.ClipboardPasteObject,
                ["create_struct"] = McpExtraHandlers.CreateStruct,
                ["get_texture_info"] = McpExtraHandlers.GetTextureInfo,
                ["get_mesh_info"] = McpExtraHandlers.GetMeshInfo,
                ["add_to_bundle_smart"] = McpExtraHandlers.AddToBundleSmart,
                ["remove_from_bundle_smart"] = McpMoreHandlers.RemoveFromBundleSmart,
                ["batch_add_to_bundle"] = McpMoreHandlers.BatchAddToBundle,
                ["get_ebx_guids"] = McpMoreHandlers.GetEbxGuids,

                // Media / Lua / deep duplicate
                ["get_lua_source"] = McpMediaHandlers.GetLuaSource,
                ["export_lua"] = McpMediaHandlers.ExportLua,
                ["import_lua"] = McpMediaHandlers.ImportLua,
                ["create_lua_asset"] = McpMediaHandlers.CreateLuaAsset,
                ["create_lua_runner_component"] = McpMediaHandlers.CreateLuaRunnerComponent,
                ["setup_lua"] = McpMediaHandlers.SetupLua,
                ["get_lua_guid_name"] = McpMediaHandlers.GetLuaGuidName,
                ["export_atlas_texture"] = McpMediaHandlers.ExportAtlasTexture,
                ["import_atlas_texture"] = McpMediaHandlers.ImportAtlasTexture,
                ["export_svg"] = McpMediaHandlers.ExportSvg,
                ["import_svg"] = McpMediaHandlers.ImportSvg,
                ["export_all_ebx"] = McpBulkHandlers.ExportAllEbx,
                ["export_ies"] = McpBulkHandlers.ExportIes,
                ["import_sound"] = McpSoundHandlers.ImportSound,
                ["create_asset"] = McpAssetCreateHandlers.CreateAsset,
                ["can_be_loaded_by_bundle"] = McpBulkHandlers.CanBeLoadedByBundle,
                ["manage_bundles"] = McpBulkHandlers.ManageBundles,
                ["check_asset_ready"] = McpBulkHandlers.CheckAssetReady,
                ["refresh_mesh_variations"] = McpBulkHandlers.RefreshMeshVariations,
                ["import_project"] = McpMergeHandlers.ImportProject,
                ["get_sound_info"] = McpMediaHandlers.GetSoundInfo,
                ["export_sound_wav"] = McpMoreHandlers.ExportSoundWav,
                ["import_sound_wav"] = McpMoreHandlers.ImportSoundWav,
                ["duplicate_deep"] = McpMediaHandlers.DuplicateDeep,
                ["create_schematic_channel_asset"] = McpMoreHandlers.CreateSchematicChannelAsset,
                ["create_schematic_channel_entity"] = McpMoreHandlers.CreateSchematicChannelEntity,

                // Advanced Bundle Editor + Blueprint utilities (v1.10)
                ["advanced_add_to_bundle"] = McpAdvancedHandlers.AdvancedAddToBundle,
                ["advanced_remove_from_bundle"] = McpAdvancedHandlers.AdvancedRemoveFromBundle,
                ["completely_add_to_bundle"] = McpAdvancedHandlers.CompletelyAddToBundle,
                ["completely_remove_from_bundle"] = McpAdvancedHandlers.CompletelyRemoveFromBundle,
                ["validate_bundle_add"] = McpAdvancedHandlers.ValidateBundleAdd,
                ["create_advanced_bundle"] = McpAdvancedHandlers.CreateAdvancedBundle,
                ["set_bundle_super"] = McpAdvancedHandlers.SetBundleSuper,
                ["delete_bundle"] = McpProjectTools.DeleteBundle,
                ["list_super_bundles"] = McpProjectTools.ListSuperBundles,
                ["project_stats"] = McpProjectTools.ProjectStats,
                ["get_project_info"] = McpProjectLifecycleHandlers.GetProjectInfo,
                ["save_project"] = McpProjectLifecycleHandlers.SaveProject,
                ["save_project_as"] = McpProjectLifecycleHandlers.SaveProjectAs,
                ["save_project_copy"] = McpProjectLifecycleHandlers.SaveProjectCopy,
                ["open_project"] = McpProjectLifecycleHandlers.OpenProject,
                ["new_project"] = McpProjectLifecycleHandlers.NewProject,
                ["revert_all"] = McpProjectLifecycleHandlers.RevertAll,
                ["get_mod_settings"] = McpProjectLifecycleHandlers.GetModSettings,
                ["set_mod_settings"] = McpProjectLifecycleHandlers.SetModSettings,
                ["export_mod"] = McpProjectLifecycleHandlers.ExportMod,
                ["get_log"] = McpProjectLifecycleHandlers.GetLog,
                ["list_linked_assets"] = McpProjectLifecycleHandlers.ListLinkedAssets,
                ["link_asset"] = McpProjectLifecycleHandlers.LinkAsset,
                ["unlink_asset"] = McpProjectLifecycleHandlers.UnlinkAsset,
                ["check_guid_invariants"] = McpProjectLifecycleHandlers.CheckGuidInvariants,
                ["capabilities"] = McpProjectLifecycleHandlers.Capabilities,
                ["batch_set_field"] = McpProjectTools.BatchSetField,
                ["diff_ebx"] = McpProjectTools.DiffEbx,
                ["validate_refs"] = McpProjectTools.ValidateRefs,
                ["bundle_closure_check"] = McpProjectTools.BundleClosureCheck,
                ["registry_gap"] = McpProjectTools.RegistryGap,
                ["list_bundle_contents"] = McpAdvancedHandlers.ListBundleContents,
                ["resolve_hash"] = McpAdvancedHandlers.ResolveHash,
                ["encode_property_flags"] = McpAdvancedHandlers.EncodePropertyFlags,
                ["list_node_ports"] = McpAdvancedHandlers.ListNodePorts,
                ["list_registered_node_types"] = McpAdvancedHandlers.ListRegisteredNodeTypes,
                ["validate_connections"] = McpAdvancedHandlers.ValidateConnections,

                // BF1 DevPlugin helpers (v1.11)
                ["set_root_flags"] = McpBf1Handlers.SetRootFlags,
                ["get_internal_pointer_ref"] = McpBf1Handlers.GetInternalPointerRef,
                ["list_network_registry"] = McpBf1Handlers.ListNetworkRegistry,
                ["add_to_network_registry"] = McpBf1Handlers.AddToNetworkRegistry,
                ["remove_from_network_registry"] = McpBf1Handlers.RemoveFromNetworkRegistry,
                ["safe_add_to_bundle"] = McpBf1Handlers.SafeAddToBundle,
                ["add_to_bf1_map_bundles"] = McpBf1Handlers.AddToBf1MapBundles,
                ["list_bf1_map_bundles"] = McpBf1Handlers.ListBf1MapBundles,
            };

        public static object Dispatch(string method, JObject paramsObj)
        {
            if (!Handlers.TryGetValue(method, out Func<JObject, object> handler))
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "Method not found: " + method,
                    ["error_code"] = "METHOD_NOT_FOUND"
                };
            }

            return handler(paramsObj);
        }
    }
}
