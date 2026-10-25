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
                if (segmentIndex >= segments.Count) segmentIndex = -1;
                if (variationIndex >= variations.Count) variationIndex = -1;

                uint sampleStart = 0;
                if (segmentIndex >= 0)
                {
                    dynamic seg = segments[segmentIndex];
                    sampleStart = (uint)seg.SamplesOffset & ~3u;
                }

                int codec = p.Value<int?>("codec") ?? ReadCodecNibble(existingBytes, sampleStart);
                string codecName = CodecName(codec);
                int channelCount = p.Value<int?>("channel_count") ?? ReadChannelCount(existingBytes, sampleStart);
                bool isSeekable = p.Value<bool?>("seekable") ?? (bool)((dynamic)root).IsSeekable;

                object helper = toolHelperType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (helper == null)
                    return McpHandlers.Error("SoundEditorPlugin ToolHelper is not initialised", "NOT_READY");

                object taskObject = importSound.Invoke(helper, new object[] { path, codecName, isSeekable, channelCount, remix });
                Task task = taskObject as Task;
                if (task == null)
                    return McpHandlers.Error("ImportSound did not return a task", "IMPORT_FAILED");
                task.GetAwaiter().GetResult();

                Type resultType = taskObject.GetType();
                byte[] spsData = (byte[])resultType.GetProperty("Item1").GetValue(taskObject);
                byte[] seekTableData = isSeekable ? (byte[])resultType.GetProperty("Item2").GetValue(taskObject) : null;
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

                    newChunk.AddToBundles(existingChunk.Bundles);
                    newChunk.AddToBundles(existingChunk.AddedBundles);
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

                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);

                // Both ModifyRes(ulong, Resource) and ModifyRes(string, Resource) exist;
                // the resource id overload is the one that takes the entry's rid.
                MethodInfo modifyResResource = typeof(AssetManager).GetMethods()
                    .First(m => m.Name == "ModifyRes"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[0].ParameterType == typeof(ulong)
                        && m.GetParameters()[1].ParameterType.Name == "Resource");
                modifyResResource.Invoke(App.AssetManager, new[] { (object)resEntry.ResRid, newWave });

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
                    ["variation_index"] = variationIndex,
                    ["segment_updated"] = segmentUpdated,
                    ["variation_updated"] = variationUpdated
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
                    ["error_code"] = "IMPORT_FAILED",
                    ["partial_changes_possible"] = true
                };
            }
        }

        // First byte of the sample data is the codec nibble.
        private static int ReadCodecNibble(byte[] chunkData, uint offset)
        {
            if (chunkData == null || offset >= chunkData.Length)
                return 2;
            int codec = chunkData[offset] & 0xF;
            return codec == 0 ? 2 : codec;
        }

        // Fifth byte of the sample data packs the channel count in the upper bits.
        private static int ReadChannelCount(byte[] chunkData, uint offset)
        {
            if (chunkData == null || offset + 5 >= chunkData.Length)
                return 1;
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
