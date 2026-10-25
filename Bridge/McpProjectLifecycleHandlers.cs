using Frosty.Core;
using Frosty.Core.Mod;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Managers.Entries;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Project lifecycle tools that Frosty only exposes through the File menu:
    /// project info, save/save-as/copy, open/new, revert-all, ModSettings and
    /// .fbmod export (explicit call only; nothing here runs automatically).
    /// </summary>
    internal static class McpProjectLifecycleHandlers
    {
        private static FrostyProject GetProject()
        {
            Window window = Application.Current?.MainWindow;
            if (window == null)
                return null;
            PropertyInfo pi = window.GetType().GetProperty("Project", BindingFlags.Public | BindingFlags.Instance);
            return pi?.GetValue(window) as FrostyProject;
        }

        public static object GetProjectInfo(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["display_name"] = project.DisplayName,
                ["filename"] = project.Filename ?? "",
                ["profile"] = project.Profile ?? "",
                ["is_dirty"] = project.IsDirty,
                ["requires_new_profile"] = project.RequiresNewProfile,
                ["game_version"] = project.gameVersion,
                ["sdk_data_version"] = ProfilesLibrary.DataVersion,
                ["current_profile"] = ProfilesLibrary.ProfileName ?? "",
                ["enable_execution"] = ProfilesLibrary.EnableExecution
            };

            ModSettings settings = project.GetModSettings();
            if (settings != null)
                result["mod_settings"] = SerializeModSettings(settings);
            return result;
        }

        public static object SaveProject(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");
            if (string.IsNullOrEmpty(project.Filename))
                return McpHandlers.Error("Project has no filename; use save_project_as", "INVALID_STATE");

            try
            {
                McpHandlers.InvokeOnUi(() => project.Save());
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["filename"] = project.Filename,
                    ["is_dirty"] = project.IsDirty
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SAVE_FAILED");
            }
        }

        public static object SaveProjectAs(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");
            if (!path.EndsWith(".fbproject", StringComparison.OrdinalIgnoreCase))
                return McpHandlers.Error("path must end with .fbproject", "INVALID_PARAMS");
            if (File.Exists(path) && !(p.Value<bool?>("overwrite") ?? false))
                return McpHandlers.Error("Target file already exists", "ALREADY_EXISTS");

            string previous = project.Filename;
            try
            {
                McpHandlers.InvokeOnUi(() =>
                {
                    project.Filename = path;
                    project.Save();
                });
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["filename"] = project.Filename,
                    ["is_dirty"] = project.IsDirty
                };
            }
            catch (Exception ex)
            {
                project.Filename = previous;
                return McpHandlers.Error(ex.Message, "SAVE_FAILED");
            }
        }

        public static object SaveProjectCopy(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");
            if (!path.EndsWith(".fbproject", StringComparison.OrdinalIgnoreCase))
                return McpHandlers.Error("path must end with .fbproject", "INVALID_PARAMS");
            if (File.Exists(path) && !(p.Value<bool?>("overwrite") ?? false))
                return McpHandlers.Error("Target file already exists", "ALREADY_EXISTS");

            try
            {
                // updateDirtyState:false keeps the current project's dirty flag.
                McpHandlers.InvokeOnUi(() => project.Save(path, false));
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["filename"] = Path.GetFullPath(path),
                    ["project_filename"] = project.Filename,
                    ["is_dirty"] = project.IsDirty
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SAVE_FAILED");
            }
        }

        public static object OpenProject(JObject p)
        {
            FrostyProject project = GetProject();
            Window window = Application.Current?.MainWindow;
            if (project == null || window == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return McpHandlers.Error("path to an existing .fbproject is required", "INVALID_PARAMS");

            bool saveFirst = p.Value<bool?>("save_first") ?? false;
            if (project.IsDirty)
            {
                if (!saveFirst)
                    return McpHandlers.Error("The current project has unsaved changes", "DIRTY_PROJECT");
                if (string.IsNullOrEmpty(project.Filename))
                    return McpHandlers.Error("The current project has no filename to save to", "INVALID_STATE");
                try { project.Save(); }
                catch (Exception ex) { return McpHandlers.Error(ex.Message, "SAVE_FAILED"); }
            }

            MethodInfo load = window.GetType().GetMethod("LoadProject",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(bool) }, null);
            if (load == null)
                return McpHandlers.Error("LoadProject is not available in this editor build", "NOT_IMPLEMENTED");

            try
            {
                FrostyProject before = project;
                McpHandlers.InvokeOnUi(() => load.Invoke(window, new object[] { path, false }));
                FrostyProject after = GetProject();
                if (ReferenceEquals(after, before))
                    return McpHandlers.Error("The editor did not load the project (profile mismatch or invalid file)", "LOAD_FAILED");

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["display_name"] = after.DisplayName,
                    ["filename"] = after.Filename,
                    ["profile"] = after.Profile ?? "",
                    ["is_dirty"] = after.IsDirty
                };
            }
            catch (TargetInvocationException tie)
            {
                return McpHandlers.Error((tie.InnerException ?? tie).Message, "LOAD_FAILED");
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "LOAD_FAILED");
            }
        }

        public static object NewProject(JObject p)
        {
            FrostyProject project = GetProject();
            Window window = Application.Current?.MainWindow;
            if (project == null || window == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            bool saveFirst = p.Value<bool?>("save_first") ?? false;
            if (project.IsDirty)
            {
                if (!saveFirst)
                    return McpHandlers.Error("The current project has unsaved changes", "DIRTY_PROJECT");
                if (string.IsNullOrEmpty(project.Filename))
                    return McpHandlers.Error("The current project has no filename to save to", "INVALID_STATE");
                try { project.Save(); }
                catch (Exception ex) { return McpHandlers.Error(ex.Message, "SAVE_FAILED"); }
            }

            MethodInfo create = window.GetType().GetMethod("NewProject",
                BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (create == null)
                return McpHandlers.Error("NewProject is not available in this editor build", "NOT_IMPLEMENTED");

            try
            {
                McpHandlers.InvokeOnUi(() => create.Invoke(window, null));
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["filename"] = GetProject()?.Filename ?? "",
                    ["is_dirty"] = GetProject()?.IsDirty ?? false
                };
            }
            catch (TargetInvocationException tie)
            {
                return McpHandlers.Error((tie.InnerException ?? tie).Message, "NEW_PROJECT_FAILED");
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "NEW_PROJECT_FAILED");
            }
        }

        public static object RevertAll(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");
            if (project.IsDirty && !(p.Value<bool?>("discard") ?? false))
                return McpHandlers.Error("The project has unsaved changes; pass discard=true to drop them", "DIRTY_PROJECT");

            var plan = new Dictionary<string, object>
            {
                ["dirty_count"] = App.AssetManager.GetDirtyCount(),
                ["is_dirty"] = project.IsDirty
            };

            return McpCommit.Require("revert_all", p, plan, () =>
            {
                try
                {
                    McpHandlers.InvokeOnUi(() =>
                    {
                        App.AssetManager.Reset();
                        project.GetModSettings()?.ClearDirtyFlag();
                        RefreshDataExplorer();
                    });
                    return new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["dirty_count"] = App.AssetManager.GetDirtyCount(),
                        ["is_dirty"] = project.IsDirty
                    };
                }
                catch (Exception ex)
                {
                    return McpHandlers.Error(ex.Message, "REVERT_FAILED");
                }
            });
        }

        public static object GetModSettings(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            ModSettings settings = project.GetModSettings();
            if (settings == null)
                return McpHandlers.Error("ModSettings are not available", "NOT_READY");

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["settings"] = SerializeModSettings(settings)
            };
        }

        public static object SetModSettings(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            ModSettings settings = project.GetModSettings();
            if (settings == null)
                return McpHandlers.Error("ModSettings are not available", "NOT_READY");

            string[] textFields = { "title", "author", "category", "version", "description", "link" };
            foreach (string field in textFields)
            {
                string value = p.Value<string>(field);
                if (value == null)
                    continue;
                if ((field == "title" || field == "author" || field == "category" || field == "version") &&
                    (value.IndexOf('{') >= 0 || value.IndexOf('}') >= 0))
                    return McpHandlers.Error(field + " must not contain braces", "INVALID_PARAMS");
            }

            string iconPath = p.Value<string>("icon_path");
            byte[] icon = null;
            if (!string.IsNullOrEmpty(iconPath))
            {
                if (!File.Exists(iconPath))
                    return McpHandlers.Error("icon_path does not exist", "INVALID_PARAMS");
                icon = File.ReadAllBytes(iconPath);
                string iconError = ValidateIcon(icon);
                if (iconError != null)
                    return McpHandlers.Error(iconError, "INVALID_PARAMS");
            }

            var screenshots = new Dictionary<int, byte[]>();
            if (p["screenshot_paths"] is JArray shots)
            {
                for (int i = 0; i < shots.Count && i < 4; i++)
                {
                    string shotPath = shots[i]?.ToString();
                    if (string.IsNullOrEmpty(shotPath))
                        continue;
                    if (!File.Exists(shotPath))
                        return McpHandlers.Error("screenshot does not exist: " + shotPath, "INVALID_PARAMS");
                    byte[] data = File.ReadAllBytes(shotPath);
                    if (data.Length > 5 * 1024 * 1024)
                        return McpHandlers.Error("screenshot exceeds 5 MB: " + shotPath, "INVALID_PARAMS");
                    screenshots[i] = data;
                }
            }

            try
            {
                if (p.Value<string>("title") != null) settings.Title = p.Value<string>("title");
                if (p.Value<string>("author") != null) settings.Author = p.Value<string>("author");
                if (p.Value<string>("category") != null) settings.Category = p.Value<string>("category");
                if (p.Value<string>("version") != null) settings.Version = p.Value<string>("version");
                if (p.Value<string>("description") != null) settings.Description = p.Value<string>("description");
                if (p.Value<string>("link") != null) settings.Link = p.Value<string>("link");
                if (p["selected_category"] != null) settings.SelectedCategory = p.Value<int>("selected_category");
                if (icon != null) settings.Icon = icon;
                foreach (KeyValuePair<int, byte[]> shot in screenshots)
                    settings.SetScreenshot(shot.Key, shot.Value);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["settings"] = SerializeModSettings(settings)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "SET_FAILED");
            }
        }

        public static object ExportMod(JObject p)
        {
            FrostyProject project = GetProject();
            if (project == null)
                return McpHandlers.Error("No Frosty project window is available", "NOT_READY");

            string path = (p.Value<string>("path") ?? "").Trim();
            if (string.IsNullOrEmpty(path))
                return McpHandlers.Error("path is required", "INVALID_PARAMS");
            if (!path.EndsWith(".fbmod", StringComparison.OrdinalIgnoreCase))
                return McpHandlers.Error("path must end with .fbmod", "INVALID_PARAMS");
            if (File.Exists(path) && !(p.Value<bool?>("overwrite") ?? false))
                return McpHandlers.Error("Target file already exists", "ALREADY_EXISTS");

            ModSettings baseSettings = project.GetModSettings();
            ModSettings export = new ModSettings
            {
                Title = p.Value<string>("title") ?? baseSettings?.Title ?? "",
                Author = p.Value<string>("author") ?? baseSettings?.Author ?? "",
                Category = p.Value<string>("category") ?? baseSettings?.Category ?? "",
                SelectedCategory = p.Value<int?>("selected_category") ?? baseSettings?.SelectedCategory ?? 0,
                Version = p.Value<string>("version") ?? baseSettings?.Version ?? "",
                Description = p.Value<string>("description") ?? baseSettings?.Description ?? "",
                Link = p.Value<string>("link") ?? baseSettings?.Link ?? ""
            };

            if (string.IsNullOrWhiteSpace(export.Title))
                return McpHandlers.Error("title is required to export a mod", "INVALID_PARAMS");
            if (string.IsNullOrWhiteSpace(export.Author))
                return McpHandlers.Error("author is required to export a mod", "INVALID_PARAMS");
            if (string.IsNullOrWhiteSpace(export.Category))
                return McpHandlers.Error("category is required to export a mod", "INVALID_PARAMS");
            if (string.IsNullOrWhiteSpace(export.Version))
                return McpHandlers.Error("version is required to export a mod", "INVALID_PARAMS");
            string[] checkedFields = { export.Title, export.Author, export.Category, export.Version };
            foreach (string field in checkedFields)
                if (field.IndexOf('{') >= 0 || field.IndexOf('}') >= 0)
                    return McpHandlers.Error("title/author/category/version must not contain braces", "INVALID_PARAMS");

            try
            {
                byte[] icon = baseSettings?.Icon;
                string iconPath = p.Value<string>("icon_path");
                if (!string.IsNullOrEmpty(iconPath))
                {
                    if (!File.Exists(iconPath))
                        return McpHandlers.Error("icon_path does not exist", "INVALID_PARAMS");
                    icon = File.ReadAllBytes(iconPath);
                    string iconError = ValidateIcon(icon);
                    if (iconError != null)
                        return McpHandlers.Error(iconError, "INVALID_PARAMS");
                }
                if (icon != null)
                    export.Icon = icon;

                for (int i = 0; i < 4; i++)
                {
                    if (baseSettings?.GetScreenshot(i) != null)
                        export.SetScreenshot(i, baseSettings.GetScreenshot(i));
                }

                McpHandlers.InvokeOnUi(() => project.WriteToMod(path, export));
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(ex.Message, "EXPORT_FAILED");
            }
        }

        public static object GetLog(JObject p)
        {
            ILogger logger = App.Logger;
            if (logger == null)
                return McpHandlers.Error("Logger is not available", "NOT_READY");

            PropertyInfo logText = logger.GetType().GetProperty("LogText", BindingFlags.Public | BindingFlags.Instance);
            string text = logText?.GetValue(logger) as string;
            if (text == null)
                return McpHandlers.Error("The active logger does not expose its text", "UNSUPPORTED_LOGGER");

            int maxChars = p.Value<int?>("max_chars") ?? 20000;
            if (maxChars < 1) maxChars = 20000;
            bool truncated = text.Length > maxChars;
            if (truncated)
                text = text.Substring(text.Length - maxChars);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["truncated"] = truncated,
                ["length"] = text.Length,
                ["text"] = text
            };
        }

        public static object ListLinkedAssets(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            AssetEntry entry = ResolveAnyAsset(p);
            if (entry == null)
                return McpHandlers.Error("Asset not found", "NOT_FOUND");

            List<AssetEntry> linked = entry.LinkedAssets.ToList();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["count"] = linked.Count,
                ["linked_assets"] = linked.Select(McpHandlers.SerializeAssetEntry).ToList()
            };
        }

        public static object LinkAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            AssetEntry entry = ResolveAnyAsset(p);
            if (entry == null)
                return McpHandlers.Error("Source asset not found", "NOT_FOUND");

            AssetEntry linked = ResolveLinkedTarget(p);
            if (linked == null)
                return McpHandlers.Error("Linked asset not found", "NOT_FOUND");
            if (ReferenceEquals(entry, linked) ||
                (string.Equals(entry.Name, linked.Name, StringComparison.OrdinalIgnoreCase) &&
                 entry.AssetType == linked.AssetType))
                return McpHandlers.Error("Cannot link an asset to itself", "INVALID_PARAMS");

            bool already = entry.LinkedAssets.Contains(linked);
            if (!already)
            {
                entry.LinkAsset(linked);
                entry.IsDirty = true;
            }
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["linked"] = linked.Name,
                ["added"] = !already,
                ["count"] = entry.LinkedAssets.Count()
            };
        }

        public static object UnlinkAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            AssetEntry entry = ResolveAnyAsset(p);
            if (entry == null)
                return McpHandlers.Error("Source asset not found", "NOT_FOUND");

            AssetEntry linked = ResolveLinkedTarget(p);
            if (linked == null)
                return McpHandlers.Error("Linked asset not found", "NOT_FOUND");

            if (!entry.LinkedAssets.Contains(linked))
                return McpHandlers.Error("Asset is not linked to this target", "NOT_LINKED");

            entry.LinkedAssets.Remove(linked);
            entry.IsDirty = true;
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["unlinked"] = linked.Name,
                ["count"] = entry.LinkedAssets.Count()
            };
        }

        public static object CheckGuidInvariants(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            int limit = p.Value<int?>("limit") ?? 100;
            List<object> mismatches = new List<object>();
            Dictionary<Guid, string> seen = new Dictionary<Guid, string>();
            int scanned = 0;

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { continue; }
                if (asset == null) continue;
                scanned++;

                if (asset.FileGuid != entry.Guid && mismatches.Count < limit)
                {
                    mismatches.Add(new Dictionary<string, object>
                    {
                        ["kind"] = "entry_file_mismatch",
                        ["name"] = entry.Name,
                        ["entry_guid"] = entry.Guid.ToString(),
                        ["file_guid"] = asset.FileGuid.ToString()
                    });
                }

                if (seen.TryGetValue(asset.FileGuid, out string other) && other != entry.Name && mismatches.Count < limit)
                {
                    mismatches.Add(new Dictionary<string, object>
                    {
                        ["kind"] = "duplicate_file_guid",
                        ["name"] = entry.Name,
                        ["other"] = other,
                        ["file_guid"] = asset.FileGuid.ToString()
                    });
                }
                else if (!seen.ContainsKey(asset.FileGuid))
                {
                    seen[asset.FileGuid] = entry.Name;
                }
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["scanned_assets"] = scanned,
                ["issue_count"] = mismatches.Count,
                ["issues"] = mismatches,
                ["truncated"] = mismatches.Count >= limit
            };
        }

        public static object Capabilities(JObject p)
        {
            var plugins = new Dictionary<string, object>();
            string[] required =
            {
                "TexturePlugin.FrostyTextureEditor",
                "MeshSetPlugin.FBXExporter",
                "SvgImagePlugin.SvgImage",
                "IesResourcePlugin.IesResource",
                "DuplicationPlugin.DuplicateAssetExtension",
                "PVZBundleManagerPlugin.BundleManager",
                "AdvancedBundleEditorPlugin.BundleEditors",
                "BlueprintEditorPlugin.ExtensionsManager",
                "Frosty.Core.Windows.FrostyTaskWindow"
            };
            foreach (string typeName in required)
                plugins[typeName] = TypeExists(typeName);

            bool pvz = ProfilesLibrary.ProfileName != null &&
                (ProfilesLibrary.ProfileName.IndexOf("PVZ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 ProfilesLibrary.ProfileName.IndexOf("Plants", StringComparison.OrdinalIgnoreCase) >= 0);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["version"] = McpBridgeServer.Version,
                ["pipe"] = McpBridgeServer.PipeName,
                ["profile"] = ProfilesLibrary.ProfileName ?? "",
                ["profile_data_version"] = ProfilesLibrary.DataVersion,
                ["profile_enable_execution"] = ProfilesLibrary.EnableExecution,
                ["asset_manager_ready"] = App.AssetManager != null,
                ["project_open"] = GetProject() != null,
                ["is_pvz_profile"] = pvz,
                ["plugins"] = plugins,
                ["localization_db"] = LocalizationDatabaseKind()
            };
        }

        private static string LocalizationDatabaseKind()
        {
            Type dbType = FindType("Frosty.Core.LocalizedStringDatabase");
            object current = dbType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return current?.GetType().Name ?? "";
        }

        private static bool TypeExists(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetType(fullName, false) != null)
                    return true;
            return false;
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

        // ---- helpers ----

        private static Dictionary<string, object> SerializeModSettings(ModSettings s)
        {
            return new Dictionary<string, object>
            {
                ["title"] = s.Title,
                ["author"] = s.Author,
                ["category"] = s.Category,
                ["selected_category"] = s.SelectedCategory,
                ["version"] = s.Version,
                ["description"] = s.Description,
                ["link"] = s.Link,
                ["is_dirty"] = s.IsDirty,
                ["has_icon"] = s.Icon != null,
                ["screenshots"] = Enumerable.Range(0, 4).Count(i => s.GetScreenshot(i) != null)
            };
        }

        private static string ValidateIcon(byte[] icon)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(icon))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(ms,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0)
                        return "icon image has no frames";
                    BitmapFrame frame = decoder.Frames[0];
                    if (frame.PixelWidth > 128 || frame.PixelHeight > 128)
                        return "icon must be 128x128 or smaller";
                }
            }
            catch (Exception ex)
            {
                return "icon is not a supported image: " + ex.Message;
            }
            return null;
        }

        private static void RefreshDataExplorer()
        {
            object window = App.EditorWindow;
            PropertyInfo explorer = window?.GetType().GetProperty("DataExplorer", BindingFlags.Public | BindingFlags.Instance);
            object dataExplorer = explorer?.GetValue(window);
            dataExplorer?.GetType().GetMethod("RefreshItems", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(dataExplorer, null);
        }

        private static AssetEntry ResolveAnyAsset(JObject p)
        {
            string kind = (p.Value<string>("kind") ?? "ebx").Trim().ToLowerInvariant();
            string name = p.Value<string>("name");
            string guid = p.Value<string>("guid");
            switch (kind)
            {
                case "ebx":
                    return McpHandlers.ResolveEbxEntry(name, guid);
                case "res":
                    if (!string.IsNullOrEmpty(guid) && ulong.TryParse(guid, out ulong rid))
                        return App.AssetManager.GetResEntry(rid);
                    return string.IsNullOrEmpty(name) ? null : App.AssetManager.GetResEntry(name);
                case "chunk":
                    if (!string.IsNullOrEmpty(guid) && Guid.TryParse(guid, out Guid cid))
                        return App.AssetManager.GetChunkEntry(cid);
                    if (!string.IsNullOrEmpty(name) && Guid.TryParse(name, out Guid cid2))
                        return App.AssetManager.GetChunkEntry(cid2);
                    return null;
                default:
                    return null;
            }
        }

        private static AssetEntry ResolveLinkedTarget(JObject p)
        {
            string linkedKind = (p.Value<string>("linked_kind") ?? "res").Trim().ToLowerInvariant();
            string linkedName = p.Value<string>("linked_name") ?? p.Value<string>("linked");
            string linkedGuid = p.Value<string>("linked_guid");

            switch (linkedKind)
            {
                case "ebx":
                    return McpHandlers.ResolveEbxEntry(linkedName, linkedGuid);
                case "res":
                    if (!string.IsNullOrEmpty(linkedGuid) && ulong.TryParse(linkedGuid, out ulong rid))
                        return App.AssetManager.GetResEntry(rid);
                    return string.IsNullOrEmpty(linkedName) ? null : App.AssetManager.GetResEntry(linkedName);
                case "chunk":
                    if (!string.IsNullOrEmpty(linkedGuid) && Guid.TryParse(linkedGuid, out Guid cid))
                        return App.AssetManager.GetChunkEntry(cid);
                    if (!string.IsNullOrEmpty(linkedName) && Guid.TryParse(linkedName, out Guid cid2))
                        return App.AssetManager.GetChunkEntry(cid2);
                    return null;
                default:
                    return null;
            }
        }
    }
}
