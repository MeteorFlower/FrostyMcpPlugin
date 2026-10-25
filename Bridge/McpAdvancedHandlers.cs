using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Tools mirroring AdvancedBundleEditorPlugin.BundleEditors / BunpyApi
    /// and BlueprintEditorPlugin utilities (hash reverse, encode flags, node ports).
    /// </summary>
    internal static class McpAdvancedHandlers
    {
        // =====================================================================
        // Advanced Bundle Editor
        // =====================================================================

        public static object AdvancedAddToBundle(JObject p)
        {
            if (!TryResolveBundlePair(p, out EbxAssetEntry entry, out BundleEntry bundle, out object err))
                return err;

            try
            {
                Type editors = GetBundleEditorsType();
                if (editors == null)
                {
                    if (!(p.Value<bool?>("allow_fallback") ?? false))
                        return McpHandlers.Error("AdvancedBundleEditorPlugin not loaded", "PLUGIN_MISSING");
                    return FallbackSmartAdd(entry, bundle, p);
                }

                bool added = (bool)InvokeStatic(editors, "AddAssetToBundle", entry, bundle);
                bool netWanted = p.Value<bool?>("netreg") ?? false;
                bool mvdbWanted = p.Value<bool?>("mvdb") ?? false;
                bool unlockWanted = p.Value<bool?>("unlock_table") ?? false;

                bool net = netWanted && (bool)InvokeStatic(editors, "AddAssetToNetRegs", entry, bundle);
                bool mvdb = mvdbWanted && (bool)InvokeStatic(editors, "AddAssetToMvdBs", entry, bundle, null);
                bool unlock = unlockWanted && (bool)InvokeStatic(editors, "AddAssetToTables", entry, bundle);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundle.Name,
                    ["added"] = added,
                    ["netreg"] = net,
                    ["mvdb"] = mvdb,
                    ["unlock_table"] = unlock,
                    ["engine"] = "AdvancedBundleEditorPlugin.BundleEditors"
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "ADD_FAILED");
            }
        }

        public static object AdvancedRemoveFromBundle(JObject p)
        {
            if (!TryResolveBundlePair(p, out EbxAssetEntry entry, out BundleEntry bundle, out object err))
                return err;

            try
            {
                Type editors = GetBundleEditorsType();
                if (editors == null)
                {
                    if (!(p.Value<bool?>("allow_fallback") ?? false))
                        return McpHandlers.Error("AdvancedBundleEditorPlugin not loaded", "PLUGIN_MISSING");
                    return McpMoreHandlers.RemoveFromBundleSmart(p);
                }

                InvokeStatic(editors, "RemoveAssetFromBundle", entry, bundle);
                if (p.Value<bool?>("netreg") ?? false)
                    InvokeStatic(editors, "RemoveAssetFromNetRegs", entry, bundle);
                if (p.Value<bool?>("mvdb") ?? false)
                    InvokeStatic(editors, "RemoveAssetFromMeshVariations", entry, bundle);
                if (p.Value<bool?>("unlock_table") ?? false)
                    InvokeStatic(editors, "RemoveAssetFromTables", entry, bundle);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundle.Name,
                    ["engine"] = "AdvancedBundleEditorPlugin.BundleEditors"
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "REMOVE_FAILED");
            }
        }

        public static object CompletelyAddToBundle(JObject p)
        {
            if (!TryResolveBundlePair(p, out EbxAssetEntry entry, out BundleEntry bundle, out object err))
                return err;

            bool recursive = p.Value<bool?>("recursive") ?? true;
            Type editors = GetBundleEditorsType();
            if (editors == null)
                return McpHandlers.Error("AdvancedBundleEditorPlugin not loaded", "PLUGIN_MISSING");

            try
            {
                var touched = new List<object>();
                if (recursive)
                {
                    RecurseDependencies(entry, dep =>
                    {
                        if (dep.Type == "ShaderGraph") return;
                        bool valid = (bool)InvokeStatic(editors, "AssetRecAddValid", dep, bundle);
                        if (!valid) return;

                        bool added = (bool)InvokeStatic(editors, "AddAssetToBundle", dep, bundle);
                        bool netOk = (bool)InvokeStatic(editors, "AssetAddNetworkValid", dep, bundle);
                        bool mvOk = (bool)InvokeStatic(editors, "AssetAddMeshVariationValid", dep, bundle);
                        if (netOk) InvokeStatic(editors, "AddAssetToNetRegs", dep, bundle);
                        if (mvOk) InvokeStatic(editors, "AddAssetToMvdBs", dep, bundle, null);
                        try { InvokeStatic(editors, "AddAssetToTables", dep, bundle); } catch { }

                        touched.Add(new Dictionary<string, object>
                        {
                            ["name"] = dep.Name,
                            ["type"] = dep.Type,
                            ["added_bundle"] = added,
                            ["netreg"] = netOk,
                            ["mvdb"] = mvOk
                        });
                    });
                }
                else
                {
                    bool added = (bool)InvokeStatic(editors, "AddAssetToBundle", entry, bundle);
                    bool netOk = (bool)InvokeStatic(editors, "AssetAddNetworkValid", entry, bundle);
                    bool mvOk = (bool)InvokeStatic(editors, "AssetAddMeshVariationValid", entry, bundle);
                    if (netOk) InvokeStatic(editors, "AddAssetToNetRegs", entry, bundle);
                    if (mvOk) InvokeStatic(editors, "AddAssetToMvdBs", entry, bundle, null);
                    try { InvokeStatic(editors, "AddAssetToTables", entry, bundle); } catch { }
                    touched.Add(new Dictionary<string, object>
                    {
                        ["name"] = entry.Name,
                        ["added_bundle"] = added,
                        ["netreg"] = netOk,
                        ["mvdb"] = mvOk
                    });
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundle.Name,
                    ["recursive"] = recursive,
                    ["touched_count"] = touched.Count,
                    ["touched"] = touched.Take(200).ToList()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "ADD_FAILED");
            }
        }

        public static object CompletelyRemoveFromBundle(JObject p)
        {
            if (!TryResolveBundlePair(p, out EbxAssetEntry entry, out BundleEntry bundle, out object err))
                return err;

            bool recursive = p.Value<bool?>("recursive") ?? true;
            Type editors = GetBundleEditorsType();
            if (editors == null)
                return McpHandlers.Error("AdvancedBundleEditorPlugin not loaded", "PLUGIN_MISSING");

            try
            {
                var touched = new List<object>();
                Action<EbxAssetEntry> remOne = dep =>
                {
                    bool valid = true;
                    try { valid = (bool)InvokeStatic(editors, "AssetRecRemValid", dep, bundle); } catch { }
                    if (!valid && recursive) return;

                    InvokeStatic(editors, "RemoveAssetFromBundle", dep, bundle);
                    try { InvokeStatic(editors, "RemoveAssetFromNetRegs", dep, bundle); } catch { }
                    try { InvokeStatic(editors, "RemoveAssetFromMeshVariations", dep, bundle); } catch { }
                    try { InvokeStatic(editors, "RemoveAssetFromTables", dep, bundle); } catch { }
                    touched.Add(new Dictionary<string, object> { ["name"] = dep.Name, ["type"] = dep.Type });
                };

                if (recursive)
                    RecurseDependencies(entry, remOne);
                else
                    remOne(entry);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundle.Name,
                    ["recursive"] = recursive,
                    ["touched_count"] = touched.Count,
                    ["touched"] = touched.Take(200).ToList()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "REMOVE_FAILED");
            }
        }

        public static object ValidateBundleAdd(JObject p)
        {
            if (!TryResolveBundlePair(p, out EbxAssetEntry entry, out BundleEntry bundle, out object err))
                return err;

            Type editors = GetBundleEditorsType();
            if (editors == null)
                return McpHandlers.Error("AdvancedBundleEditorPlugin not loaded", "PLUGIN_MISSING");

            try
            {
                bool rec = (bool)InvokeStatic(editors, "AssetRecAddValid", entry, bundle);
                bool net = (bool)InvokeStatic(editors, "AssetAddNetworkValid", entry, bundle);
                bool mv = (bool)InvokeStatic(editors, "AssetAddMeshVariationValid", entry, bundle);
                bool op = true;
                try { op = (bool)InvokeStatic(editors, "AssetOpAddValid", entry, bundle); } catch { }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundle.Name,
                    ["rec_add_valid"] = rec,
                    ["network_valid"] = net,
                    ["mesh_variation_valid"] = mv,
                    ["op_add_valid"] = op,
                    ["already_in_bundle"] = entry.EnumerateBundles().Contains(App.AssetManager.GetBundleId(bundle))
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "VALIDATE_FAILED");
            }
        }

        public static object CreateAdvancedBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string bundlePath = (p.Value<string>("bundle") ?? p.Value<string>("name") ?? "").Trim();
            string superBundle = (p.Value<string>("super_bundle") ?? "").Trim();
            if (string.IsNullOrEmpty(bundlePath) || string.IsNullOrEmpty(superBundle))
                return McpHandlers.Error("bundle and super_bundle are required", "INVALID_PARAMS");

            string bundleType = p.Value<string>("bundle_type") ?? "Shared";
            bool generateBlueprints = p.Value<bool?>("generate_blueprints") ?? true;
            string blueprintType = p.Value<string>("blueprint_type") ?? "BlueprintBundle";

            Type bunpy = FindType("AdvancedBundleEditorPlugin.BunpyApi");
            if (bunpy == null)
                return McpHandlers.Error("AdvancedBundleEditorPlugin.BunpyApi not loaded", "PLUGIN_MISSING");

            try
            {
                MethodInfo add = bunpy.GetMethod("AddBundle", BindingFlags.Public | BindingFlags.Static);
                add.Invoke(null, new object[] { bundlePath, superBundle, bundleType, generateBlueprints, blueprintType });

                int bid = McpHandlers.ResolveBundleId(bundlePath);
                BundleEntry be = bid >= 0 ? App.AssetManager.GetBundleEntry(bid) : null;

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["bundle"] = bundlePath,
                    ["super_bundle"] = superBundle,
                    ["bundle_type"] = bundleType,
                    ["generate_blueprints"] = generateBlueprints,
                    ["blueprint_type"] = blueprintType,
                    ["bundle_id"] = bid,
                    ["created"] = be != null,
                    ["entry_name"] = be?.Name
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "CREATE_FAILED");
            }
        }

        public static object SetBundleSuper(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string bundleName = (p.Value<string>("bundle") ?? p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            string superName = (p.Value<string>("super_bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName) || string.IsNullOrEmpty(superName))
                return McpHandlers.Error("bundle and super_bundle are required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bid);

            // A .fbmod only carries Added bundles, so re-pointing a vanilla bundle
            // changes the editor view and nothing else. Refuse unless forced.
            bool force = p.Value<bool?>("force") ?? false;
            if (!be.Added && !force)
            {
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "Bundle is vanilla; changing its super bundle would not be written to a .fbmod",
                    ["error_code"] = "NOT_EXPORTED",
                    ["bundle"] = be.Name,
                    ["bundle_id"] = bid,
                    ["game_effect"] = "none"
                };
            }

            int sbIndex = App.AssetManager.GetSuperBundleId(superName);
            if (sbIndex < 0)
            {
                App.AssetManager.AddSuperBundle(superName);
                sbIndex = App.AssetManager.GetSuperBundleId(superName);
            }
            if (sbIndex < 0)
                return McpHandlers.Error("SuperBundle not found and could not be created", "NOT_FOUND");

            int oldIndex = be.SuperBundleId;
            be.SuperBundleId = sbIndex;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["bundle"] = be.Name,
                ["bundle_id"] = bid,
                ["added_bundle"] = be.Added,
                ["game_effect"] = be.Added ? "mod" : "none",
                ["old_super_bundle"] = App.AssetManager.GetSuperBundle(oldIndex)?.Name,
                ["super_bundle"] = App.AssetManager.GetSuperBundle(sbIndex)?.Name
            };
        }

        public static object ListBundleContents(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bid);
            string typeFilter = (p.Value<string>("type") ?? "").Trim();
            bool modifiedOnly = p.Value<bool?>("modified_only") ?? false;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);

            // EnumerateEbx(BundleEntry) honours Bundles/AddedBundles but not RemBundles;
            // filter by the effective list so the four bundle tools agree.
            var all = new List<object>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.EnumerateBundles().Contains(bid)) continue;
                if (modifiedOnly && !e.IsModified) continue;
                if (!string.IsNullOrEmpty(typeFilter) &&
                    !string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !TypeLibrary.IsSubClassOf(e.Type, typeFilter))
                    continue;
                all.Add(McpHandlers.SerializeEbxEntry(e));
            }

            var page = all.Skip(offset).Take(limit).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["bundle"] = be.Name,
                ["bundle_type"] = be.Type.ToString(),
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["assets"] = page
            };
        }

        // =====================================================================
        // Blueprint Editor utilities
        // =====================================================================

        public static object ResolveHash(JObject p)
        {
            string hashStr = (p.Value<string>("hash") ?? p.Value<string>("value") ?? "").Trim();
            if (hashStr.Length == 0)
                return McpHandlers.Error("hash is required (int or 0xHEX)", "INVALID_PARAMS");

            try
            {
                int hash;
                if (p["hash"] != null && p["hash"].Type != JTokenType.String)
                    hash = Convert.ToInt32(p["hash"].ToString());
                else if (hashStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hash = int.Parse(hashStr.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier);
                else
                    hash = int.Parse(hashStr, System.Globalization.NumberStyles.Integer);

                // Utils.GetString returns "0x" + the raw hash when nothing is registered.
                string resolved = Utils.GetString(hash);
                bool known = !string.IsNullOrEmpty(resolved) && resolved != "0x" + hash.ToString("x8");
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["hash"] = hash,
                    ["hash_hex"] = "0x" + unchecked((uint)hash).ToString("X8"),
                    ["string"] = resolved,
                    ["known"] = known
                };
            }
            catch (FormatException)
            {
                return McpHandlers.Error("hash must be a decimal int or 0x-prefixed hex", "INVALID_PARAMS");
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "RESOLVE_FAILED");
            }
        }

        public static object EncodePropertyFlags(JObject p)
        {
            // Mirror BlueprintEditor PropertyFlagsHelper / existing decode_connection_flags
            string realm = (p.Value<string>("realm") ?? "ClientAndServer").Trim();
            string propType = (p.Value<string>("prop_type") ?? p.Value<string>("property_type") ?? "Default").Trim();
            bool sourceCantBeStatic = p.Value<bool?>("source_cant_be_static") ?? false;

            uint realmBits = ParseRealmBits(realm);
            uint propBits = ParsePropTypeBits(propType);
            uint flags = realmBits | (propBits << 4);
            if (sourceCantBeStatic)
                flags |= 8;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["flags"] = flags,
                ["flags_hex"] = "0x" + flags.ToString("X8"),
                ["realm"] = realm,
                ["prop_type"] = propType,
                ["source_cant_be_static"] = sourceCantBeStatic
            };
        }

        public static object ListNodePorts(JObject p)
        {
            string typeName = (p.Value<string>("type") ?? p.Value<string>("component_type") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("type is required (e.g. DelayEntityData)", "INVALID_PARAMS");

            Type sdkType = TypeLibrary.GetType(typeName);
            if (sdkType == null)
                return McpHandlers.Error("Type not found in SDK: " + typeName, "TYPE_NOT_FOUND");

            var inputs = new List<object>();
            var outputs = new List<object>();
            string source = "sdk_properties";

            // Prefer BlueprintEditor EntityNode TypeMapping if loaded
            try
            {
                Type mgr = FindType("BlueprintEditorPlugin.ExtensionsManager");
                if (mgr != null)
                {
                    // Ensure initiated
                    try
                    {
                        MethodInfo initiate = mgr.GetMethod("Initiate", BindingFlags.Public | BindingFlags.Static);
                        initiate?.Invoke(null, null);
                    }
                    catch { }

                    PropertyInfo extProp = mgr.GetProperty("EntityNodeExtensions", BindingFlags.Public | BindingFlags.Static);
                    object dictObj = extProp?.GetValue(null);
                    if (dictObj is IDictionary dict && dict.Contains(sdkType.Name))
                    {
                        Type nodeType = dict[sdkType.Name] as Type;
                        if (nodeType != null)
                        {
                            object node = Activator.CreateInstance(nodeType);
                            // Minimal setup: Object + OnCreation
                            object entity = TypeLibrary.CreateObject(sdkType.Name);
                            PropertyInfo objPi = node.GetType().GetProperty("Object")
                                ?? node.GetType().GetProperty("Object", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            // EntityNode.Object may be on base
                            TrySetProp(node, "Object", entity);
                            MethodInfo onCreation = node.GetType().GetMethod("OnCreation", BindingFlags.Public | BindingFlags.Instance);
                            onCreation?.Invoke(node, null);

                            CollectPorts(node, "Inputs", inputs);
                            CollectPorts(node, "Outputs", outputs);
                            source = "EntityNode." + nodeType.Name;
                        }
                    }

                    // EntityMappingNode fallback (.nmc)
                    if (inputs.Count == 0 && outputs.Count == 0)
                    {
                        Type mapNode = FindType("BlueprintEditorPlugin.Editors.BlueprintEditor.Nodes.EntityMappingNode");
                        PropertyInfo maps = mapNode?.GetProperty("EntityMappings", BindingFlags.Public | BindingFlags.Static);
                        object mapDict = maps?.GetValue(null);
                        if (mapDict is IDictionary md && md.Contains(sdkType.Name))
                        {
                            source = "EntityMappingNode";
                            // Mapping values vary; expose raw
                            return new Dictionary<string, object>
                            {
                                ["success"] = true,
                                ["type"] = sdkType.Name,
                                ["source"] = source,
                                ["mapping"] = md[sdkType.Name]?.ToString(),
                                ["hint"] = "Has .nmc mapping; open Blueprint Editor for full ports or use research_component_usage"
                            };
                        }
                    }
                }
            }
            catch { }

            if (inputs.Count == 0 && outputs.Count == 0)
            {
                // SDK property dump as fallback "ports"
                foreach (PropertyInfo pi in sdkType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (pi.GetIndexParameters().Length > 0) continue;
                    if (pi.Name == "___Guid" || pi.Name == "TransientId" || pi.Name == "Flags") continue;
                    outputs.Add(new Dictionary<string, object>
                    {
                        ["name"] = pi.Name,
                        ["type"] = pi.PropertyType.Name,
                        ["direction"] = "property"
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["type"] = sdkType.Name,
                ["source"] = source,
                ["input_count"] = inputs.Count,
                ["output_count"] = outputs.Count,
                ["inputs"] = inputs,
                ["outputs"] = outputs
            };
        }

        public static object ListRegisteredNodeTypes(JObject p)
        {
            string query = (p.Value<string>("query") ?? "").Trim();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);

            var names = new List<string>();
            try
            {
                Type mgr = FindType("BlueprintEditorPlugin.ExtensionsManager");
                if (mgr != null)
                {
                    try { mgr.GetMethod("Initiate", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null); } catch { }
                    PropertyInfo extProp = mgr.GetProperty("EntityNodeExtensions", BindingFlags.Public | BindingFlags.Static);
                    if (extProp?.GetValue(null) is IDictionary dict)
                    {
                        foreach (object key in dict.Keys)
                            names.Add(key.ToString());
                    }
                }

                Type mapNode = FindType("BlueprintEditorPlugin.Editors.BlueprintEditor.Nodes.EntityMappingNode");
                PropertyInfo maps = mapNode?.GetProperty("EntityMappings", BindingFlags.Public | BindingFlags.Static);
                if (maps?.GetValue(null) is IDictionary md)
                {
                    foreach (object key in md.Keys)
                    {
                        string k = key.ToString();
                        if (!names.Contains(k)) names.Add(k);
                    }
                }
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex) + " (is FrostyBlueprintEditor loaded?)", "PLUGIN_MISSING");
            }

            if (!string.IsNullOrEmpty(query))
                names = names.Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);

            var page = names.Skip(offset).Take(limit).Select(n => (object)new Dictionary<string, object> { ["name"] = n }).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = names.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["types"] = page
            };
        }

        public static object ValidateConnections(JObject p)
        {
            if (!McpBlueprintHandlersTryLoad(p, out EbxAssetEntry entry, out dynamic root, out object err))
                return err;

            if (!HasConnectionArrays(root))
                return McpHandlers.Error("Asset root type is not a blueprint (no connection arrays)", "NOT_A_BLUEPRINT");

            var issues = new List<object>();
            var scanErrors = new List<object>();
            int propCount = 0, eventCount = 0, linkCount = 0;

            try
            {
                for (int i = 0; i < root.PropertyConnections.Count; i++)
                {
                    propCount++;
                    dynamic c = root.PropertyConnections[i];
                    CheckPointer(c.Source, "property", i, "source", issues);
                    CheckPointer(c.Target, "property", i, "target", issues);
                    string sf = "", tf = "";
                    try { sf = (string)(FrostySdk.Ebx.CString)c.SourceField; } catch { }
                    try { tf = (string)(FrostySdk.Ebx.CString)c.TargetField; } catch { }
                    if (string.IsNullOrEmpty(sf) || string.IsNullOrEmpty(tf))
                    {
                        issues.Add(new Dictionary<string, object>
                        {
                            ["kind"] = "property",
                            ["index"] = i,
                            ["issue"] = "empty_field",
                            ["source_field"] = sf,
                            ["target_field"] = tf
                        });
                    }
                    CheckFlagBits(c, "property", i, issues);
                }
            }
            catch (Exception ex)
            {
                scanErrors.Add(new Dictionary<string, object> { ["section"] = "property", ["error"] = Unwrap(ex) });
            }

            try
            {
                for (int i = 0; i < root.EventConnections.Count; i++)
                {
                    eventCount++;
                    dynamic c = root.EventConnections[i];
                    CheckPointer(c.Source, "event", i, "source", issues);
                    CheckPointer(c.Target, "event", i, "target", issues);
                }
            }
            catch (Exception ex)
            {
                scanErrors.Add(new Dictionary<string, object> { ["section"] = "event", ["error"] = Unwrap(ex) });
            }

            try
            {
                for (int i = 0; i < root.LinkConnections.Count; i++)
                {
                    linkCount++;
                    dynamic c = root.LinkConnections[i];
                    CheckPointer(c.Source, "link", i, "source", issues);
                    CheckPointer(c.Target, "link", i, "target", issues);
                }
            }
            catch (Exception ex)
            {
                scanErrors.Add(new Dictionary<string, object> { ["section"] = "link", ["error"] = Unwrap(ex) });
            }

            return new Dictionary<string, object>
            {
                // A failed section scan means the asset was not fully checked, so
                // this must not look like a clean pass.
                ["success"] = scanErrors.Count == 0,
                ["error"] = scanErrors.Count == 0 ? null : "One or more connection sections could not be read",
                ["error_code"] = scanErrors.Count == 0 ? null : "SCAN_INCOMPLETE",
                ["name"] = entry.Name,
                ["property_count"] = propCount,
                ["event_count"] = eventCount,
                ["link_count"] = linkCount,
                ["issue_count"] = issues.Count,
                ["issues"] = issues,
                ["scan_error_count"] = scanErrors.Count,
                ["scan_errors"] = scanErrors
            };
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static bool TryResolveBundlePair(JObject p, out EbxAssetEntry entry, out BundleEntry bundle, out object error)
        {
            entry = null;
            bundle = null;
            error = null;
            if (App.AssetManager == null)
            {
                error = McpHandlers.Error("AssetManager not ready", "NOT_READY");
                return false;
            }
            entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
            {
                error = McpHandlers.Error("EBX not found", "NOT_FOUND");
                return false;
            }
            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
            {
                error = McpHandlers.Error("bundle is required", "INVALID_PARAMS");
                return false;
            }
            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
            {
                error = McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");
                return false;
            }
            bundle = App.AssetManager.GetBundleEntry(bid);
            return true;
        }

        private static Type GetBundleEditorsType()
        {
            return FindType("AdvancedBundleEditorPlugin.BundleEditors");
        }

        private static object FallbackSmartAdd(EbxAssetEntry entry, BundleEntry bundle, JObject p)
        {
            var args = new JObject
            {
                ["name"] = entry.Name,
                ["guid"] = entry.Guid.ToString(),
                ["bundle"] = bundle.Name
            };
            return McpExtraHandlers.AddToBundleSmart(args);
        }

        private static void RecurseDependencies(EbxAssetEntry root, Action<EbxAssetEntry> action)
        {
            var queue = new List<Guid> { root.Guid };
            queue.AddRange(root.EnumerateDependencies());
            var seen = new HashSet<Guid>();
            while (queue.Count > 0)
            {
                Guid g = queue[0];
                queue.RemoveAt(0);
                if (!seen.Add(g)) continue;
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(g);
                if (e == null) continue;
                action(e);
                queue.AddRange(e.EnumerateDependencies());
            }
        }

        private static object InvokeStatic(Type type, string method, params object[] args)
        {
            MethodInfo mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null)
                throw new MissingMethodException(type.FullName, method);
            try
            {
                return mi.Invoke(null, args);
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
        }

        private static bool McpBlueprintHandlersTryLoad(JObject p, out EbxAssetEntry entry, out dynamic root, out object error)
        {
            entry = null;
            root = null;
            error = null;
            if (App.AssetManager == null)
            {
                error = McpHandlers.Error("AssetManager not ready", "NOT_READY");
                return false;
            }
            entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
            {
                error = McpHandlers.Error("EBX not found", "NOT_FOUND");
                return false;
            }
            var asset = App.AssetManager.GetEbx(entry);
            root = asset.RootObject;
            return true;
        }

        private static bool HasConnectionArrays(dynamic root)
        {
            Type t = ((object)root).GetType();
            return t.GetProperty("PropertyConnections") != null
                && t.GetProperty("EventConnections") != null
                && t.GetProperty("LinkConnections") != null;
        }

        private static void CheckFlagBits(dynamic connection, string kind, int index, List<object> issues)
        {
            int flags = 0;
            try { flags = (int)connection.Flags; } catch { return; }
            // realm is bits 0-2; bit 3 is source_cant_be_static.
            int realm = flags & 0x7;
            int propType = (flags >> 4) & 0x3;
            if (realm < 0 || realm > 5)
            {
                issues.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["index"] = index,
                    ["issue"] = "invalid_realm",
                    ["realm_bits"] = realm
                });
            }
            if (propType == 3)
            {
                issues.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["index"] = index,
                    ["issue"] = "invalid_prop_type",
                    ["prop_type_bits"] = propType
                });
            }
        }

        private static void CheckPointer(PointerRef pr, string kind, int index, string side, List<object> issues)
        {
            if (pr.Type == FrostySdk.IO.PointerRefType.Null)
            {
                issues.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["index"] = index,
                    ["side"] = side,
                    ["issue"] = "null_pointer"
                });
            }
        }

        private static void CollectPorts(object node, string propName, List<object> dest)
        {
            try
            {
                PropertyInfo pi = node.GetType().GetProperty(propName);
                object col = pi?.GetValue(node);
                if (!(col is IEnumerable en)) return;
                foreach (object port in en)
                {
                    string name = null;
                    string dir = null;
                    string ptype = null;
                    try { name = port.GetType().GetProperty("Name")?.GetValue(port)?.ToString(); } catch { }
                    try { dir = port.GetType().GetProperty("Direction")?.GetValue(port)?.ToString(); } catch { }
                    try { ptype = port.GetType().GetProperty("Type")?.GetValue(port)?.ToString(); } catch { }
                    if (string.IsNullOrEmpty(name))
                        try { name = port.GetType().GetProperty("Title")?.GetValue(port)?.ToString(); } catch { }
                    dest.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["direction"] = dir,
                        ["port_type"] = ptype,
                        ["clr_type"] = port.GetType().Name
                    });
                }
            }
            catch { }
        }

        private static void TrySetProp(object obj, string name, object value)
        {
            Type t = obj.GetType();
            while (t != null)
            {
                PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (pi == null)
                    pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (pi != null && pi.CanWrite)
                {
                    pi.SetValue(obj, value);
                    return;
                }
                // Field?
                FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) { fi.SetValue(obj, value); return; }
                t = t.BaseType;
            }
        }

        private static uint ParseRealmBits(string realm)
        {
            string r = realm.Replace("Realm_", "");
            switch (r.ToLowerInvariant())
            {
                case "clientandserver": return 1;
                case "client": return 2;
                case "server": return 3;
                case "networkedclient": return 4;
                case "networkedclientandserver": return 5;
                case "invalid": return 0;
                default: return 1;
            }
        }

        private static uint ParsePropTypeBits(string propType)
        {
            string t = propType.Replace("PropertyType_", "").Replace("PropType_", "");
            switch (t.ToLowerInvariant())
            {
                case "default": return 0;
                case "interface": return 1;
                case "exposed":
                case "dynamic": return 2;
                case "invalid": return 3;
                default: return 0;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static string Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
                return tie.InnerException.Message;
            return ex.Message;
        }
    }
}
