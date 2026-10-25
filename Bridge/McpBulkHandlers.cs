using Frosty.Core;
using Frosty.Core.Viewport;
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

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Bulk and format-specific helpers the editor only exposes as menu commands:
    /// whole-project EBX text dumps, IES profiles, the PVZ bundle manager pass, and
    /// the bundle readiness checks that go with it.
    /// </summary>
    internal static class McpBulkHandlers
    {
        public static object ExportAllEbx(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string outDir = (p.Value<string>("dir") ?? "").Trim();
            if (string.IsNullOrEmpty(outDir))
                return McpHandlers.Error("dir is required", "INVALID_PARAMS");

            string format = (p.Value<string>("format") ?? "xml").Trim().ToLowerInvariant();
            if (format != "xml" && format != "yaml")
                return McpHandlers.Error("format must be xml or yaml", "INVALID_PARAMS");

            bool skipConversations = p.Value<bool?>("skip_conversations") ?? false;
            bool skipVirtual = p.Value<bool?>("skip_virtual") ?? false;
            bool overwrite = p.Value<bool?>("overwrite") ?? false;
            string nameFilter = (p.Value<string>("query") ?? "").Trim();
            string typeFilter = (p.Value<string>("type") ?? "").Trim();
            int limit = p.Value<int?>("limit") ?? 0;

            int tabSize = Config.Get<int>("ExportTabSize", 2);

            List<EbxAssetEntry> entries = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (!string.IsNullOrEmpty(typeFilter) &&
                    !string.Equals(entry.Type, typeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !TypeLibrary.IsSubClassOf(entry.Type, typeFilter))
                    continue;
                if (!string.IsNullOrEmpty(nameFilter) &&
                    entry.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (skipConversations && entry.Path != null &&
                    entry.Path.StartsWith("conversations", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (skipVirtual && entry.Path != null && entry.Path.StartsWith("virtual"))
                    continue;
                entries.Add(entry);
                if (limit > 0 && entries.Count >= limit)
                    break;
            }

            var exported = new List<object>();
            var failures = new List<object>();
            int skippedExisting = 0;
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "INVALID_PARAMS");
            }

            foreach (EbxAssetEntry entry in entries)
            {
                string relativeDir = string.IsNullOrEmpty(entry.Path) ? "" : entry.Path.Replace('/', Path.DirectorySeparatorChar);
                string fullDir = Path.Combine(outDir, relativeDir);
                string extension = format == "yaml" ? ".yaml" : ".xml";
                char[] invalid = Path.GetInvalidFileNameChars();
                string safeName = string.Concat((entry.Filename + extension).Where(c => Array.IndexOf(invalid, c) < 0));
                string target = Path.Combine(fullDir, safeName);

                if (!overwrite && File.Exists(target))
                {
                    skippedExisting++;
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(fullDir);
                    EbxAsset asset = App.AssetManager.GetEbx(entry);
                    if (asset == null)
                        throw new InvalidOperationException("Failed to load EBX data");

                    using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write))
                    {
                        if (format == "yaml")
                        {
                            using (EbxYamlWriter writer = new EbxYamlWriter(fs, App.AssetManager))
                                writer.WriteObjects(asset.Objects);
                        }
                        else
                        {
                            using (EbxXmlWriter writer = new EbxXmlWriter(fs, App.AssetManager))
                                writer.WriteObjects(asset.Objects);
                        }
                    }

                    exported.Add(new Dictionary<string, object>
                    {
                        ["name"] = entry.Name,
                        ["path"] = target
                    });
                }
                catch (Exception ex)
                {
                    failures.Add(new Dictionary<string, object>
                    {
                        ["name"] = entry.Name,
                        ["error"] = Unwrap(ex),
                        ["error_code"] = "EXPORT_FAILED"
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = failures.Count == 0,
                ["dir"] = Path.GetFullPath(outDir),
                ["format"] = format,
                ["exported_count"] = exported.Count,
                ["skipped_existing"] = skippedExisting,
                ["failed_count"] = failures.Count,
                ["exported"] = exported,
                ["failures"] = failures
            };
        }

        public static object ExportIes(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path (.dds) is required", "INVALID_PARAMS");

            Type iesType = FindType("IesResourcePlugin.IesResource");
            if (iesType == null)
                return McpHandlers.Error("IesResourcePlugin not loaded", "PLUGIN_MISSING");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                PropertyInfo resProp = ((object)root).GetType().GetProperty("Resource");
                if (resProp == null)
                    return McpHandlers.Error("Asset has no Resource property", "TYPE_MISMATCH");

                ulong rid = Convert.ToUInt64(resProp.GetValue(root));
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                if (resEntry == null)
                    return McpHandlers.Error("IES resource not found", "NOT_FOUND");

                MethodInfo getResAs = typeof(AssetManager).GetMethods()
                    .First(m => m.Name == "GetResAs" && m.IsGenericMethodDefinition);
                object ies = getResAs.MakeGenericMethod(iesType).Invoke(App.AssetManager, new object[] { resEntry, null });
                if (ies == null)
                    return McpHandlers.Error("Resource is not an IES profile", "TYPE_MISMATCH");

                int size = (int)iesType.GetProperty("Size").GetValue(ies);
                Stream data = (Stream)iesType.GetProperty("Data").GetValue(ies);
                data.Position = 0;
                byte[] buffer = new byte[data.Length];
                int read = data.Read(buffer, 0, buffer.Length);

                TextureUtils.DDSHeader header = new TextureUtils.DDSHeader
                {
                    dwHeight = size,
                    dwWidth = size,
                    dwMipMapCount = 1,
                    dwPitchOrLinearSize = read,
                    ddspf = { dwFourCC = 0x30315844 },
                    HasExtendedHeader = true
                };
                header.ExtendedHeader.dxgiFormat = SharpDX.DXGI.Format.R16_Float;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                using (NativeWriter writer = new NativeWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
                {
                    header.Write(writer);
                    if (read == buffer.Length)
                        writer.Write(buffer);
                    else
                    {
                        byte[] trimmed = new byte[read];
                        Array.Copy(buffer, trimmed, read);
                        writer.Write(trimmed);
                    }
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = size,
                    ["bytes"] = read
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "EXPORT_FAILED");
            }
        }

        public static object CanBeLoadedByBundle(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");
            BundleEntry bundle = App.AssetManager.GetBundleEntry(bid);

            object manager = GetBundleManager();
            if (manager != null)
            {
                try
                {
                    int cacheEntries = ReadBundleCacheCount(manager);
                    if (cacheEntries == 0)
                    {
                        return new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["name"] = entry.Name,
                            ["bundle"] = bundle.Name,
                            ["can_be_loaded"] = null,
                            ["degraded"] = true,
                            ["engine"] = "bundle_cache_not_loaded",
                            ["hint"] = "PVZBundleManagerPlugin has no parent/prev bundle table yet"
                        };
                    }

                    MethodInfo method = manager.GetType().GetMethod("CanBeLoadedByBundle",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                        new[] { typeof(EbxAssetEntry), typeof(int) }, null);
                    if (method != null)
                    {
                        bool ok = (bool)method.Invoke(manager, new object[] { entry, bid });
                        return new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["name"] = entry.Name,
                            ["bundle"] = bundle.Name,
                            ["can_be_loaded"] = ok,
                            ["engine"] = "PVZBundleManagerPlugin"
                        };
                    }
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(Unwrap(ex), "PLUGIN_FAILED");
                }
            }

            // Fallback: direct dependency membership only. Parent/prev bundle
            // resolution needs PVZBundleManagerPlugin.
            List<object> outside = new List<object>();
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            foreach (Guid dep in asset.Dependencies)
            {
                EbxAssetEntry depEntry = App.AssetManager.GetEbxEntry(dep);
                if (depEntry == null)
                {
                    outside.Add(new Dictionary<string, object> { ["dep_guid"] = dep.ToString(), ["missing"] = true });
                    continue;
                }
                if (!depEntry.EnumerateBundles().Contains(bid))
                    outside.Add(new Dictionary<string, object> { ["dep"] = depEntry.Name });
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["bundle"] = bundle.Name,
                ["can_be_loaded"] = outside.Count == 0,
                ["outside_count"] = outside.Count,
                ["outside"] = outside,
                ["engine"] = "direct_dependencies_only"
            };
        }

        public static object CheckAssetReady(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string bundleName = (p.Value<string>("bundle") ?? "").Trim();
            if (string.IsNullOrEmpty(bundleName))
                return McpHandlers.Error("bundle is required", "INVALID_PARAMS");

            int bid = McpHandlers.ResolveBundleId(bundleName);
            if (bid < 0)
                return McpHandlers.Error("Bundle not found: " + bundleName, "NOT_FOUND");
            BundleEntry bundle = App.AssetManager.GetBundleEntry(bid);

            var reasons = new List<object>();
            if (!entry.EnumerateBundles().Contains(bid))
                reasons.Add(new Dictionary<string, object> { ["issue"] = "asset_not_in_bundle" });

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "LOAD_FAILED");
            }

            // Same-bundle membership is stricter than what the engine accepts: a
            // dependency in a parent or previous bundle loads fine. Ask the bundle
            // manager when it can answer, and say so when it cannot.
            Func<EbxAssetEntry, bool> canLoad = BuildLoadabilityCheck(bid, out bool loadabilityKnown);
            var missingDeps = new List<object>();
            foreach (Guid dep in asset.Dependencies)
            {
                EbxAssetEntry depEntry = App.AssetManager.GetEbxEntry(dep);
                if (depEntry == null)
                {
                    missingDeps.Add(new Dictionary<string, object> { ["dep_guid"] = dep.ToString(), ["missing"] = true });
                    continue;
                }
                if (canLoad != null)
                {
                    bool loadable;
                    try { loadable = canLoad(depEntry); }
                    catch { loadable = depEntry.EnumerateBundles().Contains(bid); }
                    if (!loadable)
                        missingDeps.Add(new Dictionary<string, object> { ["dep"] = depEntry.Name });
                    continue;
                }
                if (!depEntry.EnumerateBundles().Contains(bid))
                    missingDeps.Add(new Dictionary<string, object>
                    {
                        ["dep"] = depEntry.Name,
                        ["check"] = "direct_membership_only"
                    });
            }
            if (missingDeps.Count > 0)
                reasons.Add(new Dictionary<string, object>
                {
                    ["issue"] = "dependencies_outside_bundle",
                    ["count"] = missingDeps.Count,
                    ["items"] = missingDeps
                });

            var missingLinked = new List<object>();
            foreach (AssetEntry linked in entry.LinkedAssets)
                if (!linked.EnumerateBundles().Contains(bid))
                    missingLinked.Add(new Dictionary<string, object> { ["linked"] = linked.Name, ["type"] = linked.Type });
            if (missingLinked.Count > 0)
                reasons.Add(new Dictionary<string, object>
                {
                    ["issue"] = "linked_assets_outside_bundle",
                    ["count"] = missingLinked.Count,
                    ["items"] = missingLinked
                });

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["bundle"] = bundle.Name,
                ["ready"] = reasons.Count == 0,
                ["issue_count"] = reasons.Count,
                ["issues"] = reasons,
                ["dependency_check"] = loadabilityKnown ? "bundle_manager" : "direct_membership_only",
                ["note"] = "Linked assets are only reported once the editor has loaded them"
            };
        }

        public static object RefreshMeshVariations(JObject p)
        {
            Type dbType = FindType("Frosty.Core.Viewport.MeshVariationDb");
            if (dbType == null)
                return McpHandlers.Error("MeshVariationDb is not available", "PLUGIN_MISSING");

            MethodInfo load = dbType.GetMethod("LoadVariations",
                BindingFlags.Public | BindingFlags.Static);
            if (load == null)
                return McpHandlers.Error("LoadVariations not found in this build", "NOT_IMPLEMENTED");

            try
            {
                object task = McpHandlers.InvokeOnUi(() => McpEditHandlers.CreateTaskWindow());
                object[] args = load.GetParameters().Length == 0
                    ? new object[0]
                    : new[] { task };

                // The scan reads every MeshVariationDatabase; the task window sends
                // its own updates through the dispatcher, so run it off the UI thread
                // instead of freezing the editor for the whole reload.
                Exception failure = null;
                System.Threading.Thread worker = new System.Threading.Thread(() =>
                {
                    try
                    {
                        load.Invoke(null, args);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });
                worker.IsBackground = true;
                worker.Start();
                worker.Join();
                if (failure != null)
                    throw failure;
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["reloaded"] = true
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "REFRESH_FAILED");
            }
        }
        // Returns a per-asset loadability test backed by PVZBundleManagerPlugin, or
        // null when the plugin is missing or has no parent/prev bundle table yet.
        private static Func<EbxAssetEntry, bool> BuildLoadabilityCheck(int bundleId, out bool known)
        {
            known = false;
            object manager = GetBundleManager();
            if (manager == null)
                return null;

            try
            {
                if (ReadBundleCacheCount(manager) == 0)
                    return null;

                MethodInfo method = manager.GetType().GetMethod("CanBeLoadedByBundle",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(EbxAssetEntry), typeof(int) }, null);
                if (method == null)
                    return null;

                known = true;
                object instance = manager;
                int bid = bundleId;
                return e => (bool)method.Invoke(instance, new object[] { e, bid });
            }
            catch
            {
                known = false;
                return null;
            }
        }

        public static object ManageBundles(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            if (!ProfilesLibrary.IsLoaded(ProfileVersion.PlantsVsZombiesGardenWarfare,
                ProfileVersion.PlantsVsZombiesGardenWarfare2,
                ProfileVersion.PlantsVsZombiesBattleforNeighborville))
                return McpHandlers.Error("Bundle manager only supports GW1, GW2 and BFN", "NOT_SUPPORTED_PROFILE");

            object instance = GetBundleManager();
            if (instance == null)
                return McpHandlers.Error("PVZBundleManagerPlugin is not loaded", "PLUGIN_MISSING");

            MethodInfo manage = instance.GetType().GetMethod("BundleManageAsset",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (manage == null)
                return McpHandlers.Error("BundleManageAsset not found in this plugin build", "NOT_IMPLEMENTED");

            var plan = new Dictionary<string, object>
            {
                ["name"] = entry.Name,
                ["type"] = entry.Type,
                ["bundles"] = entry.EnumerateBundles().Select(id => App.AssetManager.GetBundleEntry(id)?.Name).Where(n => n != null).ToList()
            };

            return McpCommit.Require("manage_bundles", p, plan, () =>
            {
                try
                {
                    int bundlesBefore = entry.EnumerateBundles().Count();
                    int modifiedBefore = (int)App.AssetManager.GetModifiedCount();
                    McpHandlers.InvokeOnUi(() => manage.Invoke(instance, new object[] { entry }));

                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["name"] = entry.Name,
                        ["bundles_before"] = bundlesBefore,
                        ["bundles_after"] = entry.EnumerateBundles().Count(),
                        ["modified_before"] = modifiedBefore,
                        ["modified_after"] = (int)App.AssetManager.GetModifiedCount(),
                        ["engine"] = "PVZBundleManagerPlugin.BundleManager"
                    };
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(Unwrap(ex), "MANAGE_FAILED");
                }
            });
        }

        // The plugin answers CanBeLoadedByBundle from this table; empty means the
        // answer would only reflect direct membership.
        private static int ReadBundleCacheCount(object manager)
        {
            FieldInfo field = manager.GetType().GetField("m_bundleAndParents",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object value = field?.GetValue(manager);
            if (value is System.Collections.ICollection collection)
                return collection.Count;
            return value == null ? 0 : 1;
        }

        // The plugin assembly has shipped under both spellings, so both are accepted.
        internal static Type FindBundleManagerPluginType()
        {
            return FindType("PVZBundleManagerPlugin.Plugin")
                ?? FindType("PvZBundleManagerPlugin.Plugin")
                ?? FindType("GW2BundleManagerPlugin.Plugin");
        }

        // Plugin.BundleManager is a public static property on the plugin class.
        private static object GetBundleManager()
        {
            Type pluginType = FindBundleManagerPluginType();
            if (pluginType == null)
                return null;
            PropertyInfo prop = pluginType.GetProperty("BundleManager",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return prop?.GetValue(null);
        }

        private static object Wrap(object result, string name)
        {
            if (result is Dictionary<string, object> existing)
                return existing;

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = name,
                ["can_be_loaded"] = result is bool b ? (object)b : result
            };
        }

        private static object Wrap(object result)
        {
            return Wrap(result, null);
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
            return ex.Message;
        }
    }
}
