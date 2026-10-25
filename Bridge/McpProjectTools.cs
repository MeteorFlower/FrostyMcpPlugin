using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Managers.Entries;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

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

            int bid = McpHandlers.ResolveBundleId(name);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bid);
            if (be == null || !be.Added)
                return McpHandlers.Error("Bundle is not an added bundle; vanilla bundles cannot be deleted", "INVALID_STATE");

            // RevertBundle removes the entry from the bundle list, and bundle ids are
            // list indices: removing anything but the last entry renumbers every later
            // bundle and leaves asset references pointing at the wrong ones.
            int bundleCount = 0;
            foreach (BundleEntry unused in App.AssetManager.EnumerateBundles())
                bundleCount++;
            if (bid != bundleCount - 1)
                return McpHandlers.Error("Only the most recently created bundle can be deleted; removing this one would renumber later bundles", "NOT_LAST_BUNDLE");

            if (p.Value<bool?>("clear_members") == false)
                return McpHandlers.Error("clear_members=false is not supported for deletion", "INVALID_PARAMS");

            int memberCount = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("", true))
                if (e.AddedBundles.Contains(bid)) memberCount++;
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes(0, true))
                if (e.AddedBundles.Contains(bid)) memberCount++;
            foreach (ChunkAssetEntry e in App.AssetManager.EnumerateChunks(true))
                if (e.AddedBundles.Contains(bid)) memberCount++;

            var plan = new Dictionary<string, object>
            {
                ["bundle"] = be.Name,
                ["bundle_id"] = bid,
                ["type"] = be.Type.ToString(),
                ["members_to_clear"] = memberCount
            };

            return McpCommit.Require("delete_bundle", p, plan, () =>
            {
                int cleared = 0;
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("", true))
                    if (e.AddedBundles.Remove(bid)) cleared++;
                foreach (ResAssetEntry e in App.AssetManager.EnumerateRes(0, true))
                    if (e.AddedBundles.Remove(bid)) cleared++;
                foreach (ChunkAssetEntry e in App.AssetManager.EnumerateChunks(true))
                    if (e.AddedBundles.Remove(bid)) cleared++;

                App.AssetManager.RevertBundle(be);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["bundle"] = name,
                    ["bundle_id"] = bid,
                    ["cleared_members"] = cleared
                };
            });
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
            int modifiedRes = App.AssetManager.EnumerateRes(0, true).Count();
            int modifiedChunks = App.AssetManager.EnumerateChunks(true).Count();
            int addedBundles = App.AssetManager.EnumerateBundles(BundleType.None, true).Count(b => b.Added);
            int addedRes = App.AssetManager.EnumerateRes(0, true).Count(r => r.IsAdded);
            int addedChunks = App.AssetManager.EnumerateChunks(true).Count(c => c.IsAdded);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                // Every figure below is counted from the same enumerations, so the
                // breakdown always adds up to modified_count.
                ["modified_count"] = (addedEbx + modifiedEbx) + modifiedRes + modifiedChunks,
                ["modified_ebx"] = addedEbx + modifiedEbx,
                ["modified_res"] = modifiedRes,
                ["modified_chunks"] = modifiedChunks,
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

                var row = new Dictionary<string, object>
                {
                    ["name"] = item.Value<string>("name"),
                    ["path"] = item.Value<string>("path"),
                    ["success"] = success
                };
                if (!success && r is Dictionary<string, object> errDict)
                {
                    if (errDict.TryGetValue("error", out object msg)) row["error"] = msg;
                    if (errDict.TryGetValue("error_code", out object code)) row["error_code"] = code;
                }
                results.Add(row);
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

            try
            {
                // Reading the vanilla copy temporarily hides ModifiedEntry from every
                // other thread, so serialize the whole diff with the editor UI thread.
                return McpHandlers.InvokeOnUi(() => DiffEbxCore(p, entry));
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "DIFF_FAILED");
            }
        }

        private static object DiffEbxCore(JObject p, EbxAssetEntry entry)
        {
            EbxAsset modified = App.AssetManager.GetEbx(entry);

            // getUnmodifiedData only bypasses ModifiedEntry.DataObject; saved edits
            // live in ModifiedEntry.Data, which GetAsset prefers. Hide the entry for
            // that single read, and only when the saved bytes would shadow vanilla.
            EbxAsset original;
            if (entry.ModifiedEntry?.Data != null)
            {
                ModifiedAssetEntry stash = entry.ModifiedEntry;
                entry.ModifiedEntry = null;
                try
                {
                    original = App.AssetManager.GetEbx(entry);
                }
                finally
                {
                    entry.ModifiedEntry = stash;
                }
            }
            else
            {
                original = App.AssetManager.GetEbx(entry, true);
            }
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
            int scanned = 0;

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
                    WalkRefs(obj, obj.GetType(), "", entry, entry.Guid, exported, issues, cap, 0);
                    if (issues.Count >= cap) break;
                }
                if (issues.Count >= cap) break;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["scanned_assets"] = scanned,
                ["issue_count"] = issues.Count,
                ["issues"] = issues,
                ["unscanned_assets"] = entries.Count - scanned,
                ["truncated"] = issues.Count >= cap || scanned < entries.Count
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

        private static readonly Regex s_internalRef = new Regex(
            "00000000-0000-0000-0000-[0-9a-fA-F]{12}", RegexOptions.Compiled);

        private static string[] ToYamlLines(EbxAsset asset)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (EbxYamlWriter writer = new EbxYamlWriter(ms, App.AssetManager))
                    writer.WriteObjects(asset.Objects);
                string text = Encoding.UTF8.GetString(ms.ToArray());

                // Object instance ids are assigned per read by the reader, so the
                // same object gets a different placeholder id each time. Collapse
                // them or every object header and pointer becomes a false diff.
                text = s_internalRef.Replace(text, "<ref>");
                return text.Replace("\r\n", "\n").Split('\n');
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

            int bid = McpHandlers.ResolveBundleId(name);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bid);
            bool includeOutside = p.Value<bool?>("include_outside") ?? true;
            int cap = p.Value<int?>("limit") ?? 200;

            int assets = 0, depTotal = 0, linkedTotal = 0;
            int missingTotal = 0, outsideTotal = 0, linkedOutsideTotal = 0;
            List<object> missing = new List<object>();
            List<object> outside = new List<object>();
            List<object> linkedOutside = new List<object>();

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                // EnumerateEbx(BundleEntry) ignores RemBundles; the effective list is
                // what get_bundle and the packer use.
                if (!entry.EnumerateBundles().Contains(bid)) continue;
                assets++;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { continue; }
                if (asset == null) continue;

                // File-level dependencies only cover EBX to EBX. Resources and chunks
                // hang off LinkedAssets, and a bundle that misses them loads only
                // partially, so report those too.
                foreach (AssetEntry linked in entry.LinkedAssets)
                {
                    linkedTotal++;
                    if (!linked.EnumerateBundles().Contains(bid))
                    {
                        linkedOutsideTotal++;
                        if (linkedOutside.Count < cap)
                            linkedOutside.Add(new Dictionary<string, object>
                            {
                                ["asset"] = entry.Name,
                                ["linked"] = linked.Name,
                                ["linked_type"] = linked.Type
                            });
                    }
                }

                foreach (Guid dep in asset.Dependencies)
                {
                    depTotal++;
                    EbxAssetEntry depEntry = App.AssetManager.GetEbxEntry(dep);
                    if (depEntry == null)
                    {
                        missingTotal++;
                        if (missing.Count < cap)
                            missing.Add(new Dictionary<string, object> { ["asset"] = entry.Name, ["dep_guid"] = dep.ToString() });
                        continue;
                    }
                    if (!depEntry.EnumerateBundles().Contains(bid))
                    {
                        outsideTotal++;
                        if (includeOutside && outside.Count < cap)
                            outside.Add(new Dictionary<string, object> { ["asset"] = entry.Name, ["dep"] = depEntry.Name });
                    }
                }
            }

            // RES and Chunk members carry their own LinkedAssets (a texture RES points
            // at its chunk), which the EBX loop above never sees.
            foreach (ResAssetEntry entry in App.AssetManager.EnumerateRes())
            {
                if (!entry.EnumerateBundles().Contains(bid)) continue;
                assets++;
                foreach (AssetEntry linked in entry.LinkedAssets)
                {
                    linkedTotal++;
                    if (!linked.EnumerateBundles().Contains(bid))
                    {
                        linkedOutsideTotal++;
                        if (linkedOutside.Count < cap)
                            linkedOutside.Add(new Dictionary<string, object>
                            {
                                ["asset"] = entry.Name,
                                ["linked"] = linked.Name,
                                ["linked_type"] = linked.Type
                            });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["bundle"] = be.Name,
                ["assets"] = assets,
                ["dependency_count"] = depTotal,
                ["missing_count"] = missingTotal,
                ["missing_returned"] = missing.Count,
                ["missing"] = missing,
                ["outside_count"] = outsideTotal,
                ["outside_returned"] = outside.Count,
                ["outside"] = outside,
                ["linked_count"] = linkedTotal,
                ["linked_outside_count"] = linkedOutsideTotal,
                ["linked_outside_returned"] = linkedOutside.Count,
                ["linked_outside"] = linkedOutside,
                ["include_outside"] = includeOutside,
                // outside is only collected when include_outside is set, so it is
                // reported as omitted rather than truncated.
                ["omitted_outside_count"] = includeOutside ? 0 : outsideTotal,
                ["truncated"] = missingTotal > missing.Count
                    || (includeOutside && outsideTotal > outside.Count)
                    || linkedOutsideTotal > linkedOutside.Count
            };
        }

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
            int registriesSkipped = 0;
            if (p["registries"] is JArray regs && regs.Count > 0)
                registries.AddRange(regs.Select(r => r.ToString()));
            else if (!string.IsNullOrWhiteSpace(p.Value<string>("registry")))
                registries.Add(p.Value<string>("registry"));
            else
            {
                // Discover the profile's own registries instead of assuming the
                // BF1 character registry names. A big profile has thousands of them
                // and each one has to be loaded, so keep the default sweep small and
                // say how many were skipped.
                int maxRegistries = p.Value<int?>("max_registries") ?? 8;
                if (maxRegistries < 1) maxRegistries = 1;
                int discovered = 0;
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (e.Type == null || !e.Type.EndsWith("NetworkRegistryAsset", StringComparison.OrdinalIgnoreCase))
                        continue;
                    discovered++;
                    if (registries.Count < maxRegistries)
                        registries.Add(e.Name);
                }
                if (registries.Count == 0)
                    return McpHandlers.Error("No NetworkRegistryAsset found; pass registries explicitly", "NOT_FOUND");
                if (discovered > registries.Count)
                    registriesSkipped = discovered - registries.Count;
            }

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
                    int regMissing = 0;
                    foreach (KeyValuePair<Guid, string> ex in exported)
                    {
                        string key = entry.Guid.ToString() + "/" + ex.Key.ToString();
                        if (kv.Value.Contains(key)) continue;
                        assetMissing++;
                        totalMissing++;
                        regMissing++;
                        if (missing.Count < cap)
                            missing.Add(new Dictionary<string, object>
                            {
                                ["class_guid"] = ex.Key.ToString(),
                                ["type"] = ex.Value
                            });
                    }
                    perRegistry[kv.Key] = new Dictionary<string, object>
                    {
                        ["missing_count"] = regMissing,
                        ["missing_returned"] = missing.Count,
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
                ["registries_checked"] = registries,
                ["registries_skipped"] = registriesSkipped,
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
