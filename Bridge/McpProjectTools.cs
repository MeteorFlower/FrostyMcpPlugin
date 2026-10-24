using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Project-level tools: bundle deletion, super bundle listing, project stats,
    /// batch field writes, modified-vs-vanilla diff, and dangling reference scan.
    /// </summary>
    internal static class McpProjectTools
    {
        public static object DeleteBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string name = (p.Value<string>("bundle") ?? p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = App.AssetManager.GetBundleId(name);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bid);
            if (be == null || !be.Added)
                return McpHandlers.Error("Bundle is not an added bundle; vanilla bundles cannot be deleted", "INVALID_STATE");

            bool clearMembers = p.Value<bool?>("clear_members") ?? true;
            int cleared = 0;
            if (clearMembers)
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("", true))
                    if (e.AddedBundles.Remove(bid)) cleared++;
                foreach (ResAssetEntry e in App.AssetManager.EnumerateRes(0, true))
                    if (e.AddedBundles.Remove(bid)) cleared++;
                foreach (ChunkAssetEntry e in App.AssetManager.EnumerateChunks(true))
                    if (e.AddedBundles.Remove(bid)) cleared++;
            }

            App.AssetManager.RevertBundle(be);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["bundle"] = name,
                ["bundle_id"] = bid,
                ["clear_members"] = clearMembers,
                ["cleared_members"] = cleared
            };
        }

        public static object ListSuperBundles(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            List<BundleEntry> bundles = App.AssetManager.EnumerateBundles().ToList();
            List<object> result = new List<object>();
            foreach (SuperBundleEntry sb in App.AssetManager.EnumerateSuperBundles())
            {
                int sbId = App.AssetManager.GetSuperBundleId(sb);
                result.Add(new Dictionary<string, object>
                {
                    ["name"] = sb.Name,
                    ["added"] = sb.Added,
                    ["bundle_count"] = bundles.Count(b => b.SuperBundleId == sbId)
                });
            }
            return new Dictionary<string, object> { ["success"] = true, ["count"] = result.Count, ["super_bundles"] = result };
        }

        public static object ProjectStats(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            int addedEbx = 0, modifiedEbx = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("", true))
            {
                if (e.IsAdded) addedEbx++; else modifiedEbx++;
            }
            int addedBundles = App.AssetManager.EnumerateBundles(BundleType.None, true).Count(b => b.Added);
            int addedRes = App.AssetManager.EnumerateRes(0, true).Count(r => r.IsAdded);
            int addedChunks = App.AssetManager.EnumerateChunks(true).Count(c => c.IsAdded);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["modified_count"] = App.AssetManager.GetModifiedCount(),
                ["dirty_count"] = App.AssetManager.GetDirtyCount(),
                ["ebx_total"] = App.AssetManager.GetEbxCount(),
                ["ebx_added"] = addedEbx,
                ["ebx_modified_existing"] = modifiedEbx,
                ["bundles_added"] = addedBundles,
                ["res_added"] = addedRes,
                ["chunks_added"] = addedChunks
            };
        }

        public static object BatchSetField(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            if (!(p["items"] is JArray items) || items.Count == 0)
                return McpHandlers.Error("items must be a non-empty JSON array", "INVALID_PARAMS");

            List<object> results = new List<object>();
            int ok = 0, failed = 0;
            foreach (JToken token in items)
            {
                if (!(token is JObject item))
                {
                    results.Add(new Dictionary<string, object> { ["success"] = false, ["error"] = "item is not an object" });
                    failed++;
                    continue;
                }

                object r;
                bool usesComponent = item["index"] != null || item["component_type"] != null;
                try
                {
                    Type handlerType = typeof(McpHandlers).Assembly.GetType("FrostyMcpPlugin.Bridge.McpBlueprintHandlers");
                    MethodInfo method = handlerType?.GetMethod(usesComponent ? "SetComponentProperty" : "SetEbxField",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method == null)
                    {
                        r = McpHandlers.Error("Field setters are not available in this build", "NOT_IMPLEMENTED");
                    }
                    else
                    {
                        r = method.Invoke(null, new object[] { item.DeepClone() });
                    }
                }
                catch (Exception ex)
                {
                    r = McpHandlers.Error(ex.Message, "SET_FAILED");
                }

                bool success = r is Dictionary<string, object> dict
                    && dict.TryGetValue("success", out object value) && value is bool b && b;
                if (success) ok++; else failed++;
                results.Add(new Dictionary<string, object>
                {
                    ["name"] = item.Value<string>("name"),
                    ["path"] = item.Value<string>("path"),
                    ["success"] = success,
                    ["result"] = r
                });
            }

            return new Dictionary<string, object>
            {
                ["success"] = failed == 0,
                ["ok_count"] = ok,
                ["failed_count"] = failed,
                ["results"] = results
            };
        }

        public static object DiffEbx(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            if (!entry.IsModified)
                return new Dictionary<string, object> { ["success"] = true, ["name"] = entry.Name, ["unchanged"] = true };

            EbxAsset modified = App.AssetManager.GetEbx(entry);
            EbxAsset original = App.AssetManager.GetEbx(entry, true);
            if (original == null)
                return McpHandlers.Error("No unmodified data available for this asset", "NO_VANILLA_DATA");

            string[] modLines = ToYamlLines(modified);
            string[] origLines = ToYamlLines(original);

            Dictionary<string, int> modCounts = CountLines(modLines);
            Dictionary<string, int> origCounts = CountLines(origLines);

            List<string> added = new List<string>();
            List<string> removed = new List<string>();
            foreach (KeyValuePair<string, int> kv in modCounts)
            {
                origCounts.TryGetValue(kv.Key, out int n);
                for (int i = 0; i < kv.Value - n; i++) added.Add(kv.Key);
            }
            foreach (KeyValuePair<string, int> kv in origCounts)
            {
                modCounts.TryGetValue(kv.Key, out int n);
                for (int i = 0; i < kv.Value - n; i++) removed.Add(kv.Key);
            }

            int cap = p.Value<int?>("limit") ?? 200;
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["added_count"] = added.Count,
                ["removed_count"] = removed.Count,
                ["added"] = added.Take(cap).ToList(),
                ["removed"] = removed.Take(cap).ToList(),
                ["truncated"] = added.Count > cap || removed.Count > cap
            };
        }

        public static object ValidateRefs(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            List<EbxAssetEntry> entries = new List<EbxAssetEntry>();
            if (p["names"] is JArray names && names.Count > 0)
            {
                foreach (JToken token in names)
                {
                    EbxAssetEntry e = McpHandlers.ResolveEbxEntry(token.ToString(), null);
                    if (e != null) entries.Add(e);
                }
            }
            else
            {
                entries.AddRange(App.AssetManager.EnumerateEbx("", true));
            }

            int cap = p.Value<int?>("limit") ?? 200;
            List<object> issues = new List<object>();
            int scanned = 0, truncatedAssets = 0;

            foreach (EbxAssetEntry entry in entries)
            {
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { continue; }
                if (asset == null) continue;
                scanned++;

                HashSet<Guid> exported = new HashSet<Guid>();
                foreach (object obj in asset.Objects)
                {
                    Guid g = GetInstanceGuid(obj);
                    if (g != Guid.Empty) exported.Add(g);
                }

                foreach (object obj in asset.Objects)
                {
                    string typeName = obj.GetType().Name;
                    WalkRefs(obj, obj.GetType(), "", entry, asset.FileGuid, exported, issues, cap, 0);
                    if (issues.Count >= cap) { truncatedAssets++; break; }
                }
                if (issues.Count >= cap) break;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["scanned_assets"] = scanned,
                ["issue_count"] = issues.Count,
                ["issues"] = issues,
                ["truncated"] = issues.Count >= cap
            };
        }

        private static void WalkRefs(object obj, Type type, string prefix, EbxAssetEntry entry, Guid selfFile,
            HashSet<Guid> exported, List<object> issues, int cap, int depth)
        {
            if (obj == null || depth > 4 || issues.Count >= cap) return;

            PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (PropertyInfo prop in props)
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object value;
                try { value = prop.GetValue(obj); }
                catch { continue; }
                if (value == null) continue;

                string path = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;

                if (value is PointerRef pref)
                {
                    CheckPointer(pref, entry, selfFile, exported, path, issues, cap);
                    continue;
                }

                string ns = value.GetType().Namespace ?? "";
                if (!ns.StartsWith("FrostySdk.Ebx")) continue;

                if (value is IEnumerable list && !(value is string))
                {
                    int i = 0;
                    foreach (object item in list)
                    {
                        if (item == null) { i++; continue; }
                        if (item is PointerRef itemRef)
                        {
                            CheckPointer(itemRef, entry, selfFile, exported, path + "[" + i + "]", issues, cap);
                        }
                        else if (item.GetType().Namespace != null && item.GetType().Namespace.StartsWith("FrostySdk.Ebx"))
                        {
                            WalkRefs(item, item.GetType(), path + "[" + i + "]", entry, selfFile, exported, issues, cap, depth + 1);
                        }
                        i++;
                    }
                    continue;
                }

                WalkRefs(value, value.GetType(), path, entry, selfFile, exported, issues, cap, depth + 1);
            }
        }

        private static void CheckPointer(PointerRef pref, EbxAssetEntry entry, Guid selfFile,
            HashSet<Guid> exported, string path, List<object> issues, int cap)
        {
            if (pref.Type == PointerRefType.Null) return;

            if (pref.Type == PointerRefType.Internal)
            {
                if (pref.Internal == null)
                    AddIssue(issues, cap, entry, path, "null_internal", Guid.Empty);
                return;
            }

            Guid fileGuid = pref.External.FileGuid;
            Guid classGuid = pref.External.ClassGuid;
            if (fileGuid == Guid.Empty) return;

            if (fileGuid == selfFile)
            {
                if (classGuid != Guid.Empty && !exported.Contains(classGuid))
                    AddIssue(issues, cap, entry, path, "self_ref_missing", classGuid);
                return;
            }

            if (App.AssetManager.GetEbxEntry(fileGuid) == null)
                AddIssue(issues, cap, entry, path, "external_missing", fileGuid);
        }

        private static void AddIssue(List<object> issues, int cap, EbxAssetEntry entry, string path, string kind, Guid guid)
        {
            if (issues.Count >= cap) return;
            issues.Add(new Dictionary<string, object>
            {
                ["asset"] = entry.Name,
                ["path"] = path,
                ["kind"] = kind,
                ["guid"] = guid == Guid.Empty ? "" : guid.ToString()
            });
        }

        private static Guid GetInstanceGuid(object obj)
        {
            try
            {
                dynamic dyn = obj;
                AssetClassGuid guid = dyn.GetInstanceGuid();
                return guid.ExportedGuid;
            }
            catch { return Guid.Empty; }
        }

        private static string[] ToYamlLines(EbxAsset asset)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (EbxYamlWriter writer = new EbxYamlWriter(ms, App.AssetManager))
                    writer.WriteObjects(asset.RootObjects);
                return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n").Split('\n');
            }
        }

        private static Dictionary<string, int> CountLines(string[] lines)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                string key = line.TrimEnd();
                if (key.Length == 0) continue;
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
            return counts;
        }

        public static object BundleClosureCheck(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string name = (p.Value<string>("bundle") ?? p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = App.AssetManager.GetBundleId(name);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bid);
            bool includeOutside = p.Value<bool?>("include_outside") ?? false;
            int cap = p.Value<int?>("limit") ?? 200;

            int assets = 0, depTotal = 0;
            List<object> missing = new List<object>();
            List<object> outside = new List<object>();

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx(be))
            {
                assets++;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { continue; }
                if (asset == null) continue;

                foreach (Guid dep in asset.Dependencies)
                {
                    depTotal++;
                    EbxAssetEntry depEntry = App.AssetManager.GetEbxEntry(dep);
                    if (depEntry == null)
                    {
                        if (missing.Count < cap)
                            missing.Add(new Dictionary<string, object> { ["asset"] = entry.Name, ["dep_guid"] = dep.ToString() });
                        continue;
                    }
                    if (!depEntry.IsInBundle(bid))
                    {
                        if (includeOutside && outside.Count < cap)
                            outside.Add(new Dictionary<string, object> { ["asset"] = entry.Name, ["dep"] = depEntry.Name });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["bundle"] = be.Name,
                ["assets"] = assets,
                ["dependency_count"] = depTotal,
                ["missing_count"] = missing.Count,
                ["missing"] = missing,
                ["outside_count"] = outside.Count,
                ["outside"] = outside,
                ["include_outside"] = includeOutside,
                ["truncated"] = missing.Count >= cap || outside.Count >= cap
            };
        }

        private static readonly string[] DefaultRegistries =
        {
            "gameplay/kits/bundling/characterssharedbundleasset_networkregistry_Win32",
            "installchunks/installed_characterssharedbundleasset_networkregistry_Win32"
        };

        public static object RegistryGap(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            List<string> assetNames = new List<string>();
            if (p["names"] is JArray names && names.Count > 0)
                assetNames.AddRange(names.Select(n => n.ToString()));
            else if (!string.IsNullOrWhiteSpace(p.Value<string>("name")))
                assetNames.Add(p.Value<string>("name"));

            if (assetNames.Count == 0)
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("", true))
                    if (e.IsAdded) assetNames.Add(e.Name);
            }

            List<string> registries = new List<string>();
            if (p["registries"] is JArray regs && regs.Count > 0)
                registries.AddRange(regs.Select(r => r.ToString()));
            else if (!string.IsNullOrWhiteSpace(p.Value<string>("registry")))
                registries.Add(p.Value<string>("registry"));
            else
                registries.AddRange(DefaultRegistries);

            Dictionary<string, HashSet<string>> regSets = new Dictionary<string, HashSet<string>>();
            foreach (string reg in registries)
            {
                HashSet<string> set = ReadRegistry(reg);
                if (set == null)
                    return McpHandlers.Error("NetworkRegistry not found: " + reg, "NOT_FOUND");
                regSets[reg] = set;
            }

            int cap = p.Value<int?>("limit") ?? 200;
            List<object> results = new List<object>();
            int totalMissing = 0;

            foreach (string assetName in assetNames)
            {
                EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(assetName, null);
                if (entry == null)
                {
                    results.Add(new Dictionary<string, object> { ["name"] = assetName, ["error"] = "not found" });
                    continue;
                }

                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch
                {
                    results.Add(new Dictionary<string, object> { ["name"] = entry.Name, ["error"] = "load failed" });
                    continue;
                }

                List<KeyValuePair<Guid, string>> exported = new List<KeyValuePair<Guid, string>>();
                foreach (object obj in asset.Objects)
                {
                    Guid guid = GetInstanceGuid(obj);
                    if (guid != Guid.Empty)
                        exported.Add(new KeyValuePair<Guid, string>(guid, obj.GetType().Name));
                }

                Dictionary<string, object> perRegistry = new Dictionary<string, object>();
                int assetMissing = 0;
                foreach (KeyValuePair<string, HashSet<string>> kv in regSets)
                {
                    List<object> missing = new List<object>();
                    foreach (KeyValuePair<Guid, string> ex in exported)
                    {
                        string key = entry.Guid.ToString() + "/" + ex.Key.ToString();
                        if (kv.Value.Contains(key)) continue;
                        assetMissing++;
                        totalMissing++;
                        if (missing.Count < cap)
                            missing.Add(new Dictionary<string, object>
                            {
                                ["class_guid"] = ex.Key.ToString(),
                                ["type"] = ex.Value
                            });
                    }
                    perRegistry[kv.Key] = new Dictionary<string, object>
                    {
                        ["missing_count"] = missing.Count,
                        ["missing"] = missing
                    };
                }

                results.Add(new Dictionary<string, object>
                {
                    ["name"] = entry.Name,
                    ["file_guid"] = entry.Guid.ToString(),
                    ["exported_count"] = exported.Count,
                    ["missing_total"] = assetMissing,
                    ["registries"] = perRegistry
                });
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["asset_count"] = assetNames.Count,
                ["missing_total"] = totalMissing,
                ["assets"] = results
            };
        }

        private static HashSet<string> ReadRegistry(string name)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
                entry = App.AssetManager.GetEbxEntry(name.ToLowerInvariant());
            if (entry == null)
                return null;

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null)
                return null;

            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                dynamic root = asset.RootObject;
                foreach (PointerRef pr in root.Objects)
                {
                    if (pr.Type != PointerRefType.External) continue;
                    set.Add(pr.External.FileGuid.ToString() + "/" + pr.External.ClassGuid.ToString());
                }
            }
            catch
            {
                return null;
            }
            return set;
        }
    }
}
