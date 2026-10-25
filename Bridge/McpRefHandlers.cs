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

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Pointer-level reference search and array insertion, the two gaps the audit
    /// left in the component tools.
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

                foreach (object obj in asset.Objects)
                {
                    CollectPointerHits(obj, obj.GetType(), "", entry, fileGuid, classGuid, hits, ref total, offset, limit);
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

        private static void CollectPointerHits(object obj, Type type, string prefix, EbxAssetEntry entry,
            Guid fileGuid, Guid classGuid, List<object> hits, ref int total, int offset, int limit)
        {
            if (obj == null)
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
                        if (!(list[i] is PointerRef itemRef) || !PointerMatches(itemRef, fileGuid, classGuid))
                            continue;
                        total++;
                        int index = total - 1;
                        if (index < offset || hits.Count >= limit)
                            continue;
                        hits.Add(new Dictionary<string, object>
                        {
                            ["asset"] = entry.Name,
                            ["object_type"] = type.Name,
                            ["path"] = path + "[" + i + "]",
                            ["target_file_guid"] = itemRef.External.FileGuid.ToString(),
                            ["target_class_guid"] = itemRef.External.ClassGuid.ToString()
                        });
                    }
                }
            }
        }

        private static bool PointerMatches(PointerRef pr, Guid fileGuid, Guid classGuid)
        {
            if (pr.Type != FrostySdk.IO.PointerRefType.External)
                return false;
            if (pr.External.FileGuid != fileGuid)
                return false;
            return classGuid == Guid.Empty || pr.External.ClassGuid == classGuid;
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

            EbxAsset hostAsset = App.AssetManager.GetEbx(entry);
            object host = hostAsset.RootObject;
            string hostGuid = p.Value<string>("class_guid") ?? p.Value<string>("component_guid") ?? "";
            if (!string.IsNullOrEmpty(hostGuid))
            {
                if (!Guid.TryParse(hostGuid, out Guid g))
                    return McpHandlers.Error("class_guid is not a GUID", "INVALID_PARAMS");
                object found = FindObjectByGuid(entry, g);
                if (found == null)
                    return McpHandlers.Error("host component not found", "NOT_FOUND");
                host = found;
            }

            if (!TryResolveList(host, path, out IList list, out string resolvedPath))
                return McpHandlers.Error("Could not resolve list at path: " + path, "NOT_FOUND");

            Type elementType = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object);
            if (index < 0) index = list.Count;
            if (index > list.Count) index = list.Count;

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

            list.Insert(index, item);

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            asset.Update();
            App.AssetManager.ModifyEbx(entry.Name, asset);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["path"] = resolvedPath,
                ["index"] = index,
                ["element_type"] = elementType.Name,
                ["copied_from"] = sourceIndex >= 0 ? sourceIndex : -1,
                ["count"] = list.Count
            };
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
