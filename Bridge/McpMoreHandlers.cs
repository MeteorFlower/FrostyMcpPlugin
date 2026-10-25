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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FrostySdk.Managers.Entries;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Additional tools: smart bundle remove, batch bundle, ebx guids,
    /// sound wav import/export (Pcm16Big), schematic channel create.
    /// </summary>
    internal static class McpMoreHandlers
    {
        // =====================================================================
        // Bundle / GUID utilities
        // =====================================================================

        public static object RemoveFromBundleSmart(JObject p)
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

            if (!entry.AddedBundles.Contains(bid) && entry.Bundles.Contains(bid))
                return McpHandlers.Error("Asset is a native member of this bundle; the removal cannot be expressed in a .fbmod", "NOT_EXPORTABLE");

            var targets = new List<AssetEntry>();
            var labels = new List<string>();
            var nativeSkipped = new List<string>();
            var errors = new List<object>();

            Action<AssetEntry, string> gather = (assetEntry, label) =>
            {
                if (assetEntry == null)
                    return;
                if (assetEntry.AddedBundles.Contains(bid))
                {
                    targets.Add(assetEntry);
                    labels.Add(label);
                }
                else if (assetEntry.Bundles.Contains(bid))
                {
                    nativeSkipped.Add(label);
                }
            };

            try
            {
                gather(entry, "ebx:" + entry.Name);

                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                string type = entry.Type ?? "";

                if (type.IndexOf("MovieTexture", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        Type rootType = ((object)root).GetType();
                        foreach (string chunkPropName in new[] { "ChunkGuid", "SubtitleChunkGuid" })
                        {
                            PropertyInfo chunkProp = rootType.GetProperty(chunkPropName);
                            if (chunkProp == null) continue;
                            object raw = chunkProp.GetValue(root);
                            Guid cid = raw is Guid g ? g : Guid.Empty;
                            if (cid == Guid.Empty) continue;
                            ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(cid);
                            if (chunk != null)
                                gather(chunk, "chunk:" + cid);
                        }
                    }
                    catch (Exception ex) { errors.Add(new Dictionary<string, object> { ["section"] = "movie", ["error"] = ex.Message }); }
                }
                else if (type.IndexOf("NewWave", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        ResAssetEntry resEntry = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant());
                        gather(resEntry, resEntry == null ? "res:" : "res:" + resEntry.Name);
                        if (((object)root).GetType().GetProperty("Chunks")?.GetValue(root) is IList chunks)
                        {
                            foreach (object item in chunks)
                            {
                                Guid cid = Guid.Empty;
                                if (item is Guid g) cid = g;
                                else { try { cid = (Guid)item.GetType().GetProperty("ChunkId").GetValue(item); } catch { } }
                                if (cid == Guid.Empty) continue;
                                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(cid);
                                if (chunk != null)
                                    gather(chunk, "chunk:" + cid);
                            }
                        }
                    }
                    catch (Exception ex) { errors.Add(new Dictionary<string, object> { ["section"] = "newwave", ["error"] = ex.Message }); }
                }
                else if (TypeLibrary.IsSubClassOf(type, "TextureBaseAsset") || type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        ResAssetEntry resEntry = App.AssetManager.GetResEntry((ulong)root.Resource);
                        if (resEntry != null)
                        {
                            gather(resEntry, "res:" + resEntry.Name);
                            Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
                            ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(texture.ChunkId);
                            if (chunk != null)
                                gather(chunk, "chunk:" + chunk.Id);
                        }
                    }
                    catch (Exception ex) { errors.Add(new Dictionary<string, object> { ["section"] = "texture", ["error"] = ex.Message }); }
                }
                else if (TypeLibrary.IsSubClassOf(type, "MeshAsset") || type.EndsWith("MeshAsset", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ResAssetEntry resEntry = App.AssetManager.GetResEntry((ulong)root.MeshSetResource);
                        if (resEntry != null)
                        {
                            gather(resEntry, "res:" + resEntry.Name);
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
                                    if (chunk != null)
                                        gather(chunk, "chunk:" + chunkId);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { errors.Add(new Dictionary<string, object> { ["section"] = "mesh", ["error"] = ex.Message }); }
                }
                else if (type.IndexOf("SoundWave", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        if (((object)root).GetType().GetProperty("Chunks")?.GetValue(root) is IList chunks)
                        {
                            foreach (object item in chunks)
                            {
                                Guid cid = Guid.Empty;
                                try { cid = (Guid)item.GetType().GetProperty("ChunkId").GetValue(item); } catch { }
                                if (cid == Guid.Empty) continue;
                                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(cid);
                                if (chunk != null)
                                    gather(chunk, "chunk:" + cid);
                            }
                        }
                    }
                    catch (Exception ex) { errors.Add(new Dictionary<string, object> { ["section"] = "sound", ["error"] = ex.Message }); }
                }
                else
                {
                    try
                    {
                        string resProperty = "Resource";
                        if (type.IndexOf("ClothWrapping", StringComparison.OrdinalIgnoreCase) >= 0)
                            resProperty = "ClothWrappingAssetResource";
                        else if (type.IndexOf("ClothColliderSet", StringComparison.OrdinalIgnoreCase) >= 0)
                            resProperty = "ClothColliderSetAssetResource";
                        else if (type.IndexOf("Cloth", StringComparison.OrdinalIgnoreCase) >= 0)
                            resProperty = "ClothAssetResource";
                        else if (type.IndexOf("Enlighten", StringComparison.OrdinalIgnoreCase) >= 0)
                            resProperty = "DatabaseResource";

                        PropertyInfo resProp = ((object)root).GetType().GetProperty(resProperty)
                            ?? ((object)root).GetType().GetProperty("CompiledLuaResource");
                        if (resProp != null)
                        {
                            ulong rid = Convert.ToUInt64(resProp.GetValue(root));
                            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                            if (resEntry != null)
                                gather(resEntry, "res:" + resEntry.Name);
                        }
                    }
                    catch (Exception ex) { errors.Add(new Dictionary<string, object> { ["section"] = "resource", ["error"] = ex.Message }); }
                }
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }

            if (targets.Count == 0)
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "No editor-added memberships to remove",
                    ["error_code"] = "NOT_MEMBER",
                    ["native_skipped"] = nativeSkipped,
                    ["errors"] = errors
                };

            var plan = new Dictionary<string, object>
            {
                ["name"] = entry.Name,
                ["bundle"] = bundleName,
                ["bundle_id"] = bid,
                ["unlinked"] = labels,
                ["unlinked_count"] = labels.Count,
                ["native_skipped"] = nativeSkipped,
                ["errors"] = errors
            };

            return McpCommit.Require("remove_from_bundle_smart", p, plan, () =>
            {
                var unlinked = new List<string>();
                for (int i = 0; i < targets.Count; i++)
                {
                    if (RemoveEntryFromBundle(targets[i], bid))
                        unlinked.Add(labels[i]);
                }
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["bundle"] = bundleName,
                    ["bundle_id"] = bid,
                    ["unlinked"] = unlinked,
                    ["unlinked_count"] = unlinked.Count,
                    ["native_skipped"] = nativeSkipped,
                    ["native_skipped_count"] = nativeSkipped.Count,
                    ["scan_errors"] = errors
                };
            });
        }

        public static object BatchAddToBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");

            bool smart = p.Value<bool?>("smart") ?? true;
            var names = new List<string>();
            if (p["names"] is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    string n = t?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            string single = (p.Value<string>("name") ?? "").Trim();
            if (!string.IsNullOrEmpty(single)) names.Add(single);
            if (names.Count == 0)
                return McpHandlers.Error("names[] or name is required", "INVALID_PARAMS");

            var results = new List<object>();
            int okCount = 0;
            foreach (string n in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var args = new JObject { ["name"] = n, ["bundle"] = bundleName };
                object r = smart ? McpExtraHandlers.AddToBundleSmart(args) : McpEditHandlers.AddToBundle(args);

                bool ok = false;
                var row = new Dictionary<string, object> { ["name"] = n };
                if (r is Dictionary<string, object> dict)
                {
                    ok = dict.TryGetValue("success", out object sv) && sv is bool sb && sb;
                    if (dict.TryGetValue("added", out object av)) row["added"] = av;
                    if (!ok)
                    {
                        if (dict.TryGetValue("error", out object msg)) row["error"] = msg;
                        if (dict.TryGetValue("error_code", out object code)) row["error_code"] = code;
                    }
                }
                row["success"] = ok;
                if (ok) okCount++;
                results.Add(row);
            }

            return new Dictionary<string, object>
            {
                ["success"] = okCount == results.Count,
                ["bundle"] = bundleName,
                ["smart"] = smart,
                ["count"] = results.Count,
                ["ok_count"] = okCount,
                ["failed_count"] = results.Count - okCount,
                ["results"] = results
            };
        }

        public static object GetEbxGuids(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            bool listObjects = p.Value<bool?>("list_objects") ?? false;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 500);
            EbxAsset asset = App.AssetManager.GetEbx(entry);

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["file_guid"] = entry.Guid.ToString(),
                ["root_class_guid"] = asset.RootInstanceGuid.ToString(),
                ["external_pointer"] = new Dictionary<string, object>
                {
                    ["file_guid"] = entry.Guid.ToString(),
                    ["class_guid"] = asset.RootInstanceGuid.ToString()
                }
            };

            if (listObjects)
            {
                var objs = new List<object>();
                int i = 0;
                foreach (object obj in asset.Objects)
                {
                    if (i >= offset && objs.Count < limit)
                    {
                        Guid cg = Guid.Empty;
                        try { cg = ((dynamic)obj).GetInstanceGuid().ExportedGuid; } catch { }
                        objs.Add(new Dictionary<string, object>
                        {
                            ["index"] = i,
                            ["type"] = obj.GetType().Name,
                            ["class_guid"] = cg.ToString()
                        });
                    }
                    i++;
                }
                result["objects"] = objs;
                result["object_count"] = i;
                result["offset"] = offset;
                result["limit"] = limit;
                result["returned"] = objs.Count;
            }

            return result;
        }

        // =====================================================================
        // Sound WAV (Pcm16Big)
        // =====================================================================

        public static object ExportSoundWav(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("SoundWave EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required (.wav)", "INVALID_PARAMS");

            int variationIndex = p.Value<int?>("variation") ?? p.Value<int?>("index") ?? 0;

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                if (variationIndex < 0 || variationIndex >= root.RuntimeVariations.Count)
                    return McpHandlers.Error("variation index out of range", "INVALID_PARAMS");

                dynamic variation = root.RuntimeVariations[variationIndex];
                dynamic soundDataChunk = root.Chunks[variation.ChunkIndex];
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry((Guid)soundDataChunk.ChunkId);
                if (chunkEntry == null)
                    return McpHandlers.Error("Chunk missing for variation", "NOT_FOUND");

                byte[] chunkBytes;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                {
                    // Read from first segment offset
                    dynamic segment = root.Segments[variation.FirstSegmentIndex];
                    long offset = (long)(uint)segment.SamplesOffset;
                    reader.Position = offset;
                    // If only one segment for this variation spanning to end/next
                    int size;
                    if ((int)(byte)variation.SegmentCount == 1 &&
                        variation.FirstSegmentIndex + 1 < root.Segments.Count &&
                        (uint)root.Segments[variation.FirstSegmentIndex + 1].SamplesOffset > offset)
                    {
                        size = (int)((uint)root.Segments[variation.FirstSegmentIndex + 1].SamplesOffset - offset);
                    }
                    else
                    {
                        size = (int)(reader.Length - offset);
                    }
                    if (size <= 0) size = (int)(reader.Length - offset);
                    chunkBytes = reader.ReadBytes(size);
                }

                // Detect codec
                byte codec = chunkBytes.Length > 8 ? chunkBytes[4] : (byte)0;
                short[] samples;
                int channels;
                int sampleRate;

                if (codec == 0x12)
                {
                    // Decode header ourselves for rate/channels, samples via Pcm16b if available
                    using (NativeReader hr = new NativeReader(new MemoryStream(chunkBytes)))
                    {
                        hr.ReadUShort();
                        hr.ReadUShort(Endian.Big);
                        hr.ReadByte(); // codec
                        channels = (hr.ReadByte() >> 2) + 1;
                        sampleRate = hr.ReadUShort(Endian.Big);
                    }

                    Type pcmType = FindType("SoundEditorPlugin.Pcm16b");
                    if (pcmType != null)
                    {
                        MethodInfo decode = pcmType.GetMethod("Decode", BindingFlags.Public | BindingFlags.Static);
                        samples = (short[])decode.Invoke(null, new object[] { chunkBytes });
                    }
                    else
                    {
                        samples = DecodePcm16Big(chunkBytes, out channels, out sampleRate);
                    }
                }
                else
                {
                    return McpHandlers.Error(
                        "Unsupported codec 0x" + codec.ToString("X2") + " (only Pcm16Big 0x12 supported for export)",
                        "UNSUPPORTED_CODEC");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                WriteWavFile(path, samples, channels, sampleRate);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["variation"] = variationIndex,
                    ["codec"] = "Pcm16Big",
                    ["channels"] = channels,
                    ["sample_rate"] = sampleRate,
                    ["samples"] = samples.Length / Math.Max(1, channels),
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object ImportSoundWav(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("SoundWave EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (.wav PCM16) is required", "INVALID_PARAMS");

            int variationIndex = p.Value<int?>("variation") ?? p.Value<int?>("index") ?? 0;

            try
            {
                short[] samples = ReadWavPcm16(path, out int channels, out int sampleRate);
                // Mono → duplicate to stereo (BF1 preference)
                if (channels == 1)
                {
                    short[] stereo = new short[samples.Length * 2];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        stereo[i * 2] = samples[i];
                        stereo[i * 2 + 1] = samples[i];
                    }
                    samples = stereo;
                    channels = 2;
                }

                byte[] pcm16Big = EncodePcm16Big(samples, channels, sampleRate);

                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                if (variationIndex < 0 || variationIndex >= root.RuntimeVariations.Count)
                    return McpHandlers.Error("variation index out of range", "INVALID_PARAMS");

                dynamic variation = root.RuntimeVariations[variationIndex];
                dynamic soundDataChunk = root.Chunks[variation.ChunkIndex];
                Guid chunkId = (Guid)soundDataChunk.ChunkId;
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
                if (chunkEntry == null)
                    return McpHandlers.Error("Chunk missing", "NOT_FOUND");

                // Simple replace for single-segment / dedicated chunk (BF1 simplified path)
                App.AssetManager.ModifyChunk(chunkEntry.Id, pcm16Big);
                soundDataChunk.ChunkSize = (uint)pcm16Big.Length;

                try { root.Seekable = false; } catch { }
                try
                {
                    root.Segments[variation.FirstSegmentIndex].SeekTableOffset = 4294967295u;
                    root.Segments[variation.FirstSegmentIndex].SamplesOffset = 0u;
                }
                catch { }

                variation.SegmentCount = (byte)1;
                try { variation.FirstLoopSegmentIndex = (byte)0; } catch { }
                try { variation.LastLoopSegmentIndex = (byte)0; } catch { }

                entry.LinkAsset(chunkEntry);
                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["variation"] = variationIndex,
                    ["chunk_id"] = chunkId.ToString(),
                    ["channels"] = channels,
                    ["sample_rate"] = sampleRate,
                    ["bytes"] = pcm16Big.Length,
                    ["note"] = "Replaced variation chunk as Pcm16Big; multi-segment shared chunks may need manual care"
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "IMPORT_FAILED");
            }
        }

        // =====================================================================
        // SchematicChannel
        // =====================================================================

        public static object CreateSchematicChannelAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string newName = (p.Value<string>("new_name") ?? p.Value<string>("name") ?? "").Trim().Trim('/');
            if (string.IsNullOrEmpty(newName))
                return McpHandlers.Error("new_name is required", "INVALID_PARAMS");

            if (App.AssetManager.GetEbxEntry(newName) != null)
                return McpHandlers.Error("Already exists: " + newName, "EXISTS");

            string template = (p.Value<string>("template") ?? "").Trim();
            EbxAssetEntry templateEntry = null;
            if (!string.IsNullOrEmpty(template))
                templateEntry = McpHandlers.ResolveEbxEntry(template, null);
            else
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(type: "SchematicChannelAsset"))
                {
                    templateEntry = e;
                    break;
                }
            }
            if (templateEntry == null)
                return McpHandlers.Error("No SchematicChannelAsset template (provide template=)", "NOT_FOUND");

            object dup = McpMediaHandlers.DuplicateDeep(new JObject
            {
                ["name"] = templateEntry.Name,
                ["guid"] = templateEntry.Guid.ToString(),
                ["new_name"] = newName
            });
            var dupDict = dup as Dictionary<string, object>;
            if (dupDict == null || !(dupDict["success"] is bool ok && ok))
                return dup;

            EbxAssetEntry newEntry = App.AssetManager.GetEbxEntry(newName);
            EbxAsset asset = App.AssetManager.GetEbx(newEntry);
            dynamic root = asset.RootObject;

            // Clear existing channels if requested
            bool clear = p.Value<bool?>("clear") ?? true;
            if (clear)
            {
                try { root.Events.Clear(); } catch { }
                try { root.Links.Clear(); } catch { }
                try { root.Properties.Clear(); } catch { }
            }

            // events: ["ClientAndServer", ...] or [{realm:"..."}]
            if (p["events"] is JArray events)
            {
                foreach (JToken t in events)
                {
                    string realm = t is JObject jo ? (jo.Value<string>("realm") ?? "ClientAndServer") : t.ToString();
                    object ch = TypeLibrary.CreateObject("EventChannel");
                    SetEnumSafe(ch, "Realm", realm);
                    root.Events.Add(ch);
                }
            }

            if (p["links"] is JArray links)
            {
                foreach (JToken t in links)
                {
                    if (!(t is JObject jo)) continue;
                    object ch = TypeLibrary.CreateObject("LinkChannel");
                    SetEnumSafe(ch, "Realm", jo.Value<string>("realm") ?? "ClientAndServer");
                    string id = jo.Value<string>("id") ?? "";
                    string linkType = jo.Value<string>("link_type") ?? "";
                    SetIntProp(ch, "Id", Utils.HashString(id));
                    SetIntProp(ch, "LinkTypeHash", Utils.HashString(linkType));
                    root.Links.Add(ch);
                }
            }

            if (p["properties"] is JArray props)
            {
                foreach (JToken t in props)
                {
                    if (!(t is JObject jo)) continue;
                    object ch = TypeLibrary.CreateObject("PropertyChannel");
                    SetEnumSafe(ch, "Realm", jo.Value<string>("realm") ?? "ClientAndServer");
                    string id = jo.Value<string>("id") ?? "";
                    string fieldType = jo.Value<string>("field_type") ?? "";
                    SetIntProp(ch, "Id", Utils.HashString(id));
                    SetIntProp(ch, "FieldTypeHash", Utils.HashString(fieldType));
                    root.Properties.Add(ch);
                }
            }

            asset.Update();
            App.AssetManager.ModifyEbx(newEntry.Name, asset);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["template"] = templateEntry.Name,
                ["name"] = newEntry.Name,
                ["guid"] = newEntry.Guid.ToString(),
                ["root_class_guid"] = asset.RootInstanceGuid.ToString(),
                ["events"] = CountList(root, "Events"),
                ["links"] = CountList(root, "Links"),
                ["properties"] = CountList(root, "Properties")
            };
        }

        public static object CreateSchematicChannelEntity(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry hostEntry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (hostEntry == null)
                return McpHandlers.Error("Host blueprint not found", "NOT_FOUND");

            string channelName = (p.Value<string>("channel_name") ?? p.Value<string>("schematic_channel") ?? "").Trim();
            string channelGuid = (p.Value<string>("channel_guid") ?? "").Trim();
            if (string.IsNullOrEmpty(channelName) && string.IsNullOrEmpty(channelGuid))
                return McpHandlers.Error("channel_name (SchematicChannelAsset) is required", "INVALID_PARAMS");

            EbxAssetEntry channelEntry = McpHandlers.ResolveEbxEntry(channelName, string.IsNullOrEmpty(channelGuid) ? null : channelGuid);
            if (channelEntry == null)
                return McpHandlers.Error("SchematicChannelAsset not found", "NOT_FOUND");

            try
            {
                EbxAsset host = App.AssetManager.GetEbx(hostEntry);
                EbxAsset channelAsset = App.AssetManager.GetEbx(channelEntry);

                string typeName = "SchematicChannelEntityData";
                if (TypeLibrary.GetType(typeName) == null)
                    return McpHandlers.Error(typeName + " not in SDK", "TYPE_NOT_FOUND");

                object newObject = TypeLibrary.CreateObject(typeName);
                AssetClassGuid classGuid = new AssetClassGuid(
                    Utils.GenerateDeterministicGuid(host.Objects, typeName, host.FileGuid), -1);
                ((dynamic)newObject).SetInstanceGuid(classGuid);

                if (TypeLibrary.IsSubClassOf(newObject, "DataBusPeer"))
                {
                    byte[] bytes = classGuid.ExportedGuid.ToByteArray();
                    uint flagValue = (uint)((bytes[2] << 16) | (bytes[1] << 8) | bytes[0]);
                    PropertyInfo flagsPi = newObject.GetType().GetProperty("Flags");
                    if (flagsPi != null && flagsPi.CanWrite)
                        flagsPi.SetValue(newObject, flagValue);
                }

                SetEnumSafe(newObject, "Realm", p.Value<string>("realm") ?? "ClientAndServer");

                var importRef = new EbxImportReference
                {
                    FileGuid = channelEntry.Guid,
                    ClassGuid = channelAsset.RootInstanceGuid
                };
                PropertyInfo channelPi = newObject.GetType().GetProperty("Channel");
                if (channelPi != null)
                    channelPi.SetValue(newObject, new PointerRef(importRef));

                AppendIntList(newObject, "InputProperties", p["input_properties"] as JArray);
                AppendIntList(newObject, "OutputProperties", p["output_properties"] as JArray);
                AppendIntList(newObject, "InputRefProperties", p["input_ref_properties"] as JArray);
                AppendIntList(newObject, "OutputRefProperties", p["output_ref_properties"] as JArray);

                host.AddObject(newObject);
                PointerRef pref = new PointerRef(newObject);
                string arrayName = p.Value<string>("array") ?? "Objects";
                bool added = TryAddPointer(host.RootObject, arrayName, pref);
                if (!added && arrayName == "Objects")
                    added = TryAddPointer(host.RootObject, "Components", pref);

                host.Update();
                App.AssetManager.ModifyEbx(hostEntry.Name, host);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["host"] = hostEntry.Name,
                    ["added_to_array"] = added ? arrayName : null,
                    ["class_guid"] = classGuid.ExportedGuid.ToString(),
                    ["channel"] = channelEntry.Name,
                    ["channel_file_guid"] = channelEntry.Guid.ToString(),
                    ["channel_class_guid"] = channelAsset.RootInstanceGuid.ToString()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "CREATE_FAILED");
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        // Linked resources keep native bundle membership; only entries this session
        // added can be taken back out (a .fbmod has no way to remove a bundle).
        private static void UnlinkOne(AssetEntry entry, int bid, string label,
            List<string> unlinked, List<string> nativeSkipped)
        {
            if (RemoveEntryFromBundle(entry, bid))
                unlinked.Add(label);
            else if (entry.Bundles.Contains(bid))
                nativeSkipped.Add(label);
        }

        private static bool RemoveEntryFromBundle(AssetEntry entry, int bid)
        {
            if (entry.AddedBundles.Contains(bid))
            {
                entry.AddedBundles.Remove(bid);
                entry.IsDirty = true;
                return true;
            }
            return false;
        }

        private static short[] DecodePcm16Big(byte[] soundBuffer, out int channels, out int sampleRate)
        {
            using (NativeReader reader = new NativeReader(new MemoryStream(soundBuffer)))
            {
                reader.ReadUShort();
                reader.ReadUShort(Endian.Big);
                reader.ReadByte();
                channels = (reader.ReadByte() >> 2) + 1;
                sampleRate = reader.ReadUShort(Endian.Big);
                reader.ReadInt(Endian.Big);

                var chans = new List<short>[channels];
                for (int i = 0; i < channels; i++) chans[i] = new List<short>();

                while (reader.Position < reader.Length - 4)
                {
                    ushort blockType = reader.ReadUShort();
                    reader.ReadUShort(Endian.Big);
                    if (blockType == 0x45) break;
                    uint samples = reader.ReadUInt(Endian.Big);
                    for (int i = 0; i < samples; i++)
                        for (int j = 0; j < channels; j++)
                            chans[j].Add(reader.ReadShort(Endian.Big));
                }

                short[] outBuffer = new short[chans[0].Count * channels];
                for (int i = 0; i < chans[0].Count; i++)
                    for (int j = 0; j < channels; j++)
                        outBuffer[(i * channels) + j] = chans[j][i];
                return outBuffer;
            }
        }

        private static byte[] EncodePcm16Big(short[] interleaved, int channels, int sampleRate)
        {
            using (NativeWriter writer = new NativeWriter(new MemoryStream()))
            {
                writer.Write(0x4800000c, Endian.Big);
                writer.Write((byte)0x12);
                writer.Write((byte)((channels - 1) << 2));
                writer.Write((ushort)sampleRate, Endian.Big);

                long pos = writer.Position;
                writer.Write(0x40000000, Endian.Big);

                int totalFrames = interleaved.Length / channels;
                int frame = 0;
                const int maxBlockFrames = 0x2600;

                while (frame < totalFrames)
                {
                    int blockFrames = Math.Min(maxBlockFrames, totalFrames - frame);
                    if (frame + blockFrames > 0x00ffffff)
                        blockFrames = 0x00ffffff - frame;
                    if (blockFrames <= 0) break;

                    int actualRead = blockFrames * channels * 2;
                    writer.Write((actualRead + 8) | 0x44000000, Endian.Big);
                    writer.Write(blockFrames, Endian.Big);

                    for (int i = 0; i < blockFrames; i++)
                    {
                        for (int c = 0; c < channels; c++)
                            writer.Write(interleaved[(frame + i) * channels + c], Endian.Big);
                    }
                    frame += blockFrames;
                }

                writer.Write(0x45000004, Endian.Big);
                writer.Position = pos;
                writer.Write(frame | 0x40000000, Endian.Big);
                return writer.ToByteArray();
            }
        }

        private static void WriteWavFile(string path, short[] interleaved, int channels, int sampleRate)
        {
            int byteRate = sampleRate * channels * 2;
            int dataSize = interleaved.Length * 2;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write((short)(channels * 2));
                bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);
                foreach (short s in interleaved)
                    bw.Write(s);
            }
        }

        private static short[] ReadWavPcm16(string path, out int channels, out int sampleRate)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                string riff = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (riff != "RIFF") throw new InvalidOperationException("Not a RIFF WAV");
                br.ReadInt32();
                string wave = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (wave != "WAVE") throw new InvalidOperationException("Not WAVE");

                channels = 0;
                sampleRate = 0;
                short[] data = null;

                while (fs.Position < fs.Length - 8)
                {
                    string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                    int size = br.ReadInt32();
                    long next = fs.Position + size;
                    if (id == "fmt ")
                    {
                        short format = br.ReadInt16();
                        channels = br.ReadInt16();
                        sampleRate = br.ReadInt32();
                        br.ReadInt32();
                        br.ReadInt16();
                        short bits = br.ReadInt16();
                        if (format != 1 || bits != 16)
                            throw new InvalidOperationException("Only PCM16 WAV supported (got format=" + format + " bits=" + bits + ")");
                    }
                    else if (id == "data")
                    {
                        int count = size / 2;
                        data = new short[count];
                        for (int i = 0; i < count; i++)
                            data[i] = br.ReadInt16();
                    }
                    fs.Position = next + (size % 2); // word align
                }

                if (data == null || channels == 0)
                    throw new InvalidOperationException("WAV missing fmt/data");
                return data;
            }
        }

        private static void SetEnumSafe(object obj, string prop, string name)
        {
            PropertyInfo pi = obj.GetType().GetProperty(prop);
            if (pi == null || string.IsNullOrEmpty(name)) return;
            try { pi.SetValue(obj, Enum.Parse(pi.PropertyType, name, true)); }
            catch
            {
                try { pi.SetValue(obj, Enum.Parse(pi.PropertyType, pi.PropertyType.Name + "_" + name, true)); }
                catch { }
            }
        }

        private static void SetIntProp(object obj, string name, int value)
        {
            PropertyInfo pi = obj.GetType().GetProperty(name);
            if (pi != null && pi.CanWrite)
                pi.SetValue(obj, value);
        }

        private static void AppendIntList(object obj, string prop, JArray arr)
        {
            if (arr == null) return;
            PropertyInfo pi = obj.GetType().GetProperty(prop);
            if (pi == null) return;
            if (!(pi.GetValue(obj) is IList list)) return;
            foreach (JToken t in arr)
            {
                string s = t?.ToString() ?? "";
                int v;
                if (int.TryParse(s, out v))
                    list.Add(v);
                else
                    list.Add(Utils.HashString(s));
            }
        }

        private static int CountList(dynamic root, string name)
        {
            try { return (int)((IList)((object)root).GetType().GetProperty(name).GetValue(root)).Count; }
            catch { return 0; }
        }

        private static bool TryAddPointer(object root, string arrayName, PointerRef pref)
        {
            PropertyInfo pi = root.GetType().GetProperty(arrayName);
            if (pi == null) return false;
            if (pi.GetValue(root) is IList list) { list.Add(pref); return true; }
            return false;
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

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
