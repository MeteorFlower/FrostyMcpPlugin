using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Pointer-level reference search and array insertion, which the component
    /// tools cannot express.
    /// </summary>
    internal static class McpRefHandlers
    {
        public static object FindPointerReferences(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string fileGuidText = (p.Value<string>("file_guid") ?? "").Trim();
            string className = (p.Value<string>("name") ?? "").Trim();
            string classGuidText = (p.Value<string>("class_guid") ?? "").Trim();

            Guid fileGuid = Guid.Empty;
            if (!string.IsNullOrEmpty(fileGuidText))
            {
                if (!Guid.TryParse(fileGuidText, out fileGuid))
                    return McpHandlers.Error("file_guid is not a GUID", "INVALID_PARAMS");
            }
            else if (!string.IsNullOrEmpty(className))
            {
                EbxAssetEntry target = McpHandlers.ResolveEbxEntry(className, null);
                if (target == null)
                    return McpHandlers.Error("EBX asset not found: " + className, "NOT_FOUND");
                fileGuid = target.Guid;
            }
            else
            {
                return McpHandlers.Error("name or file_guid is required", "INVALID_PARAMS");
            }

            Guid classGuid = Guid.Empty;
            if (!string.IsNullOrEmpty(classGuidText) && !Guid.TryParse(classGuidText, out classGuid))
                return McpHandlers.Error("class_guid is not a GUID", "INVALID_PARAMS");

            int maxScan = p.Value<int?>("max_scan") ?? 3000;
            bool scanAll = maxScan <= 0;
            if (scanAll)
                maxScan = int.MaxValue;
            string query = (p.Value<string>("query") ?? "").Trim();
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);

            var hits = new List<object>();
            int total = 0, scanned = 0, loadFailures = 0;
            bool scanTruncated = false;

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (!string.IsNullOrEmpty(query) &&
                    entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!scanAll && scanned >= maxScan)
                {
                    scanTruncated = true;
                    break;
                }

                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { loadFailures++; continue; }
                if (asset == null)
                {
                    loadFailures++;
                    continue;
                }
                scanned++;

                int objectIndex = 0;
                foreach (object obj in asset.Objects)
                {
                    CollectPointerHits(obj, obj.GetType(), "", objectIndex, 0, new HashSet<int>(),
                        entry, fileGuid, classGuid, hits, ref total, offset, limit);
                    objectIndex++;
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["file_guid"] = fileGuid.ToString(),
                ["class_guid"] = classGuid == Guid.Empty ? null : classGuid.ToString(),
                ["scanned_assets"] = scanned,
                ["max_scan"] = scanAll ? 0 : maxScan,
                ["max_scan_meaning"] = scanAll ? "0 = scan all assets" : "stop after max_scan assets",
                ["scan_truncated"] = scanTruncated,
                ["load_failures"] = loadFailures,
                ["total"] = total,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = hits.Count,
                // A scan that stopped early cannot say "nothing points here". Without
                // this the reply reads as a clean 0 and the caller concludes there are
                // no references, when the asset that holds them was never reached.
                ["note"] = scanTruncated
                    ? (total == 0
                        ? "SCAN INCOMPLETE: stopped at max_scan (" + maxScan + ") before covering the project, so 0 hits does NOT mean unreferenced. Raise max_scan or pass query to narrow."
                        : "SCAN INCOMPLETE: stopped at max_scan (" + maxScan + "); more references may exist. Raise max_scan or pass query to narrow.")
                    : (total == 0
                        ? "0 hits can also mean the referenced object's class guid is not in the target's exported set; cross-check with find_references / search_ebx_fields"
                        : null),
                ["references"] = hits
            };
        }

        // A reference can sit inside a struct or a BoxedValue, so those are walked
        // too. Only value types and BoxedValue are followed: a Frostbite class is
        // reached through a PointerRef, not through containment.
        private static void CollectPointerHits(object obj, Type type, string prefix, int objectIndex, int depth,
            HashSet<int> visited, EbxAssetEntry entry, Guid fileGuid, Guid classGuid,
            List<object> hits, ref int total, int offset, int limit)
        {
            if (obj == null || depth > 6)
                return;

            foreach (PropertyInfo pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0)
                    continue;

                object value;
                try { value = pi.GetValue(obj); }
                catch { continue; }
                if (value == null)
                    continue;

                string path = string.IsNullOrEmpty(prefix) ? pi.Name : prefix + "." + pi.Name;

                if (value is PointerRef pr)
                {
                    if (!PointerMatches(pr, fileGuid, classGuid))
                        continue;
                    total++;
                    int index = total - 1;
                    if (index < offset || hits.Count >= limit)
                        continue;
                    hits.Add(new Dictionary<string, object>
                    {
                        ["asset"] = entry.Name,
                        ["object_index"] = objectIndex,
                        ["object_type"] = type.Name,
                        ["path"] = path,
                        ["target_file_guid"] = pr.External.FileGuid.ToString(),
                        ["target_class_guid"] = pr.External.ClassGuid.ToString()
                    });
                    continue;
                }

                if (value is IList list && !(value is string))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        object element = list[i];
                        if (element is PointerRef itemRef)
                        {
                            if (!PointerMatches(itemRef, fileGuid, classGuid))
                                continue;
                            total++;
                            int index = total - 1;
                            if (index < offset || hits.Count >= limit)
                                continue;
                            hits.Add(new Dictionary<string, object>
                            {
                                ["asset"] = entry.Name,
                                ["object_index"] = objectIndex,
                                ["object_type"] = type.Name,
                                ["path"] = path + "[" + i + "]",
                                ["target_file_guid"] = itemRef.External.FileGuid.ToString(),
                                ["target_class_guid"] = itemRef.External.ClassGuid.ToString()
                            });
                            continue;
                        }

                        if (ShouldWalkInto(element) && visited.Add(RuntimeHelpers.GetHashCode(element)))
                            CollectPointerHits(element, element.GetType(), path + "[" + i + "]", objectIndex,
                                depth + 1, visited, entry, fileGuid, classGuid, hits, ref total, offset, limit);
                    }
                    continue;
                }

                if (ShouldWalkInto(value) && visited.Add(RuntimeHelpers.GetHashCode(value)))
                    CollectPointerHits(value, value.GetType(), path, objectIndex, depth + 1, visited,
                        entry, fileGuid, classGuid, hits, ref total, offset, limit);
            }
        }

        private static bool ShouldWalkInto(object value)
        {
            if (value == null)
                return false;
            Type type = value.GetType();
            if (type.IsValueType)
                return true;
            if (type.Name == "BoxedValue")
                return true;

            // EBX structs are C# classes with EbxFieldType.Struct, not value types.
            // StatEventParamsData is one: its PointerRefs live inside a nested
            // struct-valued property, so skipping it made find_pointer_references
            // report 0 for references that find_references could see.
            EbxClassMetaAttribute meta = type.GetCustomAttribute<EbxClassMetaAttribute>();
            return meta != null && meta.Type == EbxFieldType.Struct;
        }

        private static bool PointerMatches(PointerRef pr, Guid fileGuid, Guid classGuid)
        {
            if (pr.Type != FrostySdk.IO.PointerRefType.External)
                return false;
            if (pr.External.FileGuid != fileGuid)
                return false;
            return classGuid == Guid.Empty || pr.External.ClassGuid == classGuid;
        }

        // find_references needs the same match without building a hit list. The
        // dependency graph is built from saved bytes, so a reference added to a
        // modified asset during this session is not in it yet.
        internal static bool HasExternalPointerTo(EbxAsset asset, Guid fileGuid)
        {
            if (asset == null || asset.Objects == null)
                return false;

            var visited = new HashSet<int>();
            foreach (object obj in asset.Objects)
            {
                if (obj != null && WalksToFile(obj, obj.GetType(), 0, visited, fileGuid))
                    return true;
            }
            return false;
        }

        private static bool WalksToFile(object obj, Type type, int depth, HashSet<int> visited, Guid fileGuid)
        {
            if (obj == null || depth > 6)
                return false;

            foreach (PropertyInfo pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0)
                    continue;

                object value;
                try { value = pi.GetValue(obj); }
                catch { continue; }
                if (value == null)
                    continue;

                if (value is PointerRef pr)
                {
                    if (PrPointsTo(pr, fileGuid))
                        return true;
                    continue;
                }

                if (value is IList list && !(value is string))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        object element = list[i];
                        if (element is PointerRef itemRef)
                        {
                            if (PrPointsTo(itemRef, fileGuid))
                                return true;
                            continue;
                        }
                        if (ShouldWalkInto(element) && visited.Add(RuntimeHelpers.GetHashCode(element)) &&
                            WalksToFile(element, element.GetType(), depth + 1, visited, fileGuid))
                            return true;
                    }
                    continue;
                }

                if (ShouldWalkInto(value) && visited.Add(RuntimeHelpers.GetHashCode(value)) &&
                    WalksToFile(value, value.GetType(), depth + 1, visited, fileGuid))
                    return true;
            }
            return false;
        }

        private static bool PrPointsTo(PointerRef pr, Guid fileGuid)
        {
            return pr.Type == FrostySdk.IO.PointerRefType.External && pr.External.FileGuid == fileGuid;
        }

        // Lists the assets whose External PointerRefs still address the given
        // component GUIDs of one file. Bounded so a big project stays responsive.
        // Returns the assets that still reference the given internal GUIDs. The
        // result carries its own completeness flags: a truncated scan looks exactly
        // like a clean one otherwise.
        internal static Dictionary<string, object> FindAffectedAssets(Guid fileGuid,
            ICollection<Guid> classGuids, int maxScan, int maxMilliseconds)
        {
            var affected = new List<object>();
            int scanned = 0;
            bool truncated = false;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (classGuids == null || classGuids.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    ["items"] = affected,
                    ["scanned"] = 0,
                    ["truncated"] = false
                };
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (scanned >= maxScan || (maxMilliseconds > 0 && stopwatch.ElapsedMilliseconds >= maxMilliseconds))
                {
                    truncated = true;
                    break;
                }
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { continue; }
                if (asset == null)
                    continue;
                scanned++;

                var hitPaths = new List<string>();
                foreach (object obj in asset.Objects)
                    CollectMatchingPaths(obj, obj.GetType(), "", fileGuid, classGuids, hitPaths, 20);

                if (hitPaths.Count == 0 || !seen.Add(entry.Name))
                    continue;
                affected.Add(new Dictionary<string, object>
                {
                    ["asset"] = entry.Name,
                    ["paths"] = hitPaths
                });
            }

            return new Dictionary<string, object>
            {
                ["items"] = affected,
                ["count"] = affected.Count,
                ["scanned"] = scanned,
                ["truncated"] = truncated
            };
        }

        private static void CollectMatchingPaths(object obj, Type type, string prefix, Guid fileGuid,
            ICollection<Guid> classGuids, List<string> hits, int cap)
        {
            if (obj == null || hits.Count >= cap)
                return;

            foreach (PropertyInfo pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0)
                    continue;
                object value;
                try { value = pi.GetValue(obj); }
                catch { continue; }
                if (value == null)
                    continue;

                string path = string.IsNullOrEmpty(prefix) ? pi.Name : prefix + "." + pi.Name;

                if (value is PointerRef pr)
                {
                    if (pr.Type == FrostySdk.IO.PointerRefType.External &&
                        pr.External.FileGuid == fileGuid && classGuids.Contains(pr.External.ClassGuid))
                        hits.Add(path);
                    continue;
                }

                if (value is IList list && !(value is string))
                {
                    for (int i = 0; i < list.Count && hits.Count < cap; i++)
                    {
                        if (list[i] is PointerRef itemRef &&
                            itemRef.Type == FrostySdk.IO.PointerRefType.External &&
                            itemRef.External.FileGuid == fileGuid && classGuids.Contains(itemRef.External.ClassGuid))
                            hits.Add(path + "[" + i + "]");
                    }
                }
            }
        }
        public static object InsertEbxItem(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX asset not found", "NOT_FOUND");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return McpHandlers.Error("path is required (e.g. 'Components' or 'Objects[0].Children')", "INVALID_PARAMS");

            int index = p.Value<int?>("index") ?? -1;
            int sourceIndex = p.Value<int?>("source_index") ?? -1;
            string hostGuid = p.Value<string>("class_guid") ?? p.Value<string>("component_guid") ?? "";
            if (!string.IsNullOrEmpty(hostGuid) && !Guid.TryParse(hostGuid, out _))
                return McpHandlers.Error("class_guid is not a GUID", "INVALID_PARAMS");

            var plan = new Dictionary<string, object>
            {
                ["name"] = entry.Name,
                ["path"] = path,
                ["index"] = index,
                ["source_index"] = sourceIndex,
                ["class_guid"] = hostGuid
            };

            // The preview has to fail the same way the commit would. Resolving the host
            // and the list here means a wrong path is rejected up front instead of
            // returning would_change:true and only failing on the commit call.
            {
                EbxAsset previewAsset;
                try
                {
                    previewAsset = App.AssetManager.GetEbx(entry);
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "LOAD_FAILED");
                }

                if (previewAsset == null)
                    return McpHandlers.Error("Failed to load EBX", "LOAD_FAILED");

                object previewHost = ResolveInsertHost(previewAsset, entry, hostGuid, p);
                if (previewHost == null)
                    return McpHandlers.Error("host component not found", "NOT_FOUND");

                if (!TryResolveList(previewHost, path, out IList previewList, out _))
                    return McpHandlers.Error("Could not resolve list at path: " + path, "NOT_FOUND");

                if (sourceIndex >= previewList.Count)
                    return McpHandlers.Error("source_index out of range (list count " + previewList.Count + ")", "INVALID_PARAMS");
            }

            return McpCommit.Require("insert_ebx_item", p, plan, () =>
            {
            // The failure path rolls back from these, so they are declared outside
            // the try: anything that throws after the list has been changed has to
            // be able to undo it.
            int insertAt = -1;
            object item = null;
            object registeredObject = null;
            EbxAsset workingAsset = null;
            try
            {
                // Resolve again inside the commit: preview and execute are two
                // separate calls and the asset may have changed between them.
                //
                // GetEbx hands back a newly deserialized asset while the entry holds
                // no modified data yet, so two calls build two separate object graphs.
                // That is what made every structure insert vanish: the element went
                // into the first graph while Update and ModifyEbx stored the second.
                // One instance for the whole call is what keeps the edit.
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                workingAsset = asset;
                object host = ResolveInsertHost(asset, entry, hostGuid, p);
                if (host == null)
                    return McpHandlers.Error("host component not found", "NOT_FOUND");

                if (!TryResolveList(host, path, out IList list, out string resolvedPath))
                    return McpHandlers.Error("Could not resolve list at path: " + path, "NOT_FOUND");

                Type elementType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object);
                insertAt = index < 0 || index > list.Count ? list.Count : index;
                int countBefore = list.Count;

                if (sourceIndex >= 0)
                {
                    if (sourceIndex >= list.Count)
                        return McpHandlers.Error("source_index out of range", "INVALID_PARAMS");
                    item = CloneListElement(list[sourceIndex], asset, entry, elementType);
                }
                else
                {
                    item = DefaultElement(elementType);
                }

                if (item == null)
                    return McpHandlers.Error("Cannot create an element of type " + elementType.Name, "UNSUPPORTED_TYPE");

                list.Insert(insertAt, item);

                // An inserted internal object that the clipboard built is not part of
                // asset.Objects yet; the renumber pass walks that list, so register it
                // first or its slot is left out of the count.
                //
                // Only data-bus objects with an instance GUID can be registered.
                // Structure-like classes (AlternateWeaponSetCustomization and friends)
                // are written inline where they are referenced; AddObject sends them
                // down the instance-GUID path and throws, which used to leave the
                // element in the list while the call reported INSERT_FAILED.
                object inserted = item is PointerRef insertedRef ? insertedRef.Internal : item;
                if (inserted != null && !inserted.GetType().IsValueType
                    && McpExtraHandlers.HasInstanceGuid(inserted)
                    && !McpBlueprintHandlers.IsContainedInObjects(asset, inserted))
                {
                    asset.AddObject(inserted);
                    registeredObject = inserted;
                }

                // Every insert that changes the component set has to leave the runtime
                // counters describing the new set. The old repair only widened them on
                // PVZCharacterEntityData and left the ids themselves untouched, so any
                // other host (weapon blueprints included) kept stale counts and gaps.
                bool componentInsert = McpBlueprintHandlers.PathTargetsComponentList(resolvedPath)
                    || (inserted != null
                        && (inserted.GetType().GetProperty("ClientIndex") != null
                            || inserted.GetType().GetProperty("ServerIndex") != null));
                string indexSides = componentInsert
                    ? McpBlueprintHandlers.NormalizeComponentSlots(asset, host)
                    : null;
                asset.Update();
                App.AssetManager.ModifyEbx(entry.Name, asset);

                var result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = resolvedPath,
                    ["index"] = insertAt,
                    ["element_type"] = elementType.Name,
                    ["copied_from"] = sourceIndex >= 0 ? sourceIndex : -1,
                    ["count"] = list.Count
                };
                if (indexSides != null)
                    result["index_sides"] = indexSides;

                // Update() drops elements it cannot re-serialise, so read the list
                // back rather than trusting the insert to have survived.
                EbxAssetEntry refreshed = App.AssetManager.GetEbxEntry(entry.Name) ?? entry;
                EbxAsset after = App.AssetManager.GetEbx(refreshed);
                // Resolve the host the same way the insert did. Using the root unless
                // host_guid was passed made every component_type host report
                // VERIFY_FAILED after a successful insert, which invites a retry that
                // inserts the element twice.
                object afterHost = ResolveInsertHost(after, refreshed, hostGuid, p);

                if (afterHost == null || !TryResolveList(afterHost, path, out IList afterList, out _))
                {
                    result["success"] = false;
                    result["error"] = "The list could not be read back after the update";
                    result["error_code"] = "VERIFY_FAILED";
                }
                else if (afterList.Count != countBefore + 1)
                {
                    result["success"] = false;
                    result["error"] = "The stored asset does not contain the inserted element "
                        + "(ModifyEbx stored a different object graph than the one that was edited)";
                    result["error_code"] = "ITEM_DROPPED";
                    result["count_before"] = countBefore;
                    result["count_after"] = afterList.Count;
                }

                return result;
            }
            catch (Exception ex)
            {
                // Leave the asset exactly as it was. Before 2.3.21 this path only
                // reported the failure: the list kept the element that had already
                // been inserted, so a caller that retried ended up with two copies.
                try
                {
                    EbxAsset rollback = workingAsset ?? App.AssetManager.GetEbx(entry);
                    if (rollback != null)
                    {
                        object rollbackHost = ResolveInsertHost(rollback, entry, hostGuid, p);
                        if (rollbackHost != null && TryResolveList(rollbackHost, path, out IList rollbackList, out _)
                            && insertAt >= 0 && insertAt < rollbackList.Count
                            && Equals(rollbackList[insertAt], item))
                        {
                            rollbackList.RemoveAt(insertAt);
                        }
                        if (registeredObject != null)
                        {
                            try { rollback.RemoveObject(registeredObject); } catch { }
                        }
                        rollback.Update();
                        App.AssetManager.ModifyEbx(entry.Name, rollback);
                    }
                }
                catch { }
                return McpHandlers.Error(
                    ex.Message + " (the insert was rolled back; the list is unchanged)",
                    "INSERT_FAILED");
            }
            });
        }

        /// <summary>
        /// A source_index insert must not share the source element. FrostyClipboard
        /// deep copies EBX classes and lists and gives internal pointers a fresh
        /// identity in the host asset; primitives and value types are returned as-is.
        /// </summary>
        private static object CloneListElement(object source, EbxAsset asset, EbxAssetEntry entry, Type elementType)
        {
            if (source == null)
                return null;

            // Pointer elements need the clipboard: it deep copies the object graph and
            // renumbers internal pointers so the copy does not share the source.
            // Struct elements must not go through it. The clipboard hands back an
            // element the EBX writer then drops on save (insert reports success, the
            // read-back has one element less). A fresh instance plus a field copy is
            // what the property grid does and it survives the round trip.
            bool isPointer = elementType == typeof(PointerRef) || elementType.Name == "PointerRef";
            if (!isPointer)
            {
                object copy = DefaultElement(elementType);
                if (copy == null)
                    return null;
                foreach (PropertyInfo pi in elementType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (pi.Name.StartsWith("__") || !pi.CanWrite)
                        continue;
                    pi.SetValue(copy, pi.GetValue(source));
                }
                return copy;
            }

            FrostyClipboard.Current.SetData(source);
            return FrostyClipboard.Current.GetData(asset, entry);
        }

        private static object DefaultElement(Type elementType)
        {
            if (elementType == typeof(PointerRef) || elementType.Name == "PointerRef")
                return new PointerRef();
            if (elementType.IsValueType)
                return Activator.CreateInstance(elementType);
            // EBX classes have to come from the TypeLibrary the same way
            // create_struct makes them: a plain Activator instance carries
            // uninitialised identity fields and the serializer drops it, which
            // showed up as a structure insert that vanished on read-back.
            try
            {
                object created = TypeLibrary.CreateObject(elementType.Name);
                if (created != null)
                    return created;
            }
            catch { }
            if (elementType.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(elementType);
            return null;
        }

        // Host resolution shared by the preview and the commit of insert_ebx_item:
        // an explicit GUID first, then an explicit component type, and the root
        // only when the caller named neither. Naming a host that does not resolve
        // returns null so the caller reports NOT_FOUND instead of silently
        // inserting into the root object.
        private static object ResolveInsertHost(EbxAsset asset, EbxAssetEntry entry, string hostGuid, JObject p)
        {
            if (!string.IsNullOrEmpty(hostGuid))
                return FindObjectByGuid(asset, Guid.Parse(hostGuid));

            string typeName = p.Value<string>("component_type") ?? p.Value<string>("host_type") ?? "";
            if (string.IsNullOrEmpty(typeName))
                return asset.RootObject;

            return McpBlueprintHandlers.ResolveComponent(asset, new JObject
            {
                ["type"] = typeName,
                ["type_index"] = p.Value<int?>("type_index") ?? 0
            }, out _);
        }

        // Objects that are not exported keep an all-zero ExportedGuid and carry
        // their identity in InternalId, so comparing ExportedGuid alone never
        // finds them. Build the same synthesised guid GetClassGuid produces
        // (internal id as hex, padded to 32 digits) and compare that: one
        // conversion for every caller instead of a second, decimal-based one
        // that disagreed for anything above 9.
        private static object FindObjectByGuid(EbxAsset asset, Guid guid)
        {
            foreach (object obj in asset.Objects)
            {
                try
                {
                    if (McpBlueprintHandlers.GetClassGuid(obj) == guid)
                        return obj;
                }
                catch { }
            }
            return null;
        }

        // Accepts 'Components', 'Objects[2].Children' and similar simple paths.
        private static bool TryResolveList(object host, string path, out IList list, out string resolvedPath)
        {
            list = null;
            resolvedPath = path;

            string[] parts = path.Split('.');
            object current = host;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int bracket = part.IndexOf('[');
                string name = bracket >= 0 ? part.Substring(0, bracket) : part;
                int index = -1;
                if (bracket >= 0)
                {
                    string inner = part.Substring(bracket + 1).TrimEnd(']');
                    if (!int.TryParse(inner, out index))
                        return false;
                }

                object value = ReadMember(current, name);
                if (value == null)
                    return false;

                if (i == parts.Length - 1 && index < 0)
                {
                    list = value as IList;
                    return list != null;
                }

                if (value is IList outer)
                {
                    if (index < 0 || index >= outer.Count)
                        return false;
                    current = outer[index];
                }
                else
                {
                    current = value;
                }
            }

            list = current as IList;
            return list != null;
        }

        private static object ReadMember(object host, string name)
        {
            if (host == null || string.IsNullOrEmpty(name))
                return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            PropertyInfo pi = host.GetType().GetProperty(name, flags);
            if (pi != null && pi.GetIndexParameters().Length == 0)
                return pi.GetValue(host);
            FieldInfo fi = host.GetType().GetField(name, flags);
            return fi?.GetValue(host);
        }
    }
}
