using Frosty.Core;
using FrostySdk;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Creates resource-backed assets through PVZBundleManagerPlugin's per-type asset
    /// handlers, which build the EBX, RES and Chunk together and link them. This is the
    /// only way to get a usable Texture/Mesh/Sound asset; create_ebx alone leaves a
    /// zero Resource field.
    /// </summary>
    internal static class McpAssetCreateHandlers
    {
        public static object CreateAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string name = (p.Value<string>("name") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
                return McpHandlers.Error("name is required", "INVALID_PARAMS");
            if (App.AssetManager.GetEbxEntry(name) != null)
                return McpHandlers.Error("Asset already exists", "ALREADY_EXISTS");

            string typeName = (p.Value<string>("type") ?? "").Trim();
            string basedOn = (p.Value<string>("based_on") ?? "").Trim();
            string newTypeName = (p.Value<string>("new_type") ?? "").Trim();
            string bundleName = (p.Value<string>("bundle") ?? "").Trim();

            if (string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(basedOn))
                return McpHandlers.Error("type or based_on is required", "INVALID_PARAMS");

            EbxAssetEntry basedOnEntry = null;
            if (!string.IsNullOrEmpty(basedOn))
            {
                basedOnEntry = McpHandlers.ResolveEbxEntry(basedOn, p.Value<string>("based_on_guid"));
                if (basedOnEntry == null)
                    return McpHandlers.Error("based_on asset not found: " + basedOn, "NOT_FOUND");
                if (string.IsNullOrEmpty(typeName))
                    typeName = basedOnEntry.Type;
            }

            Type newType = null;
            if (!string.IsNullOrEmpty(newTypeName))
            {
                newType = TypeLibrary.GetType(newTypeName);
                if (newType == null)
                    return McpHandlers.Error("new_type not found: " + newTypeName, "TYPE_NOT_FOUND");
            }

            int bundleId = -1;
            if (!string.IsNullOrEmpty(bundleName))
            {
                bundleId = McpHandlers.ResolveBundleId(bundleName);
                if (bundleId < 0)
                    return McpHandlers.Error("Bundle not found: " + bundleName, "BUNDLE_NOT_FOUND");
            }

            Type dbType = FindType("PvZBundleManagerPlugin.Ports.Classes.AssetHandlerDB")
                ?? FindType("PVZBundleManagerPlugin.Ports.Classes.AssetHandlerDB")
                ?? FindType("PVZBundleManagerPlugin.AssetHandlerDB");
            if (dbType == null)
                return McpHandlers.Error("PVZBundleManagerPlugin is not loaded", "PLUGIN_MISSING");

            MethodInfo getHandler = dbType.GetMethod("GetAssetHandler",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(string), typeof(bool) }, null);
            if (getHandler == null)
                return McpHandlers.Error("AssetHandlerDB.GetAssetHandler not found in this plugin build", "NOT_IMPLEMENTED");

            object handler = getHandler.Invoke(null, new object[] { typeName, true });
            if (handler == null)
                return McpHandlers.Error("No asset handler for type " + typeName, "PLUGIN_MISSING");

            string handlerType = handler.GetType().Name;
            if (handlerType == "BaseAssetHandler" && !string.Equals(typeName, "Base", StringComparison.OrdinalIgnoreCase))
            {
                // The base handler cannot build RES/Chunk; it would produce a broken asset
                // for anything resource-backed.
                if (basedOnEntry == null)
                    return McpHandlers.Error("No specialized handler for " + typeName + "; provide based_on", "PLUGIN_MISSING");
            }

            MethodInfo create = handler.GetType().GetMethod("Create",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(EbxAssetEntry), typeof(Type) }, null);
            if (create == null)
                return McpHandlers.Error("Asset handler has no Create method", "NOT_IMPLEMENTED");

            var plan = new Dictionary<string, object>
            {
                ["name"] = name,
                ["type"] = typeName,
                ["handler"] = handlerType,
                ["based_on"] = basedOnEntry?.Name,
                ["bundle"] = string.IsNullOrEmpty(bundleName) ? null : bundleName
            };

            return McpCommit.Require("create_asset", p, plan, () =>
            {
                try
                {
                    EbxAssetEntry created = (EbxAssetEntry)create.Invoke(handler, new object[] { name, basedOnEntry, newType });
                    if (created == null)
                        return McpHandlers.Error("Asset handler returned no entry", "CREATE_FAILED");

                    if (bundleId >= 0)
                        McpHandlers.AddToBundleExportable(created, bundleId);

                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["handler"] = handlerType,
                        ["asset"] = McpHandlers.SerializeEbxEntry(created),
                        ["linked_assets"] = created.LinkedAssets.Select(McpHandlers.SerializeAssetEntry).ToList()
                    };
                }
                catch (TargetInvocationException tie)
                {
                    return McpHandlers.Error((tie.InnerException ?? tie).Message, "CREATE_FAILED");
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "CREATE_FAILED");
                }
            });
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
    }
}
