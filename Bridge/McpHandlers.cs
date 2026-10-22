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
        private const int DefaultXmlMaxChars = 200000;

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

            EbxAssetEntry entry = ResolveEbxEntry(name, guidStr);
            if (entry == null)
                return Error("EBX asset not found", "NOT_FOUND");

            try
            {
                InvokeOnUi(() =>
                {
                    if (App.EditorWindow == null)
                        throw new InvalidOperationException("Editor window is not available");
                    App.EditorWindow.OpenAsset(entry, createDefault);
                });
            }
            catch (Exception ex)
            {
                return Error(ex.Message, "OPEN_FAILED");
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["asset"] = SerializeEbxEntry(entry)
            };
        }

        public static object SearchEbx(JObject p)
        {
            if (App.AssetManager == null)
                return Error("AssetManager not ready", "NOT_READY");

            string query = (p.Value<string>("query") ?? "").Trim().ToLowerInvariant();
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
            AttachBundleInfo(result, entry);
            result["linked_assets"] = entry.LinkedAssets.Select(SerializeAssetEntry).ToList();
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
                        using (EbxXmlWriter writer = new EbxXmlWriter(asset, ms, App.AssetManager, 4, false))
                            writer.WriteObjects();
                    }
                    else
                    {
                        using (EbxYamlWriter writer = new EbxYamlWriter(ms, App.AssetManager))
                            writer.WriteObjects(asset.RootObjects);
                    }
                    text = Encoding.UTF8.GetString(ms.ToArray());
                }

                if (summary)
                {
                    var counts = new Dictionary<string, int>();
                    foreach (Match m in Regex.Matches(text, @"<([A-Za-z_][A-Za-z0-9_]*)(?:\s|>|/)"))
                    {
                        string tag = m.Groups[1].Value;
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
                    bool cut = filtered.Length > maxChars;
                    if (cut)
                        filtered = filtered.Substring(0, maxChars);
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
                        ["length"] = filtered.Length,
                        ["content"] = filtered
                    };
                }

                bool truncated = text.Length > maxChars;
                if (truncated)
                    text = text.Substring(0, maxChars);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["type"] = entry.Type,
                    ["guid"] = entry.Guid.ToString(),
                    ["format"] = xml ? "xml" : "yaml",
                    ["truncated"] = truncated,
                    ["length"] = text.Length,
                    ["content"] = text
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
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true, includeLinked: true))
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

            int bundleId = App.AssetManager.GetBundleId(name);
            if (bundleId < 0)
                return Error("Bundle not found", "NOT_FOUND");

            BundleEntry be = App.AssetManager.GetBundleEntry(bundleId);
            List<object> ebx = App.AssetManager.EnumerateEbx(be).Select(SerializeEbxEntry).Cast<object>().ToList();
            List<object> res = App.AssetManager.EnumerateRes(be).Select(SerializeResEntry).Cast<object>().ToList();
            List<object> chunks = App.AssetManager.EnumerateChunks(be).Select(SerializeChunkEntry).Cast<object>().ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = be.Name,
                ["type"] = be.Type.ToString(),
                ["super_bundle"] = App.AssetManager.GetSuperBundle(be.SuperBundleId)?.Name,
                ["ebx_count"] = ebx.Count,
                ["res_count"] = res.Count,
                ["chunk_count"] = chunks.Count,
                ["ebx"] = ebx.Take(DefaultLimit).ToList(),
                ["res"] = res.Take(DefaultLimit).ToList(),
                ["chunks"] = chunks.Take(DefaultLimit).ToList()
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

                List<object> deps = new List<object>();

                foreach (AssetEntry linked in entry.LinkedAssets)
                    deps.Add(SerializeAssetEntry(linked));

                HashSet<Guid> seen = new HashSet<Guid>();
                foreach (Guid fileGuid in entry.EnumerateDependencies())
                {
                    if (!seen.Add(fileGuid))
                        continue;

                    EbxAssetEntry dep = App.AssetManager.GetEbxEntry(fileGuid);
                    if (dep != null)
                        deps.Add(SerializeEbxEntry(dep));
                    else
                        deps.Add(new Dictionary<string, object>
                        {
                            ["asset_type"] = "ebx",
                            ["guid"] = fileGuid.ToString(),
                            ["missing"] = true
                        });
                }

                foreach (Guid fileGuid in asset.Dependencies)
                {
                    if (!seen.Add(fileGuid))
                        continue;

                    EbxAssetEntry dep = App.AssetManager.GetEbxEntry(fileGuid);
                    if (dep != null)
                        deps.Add(SerializeEbxEntry(dep));
                    else
                        deps.Add(new Dictionary<string, object>
                        {
                            ["asset_type"] = "ebx",
                            ["guid"] = fileGuid.ToString(),
                            ["missing"] = true
                        });
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["guid"] = entry.Guid.ToString(),
                    ["count"] = deps.Count,
                    ["dependencies"] = deps
                };
            }
            catch (Exception ex)
            {
                return Error(ex.Message, "DEPS_FAILED");
            }
        }

        public static object GetTypeInfo(JObject p)
        {
            string typeName = p.Value<string>("name");
            if (string.IsNullOrEmpty(typeName))
                return Error("name is required", "INVALID_PARAMS");

            Type type = TypeLibrary.GetType(typeName);
            if (type == null)
                return Error("Type not found", "NOT_FOUND");

            List<object> props = new List<object>();
            foreach (var pi in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                props.Add(new Dictionary<string, object>
                {
                    ["name"] = pi.Name,
                    ["type"] = pi.PropertyType.Name
                });
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = type.Name,
                ["fullname"] = type.Namespace ?? "",
                ["full_name"] = type.FullName,
                ["base_type"] = type.BaseType?.Name,
                ["property_count"] = props.Count,
                ["properties"] = props
            };
        }

        public static object SearchTypes(JObject p)
        {
            string query = (p.Value<string>("query") ?? "").Trim();
            string baseType = (p.Value<string>("base_type") ?? "").Trim();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            Type[] types;
            try
            {
                types = string.IsNullOrEmpty(baseType)
                    ? TypeLibrary.GetConcreteTypes()
                    : TypeLibrary.GetTypes(baseType);
            }
            catch
            {
                types = TypeLibrary.GetConcreteTypes() ?? Array.Empty<Type>();
            }

            if (types == null)
                types = Array.Empty<Type>();

            IEnumerable<Type> filtered = types;
            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(t =>
                    t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            List<Type> all = filtered.OrderBy(t => t.Name).ToList();
            List<object> page = all.Skip(offset).Take(limit)
                .Select(t => (object)new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["full_name"] = t.FullName,
                    ["base_type"] = t.BaseType?.Name
                }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["types"] = page
            };
        }

        // ---- helpers (shared with edit handlers) ----

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
                ["filename"] = entry.Filename,
                ["path"] = entry.Path,
                ["is_modified"] = entry.IsModified,
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
                ["filename"] = entry.Filename,
                ["path"] = entry.Path,
                ["is_modified"] = entry.IsModified,
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
                ["filename"] = entry.Filename,
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
            result["added_bundle_count"] = entry.AddedBundles?.Count ?? 0;
            result["removed_bundle_count"] = entry.RemBundles?.Count ?? 0;
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
