using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using FrostySdk.Managers.Entries;

namespace FrostyMcpPlugin.Bridge
{
    internal static class McpHandlers
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 10000;
        private const int DefaultXmlMaxChars = 60000;

        public static object Ping(JObject _)
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["pong"] = true,
                ["version"] = McpBridgeServer.Version,
                ["pipe"] = McpBridgeServer.PipeName,
                ["profile_loaded"] = App.AssetManager != null,
                ["profile"] = ProfilesLibrary.ProfileName ?? "",
                ["selected_asset"] = App.SelectedAsset?.Name
            };
        }

        public static object GetEditorInfo(JObject _)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            var ebxCount = App.AssetManager.GetEbxCount();
            var dirtyCount = App.AssetManager.GetDirtyCount();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["version"] = McpBridgeServer.Version,
                ["profile"] = ProfilesLibrary.ProfileName ?? "",
                ["display_name"] = ProfilesLibrary.DisplayName ?? "",
                ["data_version"] = ProfilesLibrary.DataVersion,
                ["selected_pack"] = App.SelectedPack ?? "",
                ["selected_asset"] = SerializeEbxEntry(App.SelectedAsset),
                ["ebx_count"] = ebxCount,
                ["dirty_count"] = dirtyCount,
                ["sdk_version"] = TypeLibrary.GetSdkVersion().ToString()
            };
        }

        public static object GetSelectedAsset(JObject _)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            AssetEntry opened = null;
            try
            {
                opened = InvokeOnUi(() => App.EditorWindow?.GetOpenedAssetEntry());
            }
            catch
            {
                // Editor window may not exist yet
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["selected"] = SerializeAssetEntry(App.SelectedAsset),
                ["opened"] = SerializeAssetEntry(opened)
            };
        }

        public static object OpenAsset(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string name = p.Value<string>("name");
            string guidStr = p.Value<string>("guid");
            bool createDefault = p.Value<bool?>("create_default_editor") ?? true;
            string kind = (p.Value<string>("kind") ?? "ebx").Trim().ToLowerInvariant();

            AssetEntry assetEntry;
            if (kind == "ebx")
            {
                assetEntry = ResolveEbxEntry(name, guidStr);
                if (assetEntry == null)
                    return Error("EBX asset not found", "NOT_FOUND");
            }
            else if (kind == "res")
            {
                assetEntry = string.IsNullOrEmpty(guidStr) || !ulong.TryParse(guidStr, out ulong rid)
                    ? (string.IsNullOrEmpty(name) ? null : App.AssetManager.GetResEntry(name))
                    : App.AssetManager.GetResEntry(rid);
                if (assetEntry == null)
                    return Error("RES asset not found", "NOT_FOUND");
            }
            else if (kind == "chunk")
            {
                string idText = string.IsNullOrEmpty(guidStr) ? name : guidStr;
                assetEntry = Guid.TryParse(idText, out Guid chunkId) ? App.AssetManager.GetChunkEntry(chunkId) : null;
                if (assetEntry == null)
                    return Error("Chunk not found", "NOT_FOUND");
            }
            else
            {
                return Error("kind must be ebx, res or chunk", "INVALID_PARAMS");
            }

            try
            {
                InvokeOnUi(() =>
                {
                    if (App.EditorWindow == null)
                        throw new InvalidOperationException("Editor window is not available");
                    App.EditorWindow.OpenAsset(assetEntry, createDefault);
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message, "OPEN_FAILED");
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["kind"] = kind,
                ["asset"] = SerializeAssetEntry(assetEntry)
            };
        }

        public static object SearchEbx(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string query = (p.Value<string>("query") ?? p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            string typeFilter = (p.Value<string>("type") ?? "").Trim();
            bool modifiedOnly = p.Value<bool?>("modified_only") ?? false;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            IEnumerable<EbxAssetEntry> source = App.AssetManager.EnumerateEbx(
                type: typeFilter,
                modifiedOnly: modifiedOnly);

            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(e =>
                    (e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (e.Type != null && e.Type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            List<EbxAssetEntry> all = source.ToList();
            List<object> page = all.Skip(offset).Take(limit).Select(SerializeEbxEntry).Cast<object>().ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["assets"] = page
            };
        }

        public static object GetEbxEntry(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return Error("EBX asset not found", "NOT_FOUND");

            Dictionary<string, object> result = SerializeEbxEntry(entry);
            result["success"] = true;
            result["is_modified"] = entry.IsModified;
            AttachBundleInfo(result, entry);

            int linkedLimit = ClampLimit(p.Value<int?>("linked_limit") ?? 200);
            List<AssetEntry> linked = entry.LinkedAssets.ToList();
            result["linked_count"] = linked.Count;
            result["linked_assets"] = linked.Take(linkedLimit).Select(SerializeAssetEntry).ToList();
            if (linked.Count > linkedLimit)
                result["linked_truncated"] = true;
            return result;
        }

        public static object GetEbxXml(JObject p)
        {
            return ExportEbxText(p, xml: true);
        }

        public static object GetEbxYaml(JObject p)
        {
            return ExportEbxText(p, xml: false);
        }

        private static object ExportEbxText(JObject p, bool xml)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return Error("EBX asset not found", "NOT_FOUND");

            int maxChars = p.Value<int?>("max_chars") ?? DefaultXmlMaxChars;
            if (maxChars <= 0)
                maxChars = DefaultXmlMaxChars;
            int charOffset = Math.Max(0, p.Value<int?>("char_offset") ?? 0);
            bool summary = p.Value<bool?>("summary") ?? false;
            string filter = (p.Value<string>("filter") ?? "").Trim();

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                if (asset == null)
                    return Error("Failed to load EBX data", "LOAD_FAILED");

                string text;
                using (MemoryStream ms = new MemoryStream())
                {
                    if (xml)
                    {
                        // Virjoinga fork: EbxXmlWriter 改成 (EbxAsset, Stream, AssetManager,
                        // tabSize, writeOffsets), 且 WriteObjects() 不再收参数
                        // (内部用 asset.Objects)。官方 1.0.6.3 的旧签名是
                        // (Stream, AssetManager) + WriteObjects(IEnumerable<object>)。
                        // Same indent as the editor's own exporter so text diffs line up.
                        int tabSize = Config.Get<int>("ExportTabSize", 2);
                        using (EbxXmlWriter writer = new EbxXmlWriter(asset, ms, App.AssetManager, tabSize, false))
                            writer.WriteObjects();
                    }
                    else
                    {
                        using (EbxYamlWriter writer = new EbxYamlWriter(ms, App.AssetManager))
                            writer.WriteObjects(asset.Objects);
                    }
                    text = Encoding.UTF8.GetString(ms.ToArray());
                }

                if (summary)
                {
                    // Count objects by type. Scanning the serialized text only worked
                    // for XML; YAML has no tags, so the summary was always empty.
                    var counts = new Dictionary<string, int>();
                    foreach (object obj in asset.Objects)
                    {
                        string tag = obj.GetType().Name;
                        counts[tag] = counts.TryGetValue(tag, out int n) ? n + 1 : 1;
                    }
                    var top = counts
                        .OrderByDescending(kv => kv.Value)
                        .Take(40)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["type"] = entry.Type,
                        ["guid"] = entry.Guid.ToString(),
                        ["format"] = xml ? "xml" : "yaml",
                        ["summary"] = true,
                        ["object_count"] = asset.Objects.Count(),
                        ["length"] = text.Length,
                        ["elements"] = top
                    };
                }

                if (!string.IsNullOrEmpty(filter))
                {
                    string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> hits = lines
                        .Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    string filtered = string.Join("\n", hits);
                    if (charOffset > filtered.Length)
                        charOffset = filtered.Length;
                    string slice = filtered.Substring(charOffset);
                    bool cut = slice.Length > maxChars;
                    if (cut)
                        slice = slice.Substring(0, maxChars);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["type"] = entry.Type,
                        ["guid"] = entry.Guid.ToString(),
                        ["format"] = xml ? "xml" : "yaml",
                        ["filter"] = filter,
                        ["match_count"] = hits.Count,
                        ["truncated"] = cut,
                        ["char_offset"] = charOffset,
                        ["next_offset"] = cut ? charOffset + maxChars : -1,
                        ["total_length"] = filtered.Length,
                        ["length"] = slice.Length,
                        ["content"] = slice
                    };
                }

                if (charOffset > text.Length)
                    charOffset = text.Length;
                string body = text.Substring(charOffset);
                bool truncated = body.Length > maxChars;
                if (truncated)
                    body = body.Substring(0, maxChars);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["type"] = entry.Type,
                    ["guid"] = entry.Guid.ToString(),
                    ["format"] = xml ? "xml" : "yaml",
                    ["truncated"] = truncated,
                    ["char_offset"] = charOffset,
                    ["next_offset"] = truncated ? charOffset + maxChars : -1,
                    ["total_length"] = text.Length,
                    ["length"] = body.Length,
                    ["content"] = body
                };
            }
            catch (Exception ex)
            {
                return Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ListModifiedAssets(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string kind = (p.Value<string>("kind") ?? "all").Trim().ToLowerInvariant();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            List<object> all = new List<object>();

            if (kind == "all" || kind == "ebx")
            {
                // Only directly modified assets are written to a .fbmod; a linked-only
                // change exports through the RES instead. Pass include_linked=true to
                // see the indirect ones anyway.
                bool includeLinked = p.Value<bool?>("include_linked") ?? false;
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true, includeLinked: includeLinked))
                    all.Add(SerializeEbxEntry(e));
            }
            if (kind == "all" || kind == "res")
            {
                foreach (ResAssetEntry e in App.AssetManager.EnumerateRes(modifiedOnly: true))
                    all.Add(SerializeResEntry(e));
            }
            if (kind == "all" || kind == "chunk")
            {
                foreach (ChunkAssetEntry e in App.AssetManager.EnumerateChunks(modifiedOnly: true))
                    all.Add(SerializeChunkEntry(e));
            }

            List<object> page = all.Skip(offset).Take(limit).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["assets"] = page
            };
        }

        public static object SearchRes(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string query = (p.Value<string>("query") ?? "").Trim();
            bool modifiedOnly = p.Value<bool?>("modified_only") ?? false;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            IEnumerable<ResAssetEntry> source = App.AssetManager.EnumerateRes(modifiedOnly: modifiedOnly);
            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(e =>
                    e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            List<ResAssetEntry> all = source.ToList();
            List<object> page = all.Skip(offset).Take(limit).Select(SerializeResEntry).Cast<object>().ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["assets"] = page
            };
        }

        public static object GetResEntry(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string name = p.Value<string>("name");
            ResAssetEntry entry = null;

            if (!string.IsNullOrEmpty(name))
                entry = App.AssetManager.GetResEntry(name);

            if (entry == null && p["res_rid"] != null)
            {
                try
                {
                    ulong rid = Convert.ToUInt64(p["res_rid"].ToString());
                    entry = App.AssetManager.GetResEntry(rid);
                }
                catch
                {
                    return Error("Invalid res_rid", "INVALID_PARAMS");
                }
            }

            if (entry == null)
                return Error("RES asset not found", "NOT_FOUND");

            Dictionary<string, object> result = SerializeResEntry(entry);
            result["success"] = true;
            return result;
        }

        public static object SearchChunks(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string query = (p.Value<string>("query") ?? "").Trim();
            bool modifiedOnly = p.Value<bool?>("modified_only") ?? false;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            IEnumerable<ChunkAssetEntry> source = App.AssetManager.EnumerateChunks(modifiedOnly: modifiedOnly);
            if (!string.IsNullOrEmpty(query))
            {
                source = source.Where(e =>
                    e.Id.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.Name != null && e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            List<ChunkAssetEntry> all = source.ToList();
            List<object> page = all.Skip(offset).Take(limit).Select(SerializeChunkEntry).Cast<object>().ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["assets"] = page
            };
        }

        public static object GetChunkEntry(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string idStr = p.Value<string>("id") ?? p.Value<string>("guid");
            if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out Guid id))
                return Error("Valid chunk id/guid required", "INVALID_PARAMS");

            ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(id);
            if (entry == null)
                return Error("Chunk not found", "NOT_FOUND");

            Dictionary<string, object> result = SerializeChunkEntry(entry);
            result["success"] = true;
            return result;
        }

        public static object ListBundles(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string query = (p.Value<string>("query") ?? "").Trim();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            List<object> all = new List<object>();
            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                if (!string.IsNullOrEmpty(query) &&
                    (be.Name == null || be.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                all.Add(new Dictionary<string, object>
                {
                    ["name"] = be.Name,
                    ["type"] = be.Type.ToString(),
                    ["super_bundle"] = App.AssetManager.GetSuperBundle(be.SuperBundleId)?.Name
                });
            }

            List<object> page = all.Skip(offset).Take(limit).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["bundles"] = page
            };
        }

        public static object GetBundle(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string name = p.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return Error("name is required", "INVALID_PARAMS");

            int bundleId = ResolveBundleId(name);
            if (bundleId < 0)
                return Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bundleId);

            // EnumerateRes/EnumerateChunks(BundleEntry) only test entry.Bundles and
            // ignore AddedBundles, so assets added to a bundle through the editor
            // vanish from the listing even though the mod writer exports them.
            // Filter by the effective bundle list instead.
            List<object> ebx = App.AssetManager.EnumerateEbx()
                .Where(e => e.EnumerateBundles().Contains(bundleId))
                .Select(SerializeEbxEntry).Cast<object>().ToList();
            List<object> res = App.AssetManager.EnumerateRes()
                .Where(e => e.EnumerateBundles().Contains(bundleId))
                .Select(SerializeResEntry).Cast<object>().ToList();
            List<object> chunks = App.AssetManager.EnumerateChunks()
                .Where(e => e.EnumerateBundles().Contains(bundleId))
                .Select(SerializeChunkEntry).Cast<object>().ToList();

            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);
            List<object> ebxPage = ebx.Skip(offset).Take(limit).ToList();
            List<object> resPage = res.Skip(offset).Take(limit).ToList();
            List<object> chunkPage = chunks.Skip(offset).Take(limit).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = be.Name,
                ["type"] = be.Type.ToString(),
                ["super_bundle"] = App.AssetManager.GetSuperBundle(be.SuperBundleId)?.Name,
                ["ebx_count"] = ebx.Count,
                ["res_count"] = res.Count,
                ["chunk_count"] = chunks.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["ebx_returned"] = ebxPage.Count,
                ["res_returned"] = resPage.Count,
                ["chunks_returned"] = chunkPage.Count,
                ["truncated"] = ebx.Count > offset + ebxPage.Count
                    || res.Count > offset + resPage.Count
                    || chunks.Count > offset + chunkPage.Count,
                ["ebx"] = ebxPage,
                ["res"] = resPage,
                ["chunks"] = chunkPage
            };
        }

        public static object GetAssetDependencies(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return Error("EBX asset not found", "NOT_FOUND");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                if (asset == null)
                    return Error("Failed to load EBX data", "LOAD_FAILED");

                bool recursive = p.Value<bool?>("recursive") ?? false;
                int maxNodes = p.Value<int?>("max_nodes") ?? 2000;
                if (maxNodes < 1) maxNodes = 1;

                List<object> deps = new List<object>();

                foreach (AssetEntry linked in entry.LinkedAssets)
                    deps.Add(SerializeAssetEntry(linked));

                HashSet<Guid> seen = new HashSet<Guid> { entry.Guid };
                int depth = 1;
                List<Guid> frontier = new List<Guid>();
                frontier.AddRange(entry.EnumerateDependencies());
                frontier.AddRange(asset.Dependencies);

                bool truncated = false;
                while (frontier.Count > 0)
                {
                    List<Guid> next = new List<Guid>();
                    foreach (Guid fileGuid in frontier)
                    {
                        if (!seen.Add(fileGuid))
                            continue;

                        if (deps.Count >= maxNodes)
                        {
                            truncated = true;
                            break;
                        }

                        EbxAssetEntry dep = App.AssetManager.GetEbxEntry(fileGuid);
                        if (dep != null)
                        {
                            Dictionary<string, object> row = SerializeEbxEntry(dep);
                            row["depth"] = depth;
                            deps.Add(row);

                            if (recursive)
                            {
                                next.AddRange(dep.EnumerateDependencies());
                                EbxAsset depAsset;
                                try { depAsset = App.AssetManager.GetEbx(dep); }
                                catch { depAsset = null; }
                                if (depAsset != null)
                                    next.AddRange(depAsset.Dependencies);
                            }
                        }
                        else
                        {
                            deps.Add(new Dictionary<string, object>
                            {
                                ["asset_type"] = "ebx",
                                ["guid"] = fileGuid.ToString(),
                                ["missing"] = true,
                                ["depth"] = depth
                            });
                        }
                    }
                    if (truncated || !recursive)
                        break;
                    frontier = next;
                    depth++;
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["guid"] = entry.Guid.ToString(),
                    ["recursive"] = recursive,
                    ["count"] = deps.Count,
                    ["truncated"] = truncated,
                    ["dependencies"] = deps
                };
            }
            catch (Exception ex)
            {
                return Error(ex.Message, "DEPS_FAILED");
            }
        }

        // ---- helpers (shared with edit handlers) ----

        // Bundle names keep the case they were indexed with ("Win32/...", "Gameplay/..."),
        // while callers pass whatever the client typed. Match exactly first, then fall
        // back to a case-insensitive scan.
        internal static int ResolveBundleId(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || App.AssetManager == null)
                return -1;

            name = name.Trim();
            int bid = App.AssetManager.GetBundleId(name);
            if (bid >= 0)
                return bid;

            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                if (string.Equals(be.Name, name, StringComparison.OrdinalIgnoreCase))
                    return App.AssetManager.GetBundleId(be.Name);
            }

            return -1;
        }

        // A membership removed by AdvancedBundleEditorPlugin lands in RemBundles.
        // AssetEntry.AddToBundle then sees IsInBundle == true and returns false
        // without clearing the tombstone, so the asset can never come back.
        internal static bool AddToBundleExportable(AssetEntry entry, int bid)
        {
            if (entry.RemBundles.Contains(bid))
            {
                entry.RemBundles.Remove(bid);
                entry.IsDirty = true;
            }
            return entry.AddToBundle(bid);
        }

        internal static EbxAssetEntry ResolveEbxEntry(string name, string guidStr)
        {
            if (!string.IsNullOrEmpty(guidStr) && Guid.TryParse(guidStr, out Guid guid))
            {
                EbxAssetEntry byGuid = App.AssetManager.GetEbxEntry(guid);
                if (byGuid != null)
                    return byGuid;
            }

            if (!string.IsNullOrEmpty(name))
                return App.AssetManager.GetEbxEntry(name);

            return null;
        }

        internal static Dictionary<string, object> SerializeEbxEntry(EbxAssetEntry entry)
        {
            if (entry == null)
                return null;

            return new Dictionary<string, object>
            {
                ["asset_type"] = "ebx",
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["guid"] = entry.Guid.ToString(),
                ["is_dirty"] = entry.IsDirty,
                ["is_added"] = entry.IsAdded,
                ["size"] = entry.Size
            };
        }

        internal static Dictionary<string, object> SerializeResEntry(ResAssetEntry entry)
        {
            if (entry == null)
                return null;

            return new Dictionary<string, object>
            {
                ["asset_type"] = "res",
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["res_rid"] = entry.ResRid.ToString(),
                ["res_type"] = entry.ResType,
                ["is_dirty"] = entry.IsDirty,
                ["is_added"] = entry.IsAdded,
                ["size"] = entry.Size
            };
        }

        internal static Dictionary<string, object> SerializeChunkEntry(ChunkAssetEntry entry)
        {
            if (entry == null)
                return null;

            return new Dictionary<string, object>
            {
                ["asset_type"] = "chunk",
                ["name"] = entry.Name,
                ["id"] = entry.Id.ToString(),
                ["is_modified"] = entry.IsModified,
                ["is_dirty"] = entry.IsDirty,
                ["is_added"] = entry.IsAdded,
                ["size"] = entry.Size,
                ["logical_offset"] = entry.LogicalOffset,
                ["logical_size"] = entry.LogicalSize
            };
        }

        internal static Dictionary<string, object> SerializeAssetEntry(AssetEntry entry)
        {
            if (entry == null)
                return null;
            if (entry is EbxAssetEntry ebx)
                return SerializeEbxEntry(ebx);
            if (entry is ResAssetEntry res)
                return SerializeResEntry(res);
            if (entry is ChunkAssetEntry chunk)
                return SerializeChunkEntry(chunk);

            return new Dictionary<string, object>
            {
                ["asset_type"] = entry.AssetType,
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["is_modified"] = entry.IsModified,
                ["is_dirty"] = entry.IsDirty
            };
        }

        /// <summary>
        /// Frosty stores vanilla membership in Bundles, editor-added ones in AddedBundles,
        /// and editor-removed ones in RemBundles. The Properties Bundles tab uses all three.
        /// </summary>
        internal static void AttachBundleInfo(Dictionary<string, object> result, AssetEntry entry)
        {
            if (result == null || entry == null || App.AssetManager == null)
                return;

            result["bundles"] = ResolveBundleNames(entry.EnumerateBundles());
            result["original_bundles"] = ResolveBundleNames(entry.Bundles);
            result["added_bundles"] = ResolveBundleNames(entry.AddedBundles);
            result["removed_bundles"] = ResolveBundleNames(entry.RemBundles);
        }

        internal static List<string> ResolveBundleNames(IEnumerable<int> ids)
        {
            List<string> names = new List<string>();
            if (ids == null || App.AssetManager == null)
                return names;

            foreach (int id in ids)
            {
                BundleEntry be = App.AssetManager.GetBundleEntry(id);
                names.Add(be?.Name ?? id.ToString());
            }
            return names;
        }

        internal static Dictionary<string, object> Error(string message, string code)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = message,
                ["error_code"] = code
            };
        }

        internal static int ClampLimit(int limit)
        {
            if (limit < 1)
                return 1;
            if (limit > MaxLimit)
                return MaxLimit;
            return limit;
        }

        internal static T InvokeOnUi<T>(Func<T> func)
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return func();
            return dispatcher.Invoke(func);
        }

        internal static void InvokeOnUi(Action action)
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            dispatcher.Invoke(action);
        }
    }
}
