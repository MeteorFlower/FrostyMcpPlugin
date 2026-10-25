using Frosty.Core;
using Frosty.Core.Viewport;
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
using System.Windows;
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

            if (App.PluginManager.GetCustomHandler(entry.Type) != null)
                return McpHandlers.Error("Asset type uses a custom handler; .bin export is not supported", "UNSUPPORTED_TYPE");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                AssetDefinition def = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                bool ok = def.Export(entry, path, "bin");
                if (!ok)
                    return McpHandlers.Error("Export failed", "EXPORT_FAILED");

                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length == 0)
                    return McpHandlers.Error("Export produced no data", "EXPORT_EMPTY");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = fi.Length
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

            // AssetDefinition.ImportFromBin only logs when the type has a custom
            // handler, yet Import() still returns true. Refuse up front instead
            // of reporting a success that changed nothing.
            if (App.PluginManager.GetCustomHandler(entry.Type) != null)
                return McpHandlers.Error("Asset type uses a custom handler; .bin import is not supported", "UNSUPPORTED_TYPE");

            var plan = new Dictionary<string, object>
            {
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["path"] = Path.GetFullPath(path),
                ["data_only"] = dataOnly,
                ["current_guid"] = entry.Guid.ToString()
            };
            Func<object> execute = () =>
            {
            try
            {
                AssetDefinition def = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
                AssetImportType importType = new AssetImportType("bin", dataOnly ? "Binary File (Data Only)" : "Binary File");
                bool ok = def.Import(entry, path, importType);
                if (!ok)
                    return McpHandlers.Error("Import failed (asset may use a custom handler)", "IMPORT_FAILED");

                // data_only=false takes the file's FileGuid, while AssetManager keeps
                // entry.Guid as it was. The two must stay equal: every tool that
                // resolves a PointerRef by asset name writes entry data from one of them.
                EbxAssetEntry refreshed = App.AssetManager.GetEbxEntry(entry.Name);
                EbxAsset imported = refreshed == null ? null : App.AssetManager.GetEbx(refreshed);
                if (refreshed == null || imported == null)
                    return McpHandlers.Error("Imported asset could not be reloaded", "IMPORT_FAILED");

                if (imported.FileGuid != refreshed.Guid)
                {
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "Imported file GUID differs from the asset entry GUID; the asset is now inconsistent",
                        ["error_code"] = "GUID_MISMATCH",
                        ["entry_guid"] = refreshed.Guid.ToString(),
                        ["file_guid"] = imported.FileGuid.ToString(),
                        ["file_guid_resolves"] = App.AssetManager.GetEbxEntry(imported.FileGuid) != null,
                        ["data_only"] = dataOnly
                    };
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["data_only"] = dataOnly,
                    ["file_guid"] = refreshed.Guid.ToString(),
                    ["asset"] = McpHandlers.SerializeEbxEntry(refreshed)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
            };

            // data_only=true keeps the asset identity; data_only=false adopts the
            // file's GUID and must be confirmed explicitly.
            if (dataOnly)
                return execute();
            return McpCommit.Require("import_ebx_bin", p, plan, execute);
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
                try { obj.Name = newName; } catch { /* some types have no Name */ }

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

            // Resolve the bundle before the asset exists: failing afterwards would
            // leave a created asset that claims success.
            int bundleId = -1;
            string bundleName = p.Value<string>("bundle");
            if (!string.IsNullOrEmpty(bundleName))
            {
                bundleId = McpHandlers.ResolveBundleId(bundleName);
                if (bundleId < 0)
                    return McpHandlers.Error("Bundle not found: " + bundleName, "BUNDLE_NOT_FOUND");
            }

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

                if (bundleId >= 0)
                    McpHandlers.AddToBundleExportable(entry, bundleId);

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

            var plan = new Dictionary<string, object>
            {
                ["asset"] = entry.Name,
                ["kind"] = kind,
                ["data_only"] = dataOnly,
                ["is_dirty"] = entry.IsDirty
            };

            return McpCommit.Require("revert_asset", p, plan, () =>
            {
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
            });
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

            string format = (p.Value<string>("format") ?? "frosty_res").Trim().ToLowerInvariant();
            if (format != "frosty_res" && format != "raw")
                return McpHandlers.Error("format must be frosty_res or raw", "INVALID_PARAMS");
            bool withMeta = format == "frosty_res";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                byte[] data;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(entry)))
                    data = reader.ReadToEnd();

                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    if (withMeta)
                        fs.Write(BuildResMeta(entry), 0, 16);
                    fs.Write(data, 0, data.Length);
                }

                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeResEntry(entry),
                    ["path"] = Path.GetFullPath(path),
                    ["format"] = format,
                    ["meta_included"] = withMeta,
                    ["meta_size"] = withMeta ? 16 : 0,
                    ["data_size"] = data.Length,
                    ["size"] = new FileInfo(path).Length
                };
                if (!withMeta)
                    result["warning"] = "Raw format cannot be exchanged with the editor's .res importer";
                return result;
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

            string format = (p.Value<string>("format") ?? "frosty_res").Trim().ToLowerInvariant();
            if (format != "frosty_res" && format != "raw")
                return McpHandlers.Error("format must be frosty_res or raw", "INVALID_PARAMS");
            bool withMeta = format == "frosty_res";

            try
            {
                byte[] file = File.ReadAllBytes(path);
                if (withMeta && file.Length < 16)
                    return McpHandlers.Error("frosty_res file is shorter than the 16-byte header", "INVALID_PARAMS");

                byte[] meta = null;
                byte[] data;
                if (withMeta)
                {
                    meta = new byte[16];
                    Array.Copy(file, 0, meta, 0, 16);
                    data = new byte[file.Length - 16];
                    Array.Copy(file, 16, data, 0, data.Length);
                }
                else
                {
                    data = file;
                }

                App.AssetManager.ModifyRes(entry.Name, data, meta);

                // Read back through the same path the editor uses and compare bytes.
                byte[] roundtrip;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(entry)))
                    roundtrip = reader.ReadToEnd();
                if (!BytesEqual(data, roundtrip))
                    return McpHandlers.Error("RES round-trip verification failed", "VERIFY_FAILED");

                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["asset"] = McpHandlers.SerializeResEntry(App.AssetManager.GetResEntry(entry.Name)),
                    ["format"] = format,
                    ["meta_included"] = withMeta,
                    ["meta_updated"] = withMeta,
                    ["bytes"] = data.Length
                };
                if (!withMeta)
                    result["warning"] = "Raw format leaves ResMeta unchanged";
                return result;
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        private static byte[] BuildResMeta(ResAssetEntry entry)
        {
            byte[] meta = new byte[16];
            if (entry.ResMeta != null)
                Array.Copy(entry.ResMeta, meta, Math.Min(16, entry.ResMeta.Length));
            return meta;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
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

                Texture texture = null;
                string textureResName = (p.Value<string>("texture_res") ?? "").Trim();
                if (!string.IsNullOrEmpty(textureResName))
                {
                    ResAssetEntry textureRes = App.AssetManager.GetResEntry(textureResName);
                    if (textureRes == null)
                        return McpHandlers.Error("texture_res not found", "NOT_FOUND");
                    texture = App.AssetManager.GetResAs<Texture>(textureRes);
                    if (texture == null)
                        return McpHandlers.Error("texture_res is not a Texture resource", "TYPE_MISMATCH");
                }

                Guid newGuid;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                    newGuid = App.AssetManager.AddChunk(reader.ReadToEnd(), new Guid(random), texture, entry.EnumerateBundles().ToArray());

                ChunkAssetEntry newEntry = App.AssetManager.GetChunkEntry(newGuid);
                if (newEntry != null && entry.SuperBundles.Count > 0)
                    newEntry.AddedSuperBundles.AddRange(entry.SuperBundles);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source"] = McpHandlers.SerializeChunkEntry(entry),
                    ["asset"] = McpHandlers.SerializeChunkEntry(newEntry),
                    ["super_bundles_copied"] = entry.SuperBundles.Count,
                    ["texture_metadata"] = texture != null
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

            bool added = McpHandlers.AddToBundleExportable(entry, bid);
            bool already = !added && entry.IsInBundle(bid);
            var warnings = new List<string>();
            if (added && IsResourceBackedType(entry.Type) && !entry.LinkedAssets.Any())
                warnings.Add("Asset type carries RES/Chunk resources that this raw membership add does not include");

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["added"] = added,
                ["already_in_bundle"] = already,
                ["bundle"] = bundleName,
                ["bundle_id"] = bid,
                ["effective_bundles"] = entry.EnumerateBundles().Count(),
                ["warnings"] = warnings,
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

            // A .fbmod can only add bundle memberships; dropping a native one
            // would be recorded in RemBundles and never exported.
            if (!entry.AddedBundles.Contains(bid) && entry.Bundles.Contains(bid))
                return McpHandlers.Error("Asset is a native member of this bundle; the removal cannot be expressed in a .fbmod", "NOT_EXPORTABLE");

            if (!entry.AddedBundles.Contains(bid))
                return McpHandlers.Error("Asset is not an editor-added member of this bundle", "NOT_MEMBER");

            var plan = new Dictionary<string, object>
            {
                ["asset"] = entry.Name,
                ["bundle"] = bundleName,
                ["bundle_id"] = bid,
                ["remaining_effective_bundles"] = entry.EnumerateBundles().Count() - 1
            };

            return McpCommit.Require("remove_from_bundle", p, plan, () =>
            {
                entry.AddedBundles.Remove(bid);
                entry.IsDirty = true;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["removed"] = true,
                    ["bundle"] = bundleName,
                    ["bundle_id"] = bid,
                    ["asset"] = McpHandlers.SerializeAssetEntry(entry)
                };
            });
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

            string sbName = (p.Value<string>("super_bundle") ?? "").Trim();
            if (string.IsNullOrEmpty(sbName))
                return McpHandlers.Error("super_bundle is required", "INVALID_PARAMS");

            int sbIndex = ResolveSuperBundleId(sbName);
            if (sbIndex < 0)
            {
                App.AssetManager.AddSuperBundle(sbName);
                sbIndex = ResolveSuperBundleId(sbName);
            }
            if (sbIndex < 0)
                return McpHandlers.Error("SuperBundle not found and could not be created: " + sbName, "NOT_FOUND");

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
                bool rawChunk = p.Value<bool?>("raw_chunk") ?? false;
                object result = ImportTextureViaReflection(entry, path, rawChunk);
                if (result != null)
                    return result;

                return McpHandlers.Error("TexturePlugin is not loaded", "PLUGIN_MISSING");
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
            bool nonRenderable = p.Value<bool?>("export_non_renderable")
                ?? Config.Get<bool>("MeshSetExportNonRenderable", false, ConfigScope.Game);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                ExportMeshViaReflection(entry, path, format, version, scale, flatten, singleLod, nonRenderable, skeleton);
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

        private static bool IsResourceBackedType(string type)
        {
            if (string.IsNullOrEmpty(type))
                return false;
            string[] markers =
            {
                "Texture", "MeshAsset", "SoundWave", "NewWave", "MovieTexture",
                "PhysicsAsset", "Cloth", "Enlighten", "SvgImage", "AtlasTexture"
            };
            return markers.Any(m => type.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int ResolveSuperBundleId(string name)
        {
            int id = App.AssetManager.GetSuperBundleId(name);
            if (id >= 0) return id;
            foreach (SuperBundleEntry sb in App.AssetManager.EnumerateSuperBundles())
                if (string.Equals(sb.Name, name, StringComparison.OrdinalIgnoreCase))
                    return App.AssetManager.GetSuperBundleId(sb);
            return -1;
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

        // Blueprint roots expose the child list as either Objects or Components;
        // accept both names in the generic property tools as the field tools do.
        private static string AliasMemberName(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            string alternate = null;
            if (string.Equals(name, "Components", StringComparison.OrdinalIgnoreCase))
                alternate = "Objects";
            else if (string.Equals(name, "Objects", StringComparison.OrdinalIgnoreCase))
                alternate = "Components";
            if (alternate == null)
                return name;
            if (type.GetProperty(name, flags) != null || type.GetField(name, flags) != null)
                return name;
            if (type.GetProperty(alternate, flags) != null || type.GetField(alternate, flags) != null)
                return alternate;
            return name;
        }

        private static object GetMember(object obj, string name)
        {
            Type type = obj.GetType();
            name = AliasMemberName(type, name);
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
            name = AliasMemberName(type, name);
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
                    if (a.FileGuid != e.Guid)
                        throw new InvalidOperationException("GUID_INCONSISTENT: entry " + e.Guid + ", file " + a.FileGuid);
                    return new PointerRef(new EbxImportReference
                    {
                        FileGuid = e.Guid,
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
                    if (a.FileGuid != e.Guid)
                        throw new InvalidOperationException("GUID_INCONSISTENT: entry " + e.Guid + ", file " + a.FileGuid);
                    return new PointerRef(new EbxImportReference
                    {
                        FileGuid = e.Guid,
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

        private static object ImportTextureViaReflection(EbxAssetEntry entry, string path, bool rawChunk)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds")
                return ImportDdsTexture(entry, path, rawChunk);

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
                return ImportDdsTexture(entry, tmp, rawChunk);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }

        // Mirrors TexturePlugin's DDS import: parse the header, reject type/format
        // mismatches, rebuild the Texture metadata and only then write the chunk.
        private static object ImportDdsTexture(EbxAssetEntry entry, string ddsPath, bool rawChunk)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic textureAsset = asset.RootObject;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(Convert.ToUInt64(textureAsset.Resource));
            if (resEntry == null)
                return McpHandlers.Error("Texture resource not found", "NOT_FOUND");

            Texture oldTexture = App.AssetManager.GetResAs<Texture>(resEntry);
            if (oldTexture == null)
                return McpHandlers.Error("Resource is not a Texture", "TYPE_MISMATCH");

            byte[] ddsData = File.ReadAllBytes(ddsPath);

            if (rawChunk || FindType("TexturePlugin.FrostyTextureEditor") == null)
            {
                if (oldTexture.Type != TextureType.TT_2d)
                    return McpHandlers.Error("Raw chunk import is only allowed for 2D textures; use a validated import", "TYPE_MISMATCH");

                int headerSize = 128;
                if (ddsData.Length > 4 && ddsData[0] == 0x44 && ddsData[1] == 0x44 && ddsData[2] == 0x53 && ddsData[3] == 0x20)
                {
                    if (ddsData.Length > 84 && BitConverter.ToInt32(ddsData, 84) == 0x30315844)
                        headerSize = 148;
                }
                else
                {
                    headerSize = 0;
                }

                byte[] rawPixels = new byte[ddsData.Length - headerSize];
                Buffer.BlockCopy(ddsData, headerSize, rawPixels, 0, rawPixels.Length);

                App.AssetManager.ModifyChunk(oldTexture.ChunkId, rawPixels, oldTexture);
                entry.LinkAsset(App.AssetManager.GetChunkEntry(oldTexture.ChunkId));
                App.AssetManager.ModifyRes(resEntry.Name, oldTexture);
                entry.LinkAsset(resEntry);
                App.AssetManager.ModifyEbx(entry.Name, asset);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(ddsPath),
                    ["chunk_id"] = oldTexture.ChunkId.ToString(),
                    ["bytes"] = rawPixels.Length,
                    ["metadata_changed"] = false,
                    ["warning"] = "Raw chunk import wrote pixel data without touching the Texture metadata",
                    ["asset"] = McpHandlers.SerializeEbxEntry(entry)
                };
            }

            TextureUtils.DDSHeader header = new TextureUtils.DDSHeader();
            byte[] buffer;
            using (NativeReader reader = new NativeReader(new MemoryStream(ddsData)))
            {
                if (!header.Read(reader))
                    return McpHandlers.Error("Invalid DDS header", "INVALID_DDS");
                buffer = reader.ReadToEnd();
            }

            TextureType type = TextureType.TT_2d;
            if (header.HasExtendedHeader)
            {
                if (header.ExtendedHeader.resourceDimension == SharpDX.Direct3D11.ResourceDimension.Texture2D)
                {
                    if ((header.ExtendedHeader.miscFlag & 4) != 0)
                        type = TextureType.TT_Cube;
                    else if (header.ExtendedHeader.arraySize > 1)
                        type = TextureType.TT_2dArray;
                }
                else if (header.ExtendedHeader.resourceDimension == SharpDX.Direct3D11.ResourceDimension.Texture3D)
                    type = TextureType.TT_3d;
            }
            else
            {
                if ((header.dwCaps2 & TextureUtils.DDSCaps2.CubeMap) != 0)
                    type = TextureType.TT_Cube;
                else if ((header.dwCaps2 & TextureUtils.DDSCaps2.Volume) != 0)
                    type = TextureType.TT_3d;
            }

            if (type != oldTexture.Type)
                return McpHandlers.Error(
                    "Imported texture type " + type + " does not match the asset type " + oldTexture.Type,
                    "TYPE_MISMATCH");

            bool oldSrgb = (oldTexture.Flags & TextureFlags.SrgbGamma) != 0;
            if (oldSrgb && (!header.HasExtendedHeader ||
                !header.ExtendedHeader.dxgiFormat.ToString().ToLowerInvariant().Contains("srgb")))
                return McpHandlers.Error("Imported format must be an SRGB variant", "FORMAT_MISMATCH");

            GetTexturePixelFormat(header, oldTexture, out string pixelFormat, out TextureFlags baseFlags);
            if (pixelFormat == "Unknown")
                return McpHandlers.Error("Unsupported DDS pixel format", "UNSUPPORTED_FORMAT");

            if (TextureUtils.IsCompressedFormat(pixelFormat) && oldTexture.MipCount > 1 &&
                (header.dwWidth % 4 != 0 || header.dwHeight % 4 != 0))
                return McpHandlers.Error("Width/height must be divisible by 4 for compressed formats with mipmaps", "FORMAT_MISMATCH");

            ushort depth = (header.HasExtendedHeader &&
                header.ExtendedHeader.resourceDimension == SharpDX.Direct3D11.ResourceDimension.Texture2D)
                ? (ushort)header.ExtendedHeader.arraySize
                : (ushort)1;
            if ((header.dwCaps2 & TextureUtils.DDSCaps2.CubeMap) != 0)
                depth = 6;
            if ((header.dwCaps2 & TextureUtils.DDSCaps2.Volume) != 0)
                depth = (ushort)header.dwDepth;

            Texture newTexture = new Texture(oldTexture.Type, pixelFormat, (ushort)header.dwWidth,
                (ushort)header.dwHeight, depth, oldTexture.Version, oldTexture.ResourceMeta)
            {
                FirstMip = oldTexture.FirstMip
            };
            if (header.dwMipMapCount <= oldTexture.FirstMip)
                newTexture.FirstMip = 0;

            newTexture.TextureGroup = oldTexture.TextureGroup;
            newTexture.CalculateMipData((byte)header.dwMipMapCount, TextureUtils.GetFormatBlockSize(pixelFormat),
                TextureUtils.IsCompressedFormat(pixelFormat), (uint)buffer.Length);
            newTexture.Flags = baseFlags | (oldTexture.Flags & ~TextureFlags.SrgbGamma);

            newTexture.Unknown4 = oldTexture.Unknown4;
            newTexture.Unknown5 = oldTexture.Unknown5;
            newTexture.b3 = oldTexture.b3;
            newTexture.b4 = oldTexture.b4;
            newTexture.flag = oldTexture.flag;
            newTexture.Unknown9 = oldTexture.Unknown9;

            // DDS stores cube/array data mip-major; Frosty chunks are slice-major.
            if (newTexture.Type == TextureType.TT_Cube || newTexture.Type == TextureType.TT_2dArray)
            {
                using (MemoryStream srcStream = new MemoryStream(buffer))
                using (MemoryStream dstStream = new MemoryStream())
                {
                    int sliceCount = newTexture.Type == TextureType.TT_2dArray ? newTexture.Depth : 6;
                    uint[] mipOffsets = new uint[newTexture.MipCount];
                    for (int i = 0; i < newTexture.MipCount - 1; i++)
                        mipOffsets[i + 1] = mipOffsets[i] + (uint)(newTexture.MipSizes[i] * sliceCount);

                    byte[] tmpBuf = new byte[newTexture.MipSizes[0]];
                    for (int slice = 0; slice < sliceCount; slice++)
                    {
                        for (int mip = 0; mip < newTexture.MipCount; mip++)
                        {
                            int mipSize = (int)newTexture.MipSizes[mip];
                            srcStream.Read(tmpBuf, 0, mipSize);
                            dstStream.Position = mipOffsets[mip] + (mipSize * slice);
                            dstStream.Write(tmpBuf, 0, mipSize);
                        }
                    }
                    buffer = dstStream.ToArray();
                }
            }

            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(oldTexture.ChunkId);
            Guid chunkId = oldTexture.ChunkId;
            Texture chunkTexture = ((newTexture.Flags & TextureFlags.OnDemandLoaded) != 0 ||
                newTexture.Type != TextureType.TT_2d) ? null : newTexture;

            if (ProfilesLibrary.MustAddChunks && chunkEntry != null && chunkEntry.Bundles.Count == 0 && !chunkEntry.IsAdded)
            {
                List<int> sbIds = chunkEntry.SuperBundles;
                chunkId = App.AssetManager.AddChunk(buffer, null, chunkTexture);
                chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
                if (chunkEntry != null)
                    chunkEntry.AddedSuperBundles.AddRange(sbIds);
            }
            else
            {
                App.AssetManager.ModifyChunk(chunkId, buffer, chunkTexture);
            }

            for (int i = 0; i < 4; i++)
                newTexture.Unknown3[i] = oldTexture.Unknown3[i];

            newTexture.SetData(chunkId, App.AssetManager);
            newTexture.AssetNameHash = unchecked((uint)Utils.HashString(resEntry.Name));
            oldTexture.Dispose();

            App.AssetManager.ModifyRes(resEntry.ResRid, newTexture);
            if (chunkEntry != null)
                resEntry.LinkAsset(chunkEntry);
            entry.LinkAsset(resEntry);
            App.AssetManager.ModifyEbx(entry.Name, asset);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["path"] = Path.GetFullPath(ddsPath),
                ["texture_type"] = newTexture.Type.ToString(),
                ["width"] = newTexture.Width,
                ["height"] = newTexture.Height,
                ["format"] = newTexture.PixelFormat,
                ["mip_count"] = newTexture.MipCount,
                ["chunk_id"] = chunkId.ToString(),
                ["bytes"] = buffer.Length,
                ["metadata_changed"] = true,
                ["asset"] = McpHandlers.SerializeEbxEntry(entry)
            };
        }

        // Same mapping as TexturePlugin's FrostyTextureEditor.GetPixelFormat.
        private static void GetTexturePixelFormat(TextureUtils.DDSHeader header, Texture oldTexture,
            out string pixelFormat, out TextureFlags flags)
        {
            pixelFormat = "Unknown";
            flags = 0;

            if (ProfilesLibrary.IsLoaded(ProfileVersion.DragonAgeInquisition, ProfileVersion.Battlefield4,
                ProfileVersion.PlantsVsZombiesGardenWarfare, ProfileVersion.NeedForSpeedRivals))
            {
                if (header.ddspf.dwFourCC == 0x31545844)
                {
                    pixelFormat = "BC1_UNORM";
                    if (oldTexture.PixelFormat.Contains("Normal"))
                        pixelFormat = oldTexture.PixelFormat;
                    else if (oldTexture.PixelFormat.StartsWith("BC1A"))
                        pixelFormat = oldTexture.PixelFormat;
                }
                else if (header.ddspf.dwFourCC == 0x32495441 || header.ddspf.dwFourCC == 0x55354342)
                    pixelFormat = "NormalDXN";
                else if (header.ddspf.dwFourCC == 0x33545844)
                    pixelFormat = "BC2_UNORM";
                else if (header.ddspf.dwFourCC == 0x35545844)
                    pixelFormat = "BC3_UNORM";
                else if (header.ddspf.dwFourCC == 0x31495441)
                    pixelFormat = "BC3A_UNORM";
                else if (header.HasExtendedHeader)
                {
                    switch (header.ExtendedHeader.dxgiFormat)
                    {
                        case SharpDX.DXGI.Format.R32G32B32A32_Float: pixelFormat = "ARGB32F"; break;
                        case SharpDX.DXGI.Format.R9G9B9E5_Sharedexp: pixelFormat = "R9G9B9E5F"; break;
                        case SharpDX.DXGI.Format.R8_UNorm: pixelFormat = "L8"; break;
                        case SharpDX.DXGI.Format.R16_UNorm: pixelFormat = "L16"; break;
                        case SharpDX.DXGI.Format.R8G8B8A8_UNorm: pixelFormat = "ARGB8888"; break;
                        case SharpDX.DXGI.Format.BC1_UNorm:
                            pixelFormat = "BC1_UNORM";
                            if (oldTexture.PixelFormat.Contains("Normal") || oldTexture.PixelFormat.StartsWith("BC1A"))
                                pixelFormat = oldTexture.PixelFormat;
                            break;
                        case SharpDX.DXGI.Format.BC2_UNorm: pixelFormat = "BC2_UNORM"; break;
                        case SharpDX.DXGI.Format.BC3_UNorm: pixelFormat = "BC3_UNORM"; break;
                        case SharpDX.DXGI.Format.BC5_UNorm: pixelFormat = "NormalDXN"; break;
                        case SharpDX.DXGI.Format.BC7_UNorm: pixelFormat = "BC7_UNORM"; break;
                        case SharpDX.DXGI.Format.BC1_UNorm_SRgb: pixelFormat = "BC1_UNORM"; flags = TextureFlags.SrgbGamma; break;
                        case SharpDX.DXGI.Format.BC2_UNorm_SRgb: pixelFormat = "BC2_UNORM"; flags = TextureFlags.SrgbGamma; break;
                        case SharpDX.DXGI.Format.BC3_UNorm_SRgb:
                            pixelFormat = oldTexture.PixelFormat == "BC3A_UNORM" ? oldTexture.PixelFormat : "BC3_UNORM";
                            flags = TextureFlags.SrgbGamma;
                            break;
                        case SharpDX.DXGI.Format.BC7_UNorm_SRgb: pixelFormat = "BC7_UNORM"; flags = TextureFlags.SrgbGamma; break;
                    }
                }
            }
            else
            {
                if (header.ddspf.dwFourCC == 0)
                {
                    if (header.ddspf.dwRBitMask == 0x000000FF && header.ddspf.dwGBitMask == 0x0000FF00 &&
                        header.ddspf.dwBBitMask == 0x00FF0000 && header.ddspf.dwABitMask == 0xFF000000)
                        pixelFormat = "R8G8B8A8_UNORM";
                }
                else if (header.ddspf.dwFourCC == 0x31545844)
                {
                    pixelFormat = "BC1_UNORM";
                    if (oldTexture.PixelFormat == "BC1A_UNORM")
                        pixelFormat = "BC1A_UNORM";
                }
                else if (header.ddspf.dwFourCC == 0x35545844)
                    pixelFormat = "BC3_UNORM";
                else if (header.ddspf.dwFourCC == 0x31495441)
                    pixelFormat = "BC4_UNORM";
                else if (header.ddspf.dwFourCC == 0x32495441 || header.ddspf.dwFourCC == 0x55354342)
                    pixelFormat = "BC5_UNORM";
                else if (header.HasExtendedHeader)
                {
                    if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC1_UNorm)
                    {
                        pixelFormat = "BC1_UNORM";
                        if (oldTexture.PixelFormat == "BC1A_UNORM")
                            pixelFormat = "BC1A_UNORM";
                    }
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC3_UNorm)
                        pixelFormat = "BC3_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC4_UNorm)
                        pixelFormat = "BC4_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC5_UNorm)
                        pixelFormat = "BC5_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC1_UNorm_SRgb && oldTexture.PixelFormat == "BC1A_SRGB")
                        pixelFormat = "BC1A_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC1_UNorm_SRgb)
                        pixelFormat = "BC1_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC3_UNorm_SRgb)
                        pixelFormat = "BC3_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC6H_Uf16)
                        pixelFormat = "BC6U_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC7_UNorm)
                        pixelFormat = "BC7_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.BC7_UNorm_SRgb)
                        pixelFormat = "BC7_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R8_UNorm)
                        pixelFormat = "R8_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R16G16B16A16_Float)
                        pixelFormat = "R16G16B16A16_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R32G32B32A32_Float)
                        pixelFormat = "R32G32B32A32_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R9G9B9E5_Sharedexp)
                        pixelFormat = "R9G9B9E5_FLOAT";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R8G8B8A8_UNorm)
                        pixelFormat = "R8G8B8A8_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R8G8B8A8_UNorm_SRgb)
                        pixelFormat = "R8G8B8A8_SRGB";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R10G10B10A2_UNorm)
                        pixelFormat = "R10G10B10A2_UNORM";
                    else if (header.ExtendedHeader.dxgiFormat == SharpDX.DXGI.Format.R16_UNorm)
                    {
                        pixelFormat = "R16_UNORM";
                        if (oldTexture.PixelFormat == "D16_UNORM")
                            pixelFormat = "D16_UNORM";
                    }
                }
            }
        }

        private static void ExportMeshViaReflection(EbxAssetEntry entry, string path, string format, string version, string scale, bool flatten, bool singleLod, bool nonRenderable, string skeleton)
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
                meshAsset, path, fbxVer, scale, flatten, singleLod, nonRenderable, skeleton,
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
            if (import == null)
                throw new InvalidOperationException("ImportFBX not found in MeshSetPlugin");

            object task = McpHandlers.InvokeOnUi(() => CreateTaskWindow());
            if (task == null)
                throw new InvalidOperationException("FrostyTaskWindow type is not available");

            McpHandlers.InvokeOnUi(() =>
                import.Invoke(importer, new object[] { path, meshSet, asset, entry, settings, task }));
        }

        private static object CreateTaskWindow()
        {
            Type taskType = FindType("Frosty.Core.Windows.FrostyTaskWindow");
            if (taskType == null)
                return null;
            ConstructorInfo ctor = taskType
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(c =>
                {
                    ParameterInfo[] ps = c.GetParameters();
                    return ps.Length == 6
                        && ps[0].ParameterType == typeof(Window)
                        && ps[1].ParameterType == typeof(string)
                        && ps[4].ParameterType == typeof(bool);
                });
            if (ctor == null)
                return null;
            try
            {
                return ctor.Invoke(new object[] { null, "Mesh import", "", null, false, null });
            }
            catch
            {
                return null;
            }
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
