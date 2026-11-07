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
using System.Threading.Tasks;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Full sound import: re-encodes the file with SoundEditorPlugin's bundled tool,
    /// rebuilds the chunk (seek table + samples), and updates the NewWaveResource
    /// segment and variation tables exactly as the editor's Import button does.
    /// </summary>
    internal static class McpSoundHandlers
    {
        public static object ImportSound(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Sound EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path to an existing audio file is required", "INVALID_PARAMS");

            Type toolHelperType = FindType("SoundEditorPlugin.Helpers.ToolHelper");
            Type newWaveType = FindType("SoundEditorPlugin.Resources.NewWaveResource");
            if (toolHelperType == null || newWaveType == null)
                return McpHandlers.Error("SoundEditorPlugin is not loaded", "PLUGIN_MISSING");

            MethodInfo importSound = toolHelperType.GetMethod("ImportSound", BindingFlags.Public | BindingFlags.Instance);
            if (importSound == null)
                return McpHandlers.Error("ToolHelper.ImportSound not found in this plugin build", "NOT_IMPLEMENTED");

            bool remix = p.Value<bool?>("remix") ?? Config.Get<bool>("AutoRemixOnImport", true);

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;

                int chunkIndex = p.Value<int?>("chunk_index") ?? 0;
                if (chunkIndex < 0 || chunkIndex >= (int)root.Chunks.Count)
                    return McpHandlers.Error("chunk_index out of range", "INVALID_PARAMS");

                dynamic soundDataChunk = root.Chunks[chunkIndex];
                Guid chunkId = (Guid)soundDataChunk.ChunkId;
                ChunkAssetEntry existingChunk = App.AssetManager.GetChunkEntry(chunkId);
                if (existingChunk == null)
                    return McpHandlers.Error("Chunk referenced by the sound asset was not found", "NOT_FOUND");

                byte[] existingBytes;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(existingChunk)))
                    existingBytes = reader.ReadToEnd();

                // The codec nibble and channel count live in the sample header at
                // the segment's SamplesOffset, the same place the editor reads them.
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(((string)root.Name).ToLowerInvariant());
                if (resEntry == null)
                    return McpHandlers.Error("NewWave resource not found", "NOT_FOUND");

                MethodInfo getResAs = typeof(AssetManager).GetMethods()
                    .First(m => m.Name == "GetResAs" && m.IsGenericMethodDefinition);
                object newWave = getResAs.MakeGenericMethod(newWaveType).Invoke(App.AssetManager, new object[] { resEntry, null });
                if (newWave == null)
                    return McpHandlers.Error("Resource is not a NewWaveResource", "TYPE_MISMATCH");

                IList segments = (IList)newWave.GetType().GetProperty("Segments").GetValue(newWave);
                IList variations = (IList)newWave.GetType().GetProperty("Variations").GetValue(newWave);

                int segmentIndex = p["segment_index"] == null ? 0 : p.Value<int>("segment_index");
                int variationIndex = p["variation_index"] == null ? 0 : p.Value<int>("variation_index");
                if (p["segment_index"] != null && (segmentIndex < 0 || segmentIndex >= segments.Count))
                    return McpHandlers.Error(
                        "segment_index must be 0.." + (segments.Count - 1) + " (" + segments.Count + " segments)",
                        "INVALID_PARAMS");
                if (p["variation_index"] != null && (variationIndex < 0 || variationIndex >= variations.Count))
                    return McpHandlers.Error(
                        "variation_index must be 0.." + (variations.Count - 1) + " (" + variations.Count + " variations)",
                        "INVALID_PARAMS");
                if (segments.Count == 0)
                    segmentIndex = -1;
                if (variations.Count == 0)
                    variationIndex = -1;

                uint sampleStart = 0;
                if (segmentIndex >= 0)
                {
                    dynamic seg = segments[segmentIndex];
                    sampleStart = (uint)seg.SamplesOffset & ~3u;
                }

                // A guessed codec silently re-encodes the asset in the wrong format, so
                // refuse to continue when the sample header cannot be read.
                int codec = p.Value<int?>("codec") ?? 0;
                if (codec == 0)
                {
                    codec = ReadCodecNibble(existingBytes, sampleStart);
                    if (codec < 0)
                        return McpHandlers.Error(
                            "Could not read the codec from the existing sample data; pass codec explicitly",
                            "CODEC_UNKNOWN");
                }
                string codecName = CodecName(codec);

                int channelCount = p.Value<int?>("channel_count") ?? 0;
                if (channelCount == 0)
                {
                    channelCount = ReadChannelCount(existingBytes, sampleStart);
                    if (channelCount < 0)
                        return McpHandlers.Error(
                            "Could not read the channel count from the existing sample data; pass channel_count explicitly",
                            "CHANNELS_UNKNOWN");
                }
                bool isSeekable = p.Value<bool?>("seekable") ?? (bool)((dynamic)root).IsSeekable;

                // Instance lives on the generic base HelperBase<T>, and reflection only
                // returns inherited statics when FlattenHierarchy is set. Without it the
                // lookup returns null and every import dies with a bogus NOT_READY.
                object helper = toolHelperType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                if (helper == null)
                    return McpHandlers.Error("ToolHelper.Instance not found on SoundEditorPlugin", "NOT_READY");

                object taskObject = importSound.Invoke(helper, new object[] { path, codecName, isSeekable, channelCount, remix });
                Task task = taskObject as Task;
                if (task == null)
                    return McpHandlers.Error("ImportSound did not return a task", "IMPORT_FAILED");
                task.GetAwaiter().GetResult();

                // ImportSound returns Task<(byte[] sps, byte[] seek)>. The task itself
                // has no Item1/Item2 - they live on the ValueTuple inside Result, so
                // reading Item1 off the task type used to blow up with an NRE.
                Type resultType = taskObject.GetType();
                object tuple = resultType.GetProperty("Result").GetValue(taskObject);
                if (tuple == null)
                    return McpHandlers.Error("Encoder returned no result", "IMPORT_FAILED");
                Type tupleType = tuple.GetType();
                byte[] spsData = (byte[])tupleType.GetField("Item1").GetValue(tuple);
                byte[] seekTableData = isSeekable ? (byte[])tupleType.GetField("Item2").GetValue(tuple) : null;
                if (spsData == null || spsData.Length == 0)
                    return McpHandlers.Error("Encoder produced no data", "IMPORT_FAILED");

                bool hasStreamPool = ((PointerRef)((dynamic)asset.RootObject).StreamPool).Type != PointerRefType.Null;

                byte[] chunkData;
                uint seekTableOffset, samplesOffset;
                if (seekTableData != null)
                {
                    uint seekTableStart = hasStreamPool ? (uint)existingBytes.Length : 0u;
                    seekTableOffset = seekTableStart | SegmentFlags(true, hasStreamPool);
                    int aligned = AlignTo((int)seekTableOffset + seekTableData.Length, 4);
                    chunkData = new byte[(aligned - seekTableStart) + spsData.Length];
                    Array.Copy(seekTableData, chunkData, seekTableData.Length);
                    Array.Copy(spsData, 0, chunkData, aligned - seekTableStart, spsData.Length);
                    samplesOffset = (uint)aligned | SegmentFlags(true, hasStreamPool);
                }
                else
                {
                    chunkData = spsData;
                    uint offsetStart = hasStreamPool ? (uint)existingBytes.Length : 0u;
                    samplesOffset = offsetStart | SegmentFlags(true, hasStreamPool);
                    seekTableOffset = SegmentFlags(false, hasStreamPool);
                }

                Guid newChunkId = !hasStreamPool ? App.AssetManager.AddChunk(chunkData) : chunkId;
                float duration = DurationSeconds(spsData);


                bool chunkIsAlreadyModified = existingChunk != null && existingChunk.IsAdded && !hasStreamPool;
                dynamic chunkToModify = chunkIsAlreadyModified || hasStreamPool
                    ? soundDataChunk
                    : Activator.CreateInstance(((object)root.Chunks[0]).GetType());

                if (!hasStreamPool)
                {
                    chunkToModify.ChunkId = newChunkId;
                    chunkToModify.ChunkSize = (uint)chunkData.Length;
                }
                else
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        ms.Write(existingBytes, 0, existingBytes.Length);
                        ms.Write(chunkData, 0, chunkData.Length);
                        App.AssetManager.ModifyChunk(newChunkId, ms.ToArray());
                        chunkToModify.ChunkSize = (uint)ms.Length;
                    }
                }

                ChunkAssetEntry newChunk = App.AssetManager.GetChunkEntry(newChunkId);

                if (chunkIsAlreadyModified)
                {
                    List<int> savedBundles = existingChunk.AddedBundles.ToList();
                    App.AssetManager.RevertAsset(existingChunk);
                    if (newChunk != null)
                        newChunk.AddToBundles(savedBundles);
                }
                else if (!hasStreamPool && newChunk != null)
                {
                    root.Chunks.Add(chunkToModify);
                    chunkIndex = (int)root.Chunks.Count - 1;

                    // Vanilla chunks carry no bundle lists; touching them without a
                    // guard throws a bare NullReferenceException.
                    newChunk.AddToBundles(SafeInts(existingChunk.Bundles));
                    newChunk.AddToBundles(SafeInts(existingChunk.AddedBundles));
                }

                bool segmentUpdated = false;
                if (segmentIndex >= 0)
                {
                    dynamic segment = segments[segmentIndex];
                    segment.SamplesOffset = samplesOffset;
                    segment.SeekTableOffset = seekTableOffset;
                    segment.SegmentLength = duration;
                    segmentUpdated = true;
                }

                bool variationUpdated = false;
                if (variationIndex >= 0)
                {
                    dynamic variation = variations[variationIndex];
                    if (hasStreamPool)
                        variation.StreamChunkIndex = (uint)chunkIndex;
                    else
                        variation.MemoryChunkIndex = (uint)chunkIndex;
                    variationUpdated = true;
                }

                // Rebuild the resource's chunk table from the EBX chunk list, keeping
                // one entry per chunk id.
                var uniqueChunks = new Dictionary<Guid, uint>();
                IList ebxChunks = (IList)root.Chunks;
                foreach (object item in ebxChunks)
                {
                    Guid id = (Guid)((dynamic)item).ChunkId;
                    if (!uniqueChunks.ContainsKey(id))
                        uniqueChunks[id] = (uint)((dynamic)item).ChunkSize;
                }

                IList resChunks = (IList)newWave.GetType().GetProperty("Chunks").GetValue(newWave);
                Type chunkEntryType = resChunks.GetType().GetGenericArguments()[0];
                resChunks.Clear();
                foreach (KeyValuePair<Guid, uint> kv in uniqueChunks)
                {
                    object resChunk = Activator.CreateInstance(chunkEntryType);
                    chunkEntryType.GetProperty("ChunkId").SetValue(resChunk, kv.Key);
                    chunkEntryType.GetProperty("ChunkSize").SetValue(resChunk,
                        Convert.ChangeType(kv.Value, chunkEntryType.GetProperty("ChunkSize").PropertyType));
                    resChunks.Add(resChunk);
                }

                // The sample data now holds the count the encoder actually wrote, but the
                // EBX field still carries the template's value. Leave them disagreeing and
                // the asset plays as the wrong layout, so sync it before the update.
                PropertyInfo channelCountProp = ((object)root).GetType().GetProperty("ChannelCount");
                if (channelCountProp != null && channelCountProp.CanWrite)
                {
                    object current = channelCountProp.GetValue((object)root);
                    object wanted = Convert.ChangeType(channelCount, channelCountProp.PropertyType);
                    if (!Equals(current, wanted))
                        channelCountProp.SetValue((object)root, wanted);
                }

                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);

                // Both ModifyRes(ulong, Resource) and ModifyRes(string, Resource) exist;
                // the resource id overload is the one that takes the entry's rid.
                MethodInfo modifyResResource = typeof(AssetManager).GetMethods()
                    .First(m => m.Name == "ModifyRes"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(ulong)
                        && m.GetParameters()[1].ParameterType.Name == "Resource");
                // The engine registers the sound bank under the resource meta, so it has
                // to be the owning asset's instance GUID. ModifyRes writes the resource's
                // inherited meta back, which leaves the bank registered under the asset
                // this one was duplicated from; the first playback then resolves to a
                // null bank and crashes. Same requirement as DuplicationPlugin's
                // NewWaveExtension, which is why that one passes the new GUID in.
                Guid instanceGuid = ((AssetClassGuid)((dynamic)asset.RootObject).GetInstanceGuid()).ExportedGuid;

                modifyResResource.Invoke(App.AssetManager, new[] { (object)resEntry.ResRid, newWave });

                if (resEntry.ModifiedEntry == null)
                    resEntry.ModifiedEntry = new ModifiedAssetEntry();
                resEntry.ModifiedEntry.ResMeta = instanceGuid.ToByteArray();

                // A duplicated waveform keeps the template's bank key, which names a
                // bank the engine no longer registers, so the first playback resolves
                // to nothing. Rewrite it here instead of asking for the manual
                // export, byte-patch, import round trip.
                int bankKeyPatched = PatchBankKey(resEntry, instanceGuid);

                // Replacing a chunk reverts the old one but leaves its record in the
                // link lists, so every import used to strand one more dead chunk on
                // both the ebx and the resource. Drop links the chunk list no longer
                // names before adding the new one.
                PruneChunkLinks(entry, root.Chunks);
                PruneChunkLinks(resEntry, root.Chunks);

                if (newChunk != null)
                    resEntry.LinkAsset(newChunk);
                entry.LinkAsset(resEntry);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["codec"] = codecName,
                    ["channel_count"] = channelCount,
                    ["seekable"] = isSeekable,
                    ["stream_pool"] = hasStreamPool,
                    ["chunk_id"] = newChunkId.ToString(),
                    ["chunk_bytes"] = chunkData.Length,
                    ["duration_seconds"] = duration,
                    ["chunk_index"] = chunkIndex,
                    ["segment_index"] = segmentIndex,
                    ["segment_count"] = segments.Count,
                    ["variation_index"] = variationIndex,
                    ["variation_count"] = variations.Count,
                    ["segment_updated"] = segmentUpdated,
                    ["variation_updated"] = variationUpdated,
                    ["bank_key_patched"] = bankKeyPatched
                };
            }
            catch (Exception ex)
            {
                // The encoder and the chunk write happen before the tables are
                // updated, so a failure can leave an added chunk behind.
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = Unwrap(ex),
                    ["detail"] = ex.ToString(),
                    ["error_code"] = "IMPORT_FAILED",
                    ["partial_changes_possible"] = true
                };
            }
        }

        private static IEnumerable<int> SafeInts(IEnumerable<int> source)
        {
            return source ?? Enumerable.Empty<int>();
        }

        // Drops chunk links that the asset's own chunk list does not name. A link
        // left over from a reverted chunk still reads back from LinkedAssets but no
        // longer resolves, which made the asset look incompletely packaged.
        private static void PruneChunkLinks(AssetEntry asset, IList ebxChunks)
        {
            if (asset == null || ebxChunks == null)
                return;

            HashSet<Guid> live = new HashSet<Guid>();
            foreach (object item in ebxChunks)
                live.Add((Guid)((dynamic)item).ChunkId);

            List<AssetEntry> dead = asset.LinkedAssets
                .Where(linked => linked is ChunkAssetEntry chunk && !live.Contains(chunk.Id))
                .ToList();
            foreach (AssetEntry linked in dead)
                asset.LinkedAssets.Remove(linked);
            if (dead.Count > 0)
                asset.IsDirty = true;
        }

        // A NewWave resource carries the sound bank key at body offset 0x0C and
        // repeats it in the segment and variation tables. The engine registers the
        // bank under the owning asset's instance GUID, so a waveform copied from a
        // template still points every reference at the template's bank. Rewriting
        // all occurrences of the old key is the same edit as exporting the resource,
        // replacing the bytes, and importing it back.
        //
        // Returns the number of occurrences rewritten; 0 means the key already
        // matched or the body was too short to hold one.
        internal static int PatchBankKey(ResAssetEntry resEntry, Guid instanceGuid)
        {
            if (resEntry == null)
                return 0;

            byte[] data;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                data = reader.ReadToEnd();
            if (data == null || data.Length < 16)
                return 0;

            uint wanted = unchecked((uint)Utils.HashString(instanceGuid.ToString()));
            uint current = BitConverter.ToUInt32(data, 0x0C);
            if (current == wanted)
                return 0;

            byte[] from = BitConverter.GetBytes(current);
            byte[] to = BitConverter.GetBytes(wanted);
            int replaced = 0;
            for (int i = 0; i + 4 <= data.Length; i++)
            {
                if (data[i] != from[0] || data[i + 1] != from[1] ||
                    data[i + 2] != from[2] || data[i + 3] != from[3])
                    continue;
                Buffer.BlockCopy(to, 0, data, i, 4);
                replaced++;
                i += 3;
            }
            if (replaced == 0)
                return 0;

            byte[] meta = resEntry.ModifiedEntry?.ResMeta ?? instanceGuid.ToByteArray();
            App.AssetManager.ModifyRes(resEntry.Name, data, meta);
            if (resEntry.ModifiedEntry == null)
                resEntry.ModifiedEntry = new ModifiedAssetEntry();
            resEntry.ModifiedEntry.ResMeta = meta;
            return replaced;
        }

        // Sample data starts with a 0x48 marker, a big-endian header size, then the
        // codec nibble at +4 and the channel count at +5. Returns -1 when the offset
        // does not point at a sample header.
        private static int ReadCodecNibble(byte[] chunkData, uint offset)
        {
            if (chunkData == null || offset + 4 >= chunkData.Length)
                return -1;
            if (chunkData[offset] != 0x48)
                return -1;
            int codec = chunkData[offset + 4] & 0xF;
            return codec == 0 ? -1 : codec;
        }

        // Byte +5 of the sample header packs the channel count in its upper six bits.
        private static int ReadChannelCount(byte[] chunkData, uint offset)
        {
            if (chunkData == null || offset + 5 >= chunkData.Length)
                return -1;
            if (chunkData[offset] != 0x48)
                return -1;
            return (chunkData[offset + 5] >> 2) + 1;
        }

        private static string CodecName(int codec)
        {
            switch (codec)
            {
                case 2: return "s16b_int";
                case 3: return "eaxma";
                case 4: return "xas_int";
                case 5: return "ealayer3_int";
                case 6: return "ealayer3pcm_int";
                case 7: return "ealayer3spike_int";
                case 9: return "easpeex";
                case 11: return "eamp3";
                case 12: return "eaopus";
                case 13: return "eaatrac9";
                case 14: return "multistreamopus";
                case 15: return "multistreamopusuncoupled";
                default: return "multistreamopus";
            }
        }

        private static uint SegmentFlags(bool valid, bool streaming)
        {
            if (valid && streaming) return 3;
            return valid ? 1u : 0u;
        }

        private static int AlignTo(int value, int alignment)
        {
            int remainder = value % alignment;
            return remainder == 0 ? value : value + (alignment - remainder);
        }

        private static float DurationSeconds(byte[] spsData)
        {
            if (spsData == null || spsData.Length < 12)
                return 0f;
            byte[] sampleRateBytes = Reverse(spsData, 4);
            byte[] sampleCountBytes = Reverse(spsData, 8);
            int header1 = BitConverter.ToInt32(sampleRateBytes, 0);
            int header2 = BitConverter.ToInt32(sampleCountBytes, 0);
            int sampleRate = header1 & 0x3FFFF;
            int sampleCount = header2 & 0x1FFFFFFF;
            return sampleRate != 0 ? sampleCount / (float)sampleRate : 0f;
        }

        private static byte[] Reverse(byte[] buffer, int index)
        {
            byte[] slice = new byte[4];
            Array.Copy(buffer, index, slice, 0, 4);
            Array.Reverse(slice);
            return slice;
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

        private static string Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
                return tie.InnerException.Message;
            if (ex is AggregateException agg && agg.InnerException != null)
                return agg.InnerException.Message;
            return ex.Message;
        }
    }
}
