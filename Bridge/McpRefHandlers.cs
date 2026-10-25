using Frosty.Core;
using FrostySdk;
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
            if (maxScan < 1) maxScan = 1;
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
                if (scanned >= maxScan)
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
                ["max_scan"] = maxScan,
                ["scan_truncated"] = scanTruncated,
                ["load_failures"] = loadFailures,
                ["total"] = total,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = hits.Count,
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
            return type.Name == "BoxedValue";
        }

        private static bool PointerMatches(PointerRef pr, Guid fileGuid, Guid classGuid)
        {
            if (pr.Type != FrostySdk.IO.PointerRefType.External)
                return false;
            if (pr.External.FileGuid != fileGuid)
                return false;
            return classGuid == Guid.Empty || pr.External.ClassGuid == classGuid;
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

            return McpCommit.Require("insert_ebx_item", p, plan, () =>
            {
            try
            {
                // Resolve again inside the commit: preview and execute are two
                // separate calls and the asset may have changed between them.
                EbxAsset hostAsset = App.AssetManager.GetEbx(entry);
                object host = hostAsset.RootObject;
                if (!string.IsNullOrEmpty(hostGuid))
                {
                    object found = FindObjectByGuid(entry, Guid.Parse(hostGuid));
                    if (found == null)
                        return McpHandlers.Error("host component not found", "NOT_FOUND");
                    host = found;
                }

                if (!TryResolveList(host, path, out IList list, out string resolvedPath))
                    return McpHandlers.Error("Could not resolve list at path: " + path, "NOT_FOUND");

                Type elementType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object);
                int insertAt = index < 0 || index > list.Count ? list.Count : index;
                int countBefore = list.Count;

                object item;
                if (sourceIndex >= 0)
                {
                    if (sourceIndex >= list.Count)
                        return McpHandlers.Error("source_index out of range", "INVALID_PARAMS");
                    item = list[sourceIndex];
                }
                else
                {
                    item = DefaultElement(elementType);
                }

                if (item == null)
                    return McpHandlers.Error("Cannot create an element of type " + elementType.Name, "UNSUPPORTED_TYPE");

                list.Insert(insertAt, item);

                EbxAsset asset = App.AssetManager.GetEbx(entry);
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

                // Update() drops elements it cannot re-serialise, so read the list
                // back rather than trusting the insert to have survived.
                EbxAssetEntry refreshed = App.AssetManager.GetEbxEntry(entry.Name) ?? entry;
                EbxAsset after = App.AssetManager.GetEbx(refreshed);
                object afterHost = after.RootObject;
                if (!string.IsNullOrEmpty(hostGuid))
                    afterHost = FindObjectByGuid(refreshed, Guid.Parse(hostGuid));

                if (afterHost == null || !TryResolveList(afterHost, path, out IList afterList, out _))
                {
                    result["success"] = false;
                    result["error"] = "The list could not be read back after the update";
                    result["error_code"] = "VERIFY_FAILED";
                }
                else if (afterList.Count != countBefore + 1)
                {
                    result["success"] = false;
                    result["error"] = "The element did not survive the asset update";
                    result["error_code"] = "ITEM_DROPPED";
                    result["count_before"] = countBefore;
                    result["count_after"] = afterList.Count;
                }

                return result;
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "INSERT_FAILED");
            }
            });
        }

        private static object DefaultElement(Type elementType)
        {
            if (elementType == typeof(PointerRef) || elementType.Name == "PointerRef")
                return new PointerRef();
            if (elementType.IsValueType)
                return Activator.CreateInstance(elementType);
            if (elementType.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(elementType);
            return null;
        }

        private static object FindObjectByGuid(EbxAssetEntry entry, Guid guid)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            foreach (object obj in asset.Objects)
            {
                try
                {
                    if (((dynamic)obj).GetInstanceGuid().ExportedGuid == guid)
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
