using FrostySdk;
using FrostySdk.Attributes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Type Explorer–aligned SDK type browsing (list / detail / nested field types).
    /// Mirrors Plugins/TypeExplorerPlugin/FrostyTypeExplorer.cs behavior for MCP.
    /// </summary>
    internal static class McpTypeExplorerHandlers
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 10000;
        private const int MaxExpandDepth = 3;

        public static object SearchTypes(JObject p)
        {
            string query = (p.Value<string>("query") ?? "").Trim();
            string baseType = (p.Value<string>("base_type") ?? "").Trim();
            string nsFilter = (p.Value<string>("namespace") ?? p.Value<string>("ebx_namespace") ?? "").Trim();
            string kindFilter = (p.Value<string>("kind") ?? "all").Trim().ToLowerInvariant();
            bool hideEmpty = p.Value<bool?>("hide_empty") ?? false;
            bool matchSubclass = p.Value<bool?>("match_subclass") ?? true;
            bool compact = p.Value<bool?>("compact") ?? true;
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            Type[] types;
            try
            {
                types = string.IsNullOrEmpty(baseType)
                    ? TypeLibrary.GetConcreteTypes()
                    : TypeLibrary.GetTypes(baseType);
            }
            catch
            {
                types = TypeLibrary.GetConcreteTypes() ?? Array.Empty<Type>();
            }

            if (types == null)
                types = Array.Empty<Type>();

            IEnumerable<Type> filtered = types;
            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(t =>
                {
                    if (t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    string ebxNs = GetEbxNamespace(t);
                    if (!string.IsNullOrEmpty(ebxNs) &&
                        (ebxNs + "." + t.Name).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    if (matchSubclass)
                    {
                        try { return TypeLibrary.IsSubClassOf(t, query); }
                        catch { return false; }
                    }
                    return false;
                });
            }

            if (!string.IsNullOrEmpty(nsFilter))
            {
                filtered = filtered.Where(t =>
                {
                    string ebxNs = GetEbxNamespace(t);
                    return !string.IsNullOrEmpty(ebxNs) &&
                           ebxNs.IndexOf(nsFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }

            if (kindFilter != "all" && !string.IsNullOrEmpty(kindFilter))
            {
                filtered = filtered.Where(t => string.Equals(GetKind(t), kindFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (hideEmpty)
            {
                filtered = filtered.Where(t =>
                    t.IsEnum || CountDeclaredFields(t) > 0);
            }

            List<Type> all = filtered
                .OrderBy(t => GetEbxNamespace(t) ?? "")
                .ThenBy(t => t.Name)
                .ToList();

            List<object> page = all.Skip(offset).Take(limit)
                .Select(t => (object)SummarizeType(t, compact))
                .ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = all.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["query"] = query,
                ["base_type"] = baseType,
                ["namespace"] = nsFilter,
                ["kind"] = kindFilter,
                ["hide_empty"] = hideEmpty,
                ["compact"] = compact,
                ["types"] = page
            };
        }

        public static object GetTypeInfo(JObject p)
        {
            string typeName = (p.Value<string>("name") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("name is required", "INVALID_PARAMS");

            Type type = ResolveType(typeName);
            if (type == null)
                return McpHandlers.Error("Type not found: " + typeName, "NOT_FOUND");

            bool declaredOnly = p.Value<bool?>("declared_only") ?? true;
            bool includeInherited = p.Value<bool?>("include_inherited") ?? false;
            int expandNested = Math.Max(0, Math.Min(MaxExpandDepth, p.Value<int?>("expand_nested") ?? 0));

            return BuildTypeDetail(type, declaredOnly, includeInherited, expandNested, 0);
        }

        public static object GetTypeField(JObject p)
        {
            string typeName = (p.Value<string>("name") ?? "").Trim();
            string fieldName = (p.Value<string>("field") ?? p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(typeName))
                return McpHandlers.Error("name is required", "INVALID_PARAMS");
            if (string.IsNullOrEmpty(fieldName))
                return McpHandlers.Error("field is required", "INVALID_PARAMS");

            Type type = ResolveType(typeName);
            if (type == null)
                return McpHandlers.Error("Type not found: " + typeName, "NOT_FOUND");

            string[] parts = fieldName.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            Type current = type;
            Dictionary<string, object> lastField = null;
            Type resolved = null;

            for (int i = 0; i < parts.Length; i++)
            {
                PropertyInfo pi = FindProperty(current, parts[i]);
                if (pi == null)
                {
                    return McpHandlers.Error(
                        "Field '" + parts[i] + "' not found on " + current.Name +
                        (i > 0 ? " (after " + string.Join(".", parts.Take(i)) + ")" : ""),
                        "NOT_FOUND");
                }

                lastField = DescribeField(pi, declared: true);
                resolved = ResolveFieldBrowseType(pi);
                if (resolved == null && i < parts.Length - 1)
                {
                    return McpHandlers.Error(
                        "Cannot walk into field '" + parts[i] + "' (type " +
                        (lastField.ContainsKey("type_display") ? lastField["type_display"] : "?") + ")",
                        "NOT_BROWSABLE");
                }

                if (i < parts.Length - 1)
                    current = resolved;
            }

            int expandNested = Math.Max(0, Math.Min(MaxExpandDepth, p.Value<int?>("expand_nested") ?? 1));
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["host_type"] = type.Name,
                ["field_path"] = fieldName,
                ["field"] = lastField
            };

            if (resolved != null)
                result["resolved_type"] = BuildTypeDetail(resolved, true, false, expandNested, 0);
            else
            {
                result["resolved_type"] = null;
                result["note"] = "Primitive or unresolved PointerRef base; see field meta.";
            }

            return result;
        }

        public static object ListTypeNamespaces(JObject p)
        {
            string query = (p.Value<string>("query") ?? "").Trim();
            int offset = Math.Max(0, p.Value<int?>("offset") ?? 0);
            int limit = ClampLimit(p.Value<int?>("limit") ?? DefaultLimit);

            Type[] types = TypeLibrary.GetConcreteTypes() ?? Array.Empty<Type>();
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, int>> kindBreakdown =
                new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (Type t in types)
            {
                string ns = GetEbxNamespace(t);
                if (string.IsNullOrEmpty(ns))
                    ns = "(none)";

                if (!string.IsNullOrEmpty(query) &&
                    ns.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!counts.ContainsKey(ns))
                    counts[ns] = 0;
                counts[ns]++;

                if (!kindBreakdown.ContainsKey(ns))
                    kindBreakdown[ns] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string kind = GetKind(t);
                if (!kindBreakdown[ns].ContainsKey(kind))
                    kindBreakdown[ns][kind] = 0;
                kindBreakdown[ns][kind]++;
            }

            List<KeyValuePair<string, int>> ordered = counts.OrderBy(kv => kv.Key).ToList();
            List<object> page = ordered.Skip(offset).Take(limit).Select(kv => (object)new Dictionary<string, object>
            {
                ["namespace"] = kv.Key,
                ["type_count"] = kv.Value,
                ["kinds"] = kindBreakdown.ContainsKey(kv.Key)
                    ? kindBreakdown[kv.Key].OrderBy(x => x.Key)
                        .ToDictionary(x => x.Key, x => (object)x.Value)
                    : new Dictionary<string, object>()
            }).ToList();

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["total"] = ordered.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["returned"] = page.Count,
                ["namespaces"] = page
            };
        }

        private static Dictionary<string, object> BuildTypeDetail(
            Type type, bool declaredOnly, bool includeInherited, int expandNested, int depth)
        {
            string kind = GetKind(type);
            string ebxNs = GetEbxNamespace(type);
            EbxClassMetaAttribute classMeta = type.GetCustomAttribute<EbxClassMetaAttribute>();

            List<string> inheritance = new List<string>();
            for (Type b = type.BaseType; b != null && b != typeof(object); b = b.BaseType)
                inheritance.Add(b.Name);

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = type.Name,
                ["kind"] = kind,
                ["ebx_namespace"] = ebxNs ?? "",
                ["display_name"] = string.IsNullOrEmpty(ebxNs) ? type.Name : (ebxNs + "." + type.Name),
                ["csharp_namespace"] = type.Namespace ?? "",
                ["full_name"] = type.FullName ?? type.Name,
                ["base_type"] = (type.BaseType != null && type.BaseType != typeof(object)) ? type.BaseType.Name : null,
                ["inheritance"] = inheritance,
                ["is_enum"] = type.IsEnum,
                ["is_value_type"] = type.IsValueType,
                ["is_class"] = type.IsClass && !type.IsEnum
            };

            if (classMeta != null)
            {
                result["ebx_class_meta"] = new Dictionary<string, object>
                {
                    ["ebx_field_type"] = classMeta.Type.ToString(),
                    ["alignment"] = classMeta.Alignment,
                    ["size"] = classMeta.Size,
                    ["flags"] = classMeta.Flags,
                    ["namespace"] = classMeta.Namespace ?? ""
                };
            }

            if (type.IsEnum)
            {
                Type underlying = Enum.GetUnderlyingType(type);
                Array values = type.GetEnumValues();
                List<object> members = new List<object>();
                foreach (object v in values)
                {
                    members.Add(new Dictionary<string, object>
                    {
                        ["name"] = v.ToString(),
                        ["value"] = Convert.ChangeType(v, underlying)
                    });
                }
                result["underlying_type"] = MapPrimitiveName(underlying.Name);
                result["member_count"] = members.Count;
                result["members"] = members;
                result["fields"] = Array.Empty<object>();
                result["field_count"] = 0;
                return result;
            }

            List<object> fields = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddProps(Type from, bool declared)
            {
                foreach (PropertyInfo pi in from.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (pi.Name.StartsWith("_"))
                        continue;
                    if (!seen.Add(pi.Name))
                        continue;

                    Dictionary<string, object> field = DescribeField(pi, declared);
                    if (expandNested > 0 && depth < expandNested)
                    {
                        Type nested = ResolveFieldBrowseType(pi);
                        if (nested != null && IsSdkType(nested) && nested != type)
                            field["nested"] = BuildTypeDetail(nested, true, false, expandNested, depth + 1);
                    }
                    fields.Add(field);
                }
            }

            if (includeInherited && !declaredOnly)
            {
                List<Type> chain = new List<Type>();
                for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                    chain.Add(t);
                chain.Reverse();
                foreach (Type t in chain)
                    AddProps(t, declared: t == type);
            }
            else if (declaredOnly)
            {
                AddProps(type, declared: true);
            }
            else
            {
                foreach (PropertyInfo pi in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (pi.Name.StartsWith("_"))
                        continue;
                    bool declared = pi.DeclaringType == type;
                    fields.Add(DescribeField(pi, declared));
                }
            }

            result["field_count"] = fields.Count;
            result["fields"] = fields;
            result["property_count"] = fields.Count;
            result["properties"] = fields;
            return result;
        }

        private static Dictionary<string, object> DescribeField(PropertyInfo pi, bool declared)
        {
            EbxFieldMetaAttribute meta = pi.GetCustomAttribute<EbxFieldMetaAttribute>();
            string propTypeName = pi.PropertyType.Name;
            string mapped = MapPrimitiveName(propTypeName);

            bool isList = propTypeName == "List`1" ||
                          (pi.PropertyType.IsGenericType && pi.PropertyType.GetGenericTypeDefinition() == typeof(List<>));
            bool isPointer = propTypeName == "PointerRef";

            string elementType = null;
            string pointerBase = null;
            string typeDisplay;

            if (isList)
            {
                Type listType = pi.PropertyType.GetGenericArguments()[0];
                if (listType.Name == "PointerRef")
                {
                    pointerBase = meta?.BaseType?.Name;
                    elementType = pointerBase != null ? ("PointerRef<" + pointerBase + ">") : "PointerRef";
                    typeDisplay = "List<" + elementType + ">";
                }
                else
                {
                    elementType = MapPrimitiveName(listType.Name);
                    typeDisplay = "List<" + elementType + ">";
                }
            }
            else if (isPointer)
            {
                pointerBase = meta?.BaseType?.Name;
                typeDisplay = pointerBase != null ? ("PointerRef<" + pointerBase + ">") : "PointerRef";
            }
            else
            {
                typeDisplay = mapped;
            }

            Dictionary<string, object> field = new Dictionary<string, object>
            {
                ["name"] = pi.Name,
                ["type"] = mapped,
                ["type_display"] = typeDisplay,
                ["declared"] = declared,
                ["declaring_type"] = pi.DeclaringType?.Name,
                ["is_array"] = isList || (meta?.IsArray ?? false),
                ["is_pointer"] = isPointer || (elementType != null && elementType.StartsWith("PointerRef"))
            };

            if (elementType != null)
                field["element_type"] = elementType;
            if (pointerBase != null)
                field["pointer_base_type"] = pointerBase;

            if (meta != null)
            {
                field["ebx_field_type"] = meta.Type.ToString();
                field["ebx_offset"] = meta.Offset;
                if (meta.IsArray)
                    field["ebx_array_type"] = meta.ArrayType.ToString();
            }

            Type browse = ResolveFieldBrowseType(pi);
            if (browse != null)
            {
                field["resolvable_type"] = browse.Name;
                field["resolvable_kind"] = GetKind(browse);
            }

            return field;
        }

        private static Dictionary<string, object> SummarizeType(Type t, bool compact = false)
        {
            string ebxNs = GetEbxNamespace(t);
            var d = new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["kind"] = GetKind(t),
                ["ebx_namespace"] = ebxNs ?? ""
            };
            if (!compact)
            {
                d["display_name"] = string.IsNullOrEmpty(ebxNs) ? t.Name : (ebxNs + "." + t.Name);
                d["full_name"] = t.FullName;
                d["base_type"] = (t.BaseType != null && t.BaseType != typeof(object) && !t.IsEnum) ? t.BaseType.Name : (t.IsEnum ? "Enum" : null);
                d["field_count"] = t.IsEnum ? (t.GetEnumNames()?.Length ?? 0) : CountDeclaredFields(t);
            }
            return d;
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            string shortName = typeName;
            int dot = typeName.LastIndexOf('.');
            if (dot >= 0 && dot < typeName.Length - 1)
                shortName = typeName.Substring(dot + 1);

            Type t = TypeLibrary.GetType(shortName);
            if (t != null)
                return t;

            t = TypeLibrary.GetType(typeName);
            if (t != null)
                return t;

            Type[] all = TypeLibrary.GetConcreteTypes() ?? Array.Empty<Type>();
            return all.FirstOrDefault(x =>
                string.Equals(x.Name, shortName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetEbxNamespace(x) + "." + x.Name, typeName, StringComparison.OrdinalIgnoreCase));
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
                ?? type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static Type ResolveFieldBrowseType(PropertyInfo pi)
        {
            EbxFieldMetaAttribute meta = pi.GetCustomAttribute<EbxFieldMetaAttribute>();
            string propTypeName = pi.PropertyType.Name;

            if (propTypeName == "List`1" ||
                (pi.PropertyType.IsGenericType && pi.PropertyType.GetGenericTypeDefinition() == typeof(List<>)))
            {
                Type listType = pi.PropertyType.GetGenericArguments()[0];
                if (listType.Name == "PointerRef")
                    return meta?.BaseType;
                return IsSdkType(listType) ? listType : null;
            }

            if (propTypeName == "PointerRef")
                return meta?.BaseType;

            return IsSdkType(pi.PropertyType) ? pi.PropertyType : null;
        }

        private static bool IsSdkType(Type t)
        {
            if (t == null || t == typeof(object))
                return false;
            if (t.IsPrimitive || t == typeof(string) || t == typeof(decimal))
                return false;
            string ns = t.Namespace ?? "";
            if (ns.StartsWith("System", StringComparison.Ordinal))
                return false;
            return ns.StartsWith("FrostySdk", StringComparison.Ordinal) ||
                   t.GetCustomAttribute<EbxClassMetaAttribute>() != null;
        }

        private static string GetEbxNamespace(Type type)
        {
            EbxClassMetaAttribute attr = type.GetCustomAttribute<EbxClassMetaAttribute>();
            if (attr != null && !string.IsNullOrEmpty(attr.Namespace))
                return attr.Namespace;
            return null;
        }

        private static string GetKind(Type type)
        {
            if (type.IsEnum)
                return "enum";
            if (type.IsValueType)
                return "struct";
            return "class";
        }

        private static int CountDeclaredFields(Type type)
        {
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Count(p => !p.Name.StartsWith("_"));
        }

        private static string MapPrimitiveName(string propName)
        {
            if (propName == "Single") return "Float32";
            if (propName == "Double") return "Float64";
            if (propName == "List`1") return "List";
            return propName;
        }

        private static int ClampLimit(int limit)
        {
            if (limit < 1) return 1;
            if (limit > MaxLimit) return MaxLimit;
            return limit;
        }
    }
}
