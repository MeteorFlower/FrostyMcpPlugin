using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// BF1DevPlugin-aligned helpers: NetworkRegistry, root Flags, safe/map bundles,
    /// Converts an internal pointer reference to an external one for NetReg lists.
    /// </summary>
    internal static class McpBf1Handlers
    {
        // BF1 MpSoldier-aligned map bundles (Core2.AllMapBundleUrlDic values, lowercase and without
    // the win32/ prefix; a win32/ retry follows when the bare name is not found)
        // Keep in sync with Tools/BF1DevPlugin/Base/Core2.cs; content paths must stay as content.
        private static readonly string[] Bf1MapBundleNames =
        {
            "levels/frontend/soldierpresentationsublevel",
            "levels/mp/mp_amiens/mp_amiens",
            "levels/mp/mp_chateau/mp_chateau",
            "levels/mp/mp_desert/mp_desert",
            "levels/mp/mp_faofortress/mp_faofortress",
            "levels/mp/mp_forest/mp_forest",
            "levels/mp/mp_italiancoast/mp_italiancoast",
            "levels/mp/mp_mountainfort/mp_mountainfort",
            "levels/mp/mp_scar/mp_scar",
            "levels/mp/mp_suez/mp_suez",
            "xpack0/levels/mp/mp_giant/content",
            "xpack1-3/levels/mp_shoveltown/mp_shoveltown",
            "xpack1-3/levels/mp_trench/mp_trench",
            "xpack1/levels/mp_fields/mp_fields",
            "xpack1/levels/mp_graveyard/mp_graveyard",
            "xpack1/levels/mp_underworld/mp_underworld",
            "xpack1/levels/mp_verdun/mp_verdun",
            "xpack2/levels/mp/mp_bridge/mp_bridge",
            "xpack2/levels/mp/mp_islands/mp_islands",
            "xpack2/levels/mp/mp_ravines/mp_ravines",
            "xpack2/levels/mp/mp_tsaritsyn/mp_tsaritsyn",
            "xpack2/levels/mp/mp_valley/mp_valley",
            "xpack2/levels/mp/mp_volga/mp_volga",
            "xpack3/levels/mp/mp_beachhead/mp_beachhead",
            "xpack3/levels/mp/mp_harbor/mp_harbor",
            "xpack3/levels/mp/mp_naval/mp_naval",
            "xpack3/levels/mp/mp_ridge/mp_ridge",
            "xpack4/levels/mp/mp_alps/content",
            "xpack4/levels/mp/mp_blitz/content",
            "xpack4/levels/mp/mp_hell/mp_hell",
            "xpack4/levels/mp/mp_london/content",
            "xpack4/levels/mp/mp_offensive/mp_offensive",
            "xpack4/levels/mp/mp_river/mp_river"
        };

        public static object SetRootFlags(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            if (p["flags"] == null)
                return McpHandlers.Error("flags is required (e.g. 15 for SpatialPrefabBlueprint)", "INVALID_PARAMS");

            ushort flags;
            string flagText = p["flags"].ToString().Trim();
            try
            {
                flags = flagText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt16(flagText.Substring(2), 16)
                    : Convert.ToUInt16(flagText);
            }
            catch
            {
                return McpHandlers.Error("flags must be a decimal or 0x-prefixed 16-bit integer", "INVALID_PARAMS");
            }

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                root.Flags = flags;
                App.AssetManager.ModifyEbx(entry.Name, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["flags"] = flags,
                    ["hint"] = "BF1 SpatialPrefabBlueprint root must be 15"
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FAILED");
            }
        }

        public static object GetInternalPointerRef(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string classGuidStr = (p.Value<string>("class_guid") ?? p.Value<string>("component_guid") ?? "").Trim();
            bool useRoot = p.Value<bool?>("root") ?? string.IsNullOrEmpty(classGuidStr);

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                Guid classGuid;
                string typeName = asset.RootObject.GetType().Name;

                if (useRoot)
                {
                    classGuid = asset.RootInstanceGuid;
                }
                else
                {
                    if (!Guid.TryParse(classGuidStr, out classGuid))
                        return McpHandlers.Error("class_guid invalid", "INVALID_PARAMS");

                    object found = null;
                    foreach (object obj in asset.ExportedObjects)
                    {
                        try
                        {
                            AssetClassGuid acg = ((dynamic)obj).GetInstanceGuid();
                            if (acg.ExportedGuid == classGuid)
                            {
                                found = obj;
                                typeName = obj.GetType().Name;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (found == null)
                        return McpHandlers.Error("Exported object with class_guid not found", "NOT_FOUND");
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["type"] = typeName,
                    ["file_guid"] = entry.Guid.ToString(),
                    ["class_guid"] = classGuid.ToString(),
                    ["asset_file_guid"] = asset.FileGuid.ToString(),
                    ["guid_invariant_ok"] = asset.FileGuid == entry.Guid,
                    ["pointer"] = new Dictionary<string, object>
                    {
                        ["file_guid"] = entry.Guid.ToString(),
                        ["class_guid"] = classGuid.ToString()
                    },
                    ["hint"] = "Pointer is ready for a NetworkRegistry; never register the MpSoldierAI root"
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "RESOLVE_FAILED");
            }
        }

        public static object ListNetworkRegistry(JObject p)
        {
            if (!TryLoadNetReg(p, out EbxAssetEntry entry, out EbxAsset asset, out dynamic root, out object err))
                return err;

            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);
            string query = (p.Value<string>("query") ?? "").Trim();

            // With no query the row is serialized only inside the page window; with a
            // query the fields are needed to test it, so those rows are built first.
            int total = 0;
            var page = new List<object>(limit);
            string queryLower = query.ToLowerInvariant();
            int i = 0;
            foreach (PointerRef pr in root.Objects)
            {
                int slot = i++;
                object item = null;
                if (!string.IsNullOrEmpty(query))
                {
                    var candidate = SerializeNetRef(pr, slot);
                    candidate.TryGetValue("file_guid", out object fg);
                    candidate.TryGetValue("class_guid", out object cg);
                    candidate.TryGetValue("asset_name", out object an);
                    candidate.TryGetValue("type", out object ty);
                    string hay = (fg + " " + cg + " " + an + " " + ty).ToLowerInvariant();
                    if (hay.IndexOf(queryLower, StringComparison.Ordinal) < 0)
                        continue;
                    item = candidate;
                }

                int index = total++;
                if (index < offset || page.Count >= limit)
                    continue;
                page.Add(item ?? SerializeNetRef(pr, slot));
            }
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["total"] = total,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["objects"] = page
            };
        }

        public static object AddToNetworkRegistry(JObject p)
        {
            if (!TryLoadNetReg(p, out EbxAssetEntry entry, out EbxAsset asset, out dynamic root, out object err))
                return err;

            List<PointerRef> toAdd = ParsePointerList(p, out string parseError);
            if (parseError != null)
                return McpHandlers.Error(parseError, "INVALID_PARAMS");
            if (toAdd.Count == 0)
                return McpHandlers.Error("Provide refs_json / file_guid+class_guid / asset name(+class_guid)", "INVALID_PARAMS");

            var added = new List<object>();
            var skipped = new List<object>();
            bool addedMpSoldierAi = false;

            try
            {
                foreach (PointerRef pr in toAdd)
                {
                    if (pr.Type != FrostySdk.IO.PointerRefType.External)
                    {
                        skipped.Add(new Dictionary<string, object> { ["reason"] = "not_external", ["ref"] = SerializeNetRef(pr, -1) });
                        continue;
                    }
                    if (ContainsPointer(root.Objects, pr))
                    {
                        skipped.Add(new Dictionary<string, object> { ["reason"] = "already_present", ["ref"] = SerializeNetRef(pr, -1) });
                        continue;
                    }
                    root.Objects.Add(pr);
                    try { asset.AddDependency(pr.External.FileGuid); } catch { }
                    EbxAssetEntry addedEntry = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    if (addedEntry != null &&
                        (addedEntry.Name.IndexOf("mpsoldierai", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         addedEntry.Type.IndexOf("MpSoldierAI", StringComparison.OrdinalIgnoreCase) >= 0))
                        addedMpSoldierAi = true;
                    added.Add(SerializeNetRef(pr, -1));
                }

                App.AssetManager.ModifyEbx(entry.Name, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["registry"] = entry.Name,
                    ["added_count"] = added.Count,
                    ["skipped_count"] = skipped.Count,
                    ["added"] = added,
                    ["skipped"] = skipped,
                    ["warning"] = addedMpSoldierAi && IsBattlefield1()
                        ? "Do not register MpSoldierAI; it crashes Giant's Shadow"
                        : null
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        public static object RemoveFromNetworkRegistry(JObject p)
        {
            if (!TryLoadNetReg(p, out EbxAssetEntry entry, out EbxAsset asset, out dynamic root, out object err))
                return err;

            List<PointerRef> targets = ParsePointerList(p, out string parseError);
            if (parseError != null)
                return McpHandlers.Error(parseError, "INVALID_PARAMS");
            if (targets.Count == 0 && p["index"] == null)
                return McpHandlers.Error("Provide refs or index", "INVALID_PARAMS");

            var indices = new List<int>();
            var removedPlan = new List<object>();
            try
            {
                if (p["index"] != null)
                {
                    int idx = Convert.ToInt32(p["index"].ToString());
                    if (idx < 0 || idx >= root.Objects.Count)
                        return McpHandlers.Error("index out of range", "INVALID_PARAMS");
                    indices.Add(idx);
                    removedPlan.Add(SerializeNetRef(root.Objects[idx], idx));
                }
                else
                {
                    for (int i = 0; i < root.Objects.Count; i++)
                    {
                        PointerRef pr = root.Objects[i];
                        foreach (PointerRef t in targets)
                        {
                            if (PointerEquals(pr, t))
                            {
                                indices.Add(i);
                                removedPlan.Add(SerializeNetRef(pr, i));
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }

            if (indices.Count == 0)
                return McpHandlers.Error("No matching entries in this registry", "NOT_FOUND");

            var plan = new Dictionary<string, object>
            {
                ["registry"] = entry.Name,
                ["removed_count"] = indices.Count,
                ["removed"] = removedPlan
            };

            return McpCommit.Require("remove_from_network_registry", p, plan, () =>
            {
                try
                {
                    var removed = new List<object>();
                    for (int i = indices.Count - 1; i >= 0; i--)
                    {
                        int idx = indices[i];
                        if (idx < 0 || idx >= root.Objects.Count)
                            continue;
                        removed.Add(SerializeNetRef(root.Objects[idx], idx));
                        root.Objects.RemoveAt(idx);
                    }
                    App.AssetManager.ModifyEbx(entry.Name, asset);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["registry"] = entry.Name,
                        ["removed_count"] = removed.Count,
                        ["removed"] = removed
                    };
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
                }
            });
        }

        public static object SafeAddToBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");

            bool hadTombstone = entry.RemBundles.Contains(bid);
            bool added = McpHandlers.AddToBundleExportable(entry, bid);
            bool already = !added && entry.IsInBundle(bid);

            // Touch EBX so export picks up bundle changes (BF1DevPlugin General.cs warning).
            // Only when something actually changed: ModifyEbx marks the asset dirty,
            // which would rewrite an untouched asset into the mod.
            if (added || hadTombstone)
            {
                try
                {
                    EbxAsset asset = App.AssetManager.GetEbx(entry);
                    App.AssetManager.ModifyEbx(entry.Name, asset);
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["bundle"] = bundleName,
                ["bundle_id"] = bid,
                ["already_in_bundle"] = already,
                ["added"] = added
            };
        }

        public static object AddToBf1MapBundles(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            bool modify = p.Value<bool?>("modify_ebx") ?? true;
            var added = new List<object>();
            var skipped = new List<object>();
            var missing = new List<string>();

            foreach (string raw in Bf1MapBundleNames)
            {
                string name = raw.ToLowerInvariant();
                int bid = App.AssetManager.GetBundleId(name);
                if (bid < 0)
                {
                    // try with win32/ prefix
                    bid = App.AssetManager.GetBundleId("win32/" + name);
                    if (bid >= 0) name = "win32/" + name;
                }
                if (bid < 0)
                {
                    missing.Add(raw);
                    continue;
                }

                if (entry.Bundles.Contains(bid) || entry.AddedBundles.Contains(bid))
                {
                    skipped.Add(new Dictionary<string, object> { ["bundle"] = name, ["bundle_id"] = bid });
                    continue;
                }

                McpHandlers.AddToBundleExportable(entry, bid);
                added.Add(new Dictionary<string, object> { ["bundle"] = name, ["bundle_id"] = bid });
            }

            if (modify)
            {
                try
                {
                    EbxAsset asset = App.AssetManager.GetEbx(entry);
                    App.AssetManager.ModifyEbx(entry.Name, asset);
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["added_count"] = added.Count,
                ["skipped_count"] = skipped.Count,
                ["missing_count"] = missing.Count,
                ["added"] = added,
                ["skipped"] = skipped.Take(50).ToList(),
                ["missing"] = missing,
                ["warning"] = "Keep content bundles as content (mp_giant/content etc). Do not swap to map name."
            };
        }

        public static object ListBf1MapBundles(JObject p)
        {
            var items = new List<object>();
            foreach (string raw in Bf1MapBundleNames)
            {
                string name = raw.ToLowerInvariant();
                int bid = App.AssetManager != null ? App.AssetManager.GetBundleId(name) : -1;
                if (bid < 0 && App.AssetManager != null)
                    bid = App.AssetManager.GetBundleId("win32/" + name);
                items.Add(new Dictionary<string, object>
                {
                    ["name"] = raw,
                    ["bundle_id"] = bid,
                    ["found"] = bid >= 0
                });
            }
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = items.Count,
                ["bundles"] = items,
                ["source"] = "BF1DevPlugin.Base.Core2.AllMapBundleUrlDic"
            };
        }

        // ---------------------------------------------------------------------

        private static bool TryLoadNetReg(JObject p, out EbxAssetEntry entry, out EbxAsset asset, out dynamic root, out object error)
        {
            entry = null;
            asset = null;
            root = null;
            error = null;
            if (App.AssetManager == null)
            {
                error = McpHandlers.Error("AssetManager not ready", "NOT_READY");
                return false;
            }

            string regName = (p.Value<string>("registry") ?? p.Value<string>("netreg") ?? p.Value<string>("name") ?? "").Trim();
            string regGuid = p.Value<string>("registry_guid") ?? p.Value<string>("guid");
            // If both asset target and registry provided, prefer registry key
            if (!string.IsNullOrEmpty(p.Value<string>("registry")) || !string.IsNullOrEmpty(p.Value<string>("netreg")))
            {
                entry = McpHandlers.ResolveEbxEntry(p.Value<string>("registry") ?? p.Value<string>("netreg"), p.Value<string>("registry_guid"));
            }
            else
            {
                entry = McpHandlers.ResolveEbxEntry(regName, regGuid);
            }

            if (entry == null)
            {
                error = McpHandlers.Error("NetworkRegistry EBX not found (pass registry=..._networkregistry_Win32)", "NOT_FOUND");
                return false;
            }

            asset = App.AssetManager.GetEbx(entry);
            root = asset.RootObject;
            if (!TypeLibraryIsNetworkRegistry(root))
            {
                // Not a NetworkRegistryAsset; accept it when it still exposes an Objects list.
                try { var _ = root.Objects; }
                catch
                {
                    error = McpHandlers.Error("Asset has no Objects list (not a NetworkRegistryAsset?)", "INVALID_TYPE");
                    return false;
                }
            }
            return true;
        }

        private static bool TypeLibraryIsNetworkRegistry(object root)
        {
            try
            {
                string n = root.GetType().Name;
                return n.IndexOf("NetworkRegistry", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool IsBattlefield1()
        {
            string profile = FrostySdk.ProfilesLibrary.ProfileName ?? "";
            return profile.IndexOf("Battlefield1", StringComparison.OrdinalIgnoreCase) >= 0
                || profile.IndexOf("Battlefield 1", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<PointerRef> ParsePointerList(JObject p, out string error)
        {
            error = null;
            var list = new List<PointerRef>();

            // refs_json: [{"file_guid":"...","class_guid":"..."}, ...]
            JToken refs = p["refs"] ?? p["pointers"];
            if (refs is JArray arr)
            {
                foreach (JToken t in arr)
                    TryAddRefToken(t, list);
            }
            else if (!string.IsNullOrWhiteSpace(p.Value<string>("refs_json")))
            {
                try
                {
                    var parsed = JArray.Parse(p.Value<string>("refs_json"));
                    foreach (JToken t in parsed)
                        TryAddRefToken(t, list);
                }
                catch { }
            }

            // Single file_guid + class_guid
            string fg = (p.Value<string>("file_guid") ?? "").Trim();
            string cg = (p.Value<string>("class_guid") ?? "").Trim();
            if (!string.IsNullOrEmpty(fg) && !string.IsNullOrEmpty(cg) &&
                Guid.TryParse(fg, out Guid fileG) && Guid.TryParse(cg, out Guid classG))
            {
                list.Add(new PointerRef(new EbxImportReference { FileGuid = fileG, ClassGuid = classG }));
            }

            // asset_name with an optional class_guid, then resolve
            string assetName = (p.Value<string>("asset") ?? p.Value<string>("asset_name") ?? p.Value<string>("target") ?? "").Trim();
            string assetGuid = p.Value<string>("asset_guid");
            if (!string.IsNullOrEmpty(assetName) || !string.IsNullOrEmpty(assetGuid))
            {
                EbxAssetEntry ae = McpHandlers.ResolveEbxEntry(
                    string.IsNullOrEmpty(assetName) ? null : assetName,
                    assetGuid);
                if (ae != null)
                {
                    EbxAsset a = App.AssetManager.GetEbx(ae);
                    Guid classGuid = a.RootInstanceGuid;
                    string cgs = (p.Value<string>("asset_class_guid") ?? p.Value<string>("class_guid") ?? "").Trim();
                    if (!string.IsNullOrEmpty(cgs))
                    {
                        if (!Guid.TryParse(cgs, out Guid acg))
                        {
                            error = "class_guid invalid";
                            return list;
                        }
                        bool found = acg == a.RootInstanceGuid;
                        if (!found)
                        {
                            foreach (object obj in a.ExportedObjects)
                            {
                                try
                                {
                                    if (((dynamic)obj).GetInstanceGuid().ExportedGuid == acg)
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                        if (!found)
                        {
                            error = "class_guid " + acg + " is not an exported object of " + ae.Name;
                            return list;
                        }
                        classGuid = acg;
                    }
                    if (a.FileGuid != ae.Guid)
                    {
                        error = "GUID_INCONSISTENT: entry " + ae.Guid + ", file " + a.FileGuid;
                        return list;
                    }
                    list.Add(new PointerRef(new EbxImportReference { FileGuid = ae.Guid, ClassGuid = classGuid }));
                }
            }

            return list;
        }

        private static void TryAddRefToken(JToken t, List<PointerRef> list)
        {
            if (t == null || t.Type == JTokenType.Null) return;
            string fg = (t.Value<string>("file_guid") ?? t.Value<string>("FileGuid") ?? "").Trim();
            string cg = (t.Value<string>("class_guid") ?? t.Value<string>("ClassGuid") ?? "").Trim();
            if (Guid.TryParse(fg, out Guid fileG) && Guid.TryParse(cg, out Guid classG))
                list.Add(new PointerRef(new EbxImportReference { FileGuid = fileG, ClassGuid = classG }));
        }

        private static bool ContainsPointer(IEnumerable<PointerRef> objects, PointerRef pr)
        {
            foreach (PointerRef x in objects)
            {
                if (PointerEquals(x, pr)) return true;
            }
            return false;
        }

        private static bool PointerEquals(PointerRef a, PointerRef b)
        {
            if (a.Type != b.Type) return false;
            if (a.Type == FrostySdk.IO.PointerRefType.External)
                return a.External.FileGuid == b.External.FileGuid && a.External.ClassGuid == b.External.ClassGuid;
            if (a.Type == FrostySdk.IO.PointerRefType.Internal)
                return ReferenceEquals(a.Internal, b.Internal);
            return a.Type == FrostySdk.IO.PointerRefType.Null && b.Type == FrostySdk.IO.PointerRefType.Null;
        }

        private static Dictionary<string, object> SerializeNetRef(PointerRef pr, int index)
        {
            var d = new Dictionary<string, object>
            {
                ["index"] = index,
                ["pointer_type"] = pr.Type.ToString()
            };
            if (pr.Type == FrostySdk.IO.PointerRefType.External)
            {
                d["file_guid"] = pr.External.FileGuid.ToString();
                d["class_guid"] = pr.External.ClassGuid.ToString();
                EbxAssetEntry e = App.AssetManager?.GetEbxEntry(pr.External.FileGuid);
                if (e != null)
                {
                    d["asset_name"] = e.Name;
                    d["type"] = e.Type;
                }
            }
            else if (pr.Type == FrostySdk.IO.PointerRefType.Internal && pr.Internal != null)
            {
                d["type"] = pr.Internal.GetType().Name;
                try { d["class_guid"] = ((dynamic)pr.Internal).GetInstanceGuid().ExportedGuid.ToString(); } catch { }
            }
            return d;
        }
    }
}
