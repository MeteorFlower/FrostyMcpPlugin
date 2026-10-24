using Frosty.Core;
using Frosty.Core.Controls;
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
using System.Linq;
using System.Reflection;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Extra MCP tools inspired by BF1DevPlugin.Base.Core / BundleEditorPlugin / BlueprintEditor hashing.
    /// </summary>
    internal static class McpExtraHandlers
    {
        // =====================================================================
        // Hashing
        // =====================================================================

        public static object HashString(JObject p)
        {
            string text = p.Value<string>("text") ?? p.Value<string>("value") ?? "";
            if (string.IsNullOrEmpty(text) && p["float"] == null)
                return McpHandlers.Error("text (or float) is required", "INVALID_PARAMS");

            bool lowercase = p.Value<bool?>("lowercase") ?? false;
            var result = new Dictionary<string, object> { ["success"] = true };

            if (!string.IsNullOrEmpty(text))
            {
                string src = lowercase ? text.ToLowerInvariant() : text;
                int fnv = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(text.Substring(2), NumberStyles.AllowHexSpecifier)
                    : Utils.HashString(src);
                uint fnvU = unchecked((uint)fnv);

                // BF1DevPlugin localized hash: hashId = char + 33 * hashId, start uint.MaxValue
                uint loc = uint.MaxValue;
                string locSrc = lowercase ? text.ToLowerInvariant() : text;
                for (int i = 0; i < locSrc.Length; i++)
                    loc = locSrc[i] + 33u * loc;

                result["text"] = text;
                result["fnv1"] = fnv;
                result["fnv1_hex"] = "0x" + fnvU.ToString("X8");
                result["fnv1_uint"] = fnvU;
                result["localized_hash"] = loc;
                result["localized_hash_hex"] = "0x" + loc.ToString("X8");
            }

            if (p["float"] != null)
            {
                float f = Convert.ToSingle(p["float"].ToString(), CultureInfo.InvariantCulture);
                byte[] bytes = BitConverter.GetBytes(f);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                int hexInt = BitConverter.ToInt32(bytes, 0);
                result["float"] = f;
                result["float_hex"] = "0x" + unchecked((uint)hexInt).ToString("X8");
                result["float_hex_int"] = hexInt;
            }

            return result;
        }

        // =====================================================================
        // Localization
        // =====================================================================

        public static object GetLocalizedString(JObject p)
        {
            if (LocalizedStringDatabase.Current == null)
                return McpHandlers.Error("LocalizedStringDatabase not ready", "NOT_READY");

            string idStr = (p.Value<string>("id") ?? p.Value<string>("string_id") ?? "").Trim();
            if (string.IsNullOrEmpty(idStr) && p["hash"] == null)
                return McpHandlers.Error("id (ID_...) or hash (uint) is required", "INVALID_PARAMS");

            try
            {
                string value;
                object idOut;
                if (p["hash"] != null || LooksLikeHash(idStr))
                {
                    uint hash = p["hash"] != null
                        ? Convert.ToUInt32(p["hash"].ToString(), CultureInfo.InvariantCulture)
                        : ParseHash(idStr);
                    value = LocalizedStringDatabase.Current.GetString(hash);
                    idOut = hash;
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["hash"] = hash,
                        ["hash_hex"] = "0x" + hash.ToString("X8"),
                        ["value"] = value,
                        ["edited"] = LocalizedStringDatabase.Current.isStringEdited(hash)
                    };
                }

                value = LocalizedStringDatabase.Current.GetString(idStr);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["id"] = idStr,
                    ["value"] = value
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "GET_FAILED");
            }
        }

        public static object SetLocalizedString(JObject p)
        {
            if (LocalizedStringDatabase.Current == null)
                return McpHandlers.Error("LocalizedStringDatabase not ready", "NOT_READY");

            string value = p.Value<string>("value");
            if (value == null)
                return McpHandlers.Error("value is required", "INVALID_PARAMS");

            string idStr = (p.Value<string>("id") ?? p.Value<string>("string_id") ?? "").Trim();
            try
            {
                if (p["hash"] != null || LooksLikeHash(idStr))
                {
                    uint hash = p["hash"] != null
                        ? Convert.ToUInt32(p["hash"].ToString(), CultureInfo.InvariantCulture)
                        : ParseHash(idStr);
                    LocalizedStringDatabase.Current.SetString(hash, value);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["hash"] = hash,
                        ["hash_hex"] = "0x" + hash.ToString("X8"),
                        ["value"] = LocalizedStringDatabase.Current.GetString(hash),
                        ["edited"] = LocalizedStringDatabase.Current.isStringEdited(hash)
                    };
                }

                if (string.IsNullOrEmpty(idStr))
                    return McpHandlers.Error("id or hash is required", "INVALID_PARAMS");

                LocalizedStringDatabase.Current.SetString(idStr, value);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["id"] = idStr,
                    ["value"] = LocalizedStringDatabase.Current.GetString(idStr)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FAILED");
            }
        }

        public static object RevertLocalizedString(JObject p)
        {
            if (LocalizedStringDatabase.Current == null)
                return McpHandlers.Error("LocalizedStringDatabase not ready", "NOT_READY");

            try
            {
                uint hash;
                if (p["hash"] != null)
                    hash = Convert.ToUInt32(p["hash"].ToString(), CultureInfo.InvariantCulture);
                else
                {
                    string idStr = (p.Value<string>("id") ?? "").Trim();
                    if (!LooksLikeHash(idStr))
                        return McpHandlers.Error("hash (uint) is required for revert", "INVALID_PARAMS");
                    hash = ParseHash(idStr);
                }

                LocalizedStringDatabase.Current.RevertString(hash);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["hash"] = hash,
                    ["value"] = LocalizedStringDatabase.Current.GetString(hash),
                    ["edited"] = LocalizedStringDatabase.Current.isStringEdited(hash)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REVERT_FAILED");
            }
        }

        public static object ListLocalizedStrings(JObject p)
        {
            if (LocalizedStringDatabase.Current == null)
                return McpHandlers.Error("LocalizedStringDatabase not ready", "NOT_READY");

            string query = (p.Value<string>("query") ?? "").Trim();
            bool modifiedOnly = p.Value<bool?>("modified_only") ?? false;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 100);

            try
            {
                IEnumerable<uint> ids = modifiedOnly
                    ? LocalizedStringDatabase.Current.EnumerateModifiedStrings()
                    : LocalizedStringDatabase.Current.EnumerateStrings();

                var matches = new List<object>();
                int total = 0;
                foreach (uint id in ids)
                {
                    string value = LocalizedStringDatabase.Current.GetString(id) ?? "";
                    if (!string.IsNullOrEmpty(query))
                    {
                        string hex = id.ToString("X");
                        if (value.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                            hex.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }

                    total++;
                    if (total <= offset)
                        continue;
                    if (matches.Count >= limit)
                        continue;

                    matches.Add(new Dictionary<string, object>
                    {
                        ["hash"] = id,
                        ["hash_hex"] = "0x" + id.ToString("X8"),
                        ["value"] = value,
                        ["edited"] = LocalizedStringDatabase.Current.isStringEdited(id)
                    });
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["query"] = query,
                    ["modified_only"] = modifiedOnly,
                    ["total"] = total,
                    ["offset"] = offset,
                    ["limit"] = limit,
                    ["returned"] = matches.Count,
                    ["strings"] = matches
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "LIST_FAILED");
            }
        }

        // =====================================================================
        // Clipboard paste (BF1DevPlugin Core.GetFrostyClipboardPasteData)
        // =====================================================================

        public static object ClipboardPasteObject(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry targetEntry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (targetEntry == null)
                return McpHandlers.Error("Target EBX not found", "NOT_FOUND");

            EbxAssetEntry sourceEntry = McpHandlers.ResolveEbxEntry(
                !string.IsNullOrEmpty(p.Value<string>("source_name")) ? p.Value<string>("source_name") : p.Value<string>("name"),
                !string.IsNullOrEmpty(p.Value<string>("source_guid")) ? p.Value<string>("source_guid") : p.Value<string>("guid"));
            if (sourceEntry == null)
                return McpHandlers.Error("Source EBX not found", "NOT_FOUND");

            string classGuidStr = (p.Value<string>("class_guid") ?? p.Value<string>("source_class_guid") ?? "").Trim();
            int sourceIndex = p.Value<int?>("source_index") ?? p.Value<int?>("index") ?? -1;
            if (string.IsNullOrEmpty(classGuidStr) && sourceIndex < 0)
                return McpHandlers.Error("source class_guid or source_index is required", "INVALID_PARAMS");

            try
            {
                EbxAsset sourceAsset = App.AssetManager.GetEbx(sourceEntry);
                EbxAsset targetAsset = App.AssetManager.GetEbx(targetEntry);

                object sourceObj = null;
                if (!string.IsNullOrEmpty(classGuidStr) && Guid.TryParse(classGuidStr, out Guid cg))
                {
                    foreach (object o in sourceAsset.Objects)
                    {
                        try
                        {
                            if (((dynamic)o).GetInstanceGuid().ExportedGuid == cg)
                            {
                                sourceObj = o;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                else if (sourceIndex >= 0 && sourceIndex < sourceAsset.Objects.Count())
                {
                    sourceObj = sourceAsset.Objects.ElementAt(sourceIndex);
                }

                if (sourceObj == null)
                    return McpHandlers.Error("Source object not found", "NOT_FOUND");

                FrostyClipboard.Current.SetData(sourceObj);
                object copy = FrostyClipboard.Current.GetData(targetAsset, targetEntry);
                if (copy == null)
                    return McpHandlers.Error("Clipboard paste returned null", "PASTE_FAILED");

                // Ensure registered
                bool contained = false;
                foreach (object o in targetAsset.Objects)
                {
                    if (ReferenceEquals(o, copy)) { contained = true; break; }
                }
                if (!contained)
                    targetAsset.AddObject(copy);

                string arrayName = p.Value<string>("array") ?? "Objects";
                PointerRef pref = new PointerRef(copy);
                bool added = TryAddPointer(targetAsset.RootObject, arrayName, pref);
                if (!added && arrayName == "Objects")
                    added = TryAddPointer(targetAsset.RootObject, "Components", pref);

                targetAsset.Update();
                App.AssetManager.ModifyEbx(targetEntry.Name, targetAsset);

                Guid newGuid = Guid.Empty;
                try { newGuid = ((dynamic)copy).GetInstanceGuid().ExportedGuid; } catch { }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source"] = sourceEntry.Name,
                    ["target"] = targetEntry.Name,
                    ["added_to_array"] = added ? arrayName : null,
                    ["class_guid"] = newGuid.ToString(),
                    ["object_type"] = copy.GetType().Name
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "PASTE_FAILED");
            }
        }

        // =====================================================================
        // Create struct (no GUID) — BF1DevPlugin Core.CreateStructObject
        // =====================================================================

        public static object CreateStruct(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string typeName = (p.Value<string>("type") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("type is required (e.g. DataField, LinearTransform)", "INVALID_PARAMS");

            Type t = TypeLibrary.GetType(typeName);
            if (t == null)
                return McpHandlers.Error("Type not found: " + typeName, "TYPE_NOT_FOUND");

            try
            {
                // Bulk mode: properties = { "__bulk_file__": path, "__offset__": n, "__limit__": n }
                if (p["properties"] is JObject bulkOpts && bulkOpts["__bulk_file__"] != null)
                    return BulkCreateStructsFromFile(p, typeName, bulkOpts);

                object inst = TypeLibrary.CreateObject(typeName);
                if (inst == null)
                    return McpHandlers.Error("CreateObject returned null", "CREATE_FAILED");

                if (p["properties"] is JObject props)
                {
                    foreach (JProperty prop in props.Properties())
                    {
                        PropertyInfo pi = inst.GetType().GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pi == null || !pi.CanWrite) continue;
                        object converted = ConvertSimple(prop.Value, pi.PropertyType);
                        pi.SetValue(inst, converted);
                    }
                }

                // Optional: append to list path on an EBX host
                string ebxName = p.Value<string>("name");
                string path = (p.Value<string>("path") ?? "").Trim();
                if (!string.IsNullOrEmpty(ebxName) && !string.IsNullOrEmpty(path))
                {
                    EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(ebxName, p.Value<string>("guid"));
                    if (entry == null)
                        return McpHandlers.Error("EBX not found for path assign", "NOT_FOUND");
                    EbxAsset asset = App.AssetManager.GetEbx(entry);
                    object host = asset.RootObject;
                    string classGuid = (p.Value<string>("class_guid") ?? "").Trim();
                    if (!string.IsNullOrEmpty(classGuid) && Guid.TryParse(classGuid, out Guid cg))
                    {
                        foreach (object o in asset.Objects)
                        {
                            try
                            {
                                if (((dynamic)o).GetInstanceGuid().ExportedGuid == cg) { host = o; break; }
                            }
                            catch { }
                        }
                    }

                    AppendOrSetStruct(host, path, inst);
                    asset.Update();
                    App.AssetManager.ModifyEbx(entry.Name, asset);

                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["type"] = typeName,
                        ["assigned_to"] = entry.Name,
                        ["path"] = path
                    };
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["type"] = typeName,
                    ["note"] = "Struct created in-memory only; provide name+path to append into an EBX list"
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "CREATE_FAILED");
            }
        }

        // Bulk-create structs from a JSON file, one object per entry, appended to a list path
        private static object BulkCreateStructsFromFile(JObject p, string typeName, JObject opts)
        {
            string file = (opts.Value<string>("__bulk_file__") ?? "").Trim();
            if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
                return McpHandlers.Error("bulk file not found: " + file, "NOT_FOUND");

            JArray items;
            try
            {
                items = JArray.Parse(System.IO.File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                return McpHandlers.Error("bulk file parse failed: " + ex.Message, "PARSE_FAILED");
            }

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found for bulk assign", "NOT_FOUND");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return McpHandlers.Error("path is required for bulk assign", "INVALID_PARAMS");

            int offset = Math.Max(0, opts.Value<int?>("__offset__") ?? 0);
            int limit = opts.Value<int?>("__limit__") ?? int.MaxValue;

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            object host = asset.RootObject;
            string classGuid = (p.Value<string>("class_guid") ?? "").Trim();
            if (!string.IsNullOrEmpty(classGuid) && Guid.TryParse(classGuid, out Guid cg))
            {
                foreach (object o in asset.Objects)
                {
                    try
                    {
                        if (((dynamic)o).GetInstanceGuid().ExportedGuid == cg) { host = o; break; }
                    }
                    catch { }
                }
            }

            int created = 0;
            List<string> errors = new List<string>();
            int end = Math.Min(items.Count, offset + limit);
            for (int i = offset; i < end; i++)
            {
                if (!(items[i] is JObject item))
                    continue;
                try
                {
                    object inst = TypeLibrary.CreateObject(typeName);
                    if (inst == null)
                    {
                        errors.Add("item " + i + ": CreateObject returned null");
                        continue;
                    }
                    foreach (JProperty prop in item.Properties())
                    {
                        PropertyInfo pi = inst.GetType().GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pi == null || !pi.CanWrite)
                            continue;
                        pi.SetValue(inst, ConvertSimple(prop.Value, pi.PropertyType));
                    }
                    AppendOrSetStruct(host, path, inst);
                    created++;
                }
                catch (Exception ex)
                {
                    errors.Add("item " + i + ": " + ex.Message);
                }
            }

            asset.Update();
            App.AssetManager.ModifyEbx(entry.Name, asset);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["type"] = typeName,
                ["assigned_to"] = entry.Name,
                ["path"] = path,
                ["created"] = created,
                ["total"] = items.Count,
                ["offset"] = offset,
                ["failed"] = errors.Count,
                ["errors"] = errors.GetRange(0, Math.Min(errors.Count, 20))
            };
        }

        // =====================================================================
        // Texture / Mesh info
        // =====================================================================

        public static object GetTextureInfo(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Texture EBX not found", "NOT_FOUND");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                ulong rid = Convert.ToUInt64(root.Resource);
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                if (resEntry == null)
                    return McpHandlers.Error("Linked Texture RES not found", "NOT_FOUND");

                Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(texture.ChunkId);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["type"] = entry.Type,
                    ["width"] = texture.Width,
                    ["height"] = texture.Height,
                    ["depth"] = texture.Depth,
                    ["mip_count"] = texture.MipCount,
                    ["first_mip"] = texture.FirstMip,
                    ["pixel_format"] = texture.PixelFormat,
                    ["texture_type"] = texture.Type.ToString(),
                    ["flags"] = texture.Flags.ToString(),
                    ["texture_group"] = texture.TextureGroup,
                    ["asset_name_hash"] = texture.AssetNameHash,
                    ["res_name"] = resEntry.Name,
                    ["res_rid"] = resEntry.ResRid.ToString(),
                    ["chunk_id"] = texture.ChunkId.ToString(),
                    ["chunk_size"] = chunk?.Size,
                    ["bundles"] = entry.EnumerateBundles().Select(id => App.AssetManager.GetBundleEntry(id)?.Name).Where(n => n != null).ToList()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "INFO_FAILED");
            }
        }

        public static object GetMeshInfo(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Mesh EBX not found", "NOT_FOUND");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                ulong rid = Convert.ToUInt64(root.MeshSetResource);
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                if (resEntry == null)
                    return McpHandlers.Error("Linked MeshSet RES not found", "NOT_FOUND");

                Type meshSetType = FindType("MeshSetPlugin.Resources.MeshSet");
                if (meshSetType == null)
                    return McpHandlers.Error("MeshSetPlugin not loaded", "PLUGIN_MISSING");

                object meshSet = GetResAs(meshSetType, resEntry);

                var lods = new List<object>();
                IList lodList = (IList)meshSetType.GetProperty("Lods").GetValue(meshSet);
                for (int i = 0; i < lodList.Count; i++)
                {
                    object lod = lodList[i];
                    Type lodType = lod.GetType();
                    Guid chunkId = Guid.Empty;
                    try { chunkId = (Guid)lodType.GetProperty("ChunkId").GetValue(lod); } catch { }
                    int sectionCount = 0;
                    try { sectionCount = (int)lodType.GetProperty("SectionCount").GetValue(lod); } catch { }
                    int boneCount = 0;
                    try { boneCount = (int)lodType.GetProperty("BoneCount").GetValue(lod); } catch { }
                    string fullName = null;
                    try { fullName = lodType.GetProperty("FullName")?.GetValue(lod)?.ToString(); } catch { }
                    object meshType = null;
                    try { meshType = lodType.GetProperty("Type")?.GetValue(lod); } catch { }

                    lods.Add(new Dictionary<string, object>
                    {
                        ["index"] = i,
                        ["full_name"] = fullName,
                        ["type"] = meshType?.ToString(),
                        ["section_count"] = sectionCount,
                        ["bone_count"] = boneCount,
                        ["chunk_id"] = chunkId == Guid.Empty ? null : chunkId.ToString()
                    });
                }

                object rootType = meshSetType.GetProperty("Type")?.GetValue(meshSet);
                string meshFullName = meshSetType.GetProperty("FullName")?.GetValue(meshSet)?.ToString();
                object nameHash = meshSetType.GetProperty("NameHash")?.GetValue(meshSet);

                // Materials from EBX if present
                var materials = new List<object>();
                try
                {
                    IList mats = root.Materials;
                    for (int i = 0; i < mats.Count; i++)
                    {
                        dynamic m = mats[i];
                        string matName = null;
                        try
                        {
                            PointerRef pr = m.Shader;
                            if (pr.Type == PointerRefType.External)
                                matName = App.AssetManager.GetEbxEntry(pr.External.FileGuid)?.Name;
                            else if (pr.Type == PointerRefType.Internal && pr.Internal != null)
                                matName = pr.Internal.GetType().Name;
                        }
                        catch { }
                        materials.Add(new Dictionary<string, object> { ["index"] = i, ["shader"] = matName });
                    }
                }
                catch { }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["type"] = entry.Type,
                    ["mesh_type"] = rootType?.ToString(),
                    ["full_name"] = meshFullName,
                    ["name_hash"] = nameHash,
                    ["res_name"] = resEntry.Name,
                    ["res_rid"] = resEntry.ResRid.ToString(),
                    ["lod_count"] = lods.Count,
                    ["lods"] = lods,
                    ["materials"] = materials,
                    ["bundles"] = entry.EnumerateBundles().Select(id => App.AssetManager.GetBundleEntry(id)?.Name).Where(n => n != null).ToList()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "INFO_FAILED");
            }
        }

        // =====================================================================
        // Smart add/remove bundle (BundleEditorPlugin patterns)
        // =====================================================================

        public static object AddToBundleSmart(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = App.AssetManager.GetBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");

            var linked = new List<string>();

            try
            {
                entry.AddToBundle(bid);
                linked.Add("ebx:" + entry.Name);

                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                string type = entry.Type ?? "";

                if (TypeLibrary.IsSubClassOf(type, "TextureBaseAsset") || type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        ResAssetEntry resEntry = App.AssetManager.GetResEntry((ulong)root.Resource);
                        if (resEntry != null)
                        {
                            resEntry.AddToBundle(bid);
                            linked.Add("res:" + resEntry.Name);
                            Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
                            ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(texture.ChunkId);
                            if (chunk != null)
                            {
                                chunk.AddToBundle(bid);
                                chunk.FirstMip = texture.FirstMip;
                                linked.Add("chunk:" + chunk.Id);
                                resEntry.LinkAsset(chunk);
                            }
                            entry.LinkAsset(resEntry);
                        }
                    }
                    catch (Exception ex) { linked.Add("texture_error:" + ex.Message); }
                }
                else if (TypeLibrary.IsSubClassOf(type, "MeshAsset") || type.EndsWith("MeshAsset", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ResAssetEntry resEntry = App.AssetManager.GetResEntry((ulong)root.MeshSetResource);
                        if (resEntry != null)
                        {
                            resEntry.AddToBundle(bid);
                            linked.Add("res:" + resEntry.Name);
                            entry.LinkAsset(resEntry);

                            Type meshSetType = FindType("MeshSetPlugin.Resources.MeshSet");
                            if (meshSetType != null)
                            {
                                object meshSet = GetResAs(meshSetType, resEntry);
                                IList lods = (IList)meshSetType.GetProperty("Lods").GetValue(meshSet);
                                foreach (object lod in lods)
                                {
                                    Guid chunkId = (Guid)lod.GetType().GetProperty("ChunkId").GetValue(lod);
                                    if (chunkId == Guid.Empty) continue;
                                    ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(chunkId);
                                    if (chunk == null) continue;
                                    chunk.AddToBundle(bid);
                                    resEntry.LinkAsset(chunk);
                                    linked.Add("chunk:" + chunkId);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { linked.Add("mesh_error:" + ex.Message); }
                }
                else if (type.IndexOf("SoundWave", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        // Common pattern: Chunks array of guids / PointerRefs
                        PropertyInfo chunksProp = ((object)root).GetType().GetProperty("Chunks");
                        if (chunksProp != null && chunksProp.GetValue(root) is IList chunks)
                        {
                            foreach (object item in chunks)
                            {
                                Guid cid = Guid.Empty;
                                if (item is Guid g) cid = g;
                                else
                                {
                                    try { cid = (Guid)item.GetType().GetProperty("ChunkId").GetValue(item); } catch { }
                                }
                                if (cid == Guid.Empty) continue;
                                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(cid);
                                if (chunk == null) continue;
                                chunk.AddToBundle(bid);
                                entry.LinkAsset(chunk);
                                linked.Add("chunk:" + cid);
                            }
                        }
                    }
                    catch (Exception ex) { linked.Add("sound_error:" + ex.Message); }
                }
                else
                {
                    // Generic: try Resource property
                    try
                    {
                        PropertyInfo resProp = ((object)root).GetType().GetProperty("Resource");
                        if (resProp != null)
                        {
                            ulong rid = Convert.ToUInt64(resProp.GetValue(root));
                            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                            if (resEntry != null)
                            {
                                resEntry.AddToBundle(bid);
                                entry.LinkAsset(resEntry);
                                linked.Add("res:" + resEntry.Name);
                            }
                        }
                    }
                    catch { }
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundleName,
                    ["bundle_id"] = bid,
                    ["linked"] = linked
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static bool LooksLikeHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        private static uint ParseHash(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.Parse(s.Substring(2), NumberStyles.AllowHexSpecifier);
            return uint.Parse(s, CultureInfo.InvariantCulture);
        }

        private static bool TryAddPointer(object root, string arrayName, PointerRef pref)
        {
            PropertyInfo pi = root.GetType().GetProperty(arrayName);
            if (pi == null) return false;
            if (pi.GetValue(root) is IList list)
            {
                list.Add(pref);
                return true;
            }
            return false;
        }

        private static void AppendOrSetStruct(object host, string path, object inst)
        {
            string[] parts = path.Split('.');
            object current = host;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = parts[i];
                string name = part;
                int idx = -1;
                int b = part.IndexOf('[');
                if (b >= 0 && part.EndsWith("]"))
                {
                    name = part.Substring(0, b);
                    idx = int.Parse(part.Substring(b + 1, part.Length - b - 2));
                }
                if (!string.IsNullOrEmpty(name))
                    current = current.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase).GetValue(current);
                if (idx >= 0)
                    current = ((IList)current)[idx];
            }

            string last = parts[parts.Length - 1];
            string lastName = last;
            int lastIdx = -1;
            int lb = last.IndexOf('[');
            if (lb >= 0 && last.EndsWith("]"))
            {
                lastName = last.Substring(0, lb);
                lastIdx = int.Parse(last.Substring(lb + 1, last.Length - lb - 2));
            }

            if (lastIdx >= 0)
            {
                object listObj = string.IsNullOrEmpty(lastName) ? current :
                    current.GetType().GetProperty(lastName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase).GetValue(current);
                ((IList)listObj)[lastIdx] = inst;
            }
            else
            {
                PropertyInfo pi = current.GetType().GetProperty(lastName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?? throw new InvalidOperationException("Missing property: " + lastName);
                object val = pi.GetValue(current);
                if (val is IList list)
                    list.Add(inst);
                else if (pi.CanWrite)
                    pi.SetValue(current, inst);
                else
                    throw new InvalidOperationException("Cannot assign to " + lastName);
            }
        }

        private static object ConvertSimple(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            if (targetType == typeof(CString) || targetType.Name == "CString")
                return (CString)token.ToString().Trim('"');
            if (targetType.IsEnum)
            {
                try { return Enum.Parse(targetType, token.ToString(), true); }
                catch { return Enum.Parse(targetType, targetType.Name + "_" + token.ToString(), true); }
            }
            Type u = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (u == typeof(bool)) return token.Type == JTokenType.Boolean ? token.Value<bool>() : Convert.ToBoolean(token.ToString());
            if (u == typeof(float)) return Convert.ToSingle(token.ToString(), CultureInfo.InvariantCulture);
            if (u == typeof(double)) return Convert.ToDouble(token.ToString(), CultureInfo.InvariantCulture);
            if (u == typeof(int)) return Convert.ToInt32(token.ToString(), CultureInfo.InvariantCulture);
            if (u == typeof(uint)) return Convert.ToUInt32(token.ToString(), CultureInfo.InvariantCulture);
            if (u == typeof(string)) return token.ToString();
            if (u == typeof(Guid)) return Guid.Parse(token.ToString());
            return token.ToObject(targetType);
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

        private static object GetResAs(Type resourceType, ResAssetEntry resEntry)
        {
            MethodInfo getResAs = typeof(AssetManager).GetMethods()
                .First(m => m.Name == "GetResAs" && m.IsGenericMethodDefinition);
            MethodInfo closed = getResAs.MakeGenericMethod(resourceType);
            // GetResAs<T>(entry, modifiedData = null): reflection does not fill optional args
            object[] args = closed.GetParameters().Length > 1
                ? new object[] { resEntry, null }
                : new object[] { resEntry };
            return closed.Invoke(App.AssetManager, args);
        }
    }
}
