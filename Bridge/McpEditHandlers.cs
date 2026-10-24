using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using FrostySdk.Managers.Entries;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Edit / mutate / import-export commands for Frosty Editor MCP bridge.
    /// Patterns follow BF1DevPlugin.Base.Core / Duplicate and AssetManager APIs.
    /// </summary>
    internal static class McpEditHandlers
    {
        // =====================================================================
        // EBX property get/set
        // =====================================================================

        public static object GetEbxProperty(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required (e.g. 'Name' or 'Transform.trans.x')", "INVALID_PARAMS");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                object value = ResolvePropertyPath(asset.RootObject, path, out string resolvedType);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = path,
                    ["type"] = resolvedType,
                    ["value"] = SerializeValue(value)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "GET_FAILED");
            }
        }

        public static object SetEbxProperty(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            if (!p.ContainsKey("value"))
                return McpHandlers.Error("value is required", "INVALID_PARAMS");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                SetPropertyPath(asset.RootObject, path, p["value"]);
                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);

                object newValue = ResolvePropertyPath(asset.RootObject, path, out string resolvedType);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = path,
                    ["type"] = resolvedType,
                    ["value"] = SerializeValue(newValue),
                    ["asset"] = McpHandlers.SerializeEbxEntry(entry)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FAILED");
            }
        }

        // =====================================================================
        // EBX bin import/export, duplicate, create, revert
        // =====================================================================

        public static object ExportEbxBin(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path (output file) is required", "INVALID_PARAMS");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                AssetDefinition def = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                bool ok = def.Export(entry, path, "bin");
                if (!ok)
                    return McpHandlers.Error("Export failed (asset may use a custom handler)", "EXPORT_FAILED");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ImportEbxBin(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (existing .bin file) is required", "INVALID_PARAMS");

            bool dataOnly = p.Value<bool?>("data_only") ?? true;

            try
            {
                AssetDefinition def = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                AssetImportType importType = new AssetImportType("bin", dataOnly ? "Binary File (Data Only)" : "Binary File");
                bool ok = def.Import(entry, path, importType);
                if (!ok)
                    return McpHandlers.Error("Import failed (asset may use a custom handler)", "IMPORT_FAILED");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["data_only"] = dataOnly,
                    ["asset"] = McpHandlers.SerializeEbxEntry(App.AssetManager.GetEbxEntry(entry.Name))
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        public static object DuplicateEbx(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            string newName = (p.Value<string>("new_name") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(newName))
                return McpHandlers.Error("new_name is required", "INVALID_PARAMS");
            if (App.AssetManager.GetEbxEntry(newName) != null)
                return McpHandlers.Error("Target name already exists", "ALREADY_EXISTS");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                EbxAsset newAsset;
                using (EbxBaseWriter writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.DoNotSort))
                {
                    writer.WriteAsset(asset);
                    byte[] buf = writer.ToByteArray();
                    using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(buf)))
                        newAsset = reader.ReadAsset<EbxAsset>();
                }

                newAsset.SetFileGuid(Guid.NewGuid());
                dynamic obj = newAsset.RootObject;
                obj.Name = newName;

                // Renumber every object, not just the root. Duplicated assets that
                // share a bundle with their source otherwise keep the source object
                // GUIDs and can collide in the game's asset/type registry at load time.
                RenumberAssetObjectGuids(newAsset);

                EbxAssetEntry newEntry = App.AssetManager.AddEbx(newName, newAsset);

                // Source bundles are optional on purpose. Inheriting them puts the
                // duplicate next to the original, which is wrong when the duplicate
                // gets its own bundle: the game then installs two bundle definition
                // assets for the same bundle and crashes while building the mesh
                // variation database. Pass copy_bundles=true when the duplicate is
                // meant to live in the source bundle.
                if (p.Value<bool?>("copy_bundles") ?? false)
                    newEntry.AddedBundles.AddRange(entry.EnumerateBundles());
                newEntry.ModifiedEntry.DependentAssets.AddRange(newAsset.Dependencies);

                bool copyLinked = p.Value<bool?>("copy_linked") ?? false;
                if (copyLinked)
                {
                    // Best-effort: duplicate linked res/chunks when present on root
                    TryDuplicateLinkedTextureRes(entry, newEntry, newAsset);
                }

                newAsset.Update();
                App.AssetManager.ModifyEbx(newEntry.Name, newAsset);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source"] = McpHandlers.SerializeEbxEntry(entry),
                    ["asset"] = McpHandlers.SerializeEbxEntry(newEntry),
                    ["file_guid"] = newAsset.FileGuid.ToString(),
                    ["class_guid"] = newAsset.RootInstanceGuid.ToString()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "DUPLICATE_FAILED");
            }
        }

        internal static void RenumberAssetObjectGuids(EbxAsset asset)
        {
            HashSet<Guid> usedGuids = new HashSet<Guid>();
            foreach (object assetObj in asset.Objects)
            {
                AssetClassGuid oldGuid = ((dynamic)assetObj).GetInstanceGuid();
                if (oldGuid.ExportedGuid != Guid.Empty)
                    usedGuids.Add(oldGuid.ExportedGuid);
            }

            foreach (object assetObj in asset.Objects)
            {
                dynamic dynObj = assetObj;
                AssetClassGuid oldGuid = dynObj.GetInstanceGuid();
                if (oldGuid.ExportedGuid == Guid.Empty)
                    continue;

                Guid newGuid;
                do
                {
                    newGuid = Guid.NewGuid();
                }
                while (!usedGuids.Add(newGuid));

                dynObj.SetInstanceGuid(new AssetClassGuid(newGuid, oldGuid.InternalId));
            }
        }

        public static object CreateEbx(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string name = (p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            string typeName = (p.Value<string>("type") ?? "").Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("name and type are required", "INVALID_PARAMS");
            if (App.AssetManager.GetEbxEntry(name) != null)
                return McpHandlers.Error("Asset already exists", "ALREADY_EXISTS");

            Type type = TypeLibrary.GetType(typeName);
            if (type == null)
                return McpHandlers.Error("Type not found: " + typeName, "TYPE_NOT_FOUND");

            try
            {
                object root = TypeLibrary.CreateObject(typeName);
                EbxAsset asset = new EbxAsset(root);
                asset.SetFileGuid(Guid.NewGuid());

                dynamic obj = asset.RootObject;
                try { obj.Name = name; } catch { /* some types have no Name */ }

                AssetClassGuid guid = new AssetClassGuid(
                    Utils.GenerateDeterministicGuid(asset.Objects, (Type)obj.GetType(), asset.FileGuid), -1);
                obj.SetInstanceGuid(guid);

                EbxAssetEntry entry = App.AssetManager.AddEbx(name, asset);

                string bundleName = p.Value<string>("bundle");
                if (!string.IsNullOrEmpty(bundleName))
                {
                    int bid = App.AssetManager.GetBundleId(bundleName.ToLowerInvariant());
                    if (bid >= 0)
                        entry.AddToBundle(bid);
                }

                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeEbxEntry(entry),
                    ["file_guid"] = asset.FileGuid.ToString(),
                    ["class_guid"] = asset.RootInstanceGuid.ToString()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "CREATE_FAILED");
            }
        }

        public static object RevertAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string kind = (p.Value<string>("kind") ?? "ebx").Trim().ToLowerInvariant();
            bool dataOnly = p.Value<bool?>("data_only") ?? false;
            AssetEntry entry = ResolveAnyAsset(kind, p);
            if (entry == null)
                return McpHandlers.Error("Asset not found", "NOT_FOUND");

            try
            {
                App.AssetManager.RevertAsset(entry, dataOnly);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeAssetEntry(entry),
                    ["data_only"] = dataOnly
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REVERT_FAILED");
            }
        }

        // =====================================================================
        // RES / Chunk
        // =====================================================================

        public static object ExportRes(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            ResAssetEntry entry = ResolveRes(p);
            if (entry == null)
                return McpHandlers.Error("RES not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(entry)))
                using (NativeWriter writer = new NativeWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
                    writer.Write(reader.ReadToEnd());

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeResEntry(entry),
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ImportRes(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            ResAssetEntry entry = ResolveRes(p);
            if (entry == null)
                return McpHandlers.Error("RES not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (existing file) is required", "INVALID_PARAMS");

            try
            {
                byte[] data = File.ReadAllBytes(path);
                App.AssetManager.ModifyRes(entry.Name, data);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeResEntry(App.AssetManager.GetResEntry(entry.Name)),
                    ["bytes"] = data.Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        public static object ExportChunk(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            ChunkAssetEntry entry = ResolveChunk(p);
            if (entry == null)
                return McpHandlers.Error("Chunk not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                using (NativeWriter writer = new NativeWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
                    writer.Write(reader.ReadToEnd());

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeChunkEntry(entry),
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ImportChunk(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            ChunkAssetEntry entry = ResolveChunk(p);
            if (entry == null)
                return McpHandlers.Error("Chunk not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (existing file) is required", "INVALID_PARAMS");

            try
            {
                byte[] data = File.ReadAllBytes(path);
                bool ok = App.AssetManager.ModifyChunk(entry.Id, data);
                if (!ok)
                    return McpHandlers.Error("ModifyChunk failed", "IMPORT_FAILED");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeChunkEntry(App.AssetManager.GetChunkEntry(entry.Id)),
                    ["bytes"] = data.Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        public static object DuplicateChunk(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            ChunkAssetEntry entry = ResolveChunk(p);
            if (entry == null)
                return McpHandlers.Error("Chunk not found", "NOT_FOUND");

            try
            {
                byte[] random = new byte[16];
                using (var rng = new RNGCryptoServiceProvider())
                {
                    while (true)
                    {
                        rng.GetBytes(random);
                        random[15] |= 1;
                        if (App.AssetManager.GetChunkEntry(new Guid(random)) == null)
                            break;
                    }
                }

                Guid newGuid;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                    newGuid = App.AssetManager.AddChunk(reader.ReadToEnd(), new Guid(random), null, entry.EnumerateBundles().ToArray());

                ChunkAssetEntry newEntry = App.AssetManager.GetChunkEntry(newGuid);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source"] = McpHandlers.SerializeChunkEntry(entry),
                    ["asset"] = McpHandlers.SerializeChunkEntry(newEntry)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "DUPLICATE_FAILED");
            }
        }

        public static object DuplicateRes(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            ResAssetEntry entry = ResolveRes(p);
            if (entry == null)
                return McpHandlers.Error("RES not found", "NOT_FOUND");

            string newName = (p.Value<string>("new_name") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(newName))
                return McpHandlers.Error("new_name is required", "INVALID_PARAMS");
            if (App.AssetManager.GetResEntry(newName) != null)
                return McpHandlers.Error("Target RES already exists", "ALREADY_EXISTS");

            try
            {
                ResourceType resType = (ResourceType)entry.ResType;
                string typeOverride = p.Value<string>("res_type");
                if (!string.IsNullOrEmpty(typeOverride) && Enum.TryParse(typeOverride, true, out ResourceType parsed))
                    resType = parsed;

                ResAssetEntry newEntry;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(entry)))
                    newEntry = App.AssetManager.AddRes(newName, resType, entry.ResMeta, reader.ReadToEnd(), entry.EnumerateBundles().ToArray());

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source"] = McpHandlers.SerializeResEntry(entry),
                    ["asset"] = McpHandlers.SerializeResEntry(newEntry)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "DUPLICATE_FAILED");
            }
        }

        // =====================================================================
        // Bundles
        // =====================================================================

        public static object AddToBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");

            string kind = (p.Value<string>("kind") ?? "ebx").Trim().ToLowerInvariant();
            AssetEntry entry = ResolveAnyAsset(kind, p);
            if (entry == null)
                return McpHandlers.Error("Asset not found", "NOT_FOUND");

            bool added = entry.AddToBundle(bid);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["added"] = added,
                ["bundle"] = bundleName,
                ["bundle_id"] = bid,
                ["asset"] = McpHandlers.SerializeAssetEntry(entry)
            };
        }

        public static object RemoveFromBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");

            string kind = (p.Value<string>("kind") ?? "ebx").Trim().ToLowerInvariant();
            AssetEntry entry = ResolveAnyAsset(kind, p);
            if (entry == null)
                return McpHandlers.Error("Asset not found", "NOT_FOUND");

            bool removed = false;
            if (entry.AddedBundles.Contains(bid))
            {
                entry.AddedBundles.Remove(bid);
                entry.IsDirty = true;
                removed = true;
            }
            else if (entry.Bundles.Contains(bid) && !entry.RemBundles.Contains(bid))
            {
                entry.RemBundles.Add(bid);
                entry.IsDirty = true;
                removed = true;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["removed"] = removed,
                ["bundle"] = bundleName,
                ["bundle_id"] = bid,
                ["asset"] = McpHandlers.SerializeAssetEntry(entry)
            };
        }

        public static object CreateBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string name = (p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
                return McpHandlers.Error("name is required", "INVALID_PARAMS");

            string typeStr = (p.Value<string>("type") ?? "BlueprintBundle").Trim();
            if (!Enum.TryParse(typeStr, true, out BundleType bundleType))
                bundleType = BundleType.BlueprintBundle;

            string sbName = (p.Value<string>("super_bundle") ?? "").Trim().ToLowerInvariant();
            int sbIndex = 0;
            if (!string.IsNullOrEmpty(sbName))
            {
                sbIndex = App.AssetManager.GetSuperBundleId(sbName);
                if (sbIndex < 0)
                {
                    App.AssetManager.AddSuperBundle(sbName);
                    sbIndex = App.AssetManager.GetSuperBundleId(sbName);
                }
            }

            try
            {
                BundleEntry be = App.AssetManager.AddBundle(name, bundleType, sbIndex);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = be.Name,
                    ["type"] = be.Type.ToString(),
                    ["added"] = be.Added,
                    ["super_bundle"] = App.AssetManager.GetSuperBundle(be.SuperBundleId)?.Name,
                    ["bundle_id"] = App.AssetManager.GetBundleId(be.Name)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "CREATE_FAILED");
            }
        }

        // =====================================================================
        // Texture / Mesh via AssetDefinition (+ reflection for mesh no-dialog)
        // =====================================================================

        public static object ExportTexture(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Texture EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            string format = (p.Value<string>("format") ?? Path.GetExtension(path).TrimStart('.') ?? "dds").ToLowerInvariant();
            if (string.IsNullOrEmpty(format))
                format = "dds";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                AssetDefinition def = App.PluginManager.GetAssetDefinition(entry.Type);
                if (def == null)
                    return McpHandlers.Error("No TextureAssetDefinition loaded (is TexturePlugin enabled?)", "PLUGIN_MISSING");

                bool ok = def.Export(entry, path, format);
                if (!ok)
                    return McpHandlers.Error("Texture export failed", "EXPORT_FAILED");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["format"] = format,
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ImportTexture(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Texture EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (png/tga/hdr/dds) is required", "INVALID_PARAMS");

            try
            {
                // Prefer TexturePlugin editor import helpers via reflection (avoids project reference)
                object result = ImportTextureViaReflection(entry, path);
                if (result != null)
                    return result;

                return McpHandlers.Error(
                    "Texture import requires TexturePlugin. Load TexturePlugin and retry, or import a pre-converted DDS via import_chunk/import_res.",
                    "PLUGIN_MISSING");
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        public static object ExportMesh(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Mesh EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            string format = (p.Value<string>("format") ?? "fbx").ToLowerInvariant();
            string skeleton = p.Value<string>("skeleton") ?? "";
            string version = p.Value<string>("fbx_version") ?? Config.Get<string>("MeshSetExportVersion", "FBX_2012", ConfigScope.Game);
            string scale = p.Value<string>("scale") ?? Config.Get<string>("MeshSetExportScale", "Centimeters", ConfigScope.Game);
            bool flatten = p.Value<bool?>("flatten_hierarchy") ?? Config.Get<bool>("MeshSetExportFlattenHierarchy", false, ConfigScope.Game);
            bool singleLod = p.Value<bool?>("single_lod") ?? Config.Get<bool>("MeshSetExportExportSingleLod", false, ConfigScope.Game);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                ExportMeshViaReflection(entry, path, format, version, scale, flatten, singleLod, skeleton);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["format"] = format
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ImportMesh(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Mesh EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (fbx file) is required", "INVALID_PARAMS");

            string skeleton = p.Value<string>("skeleton") ?? "";

            try
            {
                ImportMeshViaReflection(entry, path, skeleton);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["asset"] = McpHandlers.SerializeEbxEntry(entry)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        public static object ExportAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            string format = (p.Value<string>("format") ?? "xml").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                AssetDefinition def = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                bool ok = def.Export(entry, path, format);
                if (!ok)
                    return McpHandlers.Error("Export failed for format: " + format, "EXPORT_FAILED");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["format"] = format,
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object FindReferences(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 100);

            List<object> refs = new List<object>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.ContainsDependency(entry.Guid))
                    refs.Add(McpHandlers.SerializeEbxEntry(e));
            }

            List<object> page = refs.Skip(offset).Take(limit).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["target"] = McpHandlers.SerializeEbxEntry(entry),
                ["total"] = refs.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["references"] = page
            };
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static AssetEntry ResolveAnyAsset(string kind, JObject p)
        {
            switch (kind)
            {
                case "res":
                    return ResolveRes(p);
                case "chunk":
                    return ResolveChunk(p);
                default:
                    return McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            }
        }

        private static ResAssetEntry ResolveRes(JObject p)
        {
            string name = p.Value<string>("name");
            if (!string.IsNullOrEmpty(name))
            {
                ResAssetEntry e = App.AssetManager.GetResEntry(name);
                if (e != null)
                    return e;
            }

            if (p["res_rid"] != null)
            {
                try
                {
                    ulong rid = Convert.ToUInt64(p["res_rid"].ToString());
                    return App.AssetManager.GetResEntry(rid);
                }
                catch { }
            }
            return null;
        }

        private static ChunkAssetEntry ResolveChunk(JObject p)
        {
            string idStr = p.Value<string>("id") ?? p.Value<string>("guid") ?? p.Value<string>("name");
            if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out Guid id))
                return null;
            return App.AssetManager.GetChunkEntry(id);
        }

        private static object ResolvePropertyPath(object root, string path, out string typeName)
        {
            object current = root;
            foreach (string rawPart in SplitPath(path))
            {
                if (current == null)
                    throw new InvalidOperationException("Null encountered at '" + rawPart + "'");

                if (TryParseIndexer(rawPart, out string propName, out int index))
                {
                    if (!string.IsNullOrEmpty(propName))
                        current = GetMember(current, propName);

                    if (!(current is IList list))
                        throw new InvalidOperationException("'" + propName + "' is not a list");
                    if (index < 0 || index >= list.Count)
                        throw new InvalidOperationException("Index out of range: " + index);
                    current = list[index];
                }
                else
                {
                    current = GetMember(current, rawPart);
                }
            }

            typeName = current?.GetType().Name ?? "null";
            return current;
        }

        private static void SetPropertyPath(object root, string path, JToken valueToken)
        {
            string[] parts = SplitPath(path).ToArray();
            if (parts.Length == 0)
                throw new InvalidOperationException("Empty path");

            object current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string rawPart = parts[i];
                if (TryParseIndexer(rawPart, out string propName, out int index))
                {
                    if (!string.IsNullOrEmpty(propName))
                        current = GetMember(current, propName);
                    IList list = current as IList
                        ?? throw new InvalidOperationException("'" + propName + "' is not a list");
                    current = list[index];
                }
                else
                {
                    current = GetMember(current, rawPart);
                }
            }

            string last = parts[parts.Length - 1];
            if (TryParseIndexer(last, out string lastProp, out int lastIndex))
            {
                object listObj = string.IsNullOrEmpty(lastProp) ? current : GetMember(current, lastProp);
                IList list = listObj as IList
                    ?? throw new InvalidOperationException("Target is not a list");
                object converted = ConvertTo(valueToken, list[lastIndex]?.GetType() ?? typeof(object));
                list[lastIndex] = converted;
            }
            else
            {
                SetMember(current, last, valueToken);
            }
        }

        private static IEnumerable<string> SplitPath(string path)
        {
            // Supports: Foo.Bar[0].Baz
            List<string> parts = new List<string>();
            string current = "";
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '.')
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current);
                        current = "";
                    }
                }
                else if (c == '[')
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current);
                        current = "";
                    }
                    int end = path.IndexOf(']', i);
                    if (end < 0)
                        throw new InvalidOperationException("Unclosed indexer in path");
                    parts.Add(path.Substring(i, end - i + 1));
                    i = end;
                }
                else
                {
                    current += c;
                }
            }
            if (current.Length > 0)
                parts.Add(current);
            return parts;
        }

        private static bool TryParseIndexer(string part, out string propName, out int index)
        {
            propName = null;
            index = -1;
            if (part.StartsWith("[") && part.EndsWith("]"))
            {
                propName = "";
                return int.TryParse(part.Substring(1, part.Length - 2), out index);
            }
            int bracket = part.IndexOf('[');
            if (bracket > 0 && part.EndsWith("]"))
            {
                propName = part.Substring(0, bracket);
                return int.TryParse(part.Substring(bracket + 1, part.Length - bracket - 2), out index);
            }
            return false;
        }

        private static object GetMember(object obj, string name)
        {
            Type type = obj.GetType();
            PropertyInfo pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi != null)
                return pi.GetValue(obj);

            FieldInfo fi = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi != null)
                return fi.GetValue(obj);

            throw new InvalidOperationException("Member not found: " + name + " on " + type.Name);
        }

        private static void SetMember(object obj, string name, JToken valueToken)
        {
            Type type = obj.GetType();
            PropertyInfo pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi != null && pi.CanWrite)
            {
                pi.SetValue(obj, ConvertTo(valueToken, pi.PropertyType));
                return;
            }

            FieldInfo fi = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi != null)
            {
                fi.SetValue(obj, ConvertTo(valueToken, fi.FieldType));
                return;
            }

            throw new InvalidOperationException("Writable member not found: " + name + " on " + type.Name);
        }

        private static object ConvertTo(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType == typeof(CString) || targetType.Name == "CString")
                return (CString)(token.Type == JTokenType.String ? token.Value<string>() : token.ToString());

            if (targetType == typeof(PointerRef) || targetType.Name == "PointerRef")
                return ConvertPointerRef(token);

            if (targetType.IsEnum)
            {
                string s = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                return Enum.Parse(targetType, s, true);
            }

            if (targetType == typeof(Guid))
                return Guid.Parse(token.ToString());

            if (targetType == typeof(bool) || targetType == typeof(Boolean))
                return token.Type == JTokenType.Boolean ? token.Value<bool>() : Convert.ToBoolean(token.ToString(), CultureInfo.InvariantCulture);

            Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying == typeof(float) || underlying == typeof(Single))
                return Convert.ToSingle(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(double) || underlying == typeof(Double))
                return Convert.ToDouble(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(int) || underlying == typeof(Int32))
                return Convert.ToInt32(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(uint) || underlying == typeof(UInt32))
                return Convert.ToUInt32(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(long) || underlying == typeof(Int64))
                return Convert.ToInt64(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(ulong) || underlying == typeof(UInt64))
                return Convert.ToUInt64(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(short) || underlying == typeof(Int16))
                return Convert.ToInt16(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(byte))
                return Convert.ToByte(token.ToString(), CultureInfo.InvariantCulture);
            if (underlying == typeof(string))
                return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();

            // Structs with x/y/z fields (Vec2/Vec3/LinearTransform pieces)
            if (token is JObject jobj && underlying.IsValueType)
            {
                object inst = Activator.CreateInstance(underlying);
                foreach (JProperty prop in jobj.Properties())
                {
                    try { SetMember(inst, prop.Name, prop.Value); }
                    catch { /* skip unknown */ }
                }
                return inst;
            }

            return token.ToObject(targetType);
        }

        private static PointerRef ConvertPointerRef(JToken token)
        {
            if (token.Type == JTokenType.Null || (token.Type == JTokenType.String && string.IsNullOrEmpty(token.Value<string>())))
                return new PointerRef();

            if (token is JObject obj)
            {
                string assetName = obj.Value<string>("asset") ?? obj.Value<string>("name");
                string fileGuid = obj.Value<string>("file_guid") ?? obj.Value<string>("FileGuid");
                string classGuid = obj.Value<string>("class_guid") ?? obj.Value<string>("ClassGuid");

                if (!string.IsNullOrEmpty(assetName))
                {
                    EbxAssetEntry e = App.AssetManager.GetEbxEntry(assetName);
                    if (e == null)
                        throw new InvalidOperationException("PointerRef asset not found: " + assetName);
                    EbxAsset a = App.AssetManager.GetEbx(e);
                    return new PointerRef(new EbxImportReference
                    {
                        FileGuid = a.FileGuid,
                        ClassGuid = a.RootInstanceGuid
                    });
                }

                if (!string.IsNullOrEmpty(fileGuid))
                {
                    return new PointerRef(new EbxImportReference
                    {
                        FileGuid = Guid.Parse(fileGuid),
                        ClassGuid = string.IsNullOrEmpty(classGuid) ? Guid.Empty : Guid.Parse(classGuid)
                    });
                }
            }

            if (token.Type == JTokenType.String)
            {
                string s = token.Value<string>();
                if (Guid.TryParse(s, out Guid g))
                    return new PointerRef(g);

                EbxAssetEntry e = App.AssetManager.GetEbxEntry(s);
                if (e != null)
                {
                    EbxAsset a = App.AssetManager.GetEbx(e);
                    return new PointerRef(new EbxImportReference
                    {
                        FileGuid = a.FileGuid,
                        ClassGuid = a.RootInstanceGuid
                    });
                }
            }

            throw new InvalidOperationException("Cannot convert value to PointerRef");
        }

        private static object SerializeValue(object value)
        {
            if (value == null)
                return null;

            if (value is CString cs)
                return (string)cs;

            if (value is PointerRef pr)
            {
                return new Dictionary<string, object>
                {
                    ["type"] = pr.Type.ToString(),
                    ["file_guid"] = pr.External.FileGuid.ToString(),
                    ["class_guid"] = pr.External.ClassGuid.ToString(),
                    ["internal"] = pr.Internal?.GetType().Name
                };
            }

            if (value is Guid g)
                return g.ToString();

            if (value is Enum)
                return value.ToString();

            if (value is IList list && !(value is string))
            {
                List<object> items = new List<object>();
                int count = Math.Min(list.Count, 100);
                for (int i = 0; i < count; i++)
                    items.Add(SerializeValue(list[i]));
                return new Dictionary<string, object>
                {
                    ["count"] = list.Count,
                    ["truncated"] = list.Count > 100,
                    ["items"] = items
                };
            }

            Type t = value.GetType();
            if (t.IsPrimitive || value is string || value is decimal)
                return value;

            // Simple struct dump (public properties)
            Dictionary<string, object> dump = new Dictionary<string, object>();
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                    continue;
                try
                {
                    object v = pi.GetValue(value);
                    if (v == null || v is string || v.GetType().IsPrimitive || v is Enum || v is Guid || v is CString || v is PointerRef)
                        dump[pi.Name] = SerializeValue(v);
                    else if (v.GetType().IsValueType && v.GetType().Namespace?.StartsWith("FrostySdk") == true)
                        dump[pi.Name] = SerializeValue(v);
                    else
                        dump[pi.Name] = v.GetType().Name;
                }
                catch { }
            }
            if (dump.Count > 0)
                return dump;

            return value.ToString();
        }

        private static void TryDuplicateLinkedTextureRes(EbxAssetEntry source, EbxAssetEntry dest, EbxAsset newAsset)
        {
            try
            {
                dynamic root = newAsset.RootObject;
                // TextureAsset.Resource is ResRid
                PropertyInfo resProp = ((object)root).GetType().GetProperty("Resource");
                if (resProp == null)
                    return;

                object ridObj = resProp.GetValue(root);
                ulong rid = Convert.ToUInt64(ridObj);
                ResAssetEntry oldRes = App.AssetManager.GetResEntry(rid);
                if (oldRes == null)
                    return;

                string newResName = dest.Name;
                if (App.AssetManager.GetResEntry(newResName) != null)
                    return;

                ResAssetEntry newRes;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(oldRes)))
                    newRes = App.AssetManager.AddRes(newResName, (ResourceType)oldRes.ResType, oldRes.ResMeta, reader.ReadToEnd(), oldRes.EnumerateBundles().ToArray());

                resProp.SetValue(root, newRes.ResRid);
                dest.LinkAsset(newRes);

                // Also duplicate chunk pointed by Texture resource if present
                try
                {
                    Texture tex = App.AssetManager.GetResAs<Texture>(newRes);
                    if (tex != null)
                    {
                        ChunkAssetEntry oldChunk = App.AssetManager.GetChunkEntry(tex.ChunkId);
                        if (oldChunk != null)
                        {
                            byte[] random = new byte[16];
                            using (var rng = new RNGCryptoServiceProvider())
                            {
                                rng.GetBytes(random);
                                random[15] |= 1;
                            }
                            Guid newChunkId;
                            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(oldChunk)))
                                newChunkId = App.AssetManager.AddChunk(reader.ReadToEnd(), new Guid(random), tex, oldChunk.EnumerateBundles().ToArray());

                            tex.ChunkId = newChunkId;
                            App.AssetManager.ModifyRes(newRes.Name, tex);
                            dest.LinkAsset(App.AssetManager.GetChunkEntry(newChunkId));
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        private static object ImportTextureViaReflection(EbxAssetEntry entry, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds")
                return ImportDdsTexture(entry, path);

            Type editorType = FindType("TexturePlugin.FrostyTextureEditor");
            Type optionsType = FindType("TexturePlugin.TextureImportOptions");
            Type blobType = FindType("TexturePlugin.BlobData");
            Type imageFormatType = FindType("TexturePlugin.ImageFormat");
            if (editorType == null || optionsType == null || blobType == null || imageFormatType == null)
                return null;

            MethodInfo convert = editorType.GetMethod("ConvertImageToDDS", BindingFlags.Public | BindingFlags.Static);
            MethodInfo release = editorType.GetMethod("ReleaseBlob", BindingFlags.Public | BindingFlags.Static);
            if (convert == null)
                return null;

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic textureAsset = asset.RootObject;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(textureAsset.Resource);
            Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);

            object options = Activator.CreateInstance(optionsType);
            SetIfExists(options, "type", texture.Type);
            SetIfExists(options, "generateMipmaps", texture.MipCount > 1);
            SetIfExists(options, "resizeTexture", false);

            int fmtIndex = ext == ".tga" ? 1 : ext == ".hdr" ? 2 : 0;
            object imageFormat = Enum.ToObject(imageFormatType, fmtIndex);
            byte[] buf = File.ReadAllBytes(path);

            object[] args = new object[] { buf, (long)buf.Length, imageFormat, options, Activator.CreateInstance(blobType) };
            convert.Invoke(null, args);

            object blob = args[4];
            PropertyInfo dataProp = blobType.GetProperty("Data");
            byte[] dds = dataProp?.GetValue(blob) as byte[];
            release?.Invoke(null, new[] { blob });

            if (dds == null || dds.Length == 0)
                return McpHandlers.Error("ConvertImageToDDS returned empty data", "IMPORT_FAILED");

            string tmp = Path.Combine(Path.GetTempPath(), "frosty_mcp_" + Guid.NewGuid().ToString("N") + ".dds");
            File.WriteAllBytes(tmp, dds);
            try
            {
                return ImportDdsTexture(entry, tmp);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        private static object ImportDdsTexture(EbxAssetEntry entry, string ddsPath)
        {
            // Reuse TexturePlugin editor's DDS path by reading TextureAssetDefinition pattern:
            // Load texture res, replace chunk data. Full mip/header parsing is in FrostyTextureEditor.
            // Call into editor via reflection on a private method is brittle — instead use AssetDefinition if Import supported.
            Type editorType = FindType("TexturePlugin.FrostyTextureEditor");
            if (editorType == null)
                return null;

            // Fallback: write DDS bytes into linked chunk (raw). User should prefer png import when TexturePlugin is present.
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic textureAsset = asset.RootObject;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(textureAsset.Resource);
            Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
            byte[] ddsData = File.ReadAllBytes(ddsPath);

            // Skip 128-byte DDS header if present
            int headerSize = 128;
            if (ddsData.Length > 4 && ddsData[0] == 0x44 && ddsData[1] == 0x44 && ddsData[2] == 0x53 && ddsData[3] == 0x20)
            {
                // DX10 extended header
                if (ddsData.Length > 84 && BitConverter.ToInt32(ddsData, 84) == 0x30315844)
                    headerSize = 148;
            }
            else
            {
                headerSize = 0;
            }

            byte[] pixelData = new byte[ddsData.Length - headerSize];
            Buffer.BlockCopy(ddsData, headerSize, pixelData, 0, pixelData.Length);

            App.AssetManager.ModifyChunk(texture.ChunkId, pixelData, texture);
            entry.LinkAsset(App.AssetManager.GetChunkEntry(texture.ChunkId));
            App.AssetManager.ModifyRes(resEntry.Name, texture);
            entry.LinkAsset(resEntry);
            App.AssetManager.ModifyEbx(entry.Name, asset);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["path"] = Path.GetFullPath(ddsPath),
                ["note"] = "DDS pixel data written to texture chunk",
                ["bytes"] = pixelData.Length,
                ["asset"] = McpHandlers.SerializeEbxEntry(entry)
            };
        }

        private static void ExportMeshViaReflection(EbxAssetEntry entry, string path, string format, string version, string scale, bool flatten, bool singleLod, string skeleton)
        {
            Type exporterType = FindType("MeshSetPlugin.FBXExporter");
            Type meshSetType = FindType("MeshSetPlugin.Resources.MeshSet");
            if (exporterType == null || meshSetType == null)
                throw new InvalidOperationException("MeshSetPlugin not loaded");

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic meshAsset = asset.RootObject;
            ulong resRid = meshAsset.MeshSetResource;
            ResAssetEntry rEntry = App.AssetManager.GetResEntry(resRid);

            MethodInfo getResAs = typeof(AssetManager).GetMethods()
                .First(m => m.Name == "GetResAs" && m.IsGenericMethodDefinition);
            MethodInfo getResAsClosed = getResAs.MakeGenericMethod(meshSetType);
            object meshSet = getResAsClosed.Invoke(App.AssetManager, new object[] { rEntry, null });

            if (string.IsNullOrEmpty(skeleton))
                skeleton = Config.Get<string>("MeshSetExportSkeleton", "", ConfigScope.Game);

            string fbxVer = version.Replace("FBX_", "");
            object exporter = Activator.CreateInstance(exporterType, new object[] { null });
            MethodInfo export = exporterType.GetMethod("ExportFBX");
            // ExportFBX(dynamic meshAsset, string filename, string fbxVersion, string units, bool flatten, bool singleLod, string skeleton, string fileType, params MeshSet[] meshSets)
            object meshSetArray = Array.CreateInstance(meshSetType, 1);
            ((Array)meshSetArray).SetValue(meshSet, 0);

            export.Invoke(exporter, new object[]
            {
                meshAsset, path, fbxVer, scale, flatten, singleLod, skeleton,
                format == "obj" ? "obj" : "binary", meshSetArray
            });
        }

        private static void ImportMeshViaReflection(EbxAssetEntry entry, string path, string skeleton)
        {
            Type importerType = FindType("MeshSetPlugin.FBXImporter");
            Type meshSetType = FindType("MeshSetPlugin.Resources.MeshSet");
            Type settingsType = FindType("MeshSetPlugin.FrostyMeshImportSettings");
            if (importerType == null || meshSetType == null)
                throw new InvalidOperationException("MeshSetPlugin not loaded");

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic meshAsset = asset.RootObject;
            ulong resRid = meshAsset.MeshSetResource;
            ResAssetEntry rEntry = App.AssetManager.GetResEntry(resRid);

            MethodInfo getResAs = typeof(AssetManager).GetMethods()
                .First(m => m.Name == "GetResAs" && m.IsGenericMethodDefinition);
            object meshSet = getResAs.MakeGenericMethod(meshSetType).Invoke(App.AssetManager, new object[] { rEntry, null });

            object settings = settingsType != null ? Activator.CreateInstance(settingsType) : null;
            if (settings != null && !string.IsNullOrEmpty(skeleton))
                SetIfExists(settings, "SkeletonAsset", skeleton);

            object importer = Activator.CreateInstance(importerType, new object[] { App.Logger });
            MethodInfo import = importerType.GetMethod("ImportFBX");
            import.Invoke(importer, new object[] { path, meshSet, asset, entry, settings });
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null)
                    return t;
            }
            return null;
        }

        private static void SetIfExists(object obj, string name, object value)
        {
            PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi != null && pi.CanWrite)
            {
                try { pi.SetValue(obj, value); } catch { }
                return;
            }
            FieldInfo fi = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi != null)
            {
                try { fi.SetValue(obj, value); } catch { }
            }
        }
    }
}
