using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Port of Plugins/FrostyProjectMerger, without the dialog boxes. The merge
    /// decision that the menu asks about is an explicit parameter here.
    /// </summary>
    internal static class McpMergeHandlers
    {
        private const ulong ProjectMagic = 0x00005954534F5246;

        public static object ImportProject(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return McpHandlers.Error("path to an existing .fbproject is required", "INVALID_PARAMS");

            bool overwriteModified = p.Value<bool?>("overwrite_modified") ?? false;

            var plan = new Dictionary<string, object>
            {
                ["path"] = Path.GetFullPath(path),
                ["overwrite_modified"] = overwriteModified
            };

            return McpCommit.Require("import_project", p, plan, () =>
            {
                try
                {
                    return Merge(path, overwriteModified);
                }
                catch (Exception ex)
                {
                    // Merging mutates the project as it reads, so a failure part way
                    // through leaves whatever was already applied.
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = ex.Message,
                        ["error_code"] = "MERGE_FAILED",
                        ["partial_changes_possible"] = true
                    };
                }
            });
        }

        private static object Merge(string path, bool allowOverwrite)
        {
            int bundlesAdded = 0, bundlesExisting = 0, ebxMerged = 0, resMerged = 0, chunksMerged = 0;
            int skippedMissing = 0, skippedProtected = 0;
            var errors = new List<object>();

            using (NativeReader reader = new NativeReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
            {
                ulong magic = reader.ReadULong();
                if (magic != ProjectMagic)
                    return McpHandlers.Error("Project file is in the legacy format; merging it could corrupt the current project", "LEGACY_PROJECT");

                uint version = reader.ReadUInt();
                if (version < 9)
                    return McpHandlers.Error("Project version " + version + " is too old to merge", "UNSUPPORTED_VERSION");

                // Header fields and mod images are not needed for the merge.
                reader.ReadNullTerminatedString();
                reader.ReadLong();
                reader.ReadLong();
                reader.ReadUInt();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();

                int size = reader.ReadInt();
                if (size > 0)
                    reader.ReadBytes(size);
                for (int i = 0; i < 4; i++)
                {
                    size = reader.ReadInt();
                    if (size > 0)
                        reader.ReadBytes(size);
                }
                reader.ReadInt();

                // Dummy entries first, so later passes can resolve them by name.
                int numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    string sbName = reader.ReadNullTerminatedString();
                    BundleType type = (BundleType)reader.ReadInt();
                    if (App.AssetManager.GetBundleId(name) < 0)
                    {
                        App.AssetManager.AddBundle(name, type, App.AssetManager.GetSuperBundleId(sbName));
                        bundlesAdded++;
                    }
                    else
                    {
                        bundlesExisting++;
                    }
                }

                numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    EbxAssetEntry entry = new EbxAssetEntry
                    {
                        Name = reader.ReadNullTerminatedString(),
                        Guid = reader.ReadGuid()
                    };
                    if (App.AssetManager.GetEbxEntry(entry.Name) == null)
                        App.AssetManager.AddEbx(entry);
                    entry.IsDirty = true;
                }

                numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    ResAssetEntry entry = new ResAssetEntry
                    {
                        Name = reader.ReadNullTerminatedString(),
                        ResRid = reader.ReadULong(),
                        ResType = reader.ReadUInt(),
                        ResMeta = reader.ReadBytes(0x10)
                    };
                    if (App.AssetManager.GetResEntry(entry.ResRid) == null)
                        App.AssetManager.AddRes(entry);
                }

                numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    ChunkAssetEntry entry = new ChunkAssetEntry
                    {
                        Id = reader.ReadGuid(),
                        H32 = reader.ReadInt()
                    };
                    if (App.AssetManager.GetChunkEntry(entry.Id) == null)
                        App.AssetManager.AddChunk(entry);
                }

                var h32map = new Dictionary<int, AssetEntry>();

                // EBX payloads
                numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    List<AssetEntry> linkedEntries = FrostyProject.LoadLinkedAssets(reader);
                    List<int> bundles = version >= 13 ? ReadBundleList(reader) : new List<int>();

                    bool isModified = reader.ReadBoolean();
                    bool isTransientModified = false;
                    string userData = "";
                    byte[] data = null;
                    bool modifiedResource = false;
                    if (isModified)
                    {
                        isTransientModified = reader.ReadBoolean();
                        if (version >= 12)
                            userData = reader.ReadNullTerminatedString();
                        if (version < 13)
                            bundles.AddRange(ReadBundleList(reader));
                        if (version >= 13)
                            modifiedResource = reader.ReadBoolean();
                        data = reader.ReadBytes(reader.ReadInt());
                    }

                    EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
                    if (entry == null)
                    {
                        skippedMissing++;
                        continue;
                    }

                    // Same rule the menu uses: overwrite when the user allowed it,
                    // when the asset already has unsaved edits, or when it is untouched.
                    if (!allowOverwrite && !entry.IsDirty && entry.IsModified)
                    {
                        skippedProtected++;
                        continue;
                    }

                    try
                    {
                        entry.LinkedAssets.AddRange(linkedEntries);
                        entry.AddedBundles.AddRange(bundles);

                        if (isModified)
                        {
                            entry.ModifiedEntry = new ModifiedAssetEntry
                            {
                                IsTransientModified = isTransientModified,
                                UserData = userData
                            };

                            if (modifiedResource)
                            {
                                entry.ModifiedEntry.DataObject = ModifiedResource.Read(data);
                            }
                            else
                            {
                                using (EbxReader ebxReader = EbxReader.CreateProjectReader(new MemoryStream(data)))
                                {
                                    EbxAsset asset = ebxReader.ReadAsset<EbxAsset>();
                                    entry.ModifiedEntry.DataObject = asset;
                                    if (entry.IsAdded)
                                        entry.Type = asset.RootObject.GetType().Name;
                                    entry.ModifiedEntry.DependentAssets.AddRange(asset.Dependencies);
                                }
                            }

                            entry.OnModified();
                            entry.IsDirty = true;
                        }

                        int hash = Utils.HashString(entry.Name);
                        if (!h32map.ContainsKey(hash))
                            h32map.Add(hash, entry);
                        ebxMerged++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new Dictionary<string, object> { ["asset"] = name, ["error"] = ex.Message });
                    }
                }

                // RES payloads
                numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    List<AssetEntry> linkedEntries = FrostyProject.LoadLinkedAssets(reader);
                    List<int> bundles = version >= 13 ? ReadBundleList(reader) : new List<int>();

                    bool isModified = reader.ReadBoolean();
                    Sha1 sha1 = Sha1.Zero;
                    long originalSize = 0;
                    byte[] resMeta = null;
                    byte[] data = null;
                    string userData = "";
                    if (isModified)
                    {
                        sha1 = reader.ReadSha1();
                        originalSize = reader.ReadLong();

                        int length = reader.ReadInt();
                        if (length > 0)
                            resMeta = reader.ReadBytes(length);

                        if (version >= 12)
                            userData = reader.ReadNullTerminatedString();

                        if (version < 13)
                            bundles.AddRange(ReadBundleList(reader));

                        data = reader.ReadBytes(reader.ReadInt());
                    }

                    ResAssetEntry entry = App.AssetManager.GetResEntry(name);
                    if (entry == null)
                    {
                        skippedMissing++;
                        continue;
                    }

                    // Same rule the menu uses: overwrite when the user allowed it,
                    // when the asset already has unsaved edits, or when it is untouched.
                    if (!allowOverwrite && !entry.IsDirty && entry.IsModified)
                    {
                        skippedProtected++;
                        continue;
                    }

                    try
                    {
                        entry.LinkedAssets.AddRange(linkedEntries);
                        entry.AddedBundles.AddRange(bundles);

                        if (isModified)
                        {
                            entry.ModifiedEntry = new ModifiedAssetEntry
                            {
                                Sha1 = sha1,
                                OriginalSize = originalSize,
                                ResMeta = resMeta,
                                UserData = userData
                            };

                            if (sha1 == Sha1.Zero)
                                entry.ModifiedEntry.DataObject = ModifiedResource.Read(data);
                            else
                                entry.ModifiedEntry.Data = data;

                            entry.OnModified();
                        }

                        int hash = Utils.HashString(entry.Name);
                        if (!h32map.ContainsKey(hash))
                            h32map.Add(hash, entry);
                        resMerged++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new Dictionary<string, object> { ["asset"] = name, ["error"] = ex.Message });
                    }
                }

                // Chunk payloads
                numItems = reader.ReadInt();
                for (int i = 0; i < numItems; i++)
                {
                    Guid id = reader.ReadGuid();
                    List<int> bundles = version >= 13 ? ReadBundleList(reader) : new List<int>();

                    Sha1 sha1 = Sha1.Zero;
                    uint logicalOffset = 0, logicalSize = 0, rangeStart = 0, rangeEnd = 0;
                    int firstMip = -1;
                    int h32 = 0;
                    bool addToChunkBundles = false;
                    string userData = "";
                    byte[] data = null;

                    if (version > 13)
                    {
                        firstMip = reader.ReadInt();
                        h32 = reader.ReadInt();
                    }

                    bool isModified = true;
                    if (version >= 13)
                        isModified = reader.ReadBoolean();

                    if (isModified)
                    {
                        sha1 = reader.ReadSha1();
                        logicalOffset = reader.ReadUInt();
                        logicalSize = reader.ReadUInt();
                        rangeStart = reader.ReadUInt();
                        rangeEnd = reader.ReadUInt();

                        if (version < 14)
                        {
                            firstMip = reader.ReadInt();
                            h32 = reader.ReadInt();
                        }

                        addToChunkBundles = reader.ReadBoolean();
                        if (version >= 12)
                            userData = reader.ReadNullTerminatedString();

                        if (version < 13)
                            bundles.AddRange(ReadBundleList(reader));

                        data = reader.ReadBytes(reader.ReadInt());
                    }

                    try
                    {
                        ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(id);
                        if (entry == null && isModified)
                        {
                            // Chunks are not edited in place: a missing chunk from the
                            // imported project is recreated and inherits the bundles of
                            // the resource that referenced it.
                            ChunkAssetEntry newEntry = new ChunkAssetEntry { Id = id, H32 = h32 };
                            App.AssetManager.AddChunk(newEntry);
                            if (h32map.TryGetValue(newEntry.H32, out AssetEntry linked))
                                foreach (int bundleId in linked.Bundles)
                                    newEntry.AddToBundle(bundleId);
                            entry = newEntry;
                        }

                        if (entry == null)
                        {
                            skippedMissing++;
                            continue;
                        }

                        entry.AddedBundles.AddRange(bundles);
                        if (isModified)
                        {
                            entry.ModifiedEntry = new ModifiedAssetEntry
                            {
                                Sha1 = sha1,
                                LogicalOffset = logicalOffset,
                                LogicalSize = logicalSize,
                                RangeStart = rangeStart,
                                RangeEnd = rangeEnd,
                                FirstMip = firstMip,
                                H32 = h32,
                                AddToChunkBundle = addToChunkBundles,
                                UserData = userData,
                                Data = data
                            };
                            entry.OnModified();
                        }
                        else
                        {
                            entry.H32 = h32;
                            entry.FirstMip = firstMip;
                        }
                        chunksMerged++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new Dictionary<string, object> { ["chunk"] = id.ToString(), ["error"] = ex.Message });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = errors.Count == 0,
                ["path"] = Path.GetFullPath(path),
                ["bundles_added"] = bundlesAdded,
                ["bundles_existing"] = bundlesExisting,
                ["ebx_merged"] = ebxMerged,
                ["res_merged"] = resMerged,
                ["chunks_merged"] = chunksMerged,
                ["skipped"] = skippedMissing + skippedProtected,
                ["skipped_missing"] = skippedMissing,
                ["skipped_protected"] = skippedProtected,
                ["failed_count"] = errors.Count,
                ["failures"] = errors
            };
        }

        private static List<int> ReadBundleList(NativeReader reader)
        {
            var bundles = new List<int>();

            int length = reader.ReadInt();
            for (int i = 0; i < length; i++)
            {
                string bundleName = reader.ReadNullTerminatedString();
                int bid = App.AssetManager.GetBundleId(bundleName);
                if (bid != -1)
                    bundles.Add(bid);
            }
            return bundles;
        }
    }
}
