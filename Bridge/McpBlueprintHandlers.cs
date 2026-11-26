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
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FrostySdk.Managers.Entries;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Blueprint-oriented MCP tools: Property/Event/Link connections,
    /// component create/duplicate/flags/properties.
    /// Ported from BF1DevPlugin.Base.Blueprint / Core patterns.
    /// </summary>
    internal static class McpBlueprintHandlers
    {
        private const uint GuidMask = 0x00FFFFFFu;
        private const uint UnknownFlag = 0x01000000u;
        private const uint ClientEventFlag = 0x02000000u;
        private const uint ServerEventFlag = 0x04000000u;
        private const uint ClientPropertyFlag = 0x08000000u;
        private const uint ServerPropertyFlag = 0x10000000u;
        private const uint ClientLinkFlag = 0x20000000u;
        private const uint ServerLinkFlag = 0x40000000u;
        private const uint UnusedFlag = 0x80000000u;

        // A malformed/self-referential object graph must not take the editor's
        // only bridge thread down for minutes. Both search paths check this
        // budget between nodes and return partial data with scan_truncated=true.
        private sealed class ScanBudget
        {
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private readonly int _milliseconds;
            private readonly int _maxNodes;
            internal int Nodes;
            internal bool Expired;

            internal ScanBudget(int milliseconds, int maxNodes)
            {
                _milliseconds = milliseconds < 1000 ? 1000 : milliseconds;
                _maxNodes = maxNodes < 1 ? int.MaxValue : maxNodes;
            }

            internal bool Stop()
            {
                if (Expired)
                    return true;
                if (_watch.ElapsedMilliseconds >= _milliseconds || Nodes >= _maxNodes)
                    Expired = true;
                return Expired;
            }

            internal void Node()
            {
                Nodes++;
            }
        }

        // =====================================================================
        // Connections: list / add / update / remove
        // =====================================================================

        public static object ListConnections(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            dynamic root = asset.RootObject;
            string kind = (p.Value<string>("kind") ?? "all").Trim().ToLowerInvariant();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);
            bool compact = p.Value<bool?>("compact") ?? true;
            string componentGuid = (p.Value<string>("component_guid") ?? "").Trim();
            string textFilter = (p.Value<string>("filter") ?? "").Trim();
            int scanMs = p.Value<int?>("max_scan_ms") ?? 20000;
            if (scanMs < 1000) scanMs = 1000;
            if (scanMs > 60000) scanMs = 60000;
            ScanBudget budget = new ScanBudget(scanMs, int.MaxValue);

            List<object> items = new List<object>();
            int scanErrors = 0;
            if ((kind == "all" || kind == "property") && !budget.Expired)
                scanErrors += AppendPropertyConnections(root, items, compact, budget);
            if ((kind == "all" || kind == "event") && !budget.Expired)
                scanErrors += AppendEventConnections(root, items, compact, budget);
            if ((kind == "all" || kind == "link") && !budget.Expired)
                scanErrors += AppendLinkConnections(root, items, compact, budget);

            if (!string.IsNullOrEmpty(componentGuid) || !string.IsNullOrEmpty(textFilter))
            {
                items = items.Where(x => ConnectionMatches(x, componentGuid, textFilter)).ToList();
            }

            List<object> page = items.Skip(offset).Take(limit).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["total"] = items.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["compact"] = compact,
                // A connection that fails to serialize must not silently truncate
                // the list: the item is kept as a placeholder and counted here.
                ["scan_errors"] = scanErrors,
                ["scan_truncated"] = budget.Expired,
                ["note"] = budget.Expired
                    ? "SCAN INCOMPLETE: the scan budget (" + scanMs + " ms) expired; only the connections serialized before the deadline are listed."
                    : scanErrors > 0
                        ? "SCAN INCOMPLETE: " + scanErrors + " connection(s) failed to serialize and are reported as placeholder items; the list may be missing data."
                        : null,
                ["connections"] = page
            };
        }

        public static object AddPropertyConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                PointerRef source = ResolvePointerRef(asset, p["source"] as JObject ?? p, "source_");
                PointerRef target = ResolvePointerRef(asset, p["target"] as JObject ?? p, "target_");
                string sourceField = p.Value<string>("source_field") ?? "";
                string targetField = p.Value<string>("target_field") ?? "";
                uint flags = ResolvePropertyConnectionFlags(p);

                dynamic conn = TypeLibrary.CreateObject("PropertyConnection");
                conn.Source = source;
                conn.Target = target;
                conn.SourceField = (CString)sourceField;
                conn.TargetField = (CString)targetField;
                conn.Flags = flags;

                // The editor recomputes the endpoints' DataBus flags from the
                // connection; without this a freshly created entity keeps all-zero
                // flags and never evaluates at runtime.
                string realmText = p.Value<string>("realm") ?? "ClientAndServer";
                bool connServer = realmText.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0;
                bool connClient = realmText.IndexOf("Client", StringComparison.OrdinalIgnoreCase) >= 0 || !connServer;
                SetConnectionEndpointFlag(source, false, connClient, connServer);
                SetConnectionEndpointFlag(target, false, connClient, connServer);

                dynamic root = asset.RootObject;
                root.PropertyConnections.Add(conn);
                SaveAsset(entry, asset);

                int index = root.PropertyConnections.Count - 1;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["kind"] = "property",
                    ["index"] = index,
                    ["connection"] = SerializePropertyConnection(conn, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        public static object AddEventConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                PointerRef source = ResolvePointerRef(asset, p["source"] as JObject ?? p, "source_");
                PointerRef target = ResolvePointerRef(asset, p["target"] as JObject ?? p, "target_");
                string sourceEvent = p.Value<string>("source_event") ?? p.Value<string>("source_field") ?? "";
                string targetEvent = p.Value<string>("target_event") ?? p.Value<string>("target_field") ?? "";
                string targetType = p.Value<string>("target_type") ?? "EventConnectionTargetType_ClientAndServer";

                dynamic conn = TypeLibrary.CreateObject("EventConnection");
                conn.Source = source;
                conn.Target = target;
                conn.SourceEvent.Name = (CString)sourceEvent;
                conn.TargetEvent.Name = (CString)targetEvent;
                SetEnumProperty(conn, "TargetType", targetType);

                bool evServer = targetType.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0;
                bool evClient = targetType.IndexOf("Client", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!evServer && !evClient)
                    evClient = true;
                SetConnectionEndpointFlag(source, true, evClient, evServer);
                SetConnectionEndpointFlag(target, true, evClient, evServer);

                dynamic root = asset.RootObject;
                root.EventConnections.Add(conn);
                SaveAsset(entry, asset);

                int index = root.EventConnections.Count - 1;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["kind"] = "event",
                    ["index"] = index,
                    ["connection"] = SerializeEventConnection(conn, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        public static object AddLinkConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                PointerRef source = ResolvePointerRef(asset, p["source"] as JObject ?? p, "source_");
                PointerRef target = ResolvePointerRef(asset, p["target"] as JObject ?? p, "target_");
                string sourceField = p.Value<string>("source_field") ?? "";
                string targetField = p.Value<string>("target_field") ?? "";

                dynamic conn = TypeLibrary.CreateObject("LinkConnection");
                conn.Source = source;
                conn.Target = target;
                conn.SourceField = (CString)sourceField;
                conn.TargetField = (CString)targetField;

                dynamic root = asset.RootObject;
                root.LinkConnections.Add(conn);
                SaveAsset(entry, asset);

                int index = root.LinkConnections.Count - 1;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["kind"] = "link",
                    ["index"] = index,
                    ["connection"] = SerializeLinkConnection(conn, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        public static object UpdatePropertyConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic root = asset.RootObject;
                int index = p.Value<int?>("index") ?? -1;
                if (index < 0 || index >= root.PropertyConnections.Count)
                    return McpHandlers.Error("Invalid property connection index", "INVALID_PARAMS");

                dynamic conn = root.PropertyConnections[index];

                if (p["source"] != null || p["source_guid"] != null || p["source_asset"] != null)
                    conn.Source = ResolvePointerRef(asset, p["source"] as JObject ?? p, "source_");
                if (p["target"] != null || p["target_guid"] != null || p["target_asset"] != null)
                    conn.Target = ResolvePointerRef(asset, p["target"] as JObject ?? p, "target_");
                if (p["source_field"] != null)
                    conn.SourceField = (CString)(p.Value<string>("source_field") ?? "");
                if (p["target_field"] != null)
                    conn.TargetField = (CString)(p.Value<string>("target_field") ?? "");
                if (HasFlagParams(p) || p["flags"] != null || p["realm"] != null || p["prop_type"] != null)
                    conn.Flags = ResolvePropertyConnectionFlags(p, (uint)conn.Flags);

                SaveAsset(entry, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["kind"] = "property",
                    ["index"] = index,
                    ["connection"] = SerializePropertyConnection(conn, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "UPDATE_FAILED");
            }
        }

        public static object UpdateEventConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic root = asset.RootObject;
                int index = p.Value<int?>("index") ?? -1;
                if (index < 0 || index >= root.EventConnections.Count)
                    return McpHandlers.Error("Invalid event connection index", "INVALID_PARAMS");

                dynamic conn = root.EventConnections[index];

                if (p["source"] != null || p["source_guid"] != null)
                    conn.Source = ResolvePointerRef(asset, p["source"] as JObject ?? p, "source_");
                if (p["target"] != null || p["target_guid"] != null)
                    conn.Target = ResolvePointerRef(asset, p["target"] as JObject ?? p, "target_");
                if (p["source_event"] != null || p["source_field"] != null)
                    conn.SourceEvent.Name = (CString)(p.Value<string>("source_event") ?? p.Value<string>("source_field") ?? "");
                if (p["target_event"] != null || p["target_field"] != null)
                    conn.TargetEvent.Name = (CString)(p.Value<string>("target_event") ?? p.Value<string>("target_field") ?? "");
                if (p["target_type"] != null)
                    SetEnumProperty(conn, "TargetType", p.Value<string>("target_type"));

                SaveAsset(entry, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["kind"] = "event",
                    ["index"] = index,
                    ["connection"] = SerializeEventConnection(conn, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "UPDATE_FAILED");
            }
        }

        public static object UpdateLinkConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic root = asset.RootObject;
                int index = p.Value<int?>("index") ?? -1;
                if (index < 0 || index >= root.LinkConnections.Count)
                    return McpHandlers.Error("Invalid link connection index", "INVALID_PARAMS");

                dynamic conn = root.LinkConnections[index];

                if (p["source"] != null || p["source_guid"] != null || p["source_asset"] != null)
                    conn.Source = ResolvePointerRef(asset, p["source"] as JObject ?? p, "source_");
                if (p["target"] != null || p["target_guid"] != null || p["target_asset"] != null)
                    conn.Target = ResolvePointerRef(asset, p["target"] as JObject ?? p, "target_");
                if (p["source_field"] != null)
                    conn.SourceField = (CString)(p.Value<string>("source_field") ?? "");
                if (p["target_field"] != null)
                    conn.TargetField = (CString)(p.Value<string>("target_field") ?? "");

                SaveAsset(entry, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["kind"] = "link",
                    ["index"] = index,
                    ["connection"] = SerializeLinkConnection(conn, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "UPDATE_FAILED");
            }
        }

        public static object RemoveConnection(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string kind = (p.Value<string>("kind") ?? "").Trim().ToLowerInvariant();
            int index = p.Value<int?>("index") ?? -1;
            if (string.IsNullOrEmpty(kind) || index < 0)
                return McpHandlers.Error("kind (property|event|link) and index are required", "INVALID_PARAMS");

            try
            {
                dynamic root = asset.RootObject;
                IList list = null;
                if (kind == "property")
                    list = (IList)root.PropertyConnections;
                else if (kind == "event")
                    list = (IList)root.EventConnections;
                else if (kind == "link")
                    list = (IList)root.LinkConnections;
                if (list == null)
                    return McpHandlers.Error("kind must be property|event|link", "INVALID_PARAMS");
                if (index >= list.Count)
                    return McpHandlers.Error("Index out of range", "INVALID_PARAMS");

                var plan = new Dictionary<string, object>
                {
                    ["name"] = entry.Name,
                    ["kind"] = kind,
                    ["index"] = index,
                    ["remaining_after"] = list.Count - 1
                };

                return McpCommit.Require("remove_connection", p, plan, () =>
                {
                    object removed = list[index];
                    if (removed == null)
                        return McpHandlers.Error("Connection at index " + index + " is null; nothing was removed", "REMOVE_FAILED");
                    int before = list.Count;
                    try
                    {
                        list.RemoveAt(index);
                        if (list.Count != before - 1)
                            return McpHandlers.Error("Connection was not removed (count stayed " + list.Count + ")", "REMOVE_FAILED");
                        SaveAsset(entry, asset);
                    }
                    catch (Exception ex)
                    {
                        if (list.Count == before - 1)
                            list.Insert(index, removed);
                        // Surface the inner exception: the outer one is always the
                        // same bare null reference and names no object, which made
                        // this failure impossible to diagnose from the tool result.
                        string detail = ex.InnerException == null
                            ? ex.ToString()
                            : ex.Message + " <- " + ex.InnerException.Message;
                        return McpHandlers.Error(detail + " (change rolled back)", "REMOVE_FAILED");
                    }
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["kind"] = kind,
                        ["removed_index"] = index,
                        ["remaining"] = list.Count
                    };
                });
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }
        }

        public static object DecodeConnectionFlags(JObject p)
        {
            uint flags = 0;
            if (p["flags"] != null)
            {
                string s = p["flags"].ToString().Trim();
                try
                {
                    flags = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToUInt32(s.Substring(2), 16)
                        : Convert.ToUInt32(s);
                }
                catch
                {
                    return McpHandlers.Error("flags must be a decimal or 0x-prefixed 32-bit integer", "INVALID_PARAMS");
                }
            }

            DecodePropertyConnectionFlags(flags, out string realm, out string propType, out bool sourceCantBeStatic);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["flags"] = flags,
                ["flags_hex"] = "0x" + flags.ToString("X8"),
                ["realm"] = realm,
                ["prop_type"] = propType,
                ["source_cant_be_static"] = sourceCantBeStatic
            };
        }

        // =====================================================================
        // Components: list / get / create / duplicate / flags / properties
        // =====================================================================

        public static object ListComponents(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string query = (p.Value<string>("query") ?? "").Trim();
            string typeFilter = (p.Value<string>("type") ?? "").Trim();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);
            bool compact = p.Value<bool?>("compact") ?? true;

            // SerializeComponent resolves several members per object, so it runs only
            // for rows inside the page window; the rest are only counted.
            int total = 0;
            List<object> page = new List<object>(limit);
            int i = 0;
            foreach (object obj in asset.Objects)
            {
                int objectIndex = i++;
                string typeName = obj.GetType().Name;
                if (!string.IsNullOrEmpty(typeFilter) &&
                    !TypeLibrary.IsSubClassOf(typeName, typeFilter) &&
                    !typeName.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(query) &&
                    typeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (GetObjectName(obj) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int index = total++;
                if (index < offset || page.Count >= limit)
                    continue;
                page.Add(SerializeComponent(obj, objectIndex, asset, compact));
            }
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["total"] = total,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["components"] = page
            };
        }

        public static object GetComponent(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            object comp = ResolveComponent(asset, p, out int index);
            if (comp == null)
                return McpHandlers.Error("Component not found (provide class_guid or index)", "NOT_FOUND");

            bool includeProperties = p.Value<bool?>("include_properties") ?? true;
            Dictionary<string, object> result = SerializeComponent(comp, index, asset);
            result["success"] = true;
            if (includeProperties)
                result["properties"] = DumpPublicProperties(comp);
            return result;
        }

        public static object CreateComponent(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string typeName = (p.Value<string>("type") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("type is required (Frostbite class name, e.g. DelayEntityData)", "INVALID_PARAMS");

            Type type = TypeLibrary.GetType(typeName);
            if (type == null)
                return McpHandlers.Error("Type not found in SDK: " + typeName, "TYPE_NOT_FOUND");

            try
            {
                List<object> preexisting = asset.Objects.ToList();
                object newObject = TypeLibrary.CreateObject(typeName);
                AssetClassGuid classGuid = new AssetClassGuid(
                    Utils.GenerateDeterministicGuid(asset.Objects, typeName, asset.FileGuid), -1);
                ((dynamic)newObject).SetInstanceGuid(classGuid);

                // DataBusPeer: seed Flags low-24 from guid bytes (BF1DevPlugin pattern)
                if (TypeLibrary.IsSubClassOf(newObject, "DataBusPeer"))
                {
                    byte[] bytes = classGuid.ExportedGuid.ToByteArray();
                    uint flagValue = (uint)((bytes[2] << 16) | (bytes[1] << 8) | bytes[0]);
                    SetObjectFlagsRaw(newObject, flagValue);
                }

                // Optional object-flags / realm presets
                if (HasObjectFlagParams(p) || p["flag_preset"] != null)
                    ApplyObjectFlags(newObject, p, preserveGuidBits: true);
                if (p["realm"] != null)
                    SetEnumProperty(newObject, "Realm", p.Value<string>("realm"));

                // Optional initial properties
                if (p["properties"] is JObject props)
                {
                    foreach (JProperty prop in props.Properties())
                        SetMemberValue(newObject, prop.Name, prop.Value);
                }

                asset.AddObject(newObject);
                MakeCreatedObjectInternal(newObject);
                RepairInternalIdCollisions(asset, preexisting);
                // Slot numbering runs after the pointer is in its array: the pass walks the
                // finished tree, and an object that nothing points at yet would be treated as
                // unreachable and park at the end of the numbering instead of its real place.
                string indexSides = null;
                if (!IsContainedInObjects(asset, newObject))
                {
                    return McpHandlers.Error(
                        "asset.AddObject did not register the new object (the serializer cannot write this type): " + typeName,
                        "OBJECT_NOT_PERSISTED");
                }
                if (!IsClassGuidUnique(asset, GetClassGuid(newObject)))
                {
                    asset.RemoveObject(newObject);
                    return McpHandlers.Error(
                        "Generated object GUID collides with an existing object: " + GetClassGuid(newObject),
                        "GUID_COLLISION");
                }
                PointerRef pref = new PointerRef(newObject);

                // A SoundGraph node that carries plugin refs needs a new voice and
                // plugin instances; this writer cannot create those, and silently
                // appending the node made it share another voice's plugins at
                // runtime. Refuse before the node enters any array.
                if (IsSoundPatchAsset(asset) && HasValidSoundGraphPluginRefs(newObject))
                {
                    asset.RemoveObject(newObject);
                    return McpHandlers.Error(
                        "SoundGraph node '" + newObject.GetType().Name + "' carries SoundGraphPluginRef members; creating it without a matching voice/plugin registration would share another voice's plugins at runtime. Create it in the Frosty GUI, or extend the tool to copy the voice/plugin tables.",
                        "SOUNDGRAPH_VOICE_UNREGISTERED");
                }

                // Target array lives on the root object unless a host component is named
                object arrayHost = asset.RootObject;
                string hostGuidStr = p.Value<string>("array_host_guid") ?? p.Value<string>("array_host");
                if (!string.IsNullOrEmpty(hostGuidStr))
                {
                    if (!Guid.TryParse(hostGuidStr, out Guid hostGuid))
                        return McpHandlers.Error("array_host_guid is not a GUID: " + hostGuidStr, "INVALID_PARAMS");
                    arrayHost = FindObjectByClassGuid(asset, hostGuid);
                    if (arrayHost == null)
                        return McpHandlers.Error("array_host_guid not found in asset: " + hostGuidStr, "NOT_FOUND");
                }

                string arrayName = p.Value<string>("array");
                bool explicitName = !string.IsNullOrEmpty(arrayName);
                if (!explicitName && string.IsNullOrEmpty(hostGuidStr) && !MustStayOnRootObjects(newObject)
                    && TryFindDefaultComponentArray(asset, out object defaultHost, out string defaultArray))
                {
                    arrayHost = defaultHost;
                    arrayName = defaultArray;
                }
                else if (explicitName && string.IsNullOrEmpty(hostGuidStr) && !MustStayOnRootObjects(newObject)
                    && string.Equals(arrayName, "Components", StringComparison.OrdinalIgnoreCase)
                    && !(GetListMember(arrayHost, "Components") is IList)
                    && TryFindDefaultComponentArray(asset, out object componentsHost, out _))
                {
                    arrayHost = componentsHost;
                }
                if (arrayName == null)
                    arrayName = "Objects";
                int atIndex = p.Value<int?>("at_index") ?? -1;
                int addedIndex = atIndex;
                string usedArray = atIndex >= 0
                    ? (TryInsertPointerToArray(arrayHost, arrayName, pref, atIndex) ? arrayName : null)
                    : TryAddPointerToArrays(arrayHost, pref, arrayName, explicitName: explicitName, out addedIndex);

                if (usedArray == null)
                {
                    // Nothing points at the new object; drop it instead of leaving an
                    // unreachable orphan that would still be written to the asset.
                    asset.RemoveObject(newObject);
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "Array not found: " + arrayName,
                        ["error_code"] = "ARRAY_NOT_FOUND",
                        ["available_arrays"] = ListPointerArrayNames(arrayHost)
                    };
                }

                // The new node must actually resolve from the array it was written
                // into. Some graph object types (SoundGraph nodes, BehaviorTree
                // nodes) are dropped by the serializer; before this check the call
                // returned success while the array kept a Null pointer.
                IList usedList = GetListMember(arrayHost, usedArray) as IList;
                if (usedList == null || FindPointerIndex(usedList, newObject) < 0)
                {
                    if (usedList != null && usedList.Count > 0 && IsNullPointerSlot(usedList[usedList.Count - 1]))
                        usedList.RemoveAt(usedList.Count - 1);
                    asset.RemoveObject(newObject);
                    SaveAsset(entry, asset);
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "Object was created but did not persist in '" + usedArray + "'; the serializer dropped it. Rolled back.",
                        ["error_code"] = "OBJECT_NOT_PERSISTED"
                    };
                }

                string spawnerHost = null;
                string entityHost = null;
                if (usedArray == "Objects" && ReferenceEquals(arrayHost, asset.RootObject) && !MustStayOnRootObjects(newObject))
                {
                    spawnerHost = RegisterIntoWidgetSpawner(asset, pref);
                    entityHost = RegisterIntoCharacterEntity(asset, pref);
                }
                string graphHost = RegisterIntoSoundGraph(asset, pref);

                indexSides = AssignComponentIndices(asset, newObject, null, p, arrayHost);

                SaveAsset(entry, asset);

                // Re-read the saved asset: object types the serializer cannot write
                // (SoundGraph / BehaviorTree nodes) come back as a Null slot. This is
                // the check that turns the old phantom success into an explicit error.
                if (addedIndex >= 0 && ReferenceEquals(arrayHost, asset.RootObject))
                {
                    EbxAsset reloaded = App.AssetManager.GetEbx(entry);
                    IList reloadedList = reloaded == null ? null : GetListMember(reloaded.RootObject, usedArray) as IList;
                    object reloadedItem = reloadedList != null && addedIndex < reloadedList.Count
                        ? UnwrapPointer(reloadedList[addedIndex])
                        : null;
                    if (reloadedItem == null || reloadedItem.GetType().Name != newObject.GetType().Name)
                    {
                        if (reloadedList != null && addedIndex < reloadedList.Count && IsNullPointerSlot(reloadedList[addedIndex]))
                        {
                            reloadedList.RemoveAt(addedIndex);
                            SaveAsset(entry, reloaded);
                        }
                        return new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = "Object was created but the serializer dropped it (array slot reads back as null); rolled back.",
                            ["error_code"] = "OBJECT_NOT_PERSISTED"
                        };
                    }
                }

                // A component belongs to the entity, not to the blueprint root.
                // Every vanilla character blueprint keeps the root Objects array
                // empty, and the editor's component path does the same
                // (ComponentNodeWrangler.cs:43-46 appends to a Components list).
                // Leaving the pointer in the root array as well makes the loader
                // treat the component as a child object of the blueprint, and the
                // component table it builds then carries an entry with no
                // instance behind it.
                if (entityHost != null && ReferenceEquals(arrayHost, asset.RootObject))
                {
                    IList rootObjects = GetListMember(asset.RootObject, "Objects") as IList;
                    int rootPos = FindPointerIndex(rootObjects, newObject);
                    if (rootPos >= 0)
                    {
                        rootObjects.RemoveAt(rootPos);
                        SaveAsset(entry, asset);
                    }
                }

                int index = IndexOfObject(asset, newObject);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["added_to_array"] = usedArray,
                    ["registered_in"] = spawnerHost,
                    ["registered_in_entity"] = entityHost,
                    ["registered_in_graph"] = graphHost,
                    ["array_host"] = arrayHost.GetType().Name,
                    ["index_sides"] = indexSides,
                    ["component"] = SerializeComponent(newObject, index, asset),
                    ["pointer"] = SerializePointerRef(pref)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "CREATE_FAILED");
            }
        }

        public static object DuplicateComponent(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            object source = ResolveComponent(asset, p, out int srcIndex);
            if (source == null)
                return McpHandlers.Error("Source component not found", "NOT_FOUND");

            try
            {
                List<object> preexisting = asset.Objects.Cast<object>().ToList();

                // Clipboard DeepCopy regenerates GUIDs correctly (same as BF1DevPlugin)
                FrostyClipboard.Current.SetData(source);
                object copy = FrostyClipboard.Current.GetData(asset, entry);
                if (copy == null)
                    return McpHandlers.Error("Clipboard duplicate returned null", "DUPLICATE_FAILED");

                // DeepCopy with asset context should already AddObject for pointer graphs;
                // ensure top-level object is registered.
                if (!asset.Objects.Contains(copy) && !IsContainedInObjects(asset, copy))
                    asset.AddObject(copy);
                if (!IsContainedInObjects(asset, copy))
                {
                    return McpHandlers.Error(
                        "asset.AddObject did not register the duplicate (the serializer cannot write this type): " + copy.GetType().Name,
                        "OBJECT_NOT_PERSISTED");
                }
                if (!IsClassGuidUnique(asset, GetClassGuid(copy)))
                {
                    asset.RemoveObject(copy);
                    return McpHandlers.Error(
                        "Duplicated object GUID collides with an existing object: " + GetClassGuid(copy),
                        "GUID_COLLISION");
                }

                // DeepCopy hands the objects over with InternalId = -1 and AddObject
                // assigns max+1, so this guards against ids that still collide or are
                // negative. Renumber only the objects this duplicate added.
                RepairInternalIdCollisions(asset, preexisting);

                // A duplicate carries the source's ClientIndex/ServerIndex; leaving them
                // alone makes two components claim the same runtime slot and the asset
                // crashes when the game instantiates it. The pass runs after the copy is
                // in its array, because it walks the finished tree.
                string indexSides = null;

                // Same guard as CreateComponent: a plugin-bearing SoundGraph node
                // copied without its voice/plugin tables would share another
                // voice's plugins at runtime.
                if (IsSoundPatchAsset(asset) && HasValidSoundGraphPluginRefs(copy))
                {
                    foreach (object addedObj in asset.Objects.ToList())
                    {
                        if (!preexisting.Contains(addedObj))
                            asset.RemoveObject(addedObj);
                    }
                    return McpHandlers.Error(
                        "Duplicated SoundGraph node '" + copy.GetType().Name + "' carries SoundGraphPluginRef members; the copy needs its own voice/plugin registration. Create it in the Frosty GUI, or extend the tool to copy the voice/plugin tables.",
                        "SOUNDGRAPH_VOICE_UNREGISTERED");
                }

                PointerRef pref = new PointerRef(copy);
                object arrayHost = asset.RootObject;
                string arrayName = p.Value<string>("array");
                bool explicitName = !string.IsNullOrEmpty(arrayName);
                if (!explicitName && !MustStayOnRootObjects(copy)
                    && TryFindDefaultComponentArray(asset, out object defaultHost, out string defaultArray))
                {
                    arrayHost = defaultHost;
                    arrayName = defaultArray;
                }
                else if (explicitName && !MustStayOnRootObjects(copy)
                    && string.Equals(arrayName, "Components", StringComparison.OrdinalIgnoreCase)
                    && !(GetListMember(arrayHost, "Components") is IList)
                    && TryFindDefaultComponentArray(asset, out object componentsHost, out _))
                {
                    arrayHost = componentsHost;
                }
                if (arrayName == null)
                    arrayName = "Objects";
                int atIndex = p.Value<int?>("at_index") ?? -1;
                int addedIndex = atIndex;
                string usedArray = atIndex >= 0
                    ? (TryInsertPointerToArray(arrayHost, arrayName, pref, atIndex) ? arrayName : null)
                    : TryAddPointerToArrays(arrayHost, pref, arrayName, explicitName: explicitName, out addedIndex);

                if (usedArray == null)
                {
                    // Roll back every object this duplicate introduced.
                    foreach (object addedObj in asset.Objects.ToList())
                    {
                        if (!preexisting.Contains(addedObj))
                            asset.RemoveObject(addedObj);
                    }
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "Array not found: " + arrayName,
                        ["error_code"] = "ARRAY_NOT_FOUND",
                        ["available_arrays"] = ListPointerArrayNames(arrayHost)
                    };
                }

                // Same persistence check as CreateComponent: a duplicate that the
                // serializer drops must not be reported as success.
                IList usedList = GetListMember(arrayHost, usedArray) as IList;
                if (usedList == null || FindPointerIndex(usedList, copy) < 0)
                {
                    if (usedList != null && usedList.Count > 0 && IsNullPointerSlot(usedList[usedList.Count - 1]))
                        usedList.RemoveAt(usedList.Count - 1);
                    foreach (object addedObj in asset.Objects.ToList())
                    {
                        if (!preexisting.Contains(addedObj))
                            asset.RemoveObject(addedObj);
                    }
                    SaveAsset(entry, asset);
                    return new Dictionary<string, object>
                    {
                        ["success"] = false,
                        ["error"] = "Duplicate did not persist in '" + usedArray + "'; rolled back.",
                        ["error_code"] = "OBJECT_NOT_PERSISTED"
                    };
                }

                string spawnerHost = null;
                string entityHost = null;
                if (usedArray == "Objects" && ReferenceEquals(arrayHost, asset.RootObject) && !MustStayOnRootObjects(copy))
                {
                    spawnerHost = RegisterIntoWidgetSpawner(asset, pref);
                    // Same requirement as CreateComponent: a character blueprint only
                    // runs the objects listed in PVZCharacterEntityData.Components.
                    entityHost = RegisterIntoCharacterEntity(asset, pref);
                }
                string graphHost = RegisterIntoSoundGraph(asset, pref);

                if (p["properties"] is JObject props)
                {
                    foreach (JProperty prop in props.Properties())
                        SetMemberValue(copy, prop.Name, prop.Value);
                }

                // The runtime counters belong to the entity that owns the component
                // list, not to whichever object happens to be first in traversal
                // order (a weapon blueprint embeds a projectile entity there).
                object indexHost = FindPointerListOwner(asset, copy, "Components")
                                   ?? FindPointerListOwner(asset, copy, "Objects");
                foreach (object addedObj in asset.Objects.ToList())
                {
                    if (!preexisting.Contains(addedObj))
                        indexSides = AssignComponentIndices(asset, addedObj, source, p, indexHost);
                }

                SaveAsset(entry, asset);

                if (addedIndex >= 0 && ReferenceEquals(arrayHost, asset.RootObject))
                {
                    EbxAsset reloaded = App.AssetManager.GetEbx(entry);
                    IList reloadedList = reloaded == null ? null : GetListMember(reloaded.RootObject, usedArray) as IList;
                    object reloadedItem = reloadedList != null && addedIndex < reloadedList.Count
                        ? UnwrapPointer(reloadedList[addedIndex])
                        : null;
                    if (reloadedItem == null || reloadedItem.GetType().Name != copy.GetType().Name)
                    {
                        if (reloadedList != null && addedIndex < reloadedList.Count && IsNullPointerSlot(reloadedList[addedIndex]))
                        {
                            reloadedList.RemoveAt(addedIndex);
                            SaveAsset(entry, reloaded);
                        }
                        return new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = "Duplicate was created but the serializer dropped it (array slot reads back as null); rolled back.",
                            ["error_code"] = "OBJECT_NOT_PERSISTED"
                        };
                    }
                }

                // A component belongs to the entity, not to the blueprint root.
                // Leaving the pointer in the root Objects array as well makes the
                // loader build a component-table entry with no instance behind it
                // (see CreateComponent for the full explanation).
                if (entityHost != null)
                {
                    IList rootObjects = GetListMember(asset.RootObject, "Objects") as IList;
                    int rootPos = FindPointerIndex(rootObjects, copy);
                    if (rootPos >= 0)
                    {
                        rootObjects.RemoveAt(rootPos);
                        SaveAsset(entry, asset);
                    }
                }

                int index = IndexOfObject(asset, copy);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source_index"] = srcIndex,
                    ["added_to_array"] = usedArray,
                    ["registered_in"] = spawnerHost,
                    ["registered_in_entity"] = entityHost,
                    ["registered_in_graph"] = graphHost,
                    ["index_sides"] = indexSides,
                    ["component"] = SerializeComponent(copy, index, asset),
                    ["pointer"] = SerializePointerRef(pref)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "DUPLICATE_FAILED");
            }
        }

        public static object SetComponentFlags(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            object comp = ResolveComponent(asset, p, out int index);
            if (comp == null)
                return McpHandlers.Error("Component not found", "NOT_FOUND");

            try
            {
                ApplyObjectFlags(comp, p, preserveGuidBits: true);
                if (p["realm"] != null)
                    SetEnumProperty(comp, "Realm", p.Value<string>("realm"));

                SaveAsset(entry, asset);
                Dictionary<string, object> result = SerializeComponent(comp, index, asset);
                result["success"] = true;
                return result;
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FLAGS_FAILED");
            }
        }

        public static object GetComponentProperty(JObject p)
        {
            if (!TryLoadBlueprint(p, out _, out EbxAsset asset, out object err))
                return err;

            object comp = ResolveComponent(asset, p, out int index);
            bool usedRootFallback = false;
            if (comp == null)
            {
                // The root object is not a component. Reading a plain struct path on a
                // non-blueprint asset used to die with "Component not found"; fall back
                // to the asset root instead and say so in the reply.
                object rootFallback = asset?.RootObjects == null ? null : asset.RootObjects.FirstOrDefault();
                if (rootFallback == null)
                    return McpHandlers.Error("Component not found", "NOT_FOUND");
                comp = rootFallback;
                index = -1;
                usedRootFallback = true;
            }

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");

            try
            {
                // Reuse path resolver from edit handlers via local copy
                object value = ResolvePath(comp, path, out string typeName);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["index"] = index,
                    ["class_guid"] = GetClassGuid(comp).ToString(),
                    ["type"] = comp.GetType().Name,
                    ["path"] = path,
                    ["value_type"] = typeName,
                    ["value"] = SerializeAny(value),
                    ["used_root_fallback"] = usedRootFallback
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "GET_FAILED");
            }
        }

        public static object SetComponentProperty(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            object comp = ResolveComponent(asset, p, out int index);
            if (comp == null)
                return McpHandlers.Error("Component not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");
            if (!p.ContainsKey("value"))
                return McpHandlers.Error("value is required", "INVALID_PARAMS");

            try
            {
                SetPath(comp, path, p["value"], asset);
                // Same rule as set_ebx_field: a Components write must leave the runtime
                // counters consistent, not just the list itself.
                string indexSides = PathTargetsComponentList(path)
                    ? NormalizeComponentSlots(asset, comp)
                    : null;
                SaveAsset(entry, asset);

                // Same as set_ebx_field: the value is saved, so a failing readback
                // must not turn into a reported failure.
                string typeName = null;
                object value = null;
                string readbackError = null;
                try
                {
                    value = ResolvePath(comp, path, out typeName);
                }
                catch (Exception readback)
                {
                    readbackError = readback.Message;
                }

                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["index"] = index,
                    ["class_guid"] = GetClassGuid(comp).ToString(),
                    ["type"] = comp.GetType().Name,
                    ["path"] = path,
                    ["applied"] = true,
                    ["value_type"] = typeName,
                    ["value"] = readbackError == null ? SerializeAny(value) : null
                };
                if (readbackError != null)
                    result["readback_error"] = readbackError;
                if (indexSides != null)
                    result["index_sides"] = indexSides;
                return result;
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FAILED");
            }
        }

        public static object RemoveComponent(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            object comp = ResolveComponent(asset, p, out int index);
            if (comp == null)
                return McpHandlers.Error("Component not found", "NOT_FOUND");

            // Do not remove root object
            if (ReferenceEquals(comp, asset.RootObject))
                return McpHandlers.Error("Cannot remove root object", "FORBIDDEN");

            Guid classGuid = GetClassGuid(comp);
            if (p["index"] == null && !IsClassGuidUnique(asset, classGuid))
                return McpHandlers.Error(
                    "class_guid matches multiple components; pass index to select one",
                    "AMBIGUOUS_COMPONENT");

            bool removeObject = p.Value<bool?>("remove_object") ?? true;
            var plan = new Dictionary<string, object>
            {
                ["name"] = entry.Name,
                ["index"] = index,
                ["class_guid"] = classGuid.ToString(),
                ["remove_object"] = removeObject
            };

            return McpCommit.Require("remove_component", p, plan, () =>
            {
                try
                {
                    dynamic root = asset.RootObject;
                    bool guidUnique = IsClassGuidUnique(asset, classGuid);

                    // Remember the entity that owns the component list: its runtime
                    // counters must be repaired after the removal.
                    object countHost = FindPointerListOwner(asset, comp, "Components")
                                       ?? FindPointerListOwner(asset, comp, "Objects");

                    // Remove only the connections/pointers that belong to this instance.
                    // A class-guid sweep would also hit another object that happens to
                    // share the same guid.
                    RemoveConnectionsTouchingObject(root, comp, guidUnique);

                    // Remove from Components / Objects arrays
                    RemovePointerFromArray(root, "Components", comp, guidUnique);
                    RemovePointerFromArray(root, "Objects", comp, guidUnique);
                    List<string> clearedPaths = RemoveReferencesToObject(asset, comp, guidUnique);

                    if (removeObject)
                        asset.RemoveObject(comp);

                    // Removing a component leaves its slot number unused. Slot ids are
                    // dense per side and the loader walks the whole table, so the gap
                    // becomes a null entry it dereferences while initialising the entity.
                    string renumber = NormalizeComponentSlots(asset, countHost);

                    SaveAsset(entry, asset);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["removed_index"] = index,
                        ["class_guid"] = classGuid.ToString(),
                        ["removed_from_objects"] = removeObject,
                        ["cleared_paths"] = clearedPaths,
                        ["cleared_count"] = clearedPaths.Count,
                        ["index_sides"] = renumber
                    };
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
                }
            });
        }

        /// <summary>
        /// Frosty property-grid "Remove item": delete an element from any EBX array
        /// (Components, Objects, MemberDatas, PropertyConnections, Interface Fields, …).
        /// </summary>
        public static object RemoveEbxItem(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return McpHandlers.Error(
                    "path is required (e.g. 'Components[1]', 'Objects[0]', '$PropertyConnections[3]', 'Fields[0]')",
                    "INVALID_PARAMS");

            bool removeObject = p.Value<bool?>("remove_object") ?? true;
            bool cleanupConnections = p.Value<bool?>("cleanup_connections") ?? true;
            bool all = p.Value<bool?>("all") ?? false;

            try
            {
                object host = asset.RootObject;
                string walkPath = path.StartsWith("$") ? path.Substring(1) : path;

                if (!path.StartsWith("$"))
                {
                    string classGuidStr = p.Value<string>("class_guid") ?? p.Value<string>("component_guid");
                    if (!string.IsNullOrEmpty(classGuidStr) && Guid.TryParse(classGuidStr, out Guid hostGuid))
                    {
                        object found = FindObjectByClassGuid(asset, hostGuid);
                        if (found != null)
                            host = found;
                    }
                    else
                    {
                        string typeName = p.Value<string>("component_type") ?? p.Value<string>("host_type");
                        int typeIndex = p.Value<int?>("type_index") ?? 0;
                        if (!string.IsNullOrEmpty(typeName))
                        {
                            object resolved = ResolveComponent(asset, new JObject
                            {
                                ["type"] = typeName,
                                ["type_index"] = typeIndex
                            }, out _);
                            if (resolved != null)
                                host = resolved;
                        }
                    }
                }

                if (!TryResolveListFromPath(host, walkPath, p.Value<int?>("index") ?? -1, out IList list, out string arrayPath, out int parsedIndex, out object hostObj))
                    return McpHandlers.Error("Could not resolve list at path: " + path, "NOT_FOUND");

                List<int> toRemove = new List<int>();
                if (all)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                        toRemove.Add(i);
                }
                else if (p["indices"] is JArray idxArr)
                {
                    foreach (JToken t in idxArr)
                        toRemove.Add(t.Value<int>());
                    toRemove = toRemove.Distinct().OrderByDescending(i => i).ToList();
                }
                else
                {
                    if (parsedIndex < 0)
                        return McpHandlers.Error("Provide path with [index], or index=, or indices=[], or all=true", "INVALID_PARAMS");
                    toRemove.Add(parsedIndex);
                }

                var previewItems = new List<object>();
                foreach (int idx in toRemove)
                {
                    if (idx < 0 || idx >= list.Count)
                        return McpHandlers.Error("Index out of range: " + idx + " (count=" + list.Count + ")", "INVALID_PARAMS");

                    object previewItem = list[idx];
                    object previewUnwrapped = UnwrapPointer(previewItem);
                    if (previewUnwrapped != null && ReferenceEquals(previewUnwrapped, asset.RootObject))
                        return McpHandlers.Error("Cannot remove root object", "FORBIDDEN");
                    previewItems.Add(new Dictionary<string, object>
                    {
                        ["index"] = idx,
                        ["type"] = previewUnwrapped?.GetType().Name ?? previewItem?.GetType().Name ?? "null",
                        ["class_guid"] = TryGetClassGuid(previewUnwrapped) is Guid pg && pg != Guid.Empty ? pg.ToString() : null
                    });
                }

                var plan = new Dictionary<string, object>
                {
                    ["name"] = entry.Name,
                    ["path"] = arrayPath,
                    ["remove_object"] = removeObject,
                    ["cleanup_connections"] = cleanupConnections,
                    ["count"] = toRemove.Count,
                    ["items"] = previewItems
                };

                return McpCommit.Require("remove_ebx_item", p, plan, () =>
                {
                List<object> removed = new List<object>();
                var clearedPaths = new List<string>();
                var keptObjects = new List<object>();
                foreach (int idx in toRemove)
                {
                    object item = list[idx];
                    object unwrapped = UnwrapPointer(item);
                    Guid itemGuid = TryGetClassGuid(unwrapped);
                    string itemType = unwrapped?.GetType().Name ?? item?.GetType().Name ?? "null";

                    list.RemoveAt(idx);

                    bool didRemoveObject = false;
                    int connectionsCleared = 0;
                    if (itemGuid != Guid.Empty && unwrapped != null && !unwrapped.GetType().IsValueType)
                    {
                        if (cleanupConnections)
                        {
                            int beforeP = CountList(asset.RootObject, "PropertyConnections");
                            int beforeE = CountList(asset.RootObject, "EventConnections");
                            int beforeL = CountList(asset.RootObject, "LinkConnections");
                            RemoveConnectionsTouchingObject(asset.RootObject, unwrapped, IsClassGuidUnique(asset, itemGuid));
                            connectionsCleared =
                                (beforeP - CountList(asset.RootObject, "PropertyConnections")) +
                                (beforeE - CountList(asset.RootObject, "EventConnections")) +
                                (beforeL - CountList(asset.RootObject, "LinkConnections"));
                        }
                        if (cleanupConnections)
                            clearedPaths.AddRange(RemoveReferencesToObject(asset, unwrapped, IsClassGuidUnique(asset, itemGuid)));

                        if (removeObject && asset.Objects != null && asset.Objects.Cast<object>().Any(o => ReferenceEquals(o, unwrapped)))
                        {
                            asset.RemoveObject(unwrapped);
                            didRemoveObject = true;
                        }
                        else if (!removeObject)
                        {
                            // remove_object=false promises the object survives. It needs
                            // a remaining reference or the serializer drops it; keep the
                            // candidate list and re-home them below.
                            keptObjects.Add(unwrapped);
                        }
                    }

                    removed.Add(new Dictionary<string, object>
                    {
                        ["index"] = idx,
                        ["type"] = itemType,
                        ["class_guid"] = itemGuid == Guid.Empty ? null : itemGuid.ToString(),
                        ["removed_object"] = didRemoveObject,
                        ["connections_cleared"] = connectionsCleared
                    });
                }

                // Re-home objects that remove_object=false asked to keep: append them
                // to the root Objects array (when the asset has one) so the serializer
                // still writes them instead of dropping the now-unreferenced object.
                var keptVia = new List<object>();
                if (keptObjects.Count > 0)
                {
                    IList rootObjects = GetListMember(asset.RootObject, "Objects") as IList;
                    foreach (object keepObj in keptObjects)
                    {
                        if (keepObj == null || ReferenceEquals(keepObj, asset.RootObject))
                            continue;
                        if (rootObjects != null && FindPointerIndex(rootObjects, keepObj) < 0)
                        {
                            rootObjects.Add(new PointerRef(keepObj));
                            keptVia.Add(new Dictionary<string, object>
                            {
                                ["class_guid"] = TryGetClassGuid(keepObj).ToString(),
                                ["kept_via"] = "Objects"
                            });
                        }
                    }
                }

                // Deleting a component this way also leaves a slot unused, with the same
                // null-table-entry consequence as remove_component.
                string renumber = NormalizeComponentSlots(asset, hostObj);

                SaveAsset(entry, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["path"] = arrayPath,
                    ["host_type"] = hostObj?.GetType().Name,
                    ["remaining"] = list.Count,
                    ["removed_count"] = removed.Count,
                    ["removed"] = removed,
                    ["kept"] = keptVia,
                    ["cleared_paths"] = clearedPaths,
                    ["cleared_count"] = clearedPaths.Count,
                    ["index_sides"] = renumber
                };
                });
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }
        }

        // =====================================================================
        // InterfaceDescriptorData: Fields / InputEvents / OutputEvents
        // (BF1DevPlugin.Base.Blueprint CreateFields / CreateInputEvents / ...)
        // =====================================================================

        public static object ListInterface(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: false);
                if (iface == null)
                {
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["has_interface"] = false,
                        ["fields"] = new List<object>(),
                        ["input_events"] = new List<object>(),
                        ["output_events"] = new List<object>()
                    };
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["has_interface"] = true,
                    ["class_guid"] = GetClassGuid((object)iface).ToString(),
                    ["fields"] = SerializeInterfaceFields(iface),
                    ["input_events"] = SerializeDynamicEvents(iface.InputEvents),
                    ["output_events"] = SerializeDynamicEvents(iface.OutputEvents)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "LIST_FAILED");
            }
        }

        public static object EnsureInterface(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic existing = TryGetInterfaceInternal(asset, createIfMissing: false);
                if (existing != null)
                {
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["created"] = false,
                        ["class_guid"] = GetClassGuid((object)existing).ToString(),
                        ["object_type"] = ((object)existing).GetType().Name,
                        ["fields_count"] = (int)existing.Fields.Count,
                        ["input_events_count"] = (int)existing.InputEvents.Count,
                        ["output_events_count"] = (int)existing.OutputEvents.Count
                    };
                }

                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: true);
                SaveAsset(entry, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["created"] = true,
                    ["class_guid"] = GetClassGuid((object)iface).ToString(),
                    ["object_type"] = ((object)iface).GetType().Name,
                    ["fields_count"] = (int)iface.Fields.Count,
                    ["input_events_count"] = (int)iface.InputEvents.Count,
                    ["output_events_count"] = (int)iface.OutputEvents.Count
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ENSURE_FAILED");
            }
        }

        public static object AddInterfaceField(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string fieldName = (p.Value<string>("field_name") ?? p.Value<string>("name_value") ?? "").Trim();
            if (string.IsNullOrEmpty(fieldName))
                return McpHandlers.Error("field_name is required", "INVALID_PARAMS");

            try
            {
                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: true);
                dynamic dataField = TypeLibrary.CreateObject("DataField");
                dataField.Name = (CString)fieldName;

                string value = p.Value<string>("value") ?? "";
                string valueType = p.Value<string>("value_type") ?? "";
                string valueRef = p.Value<string>("value_ref") ?? "";

                // A path that resolves to a real EBX is a reference, not the
                // DataField's CString Value. The wrapper does not forward
                // value_type/value_ref in every build, so the path form has to
                // stand on its own.
                if (string.IsNullOrWhiteSpace(valueRef) && !string.IsNullOrWhiteSpace(value))
                {
                    string candidate = value.Trim();
                    EbxAssetEntry target = App.AssetManager.GetEbxEntry(candidate)
                        ?? App.AssetManager.GetEbxEntry(candidate.ToLowerInvariant());
                    if (target != null)
                    {
                        valueRef = candidate;
                        if (string.IsNullOrWhiteSpace(valueType))
                            valueType = App.AssetManager.GetEbx(target).RootObject.GetType().Name;
                        value = "";
                    }
                }

                if (!string.IsNullOrWhiteSpace(value))
                    dataField.Value = (CString)value;

                // A field that carries a real asset needs ValueType plus ValueRef;
                // leaving those out produced a field whose ValueRef read back as
                // Null while the reply still echoed the path.
                if (!string.IsNullOrWhiteSpace(valueType))
                {
                    string trimmed = valueType.Trim();
                    JToken typeToken = trimmed.StartsWith("{")
                        ? (JToken)JToken.Parse(trimmed)
                        : new JValue("TypeRef '" + trimmed + "'");
                    SetMemberValue(dataField, "ValueType", typeToken, asset);
                }

                if (!string.IsNullOrWhiteSpace(valueRef))
                    SetMemberValue(dataField, "ValueRef", new JObject { ["asset"] = valueRef.Trim() }, asset);

                string access = p.Value<string>("access_type") ?? "FieldAccessType_Target";
                SetEnumProperty((object)dataField, "AccessType", access);

                iface.Fields.Add(dataField);
                SaveAsset(entry, asset);

                int index = iface.Fields.Count - 1;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["index"] = index,
                    ["field"] = SerializeDataField(dataField, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        public static object UpdateInterfaceField(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: false);
                if (iface == null)
                    return McpHandlers.Error("Interface not found", "NOT_FOUND");

                int index = ResolveInterfaceFieldIndex(iface, p);
                if (index < 0)
                    return McpHandlers.Error("Field not found (provide index or field_name)", "NOT_FOUND");

                dynamic field = iface.Fields[index];
                if (p["new_name"] != null)
                    field.Name = (CString)(p.Value<string>("new_name") ?? "");
                if (p["value"] != null)
                    field.Value = (CString)(p.Value<string>("value") ?? "");
                if (p["access_type"] != null)
                    SetEnumProperty((object)field, "AccessType", p.Value<string>("access_type"));

                SaveAsset(entry, asset);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["index"] = index,
                    ["field"] = SerializeDataField(field, index)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "UPDATE_FAILED");
            }
        }

        public static object RemoveInterfaceField(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            try
            {
                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: false);
                if (iface == null)
                    return McpHandlers.Error("Interface not found", "NOT_FOUND");

                int index = ResolveInterfaceFieldIndex(iface, p);
                if (index < 0)
                    return McpHandlers.Error("Field not found", "NOT_FOUND");

                var plan = new Dictionary<string, object>
                {
                    ["name"] = entry.Name,
                    ["index"] = index,
                    ["remaining_after"] = (int)iface.Fields.Count - 1
                };

                return McpCommit.Require("remove_interface_field", p, plan, () =>
                {
                    object removed = iface.Fields[index];
                    iface.Fields.RemoveAt(index);
                    try
                    {
                        SaveAsset(entry, asset);
                    }
                    catch (Exception ex)
                    {
                        iface.Fields.Insert(index, removed);
                        return McpHandlers.Error(ex.Message + " (change rolled back)", "REMOVE_FAILED");
                    }
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["removed_index"] = index,
                        ["remaining"] = (int)iface.Fields.Count
                    };
                });
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }
        }

        public static object AddInterfaceEvent(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string kind = (p.Value<string>("kind") ?? "input").Trim().ToLowerInvariant();
            string eventName = (p.Value<string>("event_name") ?? p.Value<string>("name_value") ?? "").Trim();
            if (string.IsNullOrEmpty(eventName))
                return McpHandlers.Error("event_name is required", "INVALID_PARAMS");
            if (kind != "input" && kind != "output")
                return McpHandlers.Error("kind must be input|output", "INVALID_PARAMS");

            try
            {
                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: true);
                dynamic dynamicEvent = TypeLibrary.CreateObject("DynamicEvent");
                dynamicEvent.Name = (CString)eventName;

                if (kind == "input")
                    iface.InputEvents.Add(dynamicEvent);
                else
                    iface.OutputEvents.Add(dynamicEvent);

                SaveAsset(entry, asset);
                int index = kind == "input" ? iface.InputEvents.Count - 1 : iface.OutputEvents.Count - 1;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["kind"] = kind,
                    ["index"] = index,
                    ["event"] = new Dictionary<string, object> { ["index"] = index, ["name"] = eventName }
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "ADD_FAILED");
            }
        }

        public static object RemoveInterfaceEvent(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string kind = (p.Value<string>("kind") ?? "input").Trim().ToLowerInvariant();
            if (kind != "input" && kind != "output")
                return McpHandlers.Error("kind must be input|output", "INVALID_PARAMS");

            try
            {
                dynamic iface = TryGetInterfaceInternal(asset, createIfMissing: false);
                if (iface == null)
                    return McpHandlers.Error("Interface not found", "NOT_FOUND");

                IList list = kind == "input" ? (IList)iface.InputEvents : (IList)iface.OutputEvents;
                int index = p.Value<int?>("index") ?? -1;
                string eventName = (p.Value<string>("event_name") ?? "").Trim();
                if (index < 0 && !string.IsNullOrEmpty(eventName))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        string n = "";
                        try { n = (string)(CString)((dynamic)list[i]).Name; } catch { }
                        if (string.Equals(n, eventName, StringComparison.OrdinalIgnoreCase))
                        {
                            index = i;
                            break;
                        }
                    }
                }
                if (index < 0 || index >= list.Count)
                    return McpHandlers.Error("Event not found", "NOT_FOUND");

                var plan = new Dictionary<string, object>
                {
                    ["name"] = entry.Name,
                    ["kind"] = kind,
                    ["index"] = index,
                    ["remaining_after"] = list.Count - 1
                };

                return McpCommit.Require("remove_interface_event", p, plan, () =>
                {
                    object removed = list[index];
                    list.RemoveAt(index);
                    try
                    {
                        SaveAsset(entry, asset);
                    }
                    catch (Exception ex)
                    {
                        list.Insert(index, removed);
                        // Surface the inner exception: the outer one is always the
                        // same bare null reference and names no object, which made
                        // this failure impossible to diagnose from the tool result.
                        string detail = ex.InnerException == null
                            ? ex.ToString()
                            : ex.Message + " <- " + ex.InnerException.Message;
                        return McpHandlers.Error(detail + " (change rolled back)", "REMOVE_FAILED");
                    }
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["kind"] = kind,
                        ["removed_index"] = index,
                        ["remaining"] = list.Count
                    };
                });
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }
        }

        public static object RemoveConnectionsByGuid(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string cg = (p.Value<string>("class_guid") ?? "").Trim();
            if (string.IsNullOrEmpty(cg) || !Guid.TryParse(cg, out Guid classGuid))
                return McpHandlers.Error("class_guid is required", "INVALID_PARAMS");

            string kind = (p.Value<string>("kind") ?? "all").Trim().ToLowerInvariant();
            if (kind != "all" && kind != "property" && kind != "event" && kind != "link")
                return McpHandlers.Error("kind must be all|property|event|link", "INVALID_PARAMS");

            int estimated;
            try
            {
                dynamic root = asset.RootObject;
                estimated = CountConnectionsTouchingKind(root, classGuid, kind);
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
            }

            if (estimated == 0)
                return McpHandlers.Error("No connections touch this class_guid", "NOT_FOUND");

            var plan = new Dictionary<string, object>
            {
                ["name"] = entry.Name,
                ["class_guid"] = classGuid.ToString(),
                ["kind"] = kind,
                ["connections"] = estimated
            };

            return McpCommit.Require("remove_connections_by_guid", p, plan, () =>
            {
                try
                {
                    dynamic root = asset.RootObject;
                    int removed = 0;
                    if (kind == "all" || kind == "property")
                    {
                        int before = root.PropertyConnections.Count;
                        RemoveConnectionsTouchingKind(root, classGuid, "property");
                        removed += before - root.PropertyConnections.Count;
                    }
                    if (kind == "all" || kind == "event")
                    {
                        int before = root.EventConnections.Count;
                        RemoveConnectionsTouchingKind(root, classGuid, "event");
                        removed += before - root.EventConnections.Count;
                    }
                    if (kind == "all" || kind == "link")
                    {
                        int before = root.LinkConnections.Count;
                        RemoveConnectionsTouchingKind(root, classGuid, "link");
                        removed += before - root.LinkConnections.Count;
                    }

                    SaveAsset(entry, asset);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["class_guid"] = classGuid.ToString(),
                        ["kind"] = kind,
                        ["removed"] = removed
                    };
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "REMOVE_FAILED");
                }
            });
        }

        // =====================================================================
        // Research how an SDK component type is used across EBX assets
        // =====================================================================

        public static object ResearchComponentUsage(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string typeName = (p.Value<string>("type") ?? p.Value<string>("component_type") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("type is required (SDK class, e.g. DelayEntityData)", "INVALID_PARAMS");

            Type sdkType = TypeLibrary.GetType(typeName);
            if (sdkType == null)
                return McpHandlers.Error("Type not found in SDK: " + typeName, "TYPE_NOT_FOUND");

            // Normalize to exact SDK name
            typeName = sdkType.Name;

            string nameQuery = (p.Value<string>("name_query") ?? p.Value<string>("query") ?? "").Trim();
            string assetTypeFilter = (p.Value<string>("asset_type") ?? "").Trim();
            bool includeSubclasses = p.Value<bool?>("include_subclasses") ?? true;
            bool modifiedOnly = p.Value<bool?>("modified_only") ?? false;
            // 0 scans the whole project. The default 150 only covers the first
            // assets in enumeration order, which made a rare component look unused
            // (UISettingsOptionCollectionData in a Stitch UI asset was reported as
            // 0 instances while the asset holds two).
            int maxScan = p.Value<int?>("max_scan") ?? 150;
            if (maxScan < 0) maxScan = 0;
            int maxExamples = p.Value<int?>("max_examples") ?? 20;
            if (maxExamples < 1) maxExamples = 1;
            if (maxExamples > 100) maxExamples = 100;
            bool includeProperties = p.Value<bool?>("include_properties") ?? true;

            // Explicit asset list (optional)
            var explicitNames = new List<string>();
            if (p["assets"] is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    string n = t?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(n))
                        explicitNames.Add(n);
                }
            }
            string singleAsset = (p.Value<string>("name") ?? "").Trim();
            if (!string.IsNullOrEmpty(singleAsset))
                explicitNames.Add(singleAsset);

            // Counters
            var propSourceFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var propTargetFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var eventSourceFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var eventTargetFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var linkSourceFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var linkTargetFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var peerAsSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // when our comp is target
            var peerAsTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // when our comp is source
            var examples = new List<object>();
            var hitAssets = new List<object>();

            int scanned = 0;
            int assetsWithType = 0;
            int instanceCount = 0;
            int loadErrors = 0;

            IEnumerable<EbxAssetEntry> candidates;
            if (explicitNames.Count > 0)
            {
                var list = new List<EbxAssetEntry>();
                foreach (string n in explicitNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    EbxAssetEntry e = McpHandlers.ResolveEbxEntry(n, null);
                    if (e != null)
                        list.Add(e);
                }
                candidates = list;
            }
            else
            {
                candidates = App.AssetManager.EnumerateEbx(
                    type: assetTypeFilter,
                    modifiedOnly: modifiedOnly);
                if (!string.IsNullOrEmpty(nameQuery))
                {
                    candidates = candidates.Where(e =>
                        e.Name != null && e.Name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }

            foreach (EbxAssetEntry entry in candidates)
            {
                if (maxScan > 0 && scanned >= maxScan)
                    break;
                scanned++;

                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch
                {
                    loadErrors++;
                    continue;
                }
                if (asset == null)
                {
                    loadErrors++;
                    continue;
                }

                // Find matching objects
                var matches = new List<Tuple<object, Guid>>();
                foreach (object obj in asset.Objects)
                {
                    string ot = obj.GetType().Name;
                    bool hit = ot.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                               (includeSubclasses && TypeLibrary.IsSubClassOf(obj, typeName));
                    if (hit)
                        matches.Add(Tuple.Create(obj, GetClassGuid(obj)));
                }

                if (matches.Count == 0)
                    continue;

                assetsWithType++;
                instanceCount += matches.Count;

                var matchGuids = new HashSet<Guid>(matches.Select(m => m.Item2));
                var assetConnections = new List<object>();

                CollectUsageConnections(
                    asset, matchGuids,
                    propSourceFields, propTargetFields,
                    eventSourceFields, eventTargetFields,
                    linkSourceFields, linkTargetFields,
                    peerAsSource, peerAsTarget,
                    assetConnections);

                hitAssets.Add(new Dictionary<string, object>
                {
                    ["name"] = entry.Name,
                    ["type"] = entry.Type,
                    ["guid"] = entry.Guid.ToString(),
                    ["instance_count"] = matches.Count,
                    // This counts connections that touch the matched components in
                    // this asset, not the asset's connection total. Reading it as
                    // the asset total made "0" look like "no connections at all".
                    ["component_connection_count"] = assetConnections.Count,
                    ["connection_count"] = assetConnections.Count,
                    ["connection_count_scope"] = "connections touching the matched components, not the whole asset"
                });

                if (examples.Count < maxExamples)
                {
                    foreach (var m in matches)
                    {
                        if (examples.Count >= maxExamples)
                            break;

                        var related = assetConnections.Where(c =>
                        {
                            var d = c as Dictionary<string, object>;
                            if (d == null) return false;
                            string role = d.ContainsKey("our_role") ? d["our_role"]?.ToString() : null;
                            string cg = d.ContainsKey("our_class_guid") ? d["our_class_guid"]?.ToString() : null;
                            return cg != null && string.Equals(cg, m.Item2.ToString(), StringComparison.OrdinalIgnoreCase);
                        }).ToList();

                        var ex = new Dictionary<string, object>
                        {
                            ["asset"] = entry.Name,
                            ["asset_type"] = entry.Type,
                            ["class_guid"] = m.Item2.ToString(),
                            ["object_type"] = m.Item1.GetType().Name,
                            ["name"] = GetObjectName(m.Item1),
                            ["realm"] = TryGetEnumString(m.Item1, "Realm"),
                            ["connections"] = related
                        };
                        if (includeProperties)
                            ex["properties"] = DumpPublicProperties(m.Item1);
                        examples.Add(ex);
                    }
                }
            }

            // SDK schema (public writable-ish properties)
            var sdkProps = new List<object>();
            foreach (PropertyInfo pi in sdkType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                if (pi.Name == "___Guid" || pi.Name == "TransientId") continue;
                sdkProps.Add(new Dictionary<string, object>
                {
                    ["name"] = pi.Name,
                    ["type"] = pi.PropertyType.Name,
                    ["can_write"] = pi.CanWrite
                });
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["type"] = typeName,
                ["include_subclasses"] = includeSubclasses,
                ["name_query"] = string.IsNullOrEmpty(nameQuery) ? null : nameQuery,
                ["asset_type"] = string.IsNullOrEmpty(assetTypeFilter) ? null : assetTypeFilter,
                ["scanned_assets"] = scanned,
                ["load_errors"] = loadErrors,
                ["assets_with_type"] = assetsWithType,
                ["instance_count"] = instanceCount,
                ["max_scan"] = maxScan,
                ["max_scan_meaning"] = maxScan == 0 ? "0 = scan the whole project" : null,
                // A scan stopped at max_scan must not read as "this type is unused":
                // the caller has no other way to tell the two apart.
                ["scan_truncated"] = maxScan > 0 && scanned >= maxScan,
                ["note"] = maxScan > 0 && scanned >= maxScan
                    ? "SCAN INCOMPLETE: stopped at max_scan before covering the project, so a low or zero instance_count does NOT mean the type is unused. Pass max_scan=0 to scan the whole project, or narrow with name_query."
                    : null,
                ["sdk"] = new Dictionary<string, object>
                {
                    ["type"] = typeName,
                    ["property_count"] = sdkProps.Count,
                    ["properties"] = sdkProps
                },
                ["field_usage"] = new Dictionary<string, object>
                {
                    ["property_as_source"] = TopCounts(propSourceFields, 50),
                    ["property_as_target"] = TopCounts(propTargetFields, 50),
                    ["event_as_source"] = TopCounts(eventSourceFields, 50),
                    ["event_as_target"] = TopCounts(eventTargetFields, 50),
                    ["link_as_source"] = TopCounts(linkSourceFields, 50),
                    ["link_as_target"] = TopCounts(linkTargetFields, 50)
                },
                ["peer_types"] = new Dictionary<string, object>
                {
                    ["when_we_are_source"] = TopCounts(peerAsTarget, 40),
                    ["when_we_are_target"] = TopCounts(peerAsSource, 40)
                },
                ["assets"] = hitAssets.Take(100).ToList(),
                ["examples"] = examples,
                ["hint"] = "Use name_query / asset_type / assets[] to narrow scan; raise max_scan for broader research."
            };
        }

        private static void CollectUsageConnections(
            EbxAsset asset,
            HashSet<Guid> matchGuids,
            Dictionary<string, int> propSourceFields,
            Dictionary<string, int> propTargetFields,
            Dictionary<string, int> eventSourceFields,
            Dictionary<string, int> eventTargetFields,
            Dictionary<string, int> linkSourceFields,
            Dictionary<string, int> linkTargetFields,
            Dictionary<string, int> peerAsSource,
            Dictionary<string, int> peerAsTarget,
            List<object> assetConnections)
        {
            dynamic root = asset.RootObject;

            try
            {
                for (int i = 0; i < root.PropertyConnections.Count; i++)
                {
                    dynamic c = root.PropertyConnections[i];
                    Guid sg = PointerClassGuid(c.Source);
                    Guid tg = PointerClassGuid(c.Target);
                    bool sourceHit = matchGuids.Contains(sg);
                    bool targetHit = matchGuids.Contains(tg);
                    if (!sourceHit && !targetHit)
                        continue;

                    string sf = "";
                    string tf = "";
                    try { sf = (string)(CString)c.SourceField; } catch { }
                    try { tf = (string)(CString)c.TargetField; } catch { }

                    if (sourceHit)
                    {
                        Bump(propSourceFields, sf);
                        Bump(peerAsTarget, PointerTypeName(c.Target, asset));
                    }
                    if (targetHit)
                    {
                        Bump(propTargetFields, tf);
                        Bump(peerAsSource, PointerTypeName(c.Source, asset));
                    }

                    var ser = SerializePropertyConnection(c, i);
                    AnnotateOurRole(ser, sourceHit, targetHit, sg, tg, matchGuids);
                    assetConnections.Add(ser);
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < root.EventConnections.Count; i++)
                {
                    dynamic c = root.EventConnections[i];
                    Guid sg = PointerClassGuid(c.Source);
                    Guid tg = PointerClassGuid(c.Target);
                    bool sourceHit = matchGuids.Contains(sg);
                    bool targetHit = matchGuids.Contains(tg);
                    if (!sourceHit && !targetHit)
                        continue;

                    string se = "";
                    string te = "";
                    try { se = (string)(CString)c.SourceEvent.Name; } catch { }
                    try { te = (string)(CString)c.TargetEvent.Name; } catch { }

                    if (sourceHit)
                    {
                        Bump(eventSourceFields, se);
                        Bump(peerAsTarget, PointerTypeName(c.Target, asset));
                    }
                    if (targetHit)
                    {
                        Bump(eventTargetFields, te);
                        Bump(peerAsSource, PointerTypeName(c.Source, asset));
                    }

                    var ser = SerializeEventConnection(c, i);
                    AnnotateOurRole(ser, sourceHit, targetHit, sg, tg, matchGuids);
                    assetConnections.Add(ser);
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < root.LinkConnections.Count; i++)
                {
                    dynamic c = root.LinkConnections[i];
                    Guid sg = PointerClassGuid(c.Source);
                    Guid tg = PointerClassGuid(c.Target);
                    bool sourceHit = matchGuids.Contains(sg);
                    bool targetHit = matchGuids.Contains(tg);
                    if (!sourceHit && !targetHit)
                        continue;

                    string sf = "";
                    string tf = "";
                    try { sf = (string)(CString)c.SourceField; } catch { }
                    try { tf = (string)(CString)c.TargetField; } catch { }

                    if (sourceHit)
                    {
                        Bump(linkSourceFields, sf);
                        Bump(peerAsTarget, PointerTypeName(c.Target, asset));
                    }
                    if (targetHit)
                    {
                        Bump(linkTargetFields, tf);
                        Bump(peerAsSource, PointerTypeName(c.Source, asset));
                    }

                    var ser = SerializeLinkConnection(c, i);
                    AnnotateOurRole(ser, sourceHit, targetHit, sg, tg, matchGuids);
                    assetConnections.Add(ser);
                }
            }
            catch { }
        }

        private static void AnnotateOurRole(
            Dictionary<string, object> ser, bool sourceHit, bool targetHit, Guid sg, Guid tg, HashSet<Guid> matchGuids)
        {
            if (sourceHit && targetHit)
            {
                ser["our_role"] = "both";
                ser["our_class_guid"] = sg.ToString();
            }
            else if (sourceHit)
            {
                ser["our_role"] = "source";
                ser["our_class_guid"] = sg.ToString();
            }
            else if (targetHit)
            {
                ser["our_role"] = "target";
                ser["our_class_guid"] = tg.ToString();
            }
        }

        private static Guid PointerClassGuid(PointerRef pr)
        {
            if (pr.Type == PointerRefType.Internal && pr.Internal != null)
                return GetClassGuid(pr.Internal);
            if (pr.Type == PointerRefType.External)
                return pr.External.ClassGuid;
            return Guid.Empty;
        }

        private static string PointerTypeName(PointerRef pr, EbxAsset asset)
        {
            if (pr.Type == PointerRefType.Internal && pr.Internal != null)
                return pr.Internal.GetType().Name;
            if (pr.Type == PointerRefType.External)
            {
                EbxAssetEntry e = App.AssetManager?.GetEbxEntry(pr.External.FileGuid);
                return e != null ? "ext:" + e.Type + ":" + e.Name : "ext:" + pr.External.FileGuid.ToString("N").Substring(0, 8);
            }
            return "null";
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            if (string.IsNullOrEmpty(key))
                key = "(empty)";
            if (!map.ContainsKey(key))
                map[key] = 0;
            map[key]++;
        }

        private static List<object> TopCounts(Dictionary<string, int> map, int take)
        {
            return map.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(kv => (object)new Dictionary<string, object> { ["name"] = kv.Key, ["count"] = kv.Value })
                .ToList();
        }

        // =====================================================================
        // Create New Class on a PointerRef field, mirroring FrostyPointerRefEditor
        // =====================================================================

        public static object ListEbxClassTypes(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            Type baseType = null;
            string path = (p.Value<string>("path") ?? "").Trim();
            string baseTypeName = (p.Value<string>("base_type") ?? "").Trim();

            if (!string.IsNullOrEmpty(path))
            {
                object host = ResolveHostForClassCreate(asset, p, out _);
                if (host == null)
                    return McpHandlers.Error("Host component not found (provide class_guid or index)", "NOT_FOUND");
                try
                {
                    ResolvePointerAssignTarget(host, path, out _, out _, out _, out baseType, out _);
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "PATH_FAILED");
                }
            }
            else if (!string.IsNullOrEmpty(baseTypeName))
            {
                baseType = TypeLibrary.GetType(baseTypeName);
                if (baseType == null)
                    return McpHandlers.Error("base_type not found: " + baseTypeName, "TYPE_NOT_FOUND");
            }
            else
            {
                return McpHandlers.Error("Provide path (on a component) or base_type", "INVALID_PARAMS");
            }

            if (baseType == null)
                return McpHandlers.Error("Could not resolve BaseType for this field (missing EbxFieldMetaAttribute?)", "NO_BASE_TYPE");

            string query = (p.Value<string>("query") ?? "").Trim();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);

            Type[] types = TypeLibrary.GetTypes(baseType) ?? Array.Empty<Type>();
            List<object> all = new List<object>();
            foreach (Type t in types.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(query) &&
                    t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Guid typeGuid = Guid.Empty;
                try
                {
                    FrostySdk.Attributes.GuidAttribute ga = t.GetCustomAttribute<FrostySdk.Attributes.GuidAttribute>();
                    if (ga != null)
                        typeGuid = ga.Guid;
                    else
                    {
                        TypeInfoGuidAttribute tga = t.GetCustomAttribute<TypeInfoGuidAttribute>();
                        if (tga != null)
                            typeGuid = tga.Guid;
                    }
                }
                catch { }

                all.Add(new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["guid"] = typeGuid == Guid.Empty ? null : typeGuid.ToString()
                });
            }

            List<object> page = all.Skip(offset).Take(limit).ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["base_type"] = baseType.Name,
                ["path"] = string.IsNullOrEmpty(path) ? null : path,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["types"] = page
            };
        }

        public static object CreateEbxClass(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string typeName = (p.Value<string>("type") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("type is required (SDK class name to create)", "INVALID_PARAMS");

            Type selectedType = TypeLibrary.GetType(typeName);
            if (selectedType == null)
                return McpHandlers.Error("Type not found in SDK: " + typeName, "TYPE_NOT_FOUND");

            string path = (p.Value<string>("path") ?? "").Trim();
            bool createOnly = p.Value<bool?>("create_only") ?? false;
            if (string.IsNullOrEmpty(path) && !createOnly)
                return McpHandlers.Error("path is required (PointerRef field on host component), or set create_only=true", "INVALID_PARAMS");

            object host = null;
            int hostIndex = -1;
            if (!createOnly || p["class_guid"] != null || p["index"] != null)
            {
                host = ResolveHostForClassCreate(asset, p, out hostIndex);
                if (host == null && !createOnly)
                    return McpHandlers.Error("Host component not found (provide class_guid or index)", "NOT_FOUND");
            }
            if (host == null)
                host = asset.RootObject;

            Type expectedBase = null;
            object existingValue = null;
            string assignMode = (p.Value<string>("mode") ?? "").Trim().ToLowerInvariant(); // set | append | auto

            if (!createOnly)
            {
                try
                {
                    ResolvePointerAssignTarget(host, path, out _, out _, out existingValue, out expectedBase, out string inferredMode);
                    if (string.IsNullOrEmpty(assignMode))
                        assignMode = inferredMode;
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "PATH_FAILED");
                }

                if (expectedBase != null && !expectedBase.IsAssignableFrom(selectedType) &&
                    !TypeLibrary.IsSubClassOf(selectedType, expectedBase.Name))
                {
                    return McpHandlers.Error(
                        "Type '" + typeName + "' is not a subclass of field BaseType '" + expectedBase.Name + "'",
                        "TYPE_MISMATCH");
                }
            }

            try
            {
                // GUID: reuse existing Internal when replacing (Frosty CreateButton behavior)
                AssetClassGuid guid = new AssetClassGuid();
                bool reuseGuid = p.Value<bool?>("reuse_guid") ?? true;
                if (reuseGuid && existingValue is PointerRef existingPr &&
                    existingPr.Type == PointerRefType.Internal && existingPr.Internal != null)
                {
                    guid = ((dynamic)existingPr.Internal).GetInstanceGuid();
                }

                if (!guid.IsExported)
                {
                    guid = new AssetClassGuid(
                        Utils.GenerateDeterministicGuid(asset.Objects, selectedType, entry.Guid), -1);
                }

                List<object> preexisting = asset.Objects.ToList();
                object newObject = TypeLibrary.CreateObject(typeName);
                if (newObject == null)
                    return McpHandlers.Error("TypeLibrary.CreateObject returned null for " + typeName, "CREATE_FAILED");

                ((dynamic)newObject).SetInstanceGuid(guid);

                if (TypeLibrary.IsSubClassOf(newObject, "DataBusPeer"))
                {
                    byte[] bytes = guid.ExportedGuid.ToByteArray();
                    uint flagValue = (uint)((bytes[2] << 16) | (bytes[1] << 8) | bytes[0]);
                    SetObjectFlagsRaw(newObject, flagValue);
                }

                if (HasObjectFlagParams(p) || p["flag_preset"] != null)
                    ApplyObjectFlags(newObject, p, preserveGuidBits: true);
                if (p["realm"] != null)
                    SetEnumProperty(newObject, "Realm", p.Value<string>("realm"));

                if (p["properties"] is JObject props)
                {
                    foreach (JProperty prop in props.Properties())
                        SetMemberValue(newObject, prop.Name, prop.Value, asset);
                }

                asset.AddObject(newObject);
                // Components listed inside the asset must be internal, otherwise the
                // saved-component loader resolves them to null. Objects that other
                // assets address by GUID (UIObjectTag tree nodes, and anything else
                // reached through an external PointerRef) must stay exported: an
                // internal GUID of the form 00000000-...-XXXXXXXX cannot be referenced
                // across files.
                bool keepExported = p.Value<bool?>("export_object") ?? false;
                if (!keepExported)
                    MakeCreatedObjectInternal(newObject);
                RepairInternalIdCollisions(asset, preexisting);
                // Numbered after the pointer is assigned; the pass walks the finished tree.
                string indexSides = null;
                if (!IsContainedInObjects(asset, newObject))
                {
                    return McpHandlers.Error(
                        "asset.AddObject did not register the new object (the serializer cannot write this type): " + typeName,
                        "OBJECT_NOT_PERSISTED");
                }
                if (!IsClassGuidUnique(asset, GetClassGuid(newObject)))
                {
                    asset.RemoveObject(newObject);
                    return McpHandlers.Error(
                        "Generated object GUID collides with an existing object: " + GetClassGuid(newObject),
                        "GUID_COLLISION");
                }
                PointerRef pref = new PointerRef(newObject);

                // Same guard as CreateComponent: a plugin-bearing SoundGraph node
                // cannot be registered by this writer (it only appends Graph.Nodes).
                if (IsSoundPatchAsset(asset) && HasValidSoundGraphPluginRefs(newObject))
                {
                    asset.RemoveObject(newObject);
                    return McpHandlers.Error(
                        "SoundGraph node '" + newObject.GetType().Name + "' carries SoundGraphPluginRef members; creating it without a matching voice/plugin registration would share another voice's plugins at runtime. Create it in the Frosty GUI, or extend the tool to copy the voice/plugin tables.",
                        "SOUNDGRAPH_VOICE_UNREGISTERED");
                }

                string assignedPath = null;
                int listIndex = -1;
                if (!createOnly)
                {
                    AssignPointerAtPath(host, path, pref, assignMode, out assignedPath, out listIndex);
                }

                string graphHost = null;
                string spawnerHost = null;
                string entityHost = null;
                if (!createOnly && ReferenceEquals(host, asset.RootObject))
                {
                    graphHost = RegisterIntoSoundGraph(asset, pref);
                    // create_component has always registered root Objects into these
                    // two tables; create_ebx_class must do the same or a Rime entity
                    // is written but never instantiated, and a character component
                    // sits outside the entity's runtime component list. Audio sound
                    // entities are the exception: they belong on the root.
                    if (!MustStayOnRootObjects(newObject))
                    {
                        spawnerHost = RegisterIntoWidgetSpawner(asset, pref);
                        entityHost = RegisterIntoCharacterEntity(asset, pref);
                    }
                }

                indexSides = AssignComponentIndices(asset, newObject, null, p, host);

                // A character component belongs to PVZCharacterEntityData.Components,
                // not to the blueprint root; keep the root array at zero.
                if (entityHost != null && ReferenceEquals(host, asset.RootObject))
                {
                    IList rootObjects = GetListMember(asset.RootObject, "Objects") as IList;
                    int rootPos = FindPointerIndex(rootObjects, newObject);
                    if (rootPos >= 0)
                        rootObjects.RemoveAt(rootPos);
                }

                // Verify the new object is actually reachable through the assigned
                // path. BehaviorTree nodes were reported as created while the asset
                // kept nothing; fail loudly and roll back instead.
                if (!createOnly && !string.IsNullOrEmpty(assignedPath))
                {
                    bool persisted;
                    try
                    {
                        object resolved = ResolvePath(host, assignedPath, out _);
                        persisted = ReferenceEquals(UnwrapPointer(resolved), newObject);
                    }
                    catch
                    {
                        persisted = false;
                    }

                    if (!persisted)
                    {
                        if (listIndex >= 0)
                        {
                            try
                            {
                                string listPath = assignedPath.Substring(0, assignedPath.LastIndexOf('['));
                                object listObj = ResolvePath(host, listPath, out _);
                                if (listObj is IList l && listIndex <= l.Count - 1 && IsNullPointerSlot(l[listIndex]))
                                    l.RemoveAt(listIndex);
                            }
                            catch { }
                        }
                        asset.RemoveObject(newObject);
                        SaveAsset(entry, asset);
                        return new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = "Object did not persist after assignment; rolled back.",
                            ["error_code"] = "OBJECT_NOT_PERSISTED"
                        };
                    }
                }

                SaveAsset(entry, asset);

                // create_only leaves the object unreferenced; checking the in-memory
                // list is not enough because the serializer drops it on re-read.
                if (createOnly)
                {
                    EbxAsset reloadedOnly = App.AssetManager.GetEbx(entry);
                    bool presentOnly = false;
                    if (reloadedOnly != null)
                    {
                        Guid onlyGuid = GetClassGuid(newObject);
                        foreach (object o in reloadedOnly.Objects)
                        {
                            if (GetClassGuid(o) == onlyGuid)
                            {
                                presentOnly = true;
                                break;
                            }
                        }
                    }
                    if (!presentOnly)
                    {
                        return new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = "create_only object was dropped because nothing references it; pass a path to assign it.",
                            ["error_code"] = "OBJECT_NOT_PERSISTED"
                        };
                    }
                }

                // Re-read the saved asset and confirm the object survived. BehaviorTree
                // nodes used to report success while nothing was written.
                if (!createOnly && listIndex >= 0 && ReferenceEquals(host, asset.RootObject))
                {
                    EbxAsset reloaded = App.AssetManager.GetEbx(entry);
                    string listPath = assignedPath.Substring(0, assignedPath.LastIndexOf('['));
                    IList reloadedList = null;
                    try
                    {
                        reloadedList = reloaded == null ? null : ResolvePath(reloaded.RootObject, listPath, out _) as IList;
                    }
                    catch { }

                    object reloadedItem = reloadedList != null && listIndex < reloadedList.Count
                        ? UnwrapPointer(reloadedList[listIndex])
                        : null;
                    if (reloadedItem == null || reloadedItem.GetType().Name != newObject.GetType().Name)
                    {
                        if (reloadedList != null && listIndex < reloadedList.Count && IsNullPointerSlot(reloadedList[listIndex]))
                        {
                            reloadedList.RemoveAt(listIndex);
                            SaveAsset(entry, reloaded);
                        }
                        return new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = "Object was created but the serializer dropped it (array slot reads back as null); rolled back.",
                            ["error_code"] = "OBJECT_NOT_PERSISTED"
                        };
                    }
                }

                int newIndex = IndexOfObject(asset, newObject);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["host_index"] = hostIndex >= 0 ? (object)hostIndex : null,
                    ["host_class_guid"] = GetClassGuid(host).ToString(),
                    ["path"] = assignedPath ?? path,
                    ["list_index"] = listIndex >= 0 ? (object)listIndex : null,
                    ["mode"] = createOnly ? "create_only" : assignMode,
                    ["registered_in_graph"] = graphHost,
                    ["registered_in"] = spawnerHost,
                    ["registered_in_entity"] = entityHost,
                    ["index_sides"] = indexSides,
                    ["component"] = SerializeComponent(newObject, newIndex, asset),
                    ["pointer"] = SerializePointerRef(pref)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "CREATE_FAILED");
            }
        }

        // =====================================================================
        // Internals
        // =====================================================================

        private static bool TryLoadBlueprint(JObject p, out EbxAssetEntry entry, out EbxAsset asset, out object error)
        {
            entry = null;
            asset = null;
            error = null;

            if (App.AssetManager == null)
            {
                error = McpHandlers.Error("AssetManager not ready", "NOT_READY");
                return false;
            }

            entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
            {
                error = McpHandlers.Error("EBX asset not found", "NOT_FOUND");
                return false;
            }

            asset = App.AssetManager.GetEbx(entry);
            if (asset == null)
            {
                error = McpHandlers.Error("Failed to load EBX", "LOAD_FAILED");
                return false;
            }
            return true;
        }

        private static void SaveAsset(EbxAssetEntry entry, EbxAsset asset)
        {
            asset.Update();
            App.AssetManager.ModifyEbx(entry.Name, asset);
        }

        // Each connection is serialized in its own try block. The old code wrapped
        // the whole loop, so one bad connection silently cut off every connection
        // after it while the call still returned success. A failed item is kept as
        // a placeholder and counted in scan_errors.
        private static int AppendPropertyConnections(dynamic root, List<object> items, bool compact, ScanBudget budget)
        {
            int count;
            try { count = (int)root.PropertyConnections.Count; }
            catch { return 0; /* asset may not be a blueprint */ }
            int errors = 0;
            for (int i = 0; i < count; i++)
            {
                if (budget.Stop())
                    break;
                budget.Node();
                try { items.Add(SerializePropertyConnection(root.PropertyConnections[i], i, compact)); }
                catch (Exception ex)
                {
                    errors++;
                    items.Add(ConnectionSerializationFailure("property", i, ex));
                }
            }
            return errors;
        }

        private static int AppendEventConnections(dynamic root, List<object> items, bool compact, ScanBudget budget)
        {
            int count;
            try { count = (int)root.EventConnections.Count; }
            catch { return 0; }
            int errors = 0;
            for (int i = 0; i < count; i++)
            {
                if (budget.Stop())
                    break;
                budget.Node();
                try { items.Add(SerializeEventConnection(root.EventConnections[i], i, compact)); }
                catch (Exception ex)
                {
                    errors++;
                    items.Add(ConnectionSerializationFailure("event", i, ex));
                }
            }
            return errors;
        }

        private static int AppendLinkConnections(dynamic root, List<object> items, bool compact, ScanBudget budget)
        {
            int count;
            try { count = (int)root.LinkConnections.Count; }
            catch { return 0; }
            int errors = 0;
            for (int i = 0; i < count; i++)
            {
                if (budget.Stop())
                    break;
                budget.Node();
                try { items.Add(SerializeLinkConnection(root.LinkConnections[i], i, compact)); }
                catch (Exception ex)
                {
                    errors++;
                    items.Add(ConnectionSerializationFailure("link", i, ex));
                }
            }
            return errors;
        }

        private static Dictionary<string, object> ConnectionSerializationFailure(string kind, int index, Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["index"] = index,
                ["serialization_failed"] = true,
                ["error"] = ex.Message
            };
        }

        private static Dictionary<string, object> SerializePropertyConnection(dynamic conn, int index, bool compact = false)
        {
            uint flags = (uint)conn.Flags;
            DecodePropertyConnectionFlags(flags, out string realm, out string propType, out bool sourceCantBeStatic);
            var d = new Dictionary<string, object>
            {
                ["kind"] = "property",
                ["index"] = index,
                ["source"] = compact ? SerializePointerRefShort(conn.Source) : SerializePointerRef(conn.Source),
                ["target"] = compact ? SerializePointerRefShort(conn.Target) : SerializePointerRef(conn.Target),
                ["source_field"] = (string)(CString)conn.SourceField,
                ["target_field"] = (string)(CString)conn.TargetField,
                ["realm"] = realm,
                ["prop_type"] = propType
            };
            if (!compact)
            {
                d["flags_hex"] = "0x" + flags.ToString("X8");
                d["source_cant_be_static"] = sourceCantBeStatic;
            }
            return d;
        }

        private static Dictionary<string, object> SerializeEventConnection(dynamic conn, int index, bool compact = false)
        {
            string sourceEvent = "";
            string targetEvent = "";
            string targetType = "";
            try { sourceEvent = (string)(CString)conn.SourceEvent.Name; } catch { }
            try { targetEvent = (string)(CString)conn.TargetEvent.Name; } catch { }
            try { targetType = conn.TargetType.ToString(); } catch { }

            return new Dictionary<string, object>
            {
                ["kind"] = "event",
                ["index"] = index,
                ["source"] = compact ? SerializePointerRefShort(conn.Source) : SerializePointerRef(conn.Source),
                ["target"] = compact ? SerializePointerRefShort(conn.Target) : SerializePointerRef(conn.Target),
                ["source_event"] = sourceEvent,
                ["target_event"] = targetEvent,
                ["target_type"] = targetType
            };
        }

        private static Dictionary<string, object> SerializeLinkConnection(dynamic conn, int index, bool compact = false)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = "link",
                ["index"] = index,
                ["source"] = compact ? SerializePointerRefShort(conn.Source) : SerializePointerRef(conn.Source),
                ["target"] = compact ? SerializePointerRefShort(conn.Target) : SerializePointerRef(conn.Target),
                ["source_field"] = (string)(CString)conn.SourceField,
                ["target_field"] = (string)(CString)conn.TargetField
            };
        }

        /// <summary>
        /// One-line pointer description for compact connection listings. Full
        /// SerializePointerRef output is kept for callers that need every field.
        /// </summary>
        private static string SerializePointerRefShort(PointerRef pref)
        {
            if (pref.Type == PointerRefType.External)
            {
                EbxAssetEntry e = App.AssetManager?.GetEbxEntry(pref.External.FileGuid);
                if (e != null)
                    return e.Name + " [" + pref.External.FileGuid + "]";
                return "External [" + pref.External.FileGuid + "]";
            }
            if (pref.Type == PointerRefType.Internal && pref.Internal != null)
                return pref.Internal.GetType().Name + " [" + GetClassGuid(pref.Internal) + "]";
            return "null";
        }

        private static bool ConnectionMatches(object item, string componentGuid, string textFilter)
        {
            if (!(item is Dictionary<string, object> d))
                return false;

            if (!string.IsNullOrEmpty(componentGuid))
            {
                bool hit = false;
                foreach (string key in new[] { "source", "target" })
                {
                    if (!d.TryGetValue(key, out object v))
                        continue;
                    if (v is Dictionary<string, object> pd)
                    {
                        if (pd.TryGetValue("class_guid", out object cg) &&
                            cg != null &&
                            cg.ToString().IndexOf(componentGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                            hit = true;
                    }
                    else if (v != null &&
                             v.ToString().IndexOf(componentGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hit = true;
                    }
                }
                if (!hit)
                    return false;
            }

            if (!string.IsNullOrEmpty(textFilter))
                return ContainsText(item, textFilter, StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static bool ContainsText(object value, string needle, StringComparison cmp)
        {
            if (value == null)
                return false;
            if (value is string s)
                return s.IndexOf(needle, cmp) >= 0;
            if (value is IDictionary<string, object> dict)
            {
                foreach (KeyValuePair<string, object> kv in dict)
                    if (ContainsText(kv.Value, needle, cmp))
                        return true;
                return false;
            }
            if (value is System.Collections.IEnumerable list && !(value is string))
            {
                foreach (object x in list)
                    if (ContainsText(x, needle, cmp))
                        return true;
                return false;
            }
            return value.ToString().IndexOf(needle, cmp) >= 0;
        }

        private static Dictionary<string, object> SerializePointerRef(PointerRef pref)
        {
            var d = new Dictionary<string, object>
            {
                ["ref_type"] = pref.Type.ToString()
            };
            if (pref.Type == PointerRefType.External)
            {
                d["file_guid"] = pref.External.FileGuid.ToString();
                d["class_guid"] = pref.External.ClassGuid.ToString();
                EbxAssetEntry e = App.AssetManager?.GetEbxEntry(pref.External.FileGuid);
                if (e != null)
                    d["asset"] = e.Name;
            }
            else if (pref.Type == PointerRefType.Internal && pref.Internal != null)
            {
                d["class_guid"] = GetClassGuid(pref.Internal).ToString();
                d["type"] = pref.Internal.GetType().Name;
                d["name"] = GetObjectName(pref.Internal);
            }
            return d;
        }

        private static Dictionary<string, object> SerializeComponent(object obj, int index, EbxAsset asset, bool compact = false)
        {
            Guid classGuid = GetClassGuid(obj);
            uint flags = GetObjectFlagsRaw(obj);
            var d = new Dictionary<string, object>
            {
                ["index"] = index,
                ["type"] = obj.GetType().Name,
                ["class_guid"] = classGuid.ToString(),
                ["name"] = GetObjectName(obj),
                ["is_root"] = ReferenceEquals(obj, asset.RootObject),
                ["flags"] = flags,
                ["realm"] = TryGetEnumString(obj, "Realm")
            };
            if (!compact)
                d["object_flags"] = DecodeObjectFlags(flags);
            return d;
        }

        private static PointerRef ResolvePointerRef(EbxAsset asset, JObject p, string prefix, Type expectedBaseType = null)
        {
            // Accept nested object {class_guid|asset|file_guid|null} or prefixed flat keys
            string classGuidStr = p.Value<string>("class_guid") ?? p.Value<string>(prefix + "guid") ?? p.Value<string>(prefix + "class_guid");
            string assetName = p.Value<string>("asset") ?? p.Value<string>(prefix + "asset");
            string fileGuidStr = p.Value<string>("file_guid") ?? p.Value<string>(prefix + "file_guid");
            string refType = (p.Value<string>("ref_type") ?? "").ToLowerInvariant();

            if (refType == "null" || (string.IsNullOrEmpty(classGuidStr) && string.IsNullOrEmpty(assetName) && string.IsNullOrEmpty(fileGuidStr) && p.Value<bool?>("null") == true))
                return new PointerRef();

            // External by asset name
            if (!string.IsNullOrEmpty(assetName))
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(assetName);
                if (e == null)
                    throw new InvalidOperationException("External asset not found: " + assetName);
                EbxAsset a = App.AssetManager.GetEbx(e);
                if (a.FileGuid != e.Guid)
                    throw new InvalidOperationException("GUID_INCONSISTENT: entry " + e.Guid + ", file " + a.FileGuid);
                Guid cg = !string.IsNullOrEmpty(classGuidStr)
                    ? Guid.Parse(classGuidStr)
                    : ResolveExternalClassGuid(a, expectedBaseType);
                return new PointerRef(new EbxImportReference { FileGuid = e.Guid, ClassGuid = cg });
            }

            // External by file_guid
            if (!string.IsNullOrEmpty(fileGuidStr))
            {
                Guid fileGuid = Guid.Parse(fileGuidStr);
                Guid cg;
                if (!string.IsNullOrEmpty(classGuidStr))
                    cg = Guid.Parse(classGuidStr);
                else if (expectedBaseType != null)
                {
                    EbxAssetEntry fileEntry = App.AssetManager.GetEbxEntry(fileGuid);
                    cg = fileEntry != null
                        ? ResolveExternalClassGuid(App.AssetManager.GetEbx(fileEntry), expectedBaseType)
                        : Guid.Empty;
                }
                else
                    cg = Guid.Empty;
                return new PointerRef(new EbxImportReference { FileGuid = fileGuid, ClassGuid = cg });
            }

            // Internal by class_guid
            if (!string.IsNullOrEmpty(classGuidStr))
            {
                Guid cg = Guid.Parse(classGuidStr);
                object obj = FindObjectByClassGuid(asset, cg);
                if (obj == null)
                    throw new InvalidOperationException("Internal component not found: " + classGuidStr);
                return new PointerRef(obj);
            }

            throw new InvalidOperationException("PointerRef requires class_guid (internal), asset/file_guid (external), or null=true");
        }

        /// <summary>
        /// External PointerRefs name a target object inside another asset. When the
        /// caller passes only the asset, the previous default was the asset root,
        /// which silently produced a wrong-typed pointer for fields such as
        /// DataContainerAsset.SkeletonCollisionData. Resolve the declared field
        /// base type against the asset's objects instead, and refuse when the
        /// choice is ambiguous.
        /// </summary>
        private static Guid ResolveExternalClassGuid(EbxAsset target, Type expectedBaseType)
        {
            if (target == null)
                return Guid.Empty;
            if (expectedBaseType == null || expectedBaseType == typeof(object) || expectedBaseType.IsValueType)
                return target.RootInstanceGuid;

            // BT node lists declare BTNodeData, and BehaviorTreeData is not a
            // BTNodeData subclass. Vanilla data links those slots to the tree's
            // Root node, not to the BehaviorTreeData object, so resolve Root.
            // A field typed as the tree itself (AISpawnData.BehaviorTreeOverride)
            // still takes the tree object.
            if (target.RootObject.GetType().Name == "BehaviorTreeData")
            {
                if (expectedBaseType.IsAssignableFrom(target.RootObject.GetType()))
                    return target.RootInstanceGuid;

                object rootNode = GetBehaviorTreeRootNode(target.RootObject);
                if (rootNode == null)
                    throw new InvalidOperationException(
                        "BT_ROOT_MISSING: asset " + target.FileGuid +
                        " has no internal Root node; pass class_guid explicitly");
                return GetClassGuid(rootNode);
            }

            var candidates = new List<object>();
            foreach (object obj in target.Objects)
            {
                if (expectedBaseType.IsAssignableFrom(obj.GetType()))
                    candidates.Add(obj);
            }

            if (candidates.Count == 0)
            {
                if (expectedBaseType.IsAssignableFrom(target.RootObject.GetType()))
                    return target.RootInstanceGuid;
                throw new InvalidOperationException(
                    "POINTER_BASE_TYPE_MISMATCH: field expects '" + expectedBaseType.Name +
                    "', asset root is '" + target.RootObject.GetType().Name +
                    "', and no contained object matches; pass class_guid explicitly");
            }

            if (candidates.Count == 1)
                return GetClassGuid(candidates[0]);

            // A base type can match several subclasses; an exact type match is
            // still unambiguous.
            var exact = new List<object>();
            foreach (object obj in candidates)
            {
                if (obj.GetType() == expectedBaseType)
                    exact.Add(obj);
            }
            if (exact.Count == 1)
                return GetClassGuid(exact[0]);

            var described = new List<string>();
            foreach (object obj in candidates.Take(6))
                described.Add(obj.GetType().Name + " " + GetClassGuid(obj).ToString("N").Substring(0, 8));
            throw new InvalidOperationException(
                "AMBIGUOUS_POINTER: field expects '" + expectedBaseType.Name + "' and the asset has " +
                candidates.Count + " matching objects (" + string.Join(", ", described) +
                "); pass class_guid explicitly");
        }

        /// <summary>
        /// BehaviorTreeData.Root is the node other trees link to for a subtree
        /// reference. Read it through the public property, or through the field
        /// on types that expose EBX fields directly.
        /// </summary>
        private static object GetBehaviorTreeRootNode(object treeObject)
        {
            object value = null;
            PropertyInfo prop = treeObject.GetType().GetProperty("Root");
            if (prop != null)
                value = prop.GetValue(treeObject);
            else
            {
                FieldInfo field = treeObject.GetType().GetField("Root");
                if (field != null)
                    value = field.GetValue(treeObject);
            }

            if (value is PointerRef pr && pr.Type == PointerRefType.Internal)
                return pr.Internal;
            return null;
        }

        internal static object ResolveComponent(EbxAsset asset, JObject p, out int index)
        {
            index = -1;
            if (p["index"] != null)
            {
                int idx = p.Value<int>("index");
                List<object> objs = asset.Objects.Cast<object>().ToList();
                if (idx >= 0 && idx < objs.Count)
                {
                    index = idx;
                    return objs[idx];
                }
            }

            string classGuidStr = p.Value<string>("class_guid") ?? p.Value<string>("component_guid");
            if (!string.IsNullOrEmpty(classGuidStr) && Guid.TryParse(classGuidStr, out Guid cg))
            {
                int i = 0;
                foreach (object obj in asset.Objects)
                {
                    if (GetClassGuid(obj) == cg)
                    {
                        index = i;
                        return obj;
                    }
                    i++;
                }
            }

            string typeName = (p.Value<string>("component_type") ?? p.Value<string>("type") ?? "").Trim();
            int typeIndex = p.Value<int?>("type_index") ?? 0;
            if (!string.IsNullOrEmpty(typeName))
            {
                // Exact type first; only then accept subclasses. The old exact-only
                // path returned NOT_FOUND when callers passed a base class name
                // (e.g. asking for a SoundGraphNodeData), which looked like the
                // component was missing even though it was present.
                int match = 0;
                int i = 0;
                foreach (object obj in asset.Objects)
                {
                    if (obj.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (match == typeIndex)
                        {
                            index = i;
                            return obj;
                        }
                        match++;
                    }
                    i++;
                }

                if (match == 0)
                {
                    match = 0;
                    i = 0;
                    foreach (object obj in asset.Objects)
                    {
                        if (TypeLibrary.IsSubClassOf(obj.GetType().Name, typeName))
                        {
                            if (match == typeIndex)
                            {
                                index = i;
                                return obj;
                            }
                            match++;
                        }
                        i++;
                    }
                }
            }

            return null;
        }

        internal static Guid GetClassGuid(object obj)
        {
            AssetClassGuid g = ((dynamic)obj).GetInstanceGuid();
            if (g.IsExported)
                return g.ExportedGuid;
            return Guid.Parse(g.InternalId.ToString("X").PadLeft(32, '0'));
        }

        private static object FindObjectByClassGuid(EbxAsset asset, Guid classGuid)
        {
            foreach (object obj in asset.Objects)
            {
                if (GetClassGuid(obj) == classGuid)
                    return obj;
            }
            return null;
        }

        private static int IndexOfObject(EbxAsset asset, object target)
        {
            int i = 0;
            foreach (object obj in asset.Objects)
            {
                if (ReferenceEquals(obj, target))
                    return i;
                i++;
            }

            // Fallback for pointer graphs that were rebuilt with new instances:
            // only trust the class-guid match when it identifies exactly one object.
            Guid g = GetClassGuid(target);
            int match = -1;
            int count = 0;
            i = 0;
            foreach (object obj in asset.Objects)
            {
                if (GetClassGuid(obj) == g)
                {
                    match = i;
                    count++;
                }
                i++;
            }
            return count == 1 ? match : -1;
        }

        internal static bool IsContainedInObjects(EbxAsset asset, object target)
        {
            foreach (object obj in asset.Objects)
            {
                if (ReferenceEquals(obj, target))
                    return true;
            }
            return false;
        }

        private static bool IsClassGuidUnique(EbxAsset asset, Guid classGuid)
        {
            int count = 0;
            foreach (object obj in asset.Objects)
            {
                if (GetClassGuid(obj) == classGuid)
                {
                    count++;
                    if (count > 1)
                        return false;
                }
            }
            return count == 1;
        }

        /// <summary>
        /// Gives every internal object added since <paramref name="preexisting"/> a
        /// unique InternalId. AddObject already picks max+1, so this repairs ids that
        /// arrive negative or duplicated.
        /// </summary>
        // Every component in an EBX file carries a ClientIndex and a ServerIndex;
        // 255 means "absent on that side". The client and server component tables are
        // sized by ClientRuntimeComponentCount / ServerRuntimeComponentCount, so a
        // component that claims a slot on a side it has no runtime implementation for
        // leaves an entry with no instance behind it and the game dies walking the
        // table. Which sides a component occupies is per instance and is not
        // derivable from its type, so the reference instance decides: the duplicate
        // source when there is one, otherwise an existing instance of the same type
        // in this asset. With neither, the realm is the best guess and the reply says
        // so; client_index / server_index override the result outright.
        internal static string AssignComponentIndices(EbxAsset asset, object newObject, object reference = null, JObject p = null, object countHost = null)
        {
            Type t = newObject.GetType();
            PropertyInfo clientPi = t.GetProperty("ClientIndex");
            PropertyInfo serverPi = t.GetProperty("ServerIndex");
            if (clientPi == null && serverPi == null)
                return null;

            int? explicitClient = p?.Value<int?>("client_index");
            int? explicitServer = p?.Value<int?>("server_index");

            object refObj = reference;
            if (refObj == null)
            {
                foreach (object obj in asset.Objects)
                {
                    if (ReferenceEquals(obj, newObject) || obj.GetType() != t)
                        continue;
                    refObj = obj;
                    break;
                }
            }

            PropertyInfo refClientPi = refObj?.GetType().GetProperty("ClientIndex");
            PropertyInfo refServerPi = refObj?.GetType().GetProperty("ServerIndex");
            bool inferredClient;
            bool inferredServer;
            string how;

            if (refClientPi != null || refServerPi != null)
            {
                int refClient = refClientPi == null ? 255 : Convert.ToInt32(refClientPi.GetValue(refObj));
                int refServer = refServerPi == null ? 255 : Convert.ToInt32(refServerPi.GetValue(refObj));
                inferredClient = refClient != 255;
                inferredServer = refServer != 255;
                how = "mirrored from " + refObj.GetType().Name;
            }
            else
            {
                string realm = null;
                PropertyInfo realmPi = t.GetProperty("Realm");
                if (realmPi != null && realmPi.GetValue(newObject) != null)
                    realm = realmPi.GetValue(newObject).ToString();
                inferredClient = realm == null || realm.IndexOf("Client", StringComparison.Ordinal) >= 0;
                inferredServer = realm == null || realm.IndexOf("Server", StringComparison.Ordinal) >= 0;
                how = "realm only (no instance of this type to copy from; check a vanilla one)";
            }

            // An explicit value only overrides its own side; the other side still
            // follows the inference, so passing server_index=255 to make a
            // client-only component does not silently drop the client slot too.
            bool onClient = explicitClient.HasValue ? explicitClient.Value != 255 : inferredClient;
            bool onServer = explicitServer.HasValue ? explicitServer.Value != 255 : inferredServer;
            if (explicitClient.HasValue || explicitServer.HasValue)
                how = "explicit, " + how;

            // The numeric value is no longer decided here; see the renumber pass below.

            // Slot ids are not "max + 1". The game builds one flat table per side by
            // walking the finished object tree depth first, so the ids have to be
            // dense in that order and the two counters have to match the counts.
            // Assigning max + 1 leaves a hole the moment an object is inserted in the
            // middle of the tree, and the loader then dereferences an empty slot while
            // initialising the entity; the observed symptom is a null read at load.
            // So the side membership decided above is kept and the numbering is
            // recomputed for the whole asset.
            if (clientPi != null && clientPi.CanWrite)
                clientPi.SetValue(newObject, Convert.ChangeType(255, clientPi.PropertyType));
            if (serverPi != null && serverPi.CanWrite)
                serverPi.SetValue(newObject, Convert.ChangeType(255, serverPi.PropertyType));

            string renumber = RenumberComponentSlots(asset, newObject, onClient, onServer, countHost);

            int finalClient = clientPi == null ? 255 : Convert.ToInt32(clientPi.GetValue(newObject));
            int finalServer = serverPi == null ? 255 : Convert.ToInt32(serverPi.GetValue(newObject));

            return how + ", client=" + finalClient + ", server=" + finalServer
                   + (renumber == null ? "" : ", " + renumber);
        }

        private static List<object> ComponentChildrenOf(object obj)
        {
            List<object> children = new List<object>();

            // Only the Components list is walked. Vanilla numbering over four playable
            // references is exactly the depth-first order of these lists, and none of
            // them carries a populated Objects or Children array, so adding either here
            // would reorder the walk and permute every slot.
            //
            // The list holds PointerRef wrappers, not the objects themselves; a walk that
            // used them as they are would find no children at all and silently fall back
            // to the order of asset.Objects.
            if (!(GetListMember(obj, "Components") is System.Collections.IEnumerable list) || list is string)
                return children;

            foreach (object item in list)
            {
                object child = UnwrapPointer(item);
                if (child != null)
                    children.Add(child);
            }
            return children;
        }

        /// <summary>
        /// Renumbers every component slot in the asset by depth-first order, per side,
        /// and syncs the runtime counters. Membership is preserved: an object that was
        /// 255 on a side stays 255 on that side. Returns a short summary for the reply.
        /// </summary>
        internal static string RenumberComponentSlots(EbxAsset asset, object newObject,
                                                      bool newOnClient, bool newOnServer,
                                                      object countHost = null)
        {
            List<object> roots = new List<object>();
            HashSet<object> hasParent = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (object obj in asset.Objects)
            {
                foreach (object child in ComponentChildrenOf(obj))
                    hasParent.Add(child);
            }
            foreach (object obj in asset.Objects)
            {
                if (!hasParent.Contains(obj))
                    roots.Add(obj);
            }

            List<object> order = new List<object>();
            HashSet<object> visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Stack<object> stack = new Stack<object>();
            for (int i = roots.Count - 1; i >= 0; --i)
                stack.Push(roots[i]);
            while (stack.Count > 0)
            {
                object obj = stack.Pop();
                if (obj == null || !visited.Add(obj))
                    continue;
                order.Add(obj);
                List<object> children = ComponentChildrenOf(obj);
                for (int i = children.Count - 1; i >= 0; --i)
                    stack.Push(children[i]);
            }
            // Anything unreachable still has to keep a valid id.
            foreach (object obj in asset.Objects)
            {
                if (visited.Add(obj))
                    order.Add(obj);
            }

            int nextClient = 0;
            int nextServer = 0;
            foreach (object obj in order)
            {
                Type t = obj.GetType();
                PropertyInfo cp = t.GetProperty("ClientIndex");
                PropertyInfo sp = t.GetProperty("ServerIndex");
                bool onClient;
                bool onServer;

                if (ReferenceEquals(obj, newObject))
                {
                    onClient = newOnClient;
                    onServer = newOnServer;
                }
                else
                {
                    onClient = cp != null && Convert.ToInt32(cp.GetValue(obj)) != 255;
                    onServer = sp != null && Convert.ToInt32(sp.GetValue(obj)) != 255;
                }

                if (cp != null && cp.CanWrite)
                    cp.SetValue(obj, Convert.ChangeType(onClient ? nextClient : 255, cp.PropertyType));
                if (onClient && cp != null)
                    ++nextClient;

                if (sp != null && sp.CanWrite)
                    sp.SetValue(obj, Convert.ChangeType(onServer ? nextServer : 255, sp.PropertyType));
                if (onServer && sp != null)
                    ++nextServer;
            }

            // The counters belong to the entity that owns the component list. In a
            // weapon blueprint with an embedded BulletEntityData the first object in
            // traversal order is the projectile, not the PVZCharacterWeaponData that
            // received the component, so prefer the caller's host and only fall back
            // to the first object carrying the fields.
            object countOwner = countHost;
            if (countOwner == null || countOwner.GetType().GetProperty("ClientRuntimeComponentCount") == null)
            {
                foreach (object obj in order)
                {
                    if (obj.GetType().GetProperty("ClientRuntimeComponentCount") != null)
                    {
                        countOwner = obj;
                        break;
                    }
                }
            }
            if (countOwner != null)
            {
                Type et = countOwner.GetType();
                PropertyInfo cc = et.GetProperty("ClientRuntimeComponentCount");
                PropertyInfo sc = et.GetProperty("ServerRuntimeComponentCount");
                if (cc != null && cc.CanWrite)
                {
                    cc.SetValue(countOwner, Convert.ChangeType(nextClient, cc.PropertyType));
                    if (sc != null && sc.CanWrite)
                        sc.SetValue(countOwner, Convert.ChangeType(nextServer, sc.PropertyType));
                }
            }

            return "renumbered client=" + nextClient + ", server=" + nextServer;
        }

        /// <summary>
        /// Recomputes the component numbering and the runtime counters after any tool
        /// changed which components the asset contains. Adding goes through
        /// AssignComponentIndices and the two remove paths call RenumberComponentSlots
        /// directly; every other mutation (insert into a Components list, a direct
        /// Components write, a clipboard paste) goes through here, so the counters can
        /// never keep describing the previous shape. Assets that declare no runtime
        /// counters are left untouched. Returns the summary, or null when untouched.
        /// </summary>
        internal static string NormalizeComponentSlots(EbxAsset asset, object hintHost = null)
        {
            object host = FindRuntimeComponentCountHost(asset, hintHost);
            if (host == null)
                return null;
            return RenumberComponentSlots(asset, null, false, false, host);
        }

        /// <summary>
        /// Default array for a component when the caller did not name one. A weapon
        /// blueprint's component table is PVZCharacterWeaponData.Components and a
        /// character blueprint's is PVZCharacterEntityData.Components; neither is the
        /// root Objects array. Appending to root Objects first and moving the pointer
        /// later leaves a window where the wrong array is serialized and makes the
        /// caller's success look correct while the runtime sees a bogus component.
        /// Assets without either host keep the old root Objects behaviour (Rime
        /// widgets and other blueprint types).
        /// </summary>
        /// <summary>
        /// Audio.SoundEntityData is the one component type that belongs in the
        /// blueprint's root Objects on a weapon blueprint even though the asset also
        /// has a PVZCharacterWeaponData.Components table. Moving it into Components
        /// changes which event path drives it, so keep it on the root array.
        /// </summary>
        private static bool MustStayOnRootObjects(object obj)
        {
            if (obj == null)
                return false;
            Type t = obj.GetType();
            if (string.Equals(t.Name, "SoundEntityData", StringComparison.Ordinal))
                return true;
            string full = t.FullName ?? "";
            return full.IndexOf(".Audio.", StringComparison.Ordinal) >= 0;
        }

        private static bool TryFindDefaultComponentArray(EbxAsset asset, out object host, out string arrayName)
        {
            host = null;
            arrayName = null;
            if (asset?.Objects == null)
                return false;

            foreach (object obj in asset.Objects)
            {
                if (obj == null)
                    continue;
                string typeName = obj.GetType().Name;
                if (typeName != "PVZCharacterWeaponData" && typeName != "PVZCharacterEntityData")
                    continue;
                if (GetListMember(obj, "Components") is IList)
                {
                    host = obj;
                    arrayName = "Components";
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Picks the object that stores ClientRuntimeComponentCount /
        /// ServerRuntimeComponentCount. The caller's host wins when it carries those
        /// fields; otherwise the first entity whose Components list is populated,
        /// because a weapon blueprint embeds a projectile entity ahead of the weapon
        /// data in traversal order and the empty one must not win.
        /// </summary>
        internal static object FindRuntimeComponentCountHost(EbxAsset asset, object hintHost = null)
        {
            if (CarriesRuntimeCounts(hintHost))
                return hintHost;

            object withList = null;
            object any = null;
            foreach (object obj in asset.Objects)
            {
                if (!CarriesRuntimeCounts(obj))
                    continue;
                if (any == null)
                    any = obj;
                if (withList == null)
                {
                    IList components = GetListMember(obj, "Components") as IList;
                    if (components != null && components.Count > 0)
                        withList = obj;
                }
            }
            return withList ?? any;
        }

        private static bool CarriesRuntimeCounts(object obj)
        {
            if (obj == null)
                return false;
            Type t = obj.GetType();
            return t.GetProperty("ClientRuntimeComponentCount") != null
                || t.GetProperty("ServerRuntimeComponentCount") != null;
        }

        /// <summary>
        /// True when a dotted path names a Components array (optionally with an element
        /// index); a write there can change which components the asset contains.
        /// </summary>
        internal static bool PathTargetsComponentList(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            string last = path;
            int dot = last.LastIndexOf('.');
            if (dot >= 0)
                last = last.Substring(dot + 1);
            int bracket = last.IndexOf('[');
            if (bracket >= 0)
                last = last.Substring(0, bracket);
            return string.Equals(last.Trim(), "Components", StringComparison.Ordinal);
        }

        /// <summary>
        /// Finds the object whose named pointer list contains <paramref name="target"/>.
        /// The runtime component counters live on that owner, which is not always the
        /// first entity in traversal order (weapon blueprints embed a projectile entity).
        /// </summary>
        internal static object FindPointerListOwner(EbxAsset asset, object target, string listName)
        {
            foreach (object obj in asset.Objects)
            {
                if (obj == null || ReferenceEquals(obj, target))
                    continue;
                IList list = GetListMember(obj, listName) as IList;
                if (list == null)
                    continue;
                if (FindPointerIndex(list, target) >= 0)
                    return obj;
            }
            return null;
        }

        // TypeLibrary.CreateObject + new AssetClassGuid(guid, -1) leaves the object
        // marked as exported, because AssetClassGuid.IsExported is simply
        // "the exported guid is not empty". A sub-object that lives in the asset's own
        // object list must be internal instead: written as an export entry, the runtime
        // resolves it to null when it walks the component list, and the game crashes in
        // the saved-component loader. Dropping the exported guid keeps the internal id
        // that AddObject just assigned.
        internal static void MakeCreatedObjectInternal(object obj)
        {
            AssetClassGuid g = ((dynamic)obj).GetInstanceGuid();
            if (!g.IsExported)
                return;

            int id = g.InternalId;
            ((dynamic)obj).SetInstanceGuid(id >= 0 ? new AssetClassGuid(id) : new AssetClassGuid(Guid.Empty, -1));
        }

        private static void RepairInternalIdCollisions(EbxAsset asset, List<object> preexisting)
        {
            HashSet<object> oldObjects = new HashSet<object>(preexisting, ReferenceEqualityComparer.Instance);
            HashSet<int> usedIds = new HashSet<int>();
            int maxId = -1;

            foreach (object obj in asset.Objects)
            {
                if (!oldObjects.Contains(obj))
                    continue;
                AssetClassGuid g = ((dynamic)obj).GetInstanceGuid();
                if (g.IsExported || g.InternalId < 0)
                    continue;
                usedIds.Add(g.InternalId);
                if (g.InternalId > maxId)
                    maxId = g.InternalId;
            }

            foreach (object obj in asset.Objects)
            {
                if (oldObjects.Contains(obj))
                    continue;
                AssetClassGuid g = ((dynamic)obj).GetInstanceGuid();
                if (g.IsExported)
                    continue;

                int id = g.InternalId;
                if (id < 0 || usedIds.Contains(id))
                {
                    id = maxId + 1;
                    ((dynamic)obj).SetInstanceGuid(new AssetClassGuid(g.ExportedGuid, id));
                }

                usedIds.Add(id);
                if (id > maxId)
                    maxId = id;
            }
        }

        private static string GetObjectName(object obj)
        {
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty("Name");
                if (pi != null)
                {
                    object v = pi.GetValue(obj);
                    if (v is CString cs)
                        return cs;
                    return v?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        private static uint GetObjectFlagsRaw(object obj)
        {
            PropertyInfo pi = obj.GetType().GetProperty("Flags");
            if (pi == null)
                return 0;
            object v = pi.GetValue(obj);
            return v == null ? 0u : Convert.ToUInt32(v);
        }

        private static void SetObjectFlagsRaw(object obj, uint flags)
        {
            PropertyInfo pi = obj.GetType().GetProperty("Flags")
                ?? throw new InvalidOperationException("Object has no Flags property: " + obj.GetType().Name);
            pi.SetValue(obj, flags);
        }

        private static Dictionary<string, object> DecodeObjectFlags(uint flags)
        {
            return new Dictionary<string, object>
            {
                ["guid_bits"] = flags & GuidMask,
                ["unknown"] = (flags & UnknownFlag) != 0,
                ["client_event"] = (flags & ClientEventFlag) != 0,
                ["server_event"] = (flags & ServerEventFlag) != 0,
                ["client_property"] = (flags & ClientPropertyFlag) != 0,
                ["server_property"] = (flags & ServerPropertyFlag) != 0,
                ["client_link"] = (flags & ClientLinkFlag) != 0,
                ["server_link"] = (flags & ServerLinkFlag) != 0,
                ["unused"] = (flags & UnusedFlag) != 0
            };
        }

        private static bool HasObjectFlagParams(JObject p)
        {
            string[] keys =
            {
                "unknown", "client_event", "server_event", "client_property", "server_property",
                "client_link", "server_link", "unused", "flag_preset"
            };
            return keys.Any(k => p[k] != null);
        }

        private static void ApplyObjectFlags(object obj, JObject p, bool preserveGuidBits)
        {
            uint current = GetObjectFlagsRaw(obj);
            uint guidBits = current & GuidMask;
            uint flags;

            string preset = (p.Value<string>("flag_preset") ?? "").Trim().ToLowerInvariant();
            if (preset == "client")
            {
                flags = guidBits | UnknownFlag | ClientEventFlag | ClientPropertyFlag | ClientLinkFlag;
            }
            else if (preset == "server")
            {
                flags = guidBits | UnknownFlag | ServerEventFlag | ServerPropertyFlag | ServerLinkFlag;
            }
            else if (preset == "client_and_server" || preset == "both")
            {
                flags = guidBits | UnknownFlag
                    | ClientEventFlag | ServerEventFlag
                    | ClientPropertyFlag | ServerPropertyFlag
                    | ClientLinkFlag | ServerLinkFlag;
            }
            else
            {
                // Start from existing flags; only toggle keys present in the payload
                flags = preserveGuidBits ? current : (current & ~GuidMask);

                void Apply(string key, uint mask)
                {
                    if (p[key] == null || p[key].Type == JTokenType.Null)
                        return;
                    if (p.Value<bool>(key))
                        flags |= mask;
                    else
                        flags &= ~mask;
                }

                Apply("unknown", UnknownFlag);
                Apply("client_event", ClientEventFlag);
                Apply("server_event", ServerEventFlag);
                Apply("client_property", ClientPropertyFlag);
                Apply("server_property", ServerPropertyFlag);
                Apply("client_link", ClientLinkFlag);
                Apply("server_link", ServerLinkFlag);
                Apply("unused", UnusedFlag);

                if (preserveGuidBits)
                    flags = (flags & ~GuidMask) | guidBits;
            }

            SetObjectFlagsRaw(obj, flags);
        }

        private static bool HasAnyExplicitObjectFlag(JObject p)
        {
            return p["unknown"] != null || p["client_event"] != null || p["server_event"] != null
                || p["client_property"] != null || p["server_property"] != null
                || p["client_link"] != null || p["server_link"] != null || p["unused"] != null;
        }

        private static bool HasFlagParams(JObject p)
        {
            return p["realm"] != null || p["prop_type"] != null || p["source_cant_be_static"] != null || p["flags"] != null;
        }

        private static uint ResolvePropertyConnectionFlags(JObject p, uint existing = 0)
        {
            if (p["flags"] != null)
            {
                string s = p["flags"].ToString();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToUInt32(s, 16);
                return Convert.ToUInt32(s);
            }

            // Partial update: start from the connection's current flags and replace
            // only the fields the caller supplied.
            uint flags = existing;

            if (p["realm"] != null)
            {
                int realm = ParseRealm(p.Value<string>("realm"));
                if (realm >= 0)
                    flags = (flags & ~0x7u) | (uint)realm;
            }

            if (p["prop_type"] != null)
            {
                int propType = ParsePropType(p.Value<string>("prop_type"));
                if (propType >= 0)
                    flags = (flags & ~0x30u) | ((uint)propType << 4);
            }

            if (p["source_cant_be_static"] != null)
            {
                if (p.Value<bool>("source_cant_be_static"))
                    flags |= 8;
                else
                    flags &= ~8u;
            }
            return flags;
        }

        private static void DecodePropertyConnectionFlags(uint flags, out string realm, out string propType, out bool sourceCantBeStatic)
        {
            int r = (int)(flags & 7);
            int pt = (int)((flags & 48) >> 4);
            sourceCantBeStatic = (flags & 8) != 0;
            switch (r)
            {
                case 0: realm = "Invalid"; break;
                case 1: realm = "ClientAndServer"; break;
                case 2: realm = "Client"; break;
                case 3: realm = "Server"; break;
                case 4: realm = "NetworkedClient"; break;
                case 5: realm = "NetworkedClientAndServer"; break;
                default: realm = r.ToString(); break;
            }
            switch (pt)
            {
                case 0: propType = "Default"; break;
                case 1: propType = "Interface"; break;
                case 2: propType = "Exposed"; break;
                case 3: propType = "Invalid"; break;
                default: propType = pt.ToString(); break;
            }
        }

        private static int ParseRealm(string s)
        {
            if (int.TryParse(s, out int n))
                return n;
            switch (s.Trim().ToLowerInvariant())
            {
                case "any": return -1;
                case "invalid": return 0;
                case "clientandserver": return 1;
                case "client": return 2;
                case "server": return 3;
                case "networkedclient": return 4;
                case "networkedclientandserver": return 5;
                default: return 1;
            }
        }

        private static int ParsePropType(string s)
        {
            if (int.TryParse(s, out int n))
                return n;
            switch (s.Trim().ToLowerInvariant())
            {
                case "default": return 0;
                case "interface": return 1;
                case "exposed": return 2;
                case "invalid": return 3;
                default: return 0;
            }
        }

        private static void SetEnumProperty(object obj, string propName, string enumName)
        {
            if (string.IsNullOrWhiteSpace(enumName))
                return;
            // Allow short form: "Client" -> "EventConnectionTargetType_Client" or "Realm_Client"
            PropertyInfo pi = obj.GetType().GetProperty(propName)
                ?? throw new InvalidOperationException("Missing property: " + propName);
            Type enumType = pi.PropertyType;
            object value;
            try
            {
                value = Enum.Parse(enumType, enumName, true);
            }
            catch
            {
                // Try with type prefix
                string prefixed = enumType.Name + "_" + enumName;
                value = Enum.Parse(enumType, prefixed, true);
            }
            pi.SetValue(obj, value);
        }

        private static string TryGetEnumString(object obj, string propName)
        {
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(propName);
                object v = pi?.GetValue(obj);
                return v?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static bool TryAddPointerToRootArray(object root, string arrayName, PointerRef pref, out int index)
        {
            index = -1;
            object listObj = GetListMember(root, arrayName);
            if (listObj is IList list)
            {
                list.Add(pref);
                index = list.Count - 1;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Appends a PointerRef to the requested array, falling back to the usual
        /// blueprint containers when the caller did not name one explicitly.
        /// Returns the array actually used, or null when nothing accepted it.
        /// </summary>
        private static bool TryInsertPointerToArray(object host, string arrayName, PointerRef pref, int atIndex)
        {
            object listObj = GetListMember(host, string.IsNullOrEmpty(arrayName) ? "Objects" : arrayName);
            if (!(listObj is IList list))
                return false;
            if (atIndex < 0) atIndex = 0;
            if (atIndex > list.Count) atIndex = list.Count;
            list.Insert(atIndex, pref);
            return true;
        }

        private static string TryAddPointerToArrays(object host, PointerRef pref, string arrayName, bool explicitName, out int addedIndex)
        {
            if (TryAddPointerToRootArray(host, arrayName, pref, out addedIndex))
                return arrayName;
            if (explicitName)
                return null;

            foreach (string fallback in new[] { "Objects", "Components", "Children" })
            {
                if (string.Equals(fallback, arrayName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryAddPointerToRootArray(host, fallback, pref, out addedIndex))
                    return fallback;
            }
            addedIndex = -1;
            return null;
        }

        private static string TryAddPointerToArrays(object host, PointerRef pref, string arrayName, bool explicitName)
        {
            return TryAddPointerToArrays(host, pref, arrayName, explicitName, out _);
        }

        /// <summary>
        /// Reads a list-typed member by name, case-insensitively, accepting both
        /// properties and fields (some Frostbite classes expose arrays as fields).
        /// </summary>
        private static object GetListMember(object host, string name)
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

        private static object GetMemberValue(object host, string name)
        {
            return GetListMember(host, name);
        }

        private static object UnwrapPointer(object o)
        {
            if (o is PointerRef pr && pr.Type == PointerRefType.Internal && pr.Internal != null)
                return pr.Internal;
            return o;
        }

        // ---------------------------------------------------------------------
        // Creation validation / auto-registration helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Position of <paramref name="target"/> in <paramref name="list"/>, or -1.
        /// A Null PointerRef or a pointer whose Internal object is gone does not
        /// match, which is how a silently dropped node is detected.
        /// </summary>
        private static int FindPointerIndex(IList list, object target)
        {
            if (list == null)
                return -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(UnwrapPointer(list[i]), target))
                    return i;
            }
            return -1;
        }

        private static bool IsNullPointerSlot(object item)
        {
            if (item == null)
                return true;
            return item is PointerRef pr && pr.Type == PointerRefType.Null;
        }

        /// <summary>
        /// Rime widgets only instantiate entities that are listed in the
        /// WidgetSpawnerItemData.Components array. An object added straight to the
        /// root Objects array exists in the file but never runs, so mirror the
        /// editor and append it to the spawner as well. Returns the host type
        /// name when registration happened.
        /// </summary>
        /// <summary>
        /// A character blueprint only runs the objects listed in
        /// PVZCharacterEntityData.Components. An object appended to the root
        /// Objects array alone is written to the file but never instantiated at
        /// runtime, which silently produces a component with no effect. Mirror the
        /// editor and append the pointer to the entity as well. Returns the host
        /// type name when registration happened.
        /// </summary>
        private static string RegisterIntoCharacterEntity(EbxAsset asset, PointerRef pref)
        {
            if (asset?.Objects == null)
                return null;

            foreach (object obj in asset.Objects)
            {
                if (obj == null || obj.GetType().Name != "PVZCharacterEntityData")
                    continue;

                object listObj = GetListMember(obj, "Components");
                if (listObj is IList list)
                {
                    foreach (object item in list)
                    {
                        if (item is PointerRef existing && ReferenceEquals(existing.Internal, pref.Internal))
                            return obj.GetType().Name;
                    }
                    list.Add(pref);
                    RepairRuntimeComponentCounts(asset, obj);
                    return obj.GetType().Name;
                }
            }
            return null;
        }

        /// <summary>
        /// The entity declares how many runtime component slots exist on each side
        /// (ClientRuntimeComponentCount / ServerRuntimeComponentCount). A component
        /// whose ClientIndex/ServerIndex lands past that count points outside the
        /// runtime array, and the character crashes as soon as it spawns. Assigning
        /// indices without widening the counts is exactly how that happens, so keep
        /// the counts at least one past the highest index in use.
        /// </summary>
        internal static void RepairRuntimeComponentCounts(EbxAsset asset, object entity)
        {
            try
            {
                int maxClient = -1;
                int maxServer = -1;

                foreach (object obj in asset.Objects)
                {
                    if (obj == null)
                        continue;

                    Type t = obj.GetType();
                    PropertyInfo ci = t.GetProperty("ClientIndex");
                    if (ci != null && ci.PropertyType == typeof(byte))
                    {
                        byte v = (byte)ci.GetValue(obj);
                        if (v != 255 && v > maxClient)
                            maxClient = v;
                    }

                    PropertyInfo si = t.GetProperty("ServerIndex");
                    if (si != null && si.PropertyType == typeof(byte))
                    {
                        byte v = (byte)si.GetValue(obj);
                        if (v != 255 && v > maxServer)
                            maxServer = v;
                    }
                }

                Type et = entity.GetType();
                PropertyInfo cc = et.GetProperty("ClientRuntimeComponentCount");
                if (cc != null && maxClient >= 0)
                {
                    int cur = Convert.ToInt32(cc.GetValue(entity));
                    if (maxClient + 1 > cur)
                        cc.SetValue(entity, Convert.ChangeType(maxClient + 1, cc.PropertyType));
                }

                PropertyInfo sc = et.GetProperty("ServerRuntimeComponentCount");
                if (sc != null && maxServer >= 0)
                {
                    int cur = Convert.ToInt32(sc.GetValue(entity));
                    if (maxServer + 1 > cur)
                        sc.SetValue(entity, Convert.ChangeType(maxServer + 1, sc.PropertyType));
                }
            }
            catch
            {
            }
        }

        private static string RegisterIntoWidgetSpawner(EbxAsset asset, PointerRef pref)
        {
            if (asset?.Objects == null)
                return null;
            foreach (object obj in asset.Objects)
            {
                if (obj == null)
                    continue;
                if (obj.GetType().Name.IndexOf("WidgetSpawnerItemData", StringComparison.Ordinal) < 0)
                    continue;
                object listObj = GetListMember(obj, "Components");
                if (listObj is IList list)
                {
                    list.Add(pref);
                    return obj.GetType().Name;
                }
            }
            return null;
        }

        private static bool IsDataBusPeer(object obj)
        {
            if (obj == null || obj.GetType().IsValueType)
                return false;
            try { return TypeLibrary.IsSubClassOf(obj, "DataBusPeer"); }
            catch { return false; }
        }

        /// <summary>
        /// Sound graph nodes must be listed in SoundGraphData.Nodes as well as in
        /// the asset's OutputNodes array. The EBX writer only keeps objects that are
        /// reachable from the root's non-transient members, and OutputNodes is
        /// derived data for the editor, so a node that only sits there is written
        /// back as a null pointer.
        /// </summary>
        private static bool IsSoundPatchAsset(EbxAsset asset)
        {
            try { return asset?.RootObject != null && asset.RootObject.GetType().Name.Contains("SoundPatchAsset"); }
            catch { return false; }
        }

        /// <summary>
        /// A node that carries SoundGraphPluginRef members needs its own voice and
        /// plugin instances in SoundGraphInfo. This writer only appends the node to
        /// Graph.Nodes, so such a node would share another voice's plugins at
        /// runtime while the call still reported success. Detect it up front.
        /// </summary>
        private static bool HasValidSoundGraphPluginRefs(object value)
        {
            return HasValidSoundGraphPluginRefs(value, 0);
        }

        private static bool HasValidSoundGraphPluginRefs(object value, int depth)
        {
            if (value == null || depth > 4)
                return false;

            Type t = value.GetType();
            if (t.Name == "SoundGraphPluginRef")
            {
                PropertyInfo valid = t.GetProperty("IsValid");
                return valid != null && valid.GetValue(value) is bool b && b;
            }

            if (value is IList list)
            {
                int n = Math.Min(list.Count, 64);
                for (int i = 0; i < n; i++)
                    if (HasValidSoundGraphPluginRefs(list[i], depth + 1))
                        return true;
                return false;
            }

            if (value is PointerRef || value is string || value is Guid || value is CString || t.IsPrimitive || t.IsEnum)
                return false;

            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                    continue;
                object v;
                try { v = pi.GetValue(value); }
                catch { continue; }
                if (HasValidSoundGraphPluginRefs(v, depth + 1))
                    return true;
            }
            return false;
        }

        private static string RegisterIntoSoundGraph(EbxAsset asset, PointerRef pref)
        {
            try
            {
                dynamic root = asset.RootObject;
                if (root == null || !root.GetType().Name.Contains("SoundPatchAsset"))
                    return null;
                // Graph is a PointerRef holding the SoundGraphData object.
                object graphObj = UnwrapPointer((object)root.Graph);
                if (graphObj == null)
                    return null;
                object nodesObj = GetListMember(graphObj, "Nodes");
                if (!(nodesObj is IList nodes))
                    return null;
                object target = pref.Internal;
                if (target != null && FindPointerIndex(nodes, target) < 0)
                    nodes.Add(new PointerRef(target));
                return "Graph.Nodes";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The blueprint editor recomputes ClientEvent/ClientProperty (and the
        /// server bits) on both endpoints when a connection is added. Doing the
        /// same here is what makes a freshly created entity evaluate at runtime.
        /// </summary>
        private static void SetConnectionEndpointFlag(PointerRef endpoint, bool isEvent, bool client, bool server)
        {
            object obj = endpoint.Internal;
            if (!IsDataBusPeer(obj))
                return;
            uint flags = GetObjectFlagsRaw(obj);
            if (client)
                flags |= isEvent ? ClientEventFlag : ClientPropertyFlag;
            if (server)
                flags |= isEvent ? ServerEventFlag : ServerPropertyFlag;
            SetObjectFlagsRaw(obj, flags);
        }

        private static Guid TryGetClassGuid(object obj)
        {
            try
            {
                if (obj == null || obj is PointerRef || obj.GetType().IsValueType)
                    return Guid.Empty;
                return GetClassGuid(obj);
            }
            catch
            {
                return Guid.Empty;
            }
        }

        private static int CountList(object host, string name)
        {
            return GetListMember(host, name) is IList list ? list.Count : 0;
        }

        private static bool TryResolveListFromPath(object start, string path, int fallbackIndex, out IList list, out string arrayPath, out int index, out object host)
        {
            list = null;
            arrayPath = path;
            index = fallbackIndex;
            host = start;
            if (start == null || string.IsNullOrWhiteSpace(path))
                return false;

            string[] parts = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            object current = UnwrapPointer(start);
            for (int p = 0; p < parts.Length; p++)
            {
                string part = parts[p];
                int bracket = part.IndexOf('[');
                string name = bracket >= 0 ? part.Substring(0, bracket) : part;
                int partIndex = -1;
                if (bracket >= 0)
                {
                    int close = part.IndexOf(']', bracket);
                    if (close > bracket)
                        int.TryParse(part.Substring(bracket + 1, close - bracket - 1), out partIndex);
                }

                object member = GetMemberValue(current, name);
                if (member == null)
                    return false;

                bool last = p == parts.Length - 1;
                if (last)
                {
                    object maybeList = member is PointerRef ? UnwrapPointer(member) : member;
                    if (maybeList is IList l)
                    {
                        list = l;
                        host = current;
                        arrayPath = name;
                        if (partIndex >= 0)
                            index = partIndex;
                        else if (fallbackIndex >= 0)
                            index = fallbackIndex;
                        return true;
                    }
                    return false;
                }

                if (member is IList mid)
                {
                    int i = partIndex >= 0 ? partIndex : 0;
                    if (i < 0 || i >= mid.Count)
                        return false;
                    current = UnwrapPointer(mid[i]);
                }
                else
                {
                    current = UnwrapPointer(member);
                }
                if (current == null)
                    return false;
            }
            return false;
        }

        /// <summary>
        /// Lists members that can accept a PointerRef, so callers can see why an
        /// add failed and which array name to use instead.
        /// </summary>
        private static List<string> ListPointerArrayNames(object host)
        {
            var names = new List<string>();
            if (host == null)
                return names;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            foreach (PropertyInfo pi in host.GetType().GetProperties(flags))
            {
                if (pi.GetIndexParameters().Length > 0)
                    continue;
                try
                {
                    if (pi.GetValue(host) is IList)
                        names.Add(pi.Name);
                }
                catch { }
            }
            foreach (FieldInfo fi in host.GetType().GetFields(flags))
            {
                try
                {
                    if (fi.GetValue(host) is IList)
                        names.Add(fi.Name);
                }
                catch { }
            }
            return names;
        }

        private static void RemovePointerFromArray(object root, string arrayName, object target, bool guidUnique)
        {
            PropertyInfo pi = root.GetType().GetProperty(arrayName);
            if (pi == null)
                return;
            if (!(pi.GetValue(root) is IList list))
                return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is PointerRef pr && pr.Type == PointerRefType.Internal && pr.Internal != null)
                {
                    if (PointerRefersTo(pr.Internal, target, guidUnique))
                        list.RemoveAt(i);
                }
            }
        }

        private static bool PointerRefersTo(object candidate, object target, bool guidUnique)
        {
            if (ReferenceEquals(candidate, target))
                return true;
            return guidUnique && candidate != null && target != null &&
                   GetClassGuid(candidate) == GetClassGuid(target);
        }

        // Arrays other than the root Components/Objects can still point at the
        // removed object (children lists, nested member arrays). Clear those too.
        // Returns the property paths that were cleared, so the caller can see what
        // the removal touched instead of only getting a success flag.
        private static List<string> RemoveReferencesToObject(EbxAsset asset, object target, bool guidUnique)
        {
            var cleared = new List<string>();
            foreach (object host in asset.Objects.ToList())
            {
                if (host == null || ReferenceEquals(host, target))
                    continue;
                foreach (PropertyInfo pi in host.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length != 0)
                        continue;
                    object value;
                    try { value = pi.GetValue(host); }
                    catch { continue; }
                    if (value == null)
                        continue;

                    string path = host.GetType().Name + "." + pi.Name;

                    if (value is PointerRef pr)
                    {
                        if (PointerRefersTo(pr, target, guidUnique))
                        {
                            try
                            {
                                if (pi.CanWrite)
                                {
                                    pi.SetValue(host, new PointerRef());
                                    cleared.Add(path);
                                }
                            }
                            catch { }
                        }
                        continue;
                    }

                    if (value is IList list && !(value is string))
                    {
                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            if (list[i] is PointerRef itemRef && PointerRefersTo(itemRef, target, guidUnique))
                            {
                                try
                                {
                                    list.RemoveAt(i);
                                    cleared.Add(path + "[" + i + "]");
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            return cleared;
        }

        private static bool PointerRefersTo(PointerRef pr, object target, bool guidUnique)
        {
            if (pr.Type == PointerRefType.Null)
                return false;
            if (pr.Type == PointerRefType.Internal)
            {
                if (ReferenceEquals(pr.Internal, target))
                    return true;
                return guidUnique && pr.Internal != null && GetClassGuid(pr.Internal) == GetClassGuid(target);
            }
            return guidUnique && pr.External.ClassGuid != Guid.Empty && pr.External.ClassGuid == GetClassGuid(target);
        }

        private static void RemoveConnectionsTouchingObject(dynamic root, object target, bool guidUnique)
        {
            RemoveConnectionsTouchingKindObject(root, target, guidUnique, "property");
            RemoveConnectionsTouchingKindObject(root, target, guidUnique, "event");
            RemoveConnectionsTouchingKindObject(root, target, guidUnique, "link");
        }

        private static void RemoveConnectionsTouchingKindObject(dynamic root, object target, bool guidUnique, string kind)
        {
            try
            {
                if (kind == "property")
                {
                    for (int i = root.PropertyConnections.Count - 1; i >= 0; i--)
                    {
                        dynamic c = root.PropertyConnections[i];
                        if (PointerTouchesObject(c.Source, target, guidUnique) || PointerTouchesObject(c.Target, target, guidUnique))
                            root.PropertyConnections.RemoveAt(i);
                    }
                }
                else if (kind == "event")
                {
                    for (int i = root.EventConnections.Count - 1; i >= 0; i--)
                    {
                        dynamic c = root.EventConnections[i];
                        if (PointerTouchesObject(c.Source, target, guidUnique) || PointerTouchesObject(c.Target, target, guidUnique))
                            root.EventConnections.RemoveAt(i);
                    }
                }
                else if (kind == "link")
                {
                    for (int i = root.LinkConnections.Count - 1; i >= 0; i--)
                    {
                        dynamic c = root.LinkConnections[i];
                        if (PointerTouchesObject(c.Source, target, guidUnique) || PointerTouchesObject(c.Target, target, guidUnique))
                            root.LinkConnections.RemoveAt(i);
                    }
                }
            }
            catch { }
        }

        private static int CountConnectionsTouchingKind(dynamic root, Guid classGuid, string kind)
        {
            int count = 0;
            if (kind == "all" || kind == "property")
                for (int i = 0; i < root.PropertyConnections.Count; i++)
                {
                    dynamic c = root.PropertyConnections[i];
                    if (PointerTouches(c.Source, classGuid) || PointerTouches(c.Target, classGuid))
                        count++;
                }
            if (kind == "all" || kind == "event")
                for (int i = 0; i < root.EventConnections.Count; i++)
                {
                    dynamic c = root.EventConnections[i];
                    if (PointerTouches(c.Source, classGuid) || PointerTouches(c.Target, classGuid))
                        count++;
                }
            if (kind == "all" || kind == "link")
                for (int i = 0; i < root.LinkConnections.Count; i++)
                {
                    dynamic c = root.LinkConnections[i];
                    if (PointerTouches(c.Source, classGuid) || PointerTouches(c.Target, classGuid))
                        count++;
                }
            return count;
        }

        private static void RemoveConnectionsTouchingKind(dynamic root, Guid classGuid, string kind)
        {
            try
            {
                if (kind == "property")
                {
                    for (int i = root.PropertyConnections.Count - 1; i >= 0; i--)
                    {
                        dynamic c = root.PropertyConnections[i];
                        if (PointerTouches(c.Source, classGuid) || PointerTouches(c.Target, classGuid))
                            root.PropertyConnections.RemoveAt(i);
                    }
                }
                else if (kind == "event")
                {
                    for (int i = root.EventConnections.Count - 1; i >= 0; i--)
                    {
                        dynamic c = root.EventConnections[i];
                        if (PointerTouches(c.Source, classGuid) || PointerTouches(c.Target, classGuid))
                            root.EventConnections.RemoveAt(i);
                    }
                }
                else if (kind == "link")
                {
                    for (int i = root.LinkConnections.Count - 1; i >= 0; i--)
                    {
                        dynamic c = root.LinkConnections[i];
                        if (PointerTouches(c.Source, classGuid) || PointerTouches(c.Target, classGuid))
                            root.LinkConnections.RemoveAt(i);
                    }
                }
            }
            catch { }
        }

        private static dynamic TryGetInterfaceInternal(EbxAsset asset, bool createIfMissing)
        {
            dynamic root = asset.RootObject;
            PropertyInfo ifaceProp = ((object)root).GetType().GetProperty("Interface");
            if (ifaceProp == null)
                throw new InvalidOperationException("Root object has no Interface property");

            PointerRef pref = (PointerRef)ifaceProp.GetValue(root);
            if (pref.Type == PointerRefType.Internal && pref.Internal != null)
                return pref.Internal;

            if (!createIfMissing)
                return null;

            // Create InterfaceDescriptorData (BF1DevPlugin GetInterfaceInternal)
            Type t = TypeLibrary.GetType("InterfaceDescriptorData");
            if (t == null)
                throw new InvalidOperationException("InterfaceDescriptorData type not found in SDK");

            object newObject = TypeLibrary.CreateObject("InterfaceDescriptorData");
            AssetClassGuid classGuid = new AssetClassGuid(
                Utils.GenerateDeterministicGuid(asset.Objects, "InterfaceDescriptorData", asset.FileGuid), -1);
            ((dynamic)newObject).SetInstanceGuid(classGuid);
            asset.AddObject(newObject);
            MakeCreatedObjectInternal(newObject);
            AssignComponentIndices(asset, newObject);
            ifaceProp.SetValue(root, new PointerRef(newObject));
            return newObject;
        }

        private static int ResolveInterfaceFieldIndex(dynamic iface, JObject p)
        {
            int index = p.Value<int?>("index") ?? -1;
            if (index >= 0)
                return index < iface.Fields.Count ? index : -1;

            string fieldName = (p.Value<string>("field_name") ?? p.Value<string>("lookup_name") ?? "").Trim();
            if (string.IsNullOrEmpty(fieldName))
                return -1;

            for (int i = 0; i < iface.Fields.Count; i++)
            {
                string n = "";
                try { n = (string)(CString)iface.Fields[i].Name; } catch { }
                if (string.Equals(n, fieldName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static List<object> SerializeInterfaceFields(dynamic iface)
        {
            var list = new List<object>();
            try
            {
                for (int i = 0; i < iface.Fields.Count; i++)
                    list.Add(SerializeDataField(iface.Fields[i], i));
            }
            catch { }
            return list;
        }

        private static Dictionary<string, object> SerializeDataField(dynamic field, int index)
        {
            string name = "";
            string value = "";
            string valueType = null;
            string valueRef = null;
            string access = null;
            try { name = (string)(CString)field.Name; } catch { }
            try { value = (string)(CString)field.Value; } catch { }
            try { valueType = field.ValueType?.TypeInfo?.ToString(); } catch { }
            try
            {
                PointerRef pr = (PointerRef)field.ValueRef;
                if (pr != null && pr.Type == PointerRefType.External)
                {
                    EbxAssetEntry target = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    valueRef = target != null ? target.Name : pr.External.FileGuid.ToString();
                }
            }
            catch { }
            try { access = field.AccessType?.ToString(); } catch { }
            return new Dictionary<string, object>
            {
                ["index"] = index,
                ["name"] = name,
                ["value"] = value,
                ["value_type"] = valueType,
                ["value_ref"] = valueRef,
                ["access_type"] = access
            };
        }

        private static List<object> SerializeDynamicEvents(dynamic events)
        {
            var list = new List<object>();
            try
            {
                for (int i = 0; i < events.Count; i++)
                {
                    string n = "";
                    try { n = (string)(CString)events[i].Name; } catch { }
                    list.Add(new Dictionary<string, object> { ["index"] = i, ["name"] = n });
                }
            }
            catch { }
            return list;
        }

        private static bool PointerTouches(PointerRef pr, Guid classGuid)
        {
            if (pr.Type == PointerRefType.Internal && pr.Internal != null)
                return GetClassGuid(pr.Internal) == classGuid;
            if (pr.Type == PointerRefType.External)
                return pr.External.ClassGuid == classGuid;
            return false;
        }

        private static bool PointerTouchesObject(PointerRef pr, object target, bool guidUnique)
        {
            if (pr.Type == PointerRefType.Internal && pr.Internal != null)
                return PointerRefersTo(pr.Internal, target, guidUnique);
            if (pr.Type == PointerRefType.External && target != null)
                return guidUnique && pr.External.ClassGuid == GetClassGuid(target);
            return false;
        }

        private static Dictionary<string, object> DumpPublicProperties(object obj)
        {
            var dump = new Dictionary<string, object>();
            foreach (PropertyInfo pi in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                    continue;
                try
                {
                    object v = pi.GetValue(obj);
                    dump[pi.Name] = SerializeAny(v);
                }
                catch
                {
                    dump[pi.Name] = "<error>";
                }
            }
            return dump;
        }

        private static object SerializeAny(object value)
        {
            if (value == null)
                return null;
            if (value is CString cs)
                return (string)cs;
            // A BoxedValueRef wraps the real value (CString, enum, struct, array).
            // The old fallback returned value.ToString(), which only named the boxed
            // type, so an interface CString field read back as an empty-looking
            // description instead of the stored string.
            if (value is FrostySdk.Ebx.BoxedValueRef boxed)
            {
                return new Dictionary<string, object>
                {
                    ["boxed_type"] = boxed.Type.ToString(),
                    ["array_type"] = boxed.ArrayType.ToString(),
                    ["value"] = SerializeAny(boxed.Value)
                };
            }
            if (value is PointerRef pr)
                return SerializePointerRef(pr);
            if (value is Guid g)
                return g.ToString();
            if (value is Enum)
                return value.ToString();
            if (value is string || value.GetType().IsPrimitive)
                return value;

            if (value is IList list && !(value is string))
            {
                var items = new List<object>();
                int n = Math.Min(list.Count, 50);
                for (int i = 0; i < n; i++)
                    items.Add(SerializeAny(list[i]));
                return new Dictionary<string, object>
                {
                    ["count"] = list.Count,
                    ["truncated"] = list.Count > 50,
                    ["items"] = items
                };
            }

            Type t = value.GetType();
            if (t.IsValueType)
            {
                var d = new Dictionary<string, object>();
                foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                        continue;
                    try
                    {
                        object v = pi.GetValue(value);
                        if (v == null || v is string || v.GetType().IsPrimitive || v is Enum || v is Guid || v is CString || v is PointerRef)
                            d[pi.Name] = SerializeAny(v);
                    }
                    catch { }
                }
                if (d.Count > 0)
                    return d;
            }

            return value.ToString();
        }

        // Minimal path get/set for component properties (mirrors McpEditHandlers)
        /// <summary>
        /// Some Frostbite roots expose the object list as Objects while others use
        /// Components (and some tools use one name for both). Fall back to the other
        /// name instead of failing with "Missing property".
        /// </summary>
        private static PropertyInfo GetPropertyAliased(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            PropertyInfo pi = type.GetProperty(name, flags);
            if (pi != null)
                return pi;
            if (string.Equals(name, "Components", StringComparison.OrdinalIgnoreCase))
                return type.GetProperty("Objects", flags);
            if (string.Equals(name, "Objects", StringComparison.OrdinalIgnoreCase))
                return type.GetProperty("Components", flags);
            return null;
        }

        /// <summary>
        /// True when the type is one of the EBX-generated data types, recognised
        /// by the field metadata attribute its members carry.
        /// </summary>
        private static bool IsEbxDataType(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            foreach (PropertyInfo pi in type.GetProperties(flags))
                if (pi.GetCustomAttribute<EbxFieldMetaAttribute>() != null)
                    return true;
            foreach (FieldInfo fi in type.GetFields(flags))
                if (fi.GetCustomAttribute<EbxFieldMetaAttribute>() != null)
                    return true;
            return false;
        }

        /// <summary>
        /// Refuses a name the target type cannot write. Without this an unknown
        /// key is dropped and the field is overwritten with a default instance,
        /// while the call still reports success: writing {"guid": "..."} at an
        /// AntRef (whose member is AssetGuid) zeroed the animation binding and
        /// crashed the character. Never swallow the lookup failure.
        /// </summary>
        private static void EnsureMembersWritable(Type type, JObject obj, string where)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            List<string> unknown = new List<string>();
            foreach (JProperty prop in obj.Properties())
            {
                PropertyInfo pi = GetPropertyAliased(type, prop.Name);
                if (pi != null && pi.CanWrite)
                    continue;
                if (pi == null && type.GetField(prop.Name, flags) == null)
                    unknown.Add(prop.Name);
                else if (pi != null)
                    unknown.Add(prop.Name + " (read-only)");
            }
            if (unknown.Count == 0)
                return;

            List<string> writable = new List<string>();
            foreach (PropertyInfo pi in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (pi.CanWrite)
                    writable.Add(pi.Name);
            throw new InvalidOperationException(
                "UNKNOWN_MEMBER: " + where + " has no field '" + string.Join("', '", unknown) +
                "'. Writable fields: " + string.Join(", ", writable));
        }

        private static object ResolvePath(object root, string path, out string typeName)
        {
            object current = root;
            foreach (string part in path.Split('.'))
            {
                if (current == null)
                    throw new InvalidOperationException("Null at " + part);

                string name = part;
                int idx = -1;
                int b = part.IndexOf('[');
                if (b >= 0 && part.EndsWith("]"))
                {
                    name = part.Substring(0, b);
                    idx = int.Parse(part.Substring(b + 1, part.Length - b - 2));
                }

                if (!string.IsNullOrEmpty(name))
                {
                    PropertyInfo pi = GetPropertyAliased(current.GetType(), name)
                        ?? throw new InvalidOperationException("Missing property: " + name);
                    current = pi.GetValue(current);
                }

                if (idx >= 0)
                {
                    if (!(current is IList list))
                        throw new InvalidOperationException("Not a list: " + name);
                    current = list[idx];
                }
            }
            typeName = current?.GetType().Name ?? "null";
            return current;
        }

        private static void SetPath(object root, string path, JToken value, EbxAsset asset)
        {
            string[] parts = path.Split('.');
            object current = root;
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
                {
                    PropertyInfo pi = GetPropertyAliased(current.GetType(), name)
                        ?? throw new InvalidOperationException("Missing property: " + name);
                    current = pi.GetValue(current);
                    if (current == null)
                        throw new InvalidOperationException("Null at path part: " + name);
                }
                if (idx >= 0)
                    current = ((IList)current)[idx];
            }

            string last = parts[parts.Length - 1];
            int lastIdx = -1;
            string lastName = last;
            int lb = last.IndexOf('[');
            if (lb >= 0 && last.EndsWith("]"))
            {
                lastName = last.Substring(0, lb);
                lastIdx = int.Parse(last.Substring(lb + 1, last.Length - lb - 2));
            }

            if (lastIdx >= 0)
            {
                PropertyInfo listProperty = string.IsNullOrEmpty(lastName)
                    ? null
                    : GetPropertyAliased(current.GetType(), lastName);
                object listObj = listProperty != null ? listProperty.GetValue(current) : current;
                IList list = (IList)listObj;
                // A null slot (or a null seed element) has no runtime type to
                // borrow, so fall back to the declared element type.
                Type elemType = list[lastIdx]?.GetType()
                    ?? (list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(object));
                Type pointerBaseType = listProperty?.GetCustomAttribute<EbxFieldMetaAttribute>()?.BaseType;
                list[lastIdx] = ConvertToken(value, elemType, asset, pointerBaseType);
            }
            else
            {
                SetMemberValue(current, lastName, value, asset);
            }
        }

        private static void SetMemberValue(object obj, string name, JToken value, EbxAsset asset = null)
        {
            PropertyInfo pi = GetPropertyAliased(obj.GetType(), name)
                ?? throw new InvalidOperationException("Missing property: " + name);
            if (!pi.CanWrite)
                throw new InvalidOperationException("Property is read-only: " + name);
            // Pointer fields declare their target class through EbxFieldMeta.
            // Passing it down lets an asset-only PointerRef resolve the right
            // internal object instead of silently falling back to the asset root.
            Type pointerBaseType = pi.GetCustomAttribute<EbxFieldMetaAttribute>()?.BaseType;
            pi.SetValue(obj, ConvertToken(value, pi.PropertyType, asset, pointerBaseType));
        }

        private static object ConvertToken(JToken token, Type targetType, EbxAsset asset, Type pointerBaseType = null)
        {
            if (token == null || token.Type == JTokenType.Null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType == typeof(CString) || targetType.Name == "CString")
                return (CString)token.ToString().Trim('"');

            if (targetType == typeof(PointerRef) || targetType.Name == "PointerRef")
            {
                if (token is JObject jo)
                    return ResolvePointerRef(asset, jo, "", pointerBaseType);
                if (token.Type == JTokenType.String)
                {
                    string s = token.Value<string>();
                    if (Guid.TryParse(s, out Guid g) && asset != null)
                    {
                        object o = FindObjectByClassGuid(asset, g);
                        if (o != null)
                            return new PointerRef(o);
                    }
                    return ResolvePointerRef(asset, new JObject { ["asset"] = s }, "", pointerBaseType);
                }
            }

            if (targetType.IsEnum)
            {
                try { return Enum.Parse(targetType, token.ToString(), true); }
                catch { return Enum.Parse(targetType, targetType.Name + "_" + token.ToString(), true); }
            }

            if (targetType == typeof(Guid))
                return Guid.Parse(token.ToString());

            Type u = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (u == typeof(bool)) return token.Type == JTokenType.Boolean ? token.Value<bool>() : Convert.ToBoolean(token.ToString());
            if (u == typeof(float)) return Convert.ToSingle(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            if (u == typeof(double)) return Convert.ToDouble(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            if (u == typeof(int))
            {
                if (token.Type == JTokenType.String && TryGetHexDigits(token.Value<string>(), out string hex))
                    return unchecked((int)uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
                return Convert.ToInt32(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (u == typeof(uint))
            {
                if (token.Type == JTokenType.String && TryGetHexDigits(token.Value<string>(), out string hex))
                    return uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                return Convert.ToUInt32(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (u == typeof(long))
            {
                if (token.Type == JTokenType.String && TryGetHexDigits(token.Value<string>(), out string hex))
                    return unchecked((long)ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
                return Convert.ToInt64(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (u == typeof(ulong))
            {
                if (token.Type == JTokenType.String && TryGetHexDigits(token.Value<string>(), out string hex))
                    return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                return Convert.ToUInt64(token.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (u == typeof(string)) return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();

            // TypeInfoRef carries { Asset, TypeInfo }. The default conversion of
            // {"type": "..."} fills Asset and leaves TypeInfo null, which makes a
            // field with no type at all; the mod then fails to export with a bare
            // null reference that names no asset. Build it explicitly and refuse
            // to hand back a typeless result.
            if (targetType.Name == "TypeInfoRef")
            {
                object info = Activator.CreateInstance(targetType);
                if (token is JObject typeInfoObject)
                {
                    JToken assetToken = typeInfoObject.Property("Asset")?.Value;
                    if (assetToken != null && assetToken.Type != JTokenType.Null)
                        SetMemberValue(info, "Asset", assetToken, asset);
                    JToken typeToken = typeInfoObject.Property("TypeInfo")?.Value
                        ?? typeInfoObject.Property("type")?.Value;
                    if (typeToken != null && typeToken.Type != JTokenType.Null)
                        SetMemberValue(info, "TypeInfo", typeToken, asset);
                }
                else
                {
                    SetMemberValue(info, "TypeInfo", token, asset);
                }
                if (((dynamic)info).TypeInfo == null)
                    throw new InvalidOperationException(
                        "TypeInfoRef requires a type; pass {\"type\": \"TextureAsset\"} or \"TypeRef 'TextureAsset'\"");
                return info;
            }

            if (targetType == typeof(TypeRef) || targetType.Name == "TypeRef")
            {
                string s = token.ToString().Trim();
                if (s.StartsWith("TypeRef '") && s.EndsWith("'") && s.Length >= 10)
                    s = s.Substring(9, s.Length - 10);
                // The writer stores whichever form the TypeRef carries: the type
                // GUID as a string, or the bare type name. Vanilla fields carry
                // the GUID, and the game resolves only that form; writing the
                // name leaves a TypeRef it cannot resolve and the loader dies on
                // a null type descriptor. Look the name up instead.
                Guid typeGuid = Guid.TryParse(s, out Guid parsedGuid)
                    ? parsedGuid
                    : TypeLibrary.Reflection.LookupGuid(s);
                return typeGuid != Guid.Empty ? new TypeRef(typeGuid) : new TypeRef(s);
            }

            if (token is JArray jarr && typeof(IList).IsAssignableFrom(targetType))
            {
                // Assembling the list element by element is the only way a
                // List<PointerRef> keeps its pointers: the default Newtonsoft
                // conversion turns every JObject into a null element.
                Type elemType = targetType.IsGenericType
                    ? targetType.GetGenericArguments()[0]
                    : typeof(object);
                IList built = targetType.IsGenericType
                    ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType))
                    : (IList)new System.Collections.ArrayList();
                foreach (JToken item in jarr)
                    built.Add(ConvertToken(item, elemType, asset, pointerBaseType));
                return built;
            }

            if (token is JObject jobj && (u.IsValueType || IsEbxDataType(u)))
            {
                EnsureMembersWritable(u, jobj, u.Name);
                object inst = Activator.CreateInstance(u);
                foreach (JProperty prop in jobj.Properties())
                    SetMemberValue(inst, prop.Name, prop.Value, asset);
                return inst;
            }

            return token.ToObject(targetType);
        }

        /// <summary>
        /// Accepts 0x1234 and $1234 for integer fields. Frosty hashes are usually
        /// written in hex, so forcing callers to convert to decimal is error prone.
        /// </summary>
        private static bool TryGetHexDigits(string text, out string digits)
        {
            digits = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (text.StartsWith("-"))
                return false;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                digits = text.Substring(2);
            else if (text.StartsWith("$"))
                digits = text.Substring(1);
            else
                return false;
            if (digits.Length == 0 || digits.Length > 16)
                return false;
            foreach (char c in digits)
            {
                if (!Uri.IsHexDigit(c))
                    return false;
            }
            return true;
        }

        // =====================================================================
        // Search / get / set fields inside components ("词条")
        // =====================================================================

        public static object SearchEbxFields(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string query = (p.Value<string>("query") ?? "").Trim();
            if (string.IsNullOrEmpty(query))
                return McpHandlers.Error("query is required", "INVALID_PARAMS");

            string scope = (p.Value<string>("scope") ?? "both").Trim().ToLowerInvariant(); // name | value | both
            bool caseInsensitive = p.Value<bool?>("case_insensitive") ?? true;
            string typeFilter = (p.Value<string>("type") ?? "").Trim();
            string classGuidFilter = (p.Value<string>("class_guid") ?? "").Trim();
            int maxDepth = p.Value<int?>("max_depth") ?? 8;
            if (maxDepth < 1) maxDepth = 1;
            if (maxDepth > 20) maxDepth = 20;
            int limit = McpHandlers.ClampLimit(p.Value<int?>("limit") ?? 200);
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            bool includeConnections = p.Value<bool?>("include_connections") ?? true;
            bool compact = p.Value<bool?>("compact") ?? true;
            int scanMs = p.Value<int?>("max_scan_ms") ?? 20000;
            if (scanMs < 1000) scanMs = 1000;
            if (scanMs > 60000) scanMs = 60000;
            int maxNodes = p.Value<int?>("max_scan_nodes") ?? 500000;
            if (maxNodes < 1000) maxNodes = 1000;
            ScanBudget budget = new ScanBudget(scanMs, maxNodes);

            StringComparison cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var matches = new List<object>();
            int total = 0;

            int objIndex = 0;
            foreach (object obj in asset.Objects)
            {
                if (budget.Stop())
                    break;
                budget.Node();
                string typeName = obj.GetType().Name;
                if (!string.IsNullOrEmpty(typeFilter) &&
                    !typeName.Equals(typeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !TypeLibrary.IsSubClassOf(typeName, typeFilter))
                {
                    objIndex++;
                    continue;
                }

                Guid cg = GetClassGuid(obj);
                if (!string.IsNullOrEmpty(classGuidFilter) &&
                    !cg.ToString().Equals(classGuidFilter, StringComparison.OrdinalIgnoreCase))
                {
                    objIndex++;
                    continue;
                }

                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                WalkSearch(obj, "", objIndex, cg, typeName, query, scope, cmp, maxDepth, 0, visited, budget, matches, ref total, limit + offset);
                objIndex++;
            }

            // Interface fields live in InterfaceDescriptorData.Fields, which the
            // property walk never reaches: each entry is a DataField whose Name is
            // the interface field name. Without this pass search_ebx_fields reports
            // total:0 for a name that list_interface plainly shows.
            if (!budget.Expired)
                SearchInterfaceFields(asset, typeFilter, query, scope, cmp, budget, matches, ref total, limit + offset);

            // total counts every match; matches only keeps the first limit+offset for paging.
            if (includeConnections && !budget.Expired)
                SearchConnections(asset, query, scope, cmp, budget, matches, ref total, limit + offset);

            List<object> page = matches.Skip(offset).Take(limit).ToList();
            if (compact)
            {
                // object_type and class_guid repeat for every hit in the same object;
                // object_index is enough to look the component up again.
                foreach (object item in page)
                {
                    if (item is Dictionary<string, object> row)
                    {
                        row.Remove("object_type");
                        row.Remove("class_guid");
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["query"] = query,
                ["scope"] = scope,
                ["total"] = total,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["compact"] = compact,
                ["scan_truncated"] = budget.Expired,
                ["scan_ms"] = scanMs,
                ["scan_nodes"] = budget.Nodes,
                ["note"] = budget.Expired
                    ? "SCAN INCOMPLETE: the scan budget expired; matches are only from the objects/properties visited before the deadline."
                    : null,
                ["matches"] = page
            };
        }

        public static object GetEbxField(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required (from search_ebx_fields)", "INVALID_PARAMS");

            // Connection synthetic paths: $PropertyConnections[0].SourceField
            if (path.StartsWith("$"))
            {
                try
                {
                    object value = ResolveConnectionPath(asset.RootObject, path.Substring(1), out string typeName);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["path"] = path,
                        ["value_type"] = typeName,
                        ["value"] = SerializeAny(value)
                    };
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "GET_FAILED");
                }
            }

            object comp = ResolveComponent(asset, p, out int index);
            // Only fall back to the root when the caller named no component at all;
            // a misspelled component_type must not silently read the root object.
            if (comp == null && p["index"] == null && string.IsNullOrEmpty(p.Value<string>("class_guid"))
                && string.IsNullOrEmpty(p.Value<string>("component_type")))
            {
                // allow path-only relative to root
                comp = asset.RootObject;
                index = 0;
            }
            if (comp == null)
                return McpHandlers.Error("Component not found (provide class_guid, index or component_type)", "NOT_FOUND");

            try
            {
                object value = ResolvePath(comp, path, out string typeName);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["object_index"] = index,
                    ["class_guid"] = GetClassGuid(comp).ToString(),
                    ["object_type"] = comp.GetType().Name,
                    ["path"] = path,
                    ["value_type"] = typeName,
                    ["value"] = SerializeAny(value)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "GET_FAILED");
            }
        }

        public static object SetEbxField(JObject p)
        {
            if (!TryLoadBlueprint(p, out EbxAssetEntry entry, out EbxAsset asset, out object err))
                return err;

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");
            if (!p.ContainsKey("value"))
                return McpHandlers.Error("value is required", "INVALID_PARAMS");

            try
            {
                if (path.StartsWith("$"))
                {
                    SetConnectionPath(asset.RootObject, path.Substring(1), p["value"], asset);
                    SaveAsset(entry, asset);
                    object value = ResolveConnectionPath(asset.RootObject, path.Substring(1), out string typeName);
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["path"] = path,
                        ["value_type"] = typeName,
                        ["value"] = SerializeAny(value)
                    };
                }

                object comp = ResolveComponent(asset, p, out int index);
                if (comp == null && p["index"] == null && string.IsNullOrEmpty(p.Value<string>("class_guid")) && string.IsNullOrEmpty(p.Value<string>("component_type")))
                {
                    comp = asset.RootObject;
                    index = 0;
                }
                if (comp == null)
                    return McpHandlers.Error("Component not found (provide class_guid or index)", "NOT_FOUND");

                // AudioGraphNodePort: writing UnconnectedValue while the port is
                // connected has no runtime effect. Report that instead of a bare
                // success, and let disconnect_first=true detach it in the same call.
                string portWarning = null;
                if (path.EndsWith(".UnconnectedValue", StringComparison.OrdinalIgnoreCase))
                {
                    string portPath = path.Substring(0, path.Length - ".UnconnectedValue".Length);
                    bool portConnected = false;
                    try
                    {
                        portConnected = ResolvePath(comp, portPath + ".IsConnected", out _) is bool connectedFlag && connectedFlag;
                    }
                    catch { }

                    if ((p.Value<bool?>("disconnect_first") ?? false) && portConnected)
                    {
                        SetPath(comp, portPath + ".IsConnected", new JValue(false), asset);
                        portConnected = false;
                    }
                    if (portConnected)
                        portWarning = "Port '" + portPath + "' is connected (IsConnected=true); UnconnectedValue has no runtime effect. Set '" + portPath + ".IsConnected' = false first, or pass disconnect_first=true.";
                }

                SetPath(comp, path, p["value"], asset);
                // A write into a Components array can replace the whole component set;
                // renumber and rewrite the runtime counters before the save so the file
                // never lands with counts that describe the previous shape.
                string indexSides = PathTargetsComponentList(path)
                    ? NormalizeComponentSlots(asset, comp)
                    : null;
                SaveAsset(entry, asset);

                // The write is already saved at this point. Reading it back
                // round-trips through reflection and throws for some pointer and
                // type fields, so a failure here must not be reported as a failed
                // write: callers retry, rewrite, and end up corrupting the field.
                string vt = null;
                object newValue = null;
                string readbackError = null;
                try
                {
                    newValue = ResolvePath(comp, path, out vt);
                }
                catch (Exception readback)
                {
                    readbackError = readback.Message;
                }

                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["object_index"] = index,
                    ["class_guid"] = GetClassGuid(comp).ToString(),
                    ["object_type"] = comp.GetType().Name,
                    ["path"] = path,
                    ["applied"] = true,
                    ["value_type"] = vt,
                    ["value"] = readbackError == null ? SerializeAny(newValue) : null
                };
                if (readbackError != null)
                    result["readback_error"] = readbackError;
                if (indexSides != null)
                    result["index_sides"] = indexSides;
                if (portWarning != null)
                    result["warning"] = portWarning;
                return result;
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FAILED");
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static void WalkSearch(
            object obj,
            string path,
            int objectIndex,
            Guid classGuid,
            string objectType,
            string query,
            string scope,
            StringComparison cmp,
            int maxDepth,
            int depth,
            HashSet<object> visited,
            ScanBudget budget,
            List<object> matches,
            ref int total,
            int maxCollect)
        {
            if (obj == null || depth > maxDepth || budget.Stop())
                return;
            budget.Node();

            Type t = obj.GetType();
            if (!t.IsValueType)
            {
                if (!visited.Add(obj))
                    return;
            }

            // PointerRef: record guid text as value, don't deep-walk Internal (covered via Objects list)
            if (obj is PointerRef pr)
            {
                string text = PointerRefToSearchText(pr);
                bool valueHit = (scope == "value" || scope == "both") &&
                                !string.IsNullOrEmpty(text) && text.IndexOf(query, cmp) >= 0;
                if (valueHit)
                    MaybeAddMatch(path, objectIndex, classGuid, objectType, "PointerRef", text, false, true, query, scope, cmp, matches, ref total, maxCollect);
                return;
            }

            if (obj is CString || obj is string || obj is Guid || obj.GetType().IsEnum || obj.GetType().IsPrimitive)
            {
                // leaf handled by parent property
                return;
            }

            if (obj is IList list && !(obj is string))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    string itemPath = string.IsNullOrEmpty(path) ? "[" + i + "]" : path + "[" + i + "]";
                    if (item == null)
                        continue;

                    // Direct string-like list items. Until 2026-10-27 this reported a
                    // value hit for every item without ever comparing it to the query,
                    // so any numeric list (RandomEventWeight, OutputProperties, ...)
                    // matched everything, including a nonsense query.
                    if (IsStringLike(item, out string itemText))
                    {
                        bool itemHit = (scope == "value" || scope == "both") &&
                                       ValueTextMatches(item, itemText, query, cmp);
                        if (itemHit)
                            MaybeAddMatch(itemPath, objectIndex, classGuid, objectType, item.GetType().Name, itemText, false, true, query, scope, cmp, matches, ref total, maxCollect);
                    }
                    else
                    {
                        WalkSearch(item, itemPath, objectIndex, classGuid, objectType, query, scope, cmp, maxDepth, depth + 1, visited, budget, matches, ref total, maxCollect);
                    }
                }
                return;
            }

            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0)
                    continue;
                // Skip noisy / recursive helpers
                if (pi.Name == "___Guid" || pi.Name == "TransientId")
                    continue;

                object value;
                try { value = pi.GetValue(obj); }
                catch { continue; }

                string childPath = string.IsNullOrEmpty(path) ? pi.Name : path + "." + pi.Name;
                bool nameHit = (scope == "name" || scope == "both") &&
                               pi.Name.IndexOf(query, cmp) >= 0;

                if (value == null)
                {
                    if (nameHit)
                        MaybeAddMatch(childPath, objectIndex, classGuid, objectType, "null", null, true, false, query, scope, cmp, matches, ref total, maxCollect);
                    continue;
                }

                if (IsStringLike(value, out string text))
                {
                    bool valueHit = (scope == "value" || scope == "both") &&
                                    ValueTextMatches(value, text, query, cmp);
                    if (nameHit || valueHit)
                        MaybeAddMatch(childPath, objectIndex, classGuid, objectType, value.GetType().Name, text, nameHit, valueHit, query, scope, cmp, matches, ref total, maxCollect);
                    continue;
                }

                if (nameHit)
                {
                    // Property name matched but the value is complex; still report it
                    MaybeAddMatch(childPath, objectIndex, classGuid, objectType, value.GetType().Name, SummarizeValue(value), true, false, query, scope, cmp, matches, ref total, maxCollect);
                }

                if (value is PointerRef || value is IList || value.GetType().IsClass || value.GetType().IsValueType)
                {
                    if (value.GetType().IsPrimitive || value is decimal)
                        continue;
                    WalkSearch(value, childPath, objectIndex, classGuid, objectType, query, scope, cmp, maxDepth, depth + 1, visited, budget, matches, ref total, maxCollect);
                }
            }
        }

        private static void SearchConnections(EbxAsset asset, string query, string scope, StringComparison cmp, ScanBudget budget, List<object> matches, ref int total, int maxCollect)
        {
            dynamic root = asset.RootObject;
            try
            {
                for (int i = 0; i < root.PropertyConnections.Count; i++)
                {
                    if (budget.Stop())
                        return;
                    budget.Node();
                    dynamic c = root.PropertyConnections[i];
                    CheckConnField("$PropertyConnections[" + i + "].SourceField", (string)(CString)c.SourceField, query, scope, cmp, matches, ref total, maxCollect);
                    CheckConnField("$PropertyConnections[" + i + "].TargetField", (string)(CString)c.TargetField, query, scope, cmp, matches, ref total, maxCollect);
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < root.EventConnections.Count; i++)
                {
                    if (budget.Stop())
                        return;
                    budget.Node();
                    dynamic c = root.EventConnections[i];
                    string se = "";
                    string te = "";
                    try { se = (string)(CString)c.SourceEvent.Name; } catch { }
                    try { te = (string)(CString)c.TargetEvent.Name; } catch { }
                    CheckConnField("$EventConnections[" + i + "].SourceEvent.Name", se, query, scope, cmp, matches, ref total, maxCollect);
                    CheckConnField("$EventConnections[" + i + "].TargetEvent.Name", te, query, scope, cmp, matches, ref total, maxCollect);
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < root.LinkConnections.Count; i++)
                {
                    if (budget.Stop())
                        return;
                    budget.Node();
                    dynamic c = root.LinkConnections[i];
                    CheckConnField("$LinkConnections[" + i + "].SourceField", (string)(CString)c.SourceField, query, scope, cmp, matches, ref total, maxCollect);
                    CheckConnField("$LinkConnections[" + i + "].TargetField", (string)(CString)c.TargetField, query, scope, cmp, matches, ref total, maxCollect);
                }
            }
            catch { }
        }

        private static void SearchInterfaceFields(EbxAsset asset, string typeFilter, string query, string scope,
            StringComparison cmp, ScanBudget budget, List<object> matches, ref int total, int maxCollect)
        {
            if (!string.IsNullOrEmpty(typeFilter) &&
                !typeFilter.Equals("InterfaceDescriptorData", StringComparison.OrdinalIgnoreCase))
                return;

            int objIndex = 0;
            foreach (object obj in asset.Objects)
            {
                if (budget.Stop())
                    return;
                budget.Node();
                if (obj.GetType().Name == "InterfaceDescriptorData")
                {
                    // Interface fields are a dynamic member (DynamicDataContainer),
                    // not a CLR property, so reflection cannot see them; the dynamic
                    // accessor that list_interface uses is the one that works.
                    System.Collections.IList fields = null;
                    try { fields = ((dynamic)obj).Fields; }
                    catch { }

                    if (fields != null)
                    {
                        for (int i = 0; i < fields.Count; i++)
                        {
                            object field = fields[i];
                            if (field == null)
                                continue;

                            string name = null;
                            object value = null;
                            try { name = (string)((dynamic)field).Name; }
                            catch { }
                            try { value = (object)((dynamic)field).Value; }
                            catch { }

                            name = name ?? "";
                            string valueText = SerializeAny(value)?.ToString() ?? "";

                            bool nameHit = (scope == "name" || scope == "both") && name.IndexOf(query, cmp) >= 0;
                            bool valueHit = (scope == "value" || scope == "both") && valueText.IndexOf(query, cmp) >= 0;
                            if (!nameHit && !valueHit)
                                continue;

                            total++;
                            if (matches.Count >= maxCollect)
                                continue;

                            matches.Add(new Dictionary<string, object>
                            {
                                ["kind"] = "interface_field",
                                ["object_index"] = objIndex,
                                ["path"] = "Fields[" + i + "].Name",
                                ["value"] = name,
                                ["value_type"] = "CString",
                                ["match_in"] = nameHit && valueHit ? "both" : nameHit ? "name" : "value"
                            });
                        }
                    }
                }
                objIndex++;
            }
        }

        private static void CheckConnField(string path, string text, string query, string scope, StringComparison cmp, List<object> matches, ref int total, int maxCollect)
        {
            if (text == null)
                text = "";
            bool nameHit = (scope == "name" || scope == "both") && path.IndexOf(query, cmp) >= 0;
            bool valueHit = (scope == "value" || scope == "both") && text.IndexOf(query, cmp) >= 0;
            if (!nameHit && !valueHit)
                return;
            total++;
            if (matches.Count >= maxCollect)
                return;
            matches.Add(new Dictionary<string, object>
            {
                ["kind"] = "connection_field",
                ["path"] = path,
                ["value"] = text,
                ["value_type"] = "CString",
                ["match_in"] = nameHit && valueHit ? "both" : nameHit ? "name" : "value"
            });
        }

        // Value matching for one field: plain text first, then a 0x-hex interpretation
        // for integer types. Without the hex path an EBX hash id (stored as a decimal
        // UInt32) can never be found by the 0xXXXXXXXX form people actually read.
        private static bool ValueTextMatches(object value, string text, string query, StringComparison cmp)
        {
            if (text != null && text.IndexOf(query, cmp) >= 0)
                return true;

            string q = query.Trim();
            if (q.Length <= 2 || !q.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return false;

            ulong want;
            if (!ulong.TryParse(q.Substring(2), System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out want))
                return false;

            switch (value)
            {
                case byte v: return v == want;
                case sbyte v: return (ulong)(long)v == want;
                case ushort v: return v == want;
                case short v: return (ulong)(long)v == want;
                case uint v: return v == want;
                case int v: return (ulong)(long)v == want;
                case ulong v: return v == want;
                case long v: return (ulong)v == want;
                default: return false;
            }
        }

        private static void MaybeAddMatch(
            string path, int objectIndex, Guid classGuid, string objectType, string valueType, string valueText,
            bool nameHit, bool valueHit, string query, string scope, StringComparison cmp,
            List<object> matches, ref int total, int maxCollect)
        {
            // Recompute hits if caller only passed tentative flags for complex name-only case
            if (!nameHit && !valueHit)
                return;

            total++;
            if (matches.Count >= maxCollect)
                return;

            matches.Add(new Dictionary<string, object>
            {
                ["kind"] = "component_field",
                ["object_index"] = objectIndex,
                ["class_guid"] = classGuid.ToString(),
                ["object_type"] = objectType,
                ["path"] = path,
                ["value"] = valueText,
                ["value_type"] = valueType,
                ["match_in"] = nameHit && valueHit ? "both" : nameHit ? "name" : "value"
            });
        }

        private static bool IsStringLike(object value, out string text)
        {
            text = null;
            if (value == null)
                return false;
            if (value is CString cs)
            {
                text = cs;
                return true;
            }
            if (value is string s)
            {
                text = s;
                return true;
            }
            if (value is Guid g)
            {
                text = g.ToString();
                return true;
            }
            if (value.GetType().IsEnum)
            {
                text = value.ToString();
                return true;
            }
            if (value.GetType().IsPrimitive || value is decimal)
            {
                text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        private static string PointerRefToSearchText(PointerRef pr)
        {
            if (pr.Type == PointerRefType.Null)
                return "";
            if (pr.Type == PointerRefType.External)
            {
                // Include the target asset name: searching for a name that only lives on
                // the other side of a pointer used to return zero hits and read as
                // "nothing references this".
                string name = null;
                try { name = App.AssetManager?.GetEbxEntry(pr.External.FileGuid)?.Name; } catch { }
                string suffix = string.IsNullOrEmpty(name) ? "" : "/" + name;
                return pr.External.FileGuid + "/" + pr.External.ClassGuid + suffix;
            }
            if (pr.Internal != null)
                return pr.Internal.GetType().Name + ":" + GetClassGuid(pr.Internal);
            return pr.Type.ToString();
        }

        private static string SummarizeValue(object value)
        {
            if (value == null)
                return null;
            if (value is IList list)
                return "List[" + list.Count + "]";
            return value.GetType().Name;
        }

        private static object ResolveConnectionPath(object root, string path, out string typeName)
        {
            // path like PropertyConnections[0].SourceField
            object value = ResolvePath(root, path, out typeName);
            return value;
        }

        private static void SetConnectionPath(object root, string path, JToken value, EbxAsset asset)
        {
            SetPath(root, path, value, asset);
        }

        private static object ResolveHostForClassCreate(EbxAsset asset, JObject p, out int hostIndex)
        {
            hostIndex = -1;
            if (p["index"] != null || !string.IsNullOrEmpty(p.Value<string>("class_guid")) ||
                !string.IsNullOrEmpty(p.Value<string>("component_type")))
            {
                return ResolveComponent(asset, p, out hostIndex);
            }
            hostIndex = 0;
            return asset.RootObject;
        }

        /// <summary>
        /// Resolve a PointerRef (or List&lt;PointerRef&gt;) assign target under host via dotted path.
        /// inferredMode: "set" for scalar PointerRef / indexed list; "append" for bare list property.
        /// </summary>
        private static void ResolvePointerAssignTarget(
            object host,
            string path,
            out object parent,
            out PropertyInfo property,
            out object existingValue,
            out Type baseType,
            out string inferredMode)
        {
            parent = null;
            property = null;
            existingValue = null;
            baseType = null;
            inferredMode = "set";

            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("path is empty");

            string[] parts = path.Split('.');
            object current = host;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = StepPathPart(current, parts[i]);
                if (current == null)
                    throw new InvalidOperationException("Null at path part: " + parts[i]);
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

            parent = current;
            if (string.IsNullOrEmpty(lastName))
            {
                // path like "Items[0]" where Items is already resolved; the split should prevent this
                throw new InvalidOperationException("Invalid path ending: " + last);
            }

            property = GetPropertyAliased(parent.GetType(), lastName)
                ?? throw new InvalidOperationException("Missing property: " + lastName);

            EbxFieldMetaAttribute meta = property.GetCustomAttribute<EbxFieldMetaAttribute>();
            if (meta != null)
                baseType = meta.BaseType;

            object propValue = property.GetValue(parent);
            if (lastIdx >= 0)
            {
                if (!(propValue is IList list))
                    throw new InvalidOperationException("Not a list: " + lastName);
                if (lastIdx < 0 || lastIdx >= list.Count)
                    throw new InvalidOperationException("List index out of range: " + lastIdx);
                existingValue = list[lastIdx];
                inferredMode = "set";
            }
            else if (propValue is IList)
            {
                existingValue = null;
                inferredMode = "append";
            }
            else
            {
                existingValue = propValue;
                inferredMode = "set";
                if (property.PropertyType != typeof(PointerRef) && property.PropertyType.Name != "PointerRef")
                    throw new InvalidOperationException(
                        "Property '" + lastName + "' is not PointerRef (got " + property.PropertyType.Name + ")");
            }
        }

        private static object StepPathPart(object current, string part)
        {
            string name = part;
            int idx = -1;
            int b = part.IndexOf('[');
            if (b >= 0 && part.EndsWith("]"))
            {
                name = part.Substring(0, b);
                idx = int.Parse(part.Substring(b + 1, part.Length - b - 2));
            }

            if (!string.IsNullOrEmpty(name))
            {
                PropertyInfo pi = GetPropertyAliased(current.GetType(), name)
                    ?? throw new InvalidOperationException("Missing property: " + name);
                current = pi.GetValue(current);
            }

            if (idx >= 0)
            {
                if (!(current is IList list))
                    throw new InvalidOperationException("Not a list: " + name);
                current = list[idx];
            }
            return current;
        }

        private static void AssignPointerAtPath(
            object host,
            string path,
            PointerRef pref,
            string mode,
            out string assignedPath,
            out int listIndex)
        {
            assignedPath = path;
            listIndex = -1;

            ResolvePointerAssignTarget(host, path, out object parent, out PropertyInfo property,
                out _, out _, out string inferredMode);

            if (string.IsNullOrEmpty(mode))
                mode = inferredMode;

            string[] parts = path.Split('.');
            string last = parts[parts.Length - 1];
            string lastName = last;
            int lastIdx = -1;
            int lb = last.IndexOf('[');
            if (lb >= 0 && last.EndsWith("]"))
            {
                lastName = last.Substring(0, lb);
                lastIdx = int.Parse(last.Substring(lb + 1, last.Length - lb - 2));
            }

            object propValue = property.GetValue(parent);

            if (mode == "append" || (mode == "auto" && inferredMode == "append"))
            {
                if (!(propValue is IList list))
                    throw new InvalidOperationException("Cannot append: '" + lastName + "' is not a list");
                list.Add(pref);
                listIndex = list.Count - 1;
                if (parts.Length > 1)
                    assignedPath = string.Join(".", parts, 0, parts.Length - 1) + "." + lastName + "[" + listIndex + "]";
                else
                    assignedPath = lastName + "[" + listIndex + "]";
                return;
            }

            // set
            if (lastIdx >= 0)
            {
                IList list = (IList)propValue;
                list[lastIdx] = pref;
                listIndex = lastIdx;
                return;
            }

            if (propValue is IList listOnly)
            {
                // bare list with a mode and no index: append as a convenience
                listOnly.Add(pref);
                listIndex = listOnly.Count - 1;
                assignedPath = lastName + "[" + listIndex + "]";
                return;
            }

            if (!property.CanWrite)
                throw new InvalidOperationException("Property is read-only: " + lastName);
            property.SetValue(parent, pref);
        }
    }
}
