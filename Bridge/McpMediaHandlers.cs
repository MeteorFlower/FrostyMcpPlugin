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
using System.Text.RegularExpressions;
using System.Xml;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Media / deep-duplicate / lua tools via reflection into
    /// LuaPlugin, AtlasTexturePlugin, SvgImagePlugin, DuplicationPlugin, SoundEditorPlugin.
    /// </summary>
    internal static class McpMediaHandlers
    {
        // =====================================================================
        // Lua
        // =====================================================================

        public static object GetLuaSource(JObject p)
        {
            if (!TryLoadLua(p, out EbxAssetEntry entry, out ResAssetEntry resEntry, out object luaRes, out object err))
                return err;

            try
            {
                string code = (string)InvokeCompat(luaRes, "DecompileBytecode");
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["entrypoint"] = GetProp(luaRes, "EntrypointName"),
                    ["parameters"] = GetProp(luaRes, "Parameters"),
                    ["bytecode_length"] = GetProp(luaRes, "BytecodeLength"),
                    ["source"] = code
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "DECOMPILE_FAILED");
            }
        }

        public static object ExportLua(JObject p)
        {
            if (!TryLoadLua(p, out EbxAssetEntry entry, out ResAssetEntry resEntry, out object luaRes, out object err))
                return err;

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required (.lua or .luares)", "INVALID_PARAMS");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".luares")
                {
                    byte[] bytes = (byte[])InvokeCompat(luaRes, "SaveBytes");
                    File.WriteAllBytes(path, bytes);
                }
                else
                {
                    string code = (string)InvokeCompat(luaRes, "DecompileBytecode");
                    File.WriteAllText(path, code ?? "", Encoding.UTF8);
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "EXPORT_FAILED");
            }
        }

        public static object ImportLua(JObject p)
        {
            if (!TryLoadLua(p, out EbxAssetEntry entry, out ResAssetEntry resEntry, out object luaRes, out object err))
                return err;

            string path = p.Value<string>("path");
            string source = p.Value<string>("source");
            string body = p.Value<string>("body"); // function body only (BF1 style)
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrEmpty(source) && string.IsNullOrEmpty(body))
                return McpHandlers.Error("path (.lua), source, or body is required", "INVALID_PARAMS");

            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (!File.Exists(path))
                        return McpHandlers.Error("File not found: " + path, "NOT_FOUND");
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".luares")
                        return McpHandlers.Error("Import of a raw .luares file is not supported", "UNSUPPORTED");
                    source = File.ReadAllText(path, Encoding.UTF8);
                }
                else if (!string.IsNullOrEmpty(body))
                {
                    source = body;
                }

                string guidName = (p.Value<string>("guid_name") ?? "").Trim();
                if (string.IsNullOrEmpty(guidName))
                {
                    EbxAsset assetForGuid = App.AssetManager.GetEbx(entry);
                    guidName = FormatLuaGuidName(assetForGuid.RootInstanceGuid);
                }

                bool wrap = p.Value<bool?>("wrap_entrypoint") ?? true;
                string trimmed = (source ?? "").TrimStart();
                bool alreadyWrapped = trimmed.StartsWith("function ", StringComparison.OrdinalIgnoreCase);
                if (wrap && !alreadyWrapped)
                {
                    source = "function CompiledLua_" + guidName +
                             "(self, event, vars, sharedvars, globalvars, deltaTime)\n" +
                             (source ?? "") + "\nend";
                }

                string[] lines = (source ?? "").Replace("\r\n", "\n").Split('\n');
                InvokeCompat(luaRes, "CompileSource", new object[] { lines });

                object len = GetProp(luaRes, "BytecodeLength");
                if (len == null || Convert.ToUInt32(len) == 0)
                    return McpHandlers.Error("Lua compilation failed (empty bytecode). Check syntax / luacmp.dll.", "COMPILE_FAILED");

                // Force entrypoint name to CompiledLua_{guid} (BF1 requirement)
                SetMember(luaRes, "EntrypointName", "CompiledLua_" + guidName);

                App.AssetManager.ModifyRes(resEntry.Name, (Resource)luaRes);
                entry.LinkAsset(resEntry);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["guid_name"] = guidName,
                    ["entrypoint"] = GetProp(luaRes, "EntrypointName"),
                    ["bytecode_length"] = GetProp(luaRes, "BytecodeLength"),
                    ["wrapped"] = wrap && !alreadyWrapped
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "IMPORT_FAILED");
            }
        }

        /// <summary>
        /// Create LuaRunnerCompiledLua EBX + CompiledLua RES by duplicating a template
        /// (Frostbite cannot create Lua from empty, same as BF1DevPlugin).
        /// </summary>
        public static object CreateLuaAsset(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string newName = (p.Value<string>("new_name") ?? p.Value<string>("name") ?? "").Trim().Trim('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(newName))
                return McpHandlers.Error("new_name is required (e.g. Lua/LuaRunnerCompiledLua_MyScript_Win32)", "INVALID_PARAMS");

            if (App.AssetManager.GetEbxEntry(newName) != null)
                return McpHandlers.Error("Target already exists: " + newName, "EXISTS");

            string template = (p.Value<string>("template") ?? "").Trim();
            EbxAssetEntry templateEntry = null;
            if (!string.IsNullOrEmpty(template))
                templateEntry = McpHandlers.ResolveEbxEntry(template, p.Value<string>("template_guid"));
            else
            {
                // Prefer BF1 conquest template if present, else first LuaRunnerCompiledLua
                string preferred = "Gameplay/GameModes/Conquest/Conquest/LuaRunnerCompiledLua_FA45F1E3_6E45_43B0_A366_B06BD560F361_Win32";
                templateEntry = App.AssetManager.GetEbxEntry(preferred);
                if (templateEntry == null)
                {
                    foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(type: "LuaRunnerCompiledLua"))
                    {
                        templateEntry = e;
                        break;
                    }
                }
            }

            if (templateEntry == null)
                return McpHandlers.Error("No LuaRunnerCompiledLua template found (provide template=)", "NOT_FOUND");

            // Reuse deep duplicate
            var dupParams = new JObject
            {
                ["name"] = templateEntry.Name,
                ["guid"] = templateEntry.Guid.ToString(),
                ["new_name"] = newName
            };
            object dupResult = DuplicateDeep(dupParams);
            if (dupResult is Dictionary<string, object> d && d.ContainsKey("success") && d["success"] is bool ok && !ok)
                return dupResult;

            EbxAssetEntry newEntry = App.AssetManager.GetEbxEntry(newName);
            if (newEntry == null)
                return McpHandlers.Error("Duplicate appeared to succeed but entry missing", "CREATE_FAILED");

            EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
            string guidName = FormatLuaGuidName(newAsset.RootInstanceGuid);

            // Optional immediate script write
            if (p["source"] != null || p["body"] != null || p["path"] != null)
            {
                var importParams = new JObject
                {
                    ["name"] = newName,
                    ["guid_name"] = guidName,
                    ["wrap_entrypoint"] = p.Value<bool?>("wrap_entrypoint") ?? true
                };
                if (p["source"] != null) importParams["source"] = p["source"];
                if (p["body"] != null) importParams["body"] = p["body"];
                if (p["path"] != null) importParams["path"] = p["path"];
                object importResult = ImportLua(importParams);
                if (importResult is Dictionary<string, object> ir && ir.ContainsKey("success") && ir["success"] is bool iok && !iok)
                    return importResult;
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["template"] = templateEntry.Name,
                ["name"] = newEntry.Name,
                ["guid"] = newEntry.Guid.ToString(),
                ["root_class_guid"] = newAsset.RootInstanceGuid.ToString(),
                ["guid_name"] = guidName,
                ["entrypoint"] = "CompiledLua_" + guidName,
                ["pointer"] = new Dictionary<string, object>
                {
                    ["ref_type"] = "External",
                    ["file_guid"] = newEntry.Guid.ToString(),
                    ["class_guid"] = newAsset.RootInstanceGuid.ToString(),
                    ["asset"] = newEntry.Name
                },
                ["hint"] = "Bind with create_lua_runner_component(compiled_lua_name=...) then import_lua(body=...)."
            };
        }

        /// <summary>
        /// Create LuaRunnerScriptEntityData in a blueprint and point CompiledLua at a LuaRunnerCompiledLua asset.
        /// </summary>
        public static object CreateLuaRunnerComponent(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            // Host blueprint
            EbxAssetEntry hostEntry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (hostEntry == null)
                return McpHandlers.Error("Host blueprint EBX not found", "NOT_FOUND");

            string luaName = (p.Value<string>("compiled_lua_name") ?? p.Value<string>("lua_name") ?? "").Trim();
            string luaGuid = (p.Value<string>("compiled_lua_guid") ?? "").Trim();
            if (string.IsNullOrEmpty(luaName) && string.IsNullOrEmpty(luaGuid))
                return McpHandlers.Error("compiled_lua_name (or compiled_lua_guid) is required", "INVALID_PARAMS");

            EbxAssetEntry luaEntry = McpHandlers.ResolveEbxEntry(luaName, string.IsNullOrEmpty(luaGuid) ? null : luaGuid);
            if (luaEntry == null)
                return McpHandlers.Error("LuaRunnerCompiledLua asset not found", "NOT_FOUND");

            try
            {
                EbxAsset hostAsset = App.AssetManager.GetEbx(hostEntry);
                EbxAsset luaAsset = App.AssetManager.GetEbx(luaEntry);

                string typeName = "LuaRunnerScriptEntityData";
                if (TypeLibrary.GetType(typeName) == null)
                    return McpHandlers.Error("LuaRunnerScriptEntityData not in SDK", "TYPE_NOT_FOUND");

                object newObject = TypeLibrary.CreateObject(typeName);
                AssetClassGuid classGuid = new AssetClassGuid(
                    Utils.GenerateDeterministicGuid(hostAsset.Objects, typeName, hostAsset.FileGuid), -1);
                ((dynamic)newObject).SetInstanceGuid(classGuid);

                if (TypeLibrary.IsSubClassOf(newObject, "DataBusPeer"))
                {
                    byte[] bytes = classGuid.ExportedGuid.ToByteArray();
                    uint flagValue = (uint)((bytes[2] << 16) | (bytes[1] << 8) | bytes[0]);
                    PropertyInfo flagsPi = newObject.GetType().GetProperty("Flags");
                    if (flagsPi != null && flagsPi.CanWrite)
                        flagsPi.SetValue(newObject, flagValue);
                }

                // Realm and flags presets arrive as JObjects shared with the blueprint helpers; apply them as given.
                if (p["realm"] != null)
                {
                    try
                    {
                        PropertyInfo realmPi = newObject.GetType().GetProperty("Realm");
                        if (realmPi != null)
                        {
                            object ev = Enum.Parse(realmPi.PropertyType, p.Value<string>("realm"), true);
                            realmPi.SetValue(newObject, ev);
                        }
                    }
                    catch
                    {
                        try
                        {
                            PropertyInfo realmPi = newObject.GetType().GetProperty("Realm");
                            object ev = Enum.Parse(realmPi.PropertyType, "Realm_" + p.Value<string>("realm"), true);
                            realmPi.SetValue(newObject, ev);
                        }
                        catch { }
                    }
                }

                string script = p.Value<string>("script") ?? "";
                try
                {
                    PropertyInfo scriptPi = newObject.GetType().GetProperty("Script");
                    if (scriptPi != null)
                        scriptPi.SetValue(newObject, (CString)script);
                }
                catch { }

                AppendCStringList(newObject, "InputEvents", p["input_events"] as JArray);
                AppendCStringList(newObject, "OutputEvents", p["output_events"] as JArray);
                AppendCStringList(newObject, "InputFloatProperties", p["input_float_properties"] as JArray);
                AppendCStringList(newObject, "OutputFloatProperties", p["output_float_properties"] as JArray);
                AppendCStringList(newObject, "InputIntProperties", p["input_int_properties"] as JArray);
                AppendCStringList(newObject, "OutputIntProperties", p["output_int_properties"] as JArray);
                AppendCStringList(newObject, "InputStringProperties", p["input_string_properties"] as JArray);
                AppendCStringList(newObject, "OutputStringProperties", p["output_string_properties"] as JArray);
                AppendCStringList(newObject, "InputBoolProperties", p["input_bool_properties"] as JArray);
                AppendCStringList(newObject, "OutputBoolProperties", p["output_bool_properties"] as JArray);
                AppendCStringList(newObject, "InputTransformProperties", p["input_transform_properties"] as JArray);
                AppendCStringList(newObject, "OutputTransformProperties", p["output_transform_properties"] as JArray);

                SetBoolProp(newObject, "AutoStartExecutingPerFrame", p.Value<bool?>("auto_start_per_frame") ?? false);
                SetBoolProp(newObject, "AutoStartForInitialization", p.Value<bool?>("auto_start_init") ?? false);
                SetBoolProp(newObject, "RunOnPropertyChange", p.Value<bool?>("run_on_property_change") ?? false);

                // External pointer to Lua asset root
                var importRef = new EbxImportReference
                {
                    FileGuid = luaEntry.Guid,
                    ClassGuid = luaAsset.RootInstanceGuid
                };
                PropertyInfo compiledPi = newObject.GetType().GetProperty("CompiledLua");
                if (compiledPi == null)
                    return McpHandlers.Error("CompiledLua property missing on LuaRunnerScriptEntityData", "TYPE_MISMATCH");
                compiledPi.SetValue(newObject, new PointerRef(importRef));

                hostAsset.AddObject(newObject);
                PointerRef pref = new PointerRef(newObject);
                string arrayName = p.Value<string>("array") ?? "Objects";
                bool added = TryAddPointer(hostAsset.RootObject, arrayName, pref);
                if (!added && arrayName == "Objects")
                    added = TryAddPointer(hostAsset.RootObject, "Components", pref);

                hostAsset.Update();
                App.AssetManager.ModifyEbx(hostEntry.Name, hostAsset);

                string guidName = FormatLuaGuidName(luaAsset.RootInstanceGuid);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["host"] = hostEntry.Name,
                    ["added_to_array"] = added ? arrayName : null,
                    ["component_class_guid"] = classGuid.ExportedGuid.ToString(),
                    ["component_type"] = typeName,
                    ["compiled_lua"] = luaEntry.Name,
                    ["compiled_lua_file_guid"] = luaEntry.Guid.ToString(),
                    ["compiled_lua_class_guid"] = luaAsset.RootInstanceGuid.ToString(),
                    ["guid_name"] = guidName,
                    ["entrypoint"] = "CompiledLua_" + guidName,
                    ["pointer"] = new Dictionary<string, object>
                    {
                        ["ref_type"] = "Internal",
                        ["class_guid"] = classGuid.ExportedGuid.ToString(),
                        ["type"] = typeName
                    }
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "CREATE_FAILED");
            }
        }

        /// <summary>
        /// Full pipeline: duplicate the Lua EBX and RES, create the runner component, then compile the script body.
        /// </summary>
        public static object SetupLua(JObject p)
        {
            string newLuaName = (p.Value<string>("lua_name") ?? p.Value<string>("new_name") ?? "").Trim().Trim('/');
            string hostName = (p.Value<string>("blueprint") ?? p.Value<string>("host") ?? p.Value<string>("name") ?? "").Trim();
            if (string.IsNullOrEmpty(newLuaName))
                return McpHandlers.Error("lua_name is required", "INVALID_PARAMS");
            if (string.IsNullOrEmpty(hostName))
                return McpHandlers.Error("blueprint (host EBX) is required", "INVALID_PARAMS");

            // 1) Create lua asset (+ optional body)
            var createParams = new JObject
            {
                ["new_name"] = newLuaName,
                ["template"] = p.Value<string>("template") ?? "",
                ["wrap_entrypoint"] = true
            };
            if (p["template_guid"] != null) createParams["template_guid"] = p["template_guid"];
            // Compile is deferred until create returns guid_name; the body can still be passed to create.
            object createResult = CreateLuaAsset(createParams);
            var createDict = createResult as Dictionary<string, object>;
            if (createDict == null || !(createDict["success"] is bool cok && cok))
                return createResult;

            string guidName = createDict["guid_name"]?.ToString();

            // 2) Create component bound to lua
            var compParams = new JObject
            {
                ["name"] = hostName,
                ["guid"] = p.Value<string>("guid") ?? "",
                ["compiled_lua_name"] = newLuaName,
                ["realm"] = p.Value<string>("realm") ?? "ClientAndServer",
                ["script"] = p.Value<string>("script") ?? "",
                ["array"] = p.Value<string>("array") ?? "Objects"
            };
            CopyArrayParam(p, compParams, "input_events");
            CopyArrayParam(p, compParams, "output_events");
            CopyArrayParam(p, compParams, "input_float_properties");
            CopyArrayParam(p, compParams, "output_float_properties");
            CopyArrayParam(p, compParams, "input_int_properties");
            CopyArrayParam(p, compParams, "output_int_properties");
            CopyArrayParam(p, compParams, "input_bool_properties");
            CopyArrayParam(p, compParams, "output_bool_properties");
            CopyArrayParam(p, compParams, "input_string_properties");
            CopyArrayParam(p, compParams, "output_string_properties");
            CopyArrayParam(p, compParams, "input_transform_properties");
            CopyArrayParam(p, compParams, "output_transform_properties");
            if (p["auto_start_per_frame"] != null) compParams["auto_start_per_frame"] = p["auto_start_per_frame"];
            if (p["auto_start_init"] != null) compParams["auto_start_init"] = p["auto_start_init"];

            object compResult = CreateLuaRunnerComponent(compParams);
            var compDict = compResult as Dictionary<string, object>;
            if (compDict == null || !(compDict["success"] is bool bok && bok))
                return compResult;

            // 3) Compile script if provided
            object importResult = null;
            if (p["source"] != null || p["body"] != null || p["path"] != null)
            {
                var importParams = new JObject
                {
                    ["name"] = newLuaName,
                    ["guid_name"] = guidName,
                    ["wrap_entrypoint"] = true
                };
                if (p["source"] != null) importParams["source"] = p["source"];
                if (p["body"] != null) importParams["body"] = p["body"];
                if (p["path"] != null) importParams["path"] = p["path"];
                importResult = ImportLua(importParams);
                var ir = importResult as Dictionary<string, object>;
                if (ir == null || !(ir["success"] is bool iok && iok))
                    return importResult;
            }

            // Summarize rather than nesting three full handler responses.
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["lua_asset"] = new Dictionary<string, object>
                {
                    ["name"] = createDict.TryGetValue("name", out object ln) ? ln : newLuaName,
                    ["guid"] = createDict.TryGetValue("guid", out object lg) ? lg : null
                },
                ["guid_name"] = guidName,
                ["entrypoint"] = "CompiledLua_" + guidName
            };
            if (compDict != null)
            {
                result["component"] = new Dictionary<string, object>
                {
                    ["class_guid"] = compDict.TryGetValue("class_guid", out object cg) ? cg : null,
                    ["added_to_array"] = compDict.TryGetValue("added_to_array", out object aa) ? aa : null
                };
            }
            if (importResult is Dictionary<string, object> importDict)
            {
                result["compile"] = new Dictionary<string, object>
                {
                    ["success"] = importDict.TryGetValue("success", out object cs) ? cs : null,
                    ["bytes"] = importDict.TryGetValue("bytes", out object cb) ? cb : null
                };
            }
            return result;
        }

        public static object GetLuaGuidName(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            string guidStr = (p.Value<string>("class_guid") ?? p.Value<string>("guid") ?? "").Trim();
            if (!string.IsNullOrEmpty(guidStr) && Guid.TryParse(guidStr, out Guid g) &&
                string.IsNullOrEmpty(p.Value<string>("name")))
            {
                string gn = FormatLuaGuidName(g);
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["class_guid"] = g.ToString(),
                    ["guid_name"] = gn,
                    ["entrypoint"] = "CompiledLua_" + gn
                };
            }

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            Guid root = asset.RootInstanceGuid;
            if (!string.IsNullOrEmpty(p.Value<string>("class_guid")) && Guid.TryParse(p.Value<string>("class_guid"), out Guid cg))
                root = cg;
            string name = FormatLuaGuidName(root);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = entry.Name,
                ["class_guid"] = root.ToString(),
                ["guid_name"] = name,
                ["entrypoint"] = "CompiledLua_" + name
            };
        }

        // =====================================================================
        // Atlas texture
        // =====================================================================

        public static object ExportAtlasTexture(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Atlas EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required (.dds)", "INVALID_PARAMS");

            try
            {
                if (!TryGetAtlasTexture(entry, out object texture, out ResAssetEntry resEntry, out object err))
                    return err;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

                ushort width = Convert.ToUInt16(GetProp(texture, "Width"));
                ushort height = Convert.ToUInt16(GetProp(texture, "Height"));
                int mipCount = Convert.ToInt32(GetProp(texture, "MipCount"));
                Stream data = (Stream)GetProp(texture, "Data");

                var header = new TextureUtils.DDSHeader
                {
                    dwHeight = height,
                    dwWidth = width,
                    dwMipMapCount = mipCount,
                    dwPitchOrLinearSize = (int)data.Length
                };
                header.ddspf.dwFourCC = 0x35545844; // DXT5

                data.Position = 0;
                using (NativeWriter writer = new NativeWriter(new FileStream(path, FileMode.Create)))
                {
                    header.Write(writer);
                    byte[] tmpBuf = new byte[data.Length];
                    data.Read(tmpBuf, 0, tmpBuf.Length);
                    writer.Write(tmpBuf);
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["width"] = width,
                    ["height"] = height,
                    ["mip_count"] = mipCount,
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "EXPORT_FAILED");
            }
        }

        public static object ImportAtlasTexture(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Atlas EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (.dds) is required", "INVALID_PARAMS");

            try
            {
                if (!TryGetAtlasTexture(entry, out object texture, out ResAssetEntry resEntry, out object err))
                    return err;

                TextureUtils.DDSHeader header = new TextureUtils.DDSHeader();
                byte[] pixelData;
                using (NativeReader reader = new NativeReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
                {
                    if (!header.Read(reader))
                        return McpHandlers.Error("Invalid DDS header", "INVALID_DDS");
                    pixelData = reader.ReadToEnd();
                }

                // DXT5 / BC3 check (mirror AtlasTexturePlugin; avoid SharpDX reference)
                bool isDxt5 = header.ddspf.dwFourCC == 0x35545844; // 'DXT5'
                bool isBc3 = false;
                if (header.HasExtendedHeader)
                {
                    try
                    {
                        object fmt = header.ExtendedHeader.GetType()
                            .GetField("dxgiFormat")?.GetValue(header.ExtendedHeader)
                            ?? header.ExtendedHeader.GetType()
                            .GetProperty("dxgiFormat")?.GetValue(header.ExtendedHeader);
                        string fs = fmt?.ToString() ?? "";
                        isBc3 = fs.IndexOf("BC3", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { }
                }
                if (!isDxt5 && !isBc3)
                    return McpHandlers.Error("Atlas textures must be DXT5 (BC3) format", "FORMAT");

                if (header.dwWidth > 16384 || header.dwHeight > 16384)
                    return McpHandlers.Error("Atlas textures cannot exceed 16384px", "SIZE");

                Guid chunkId = (Guid)GetProp(texture, "ChunkId");
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
                if (chunkEntry == null)
                    return McpHandlers.Error("Atlas chunk not found", "NOT_FOUND");

                App.AssetManager.ModifyChunk(chunkEntry.Id, pixelData);

                // AtlasTexture(other) copy ctor + SetData
                Type atlasType = texture.GetType();
                object newTexture = Activator.CreateInstance(atlasType, texture);
                InvokeCompat(newTexture, "SetData", header.dwWidth, header.dwHeight, chunkEntry.Id, App.AssetManager);

                ulong resRid = resEntry.ResRid;
                App.AssetManager.ModifyRes(resRid, (Resource)newTexture);
                resEntry.LinkAsset(App.AssetManager.GetChunkEntry(chunkId));
                entry.LinkAsset(resEntry);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["width"] = header.dwWidth,
                    ["height"] = header.dwHeight,
                    ["chunk_id"] = chunkId.ToString(),
                    ["bytes"] = pixelData.Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "IMPORT_FAILED");
            }
        }

        // =====================================================================
        // SVG export
        // =====================================================================

        public static object ExportSvg(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("SvgImage EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path))
                return McpHandlers.Error("path is required (.svg)", "INVALID_PARAMS");

            try
            {
                Type svgType = FindType("SvgImagePlugin.SvgImage");
                if (svgType == null)
                    return McpHandlers.Error("SvgImagePlugin not loaded", "PLUGIN_MISSING");

                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                ulong rid = Convert.ToUInt64(root.Resource);
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                object image = GetResAs(svgType, resEntry);

                float width = Convert.ToSingle(GetProp(image, "Width"));
                float height = Convert.ToSingle(GetProp(image, "Height"));
                IEnumerable shapes = (IEnumerable)GetProp(image, "Shapes");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.AppendChild(xmlDoc.CreateXmlDeclaration("1.0", "UTF-8", "no"));
                xmlDoc.AppendChild(xmlDoc.CreateDocumentType("svg", "-//W3C//DTD SVG 20010904//EN",
                    "http://www.w3.org/TR/2001/REC-SVG-20010904/DTD/svg10.dtd", ""));

                XmlElement svgElem = xmlDoc.CreateElement("svg");
                svgElem.SetAttribute("version", "1.1");
                svgElem.SetAttribute("xmlns", "http://www.w3.org/2000/svg");
                svgElem.SetAttribute("viewBox", "0 0 " + (int)width + " " + (int)height);
                xmlDoc.AppendChild(svgElem);

                foreach (object shape in shapes)
                {
                    StringBuilder sb = new StringBuilder();
                    IEnumerable paths = (IEnumerable)GetProp(shape, "Paths");
                    foreach (object spath in paths)
                    {
                        IList points = (IList)GetProp(spath, "Points");
                        if (points == null || points.Count == 0) continue;
                        object p0 = points[0];
                        sb.Append("M " + GetProp(p0, "X") + "," + GetProp(p0, "Y") + " ");
                        sb.Append("C ");
                        for (int i = 1; i < points.Count; i++)
                        {
                            object pt = points[i];
                            sb.Append(GetProp(pt, "X") + "," + GetProp(pt, "Y") + " ");
                        }
                        bool closed = false;
                        try { closed = Convert.ToBoolean(GetProp(spath, "Closed")); } catch { }
                        if (closed) sb.Append("Z ");
                    }

                    XmlElement pathElem = xmlDoc.CreateElement("path");
                    pathElem.SetAttribute("stroke", "none");
                    pathElem.SetAttribute("fill", "none");

                    bool stroke = false, fill = false;
                    try { stroke = Convert.ToBoolean(GetProp(shape, "Stroke")); } catch { }
                    try { fill = Convert.ToBoolean(GetProp(shape, "Fill")); } catch { }
                    if (stroke)
                    {
                        uint sc = Convert.ToUInt32(GetProp(shape, "StrokeColor"));
                        pathElem.SetAttribute("stroke", "#" + sc.ToString("x8"));
                        pathElem.SetAttribute("stroke-width", Convert.ToString(GetProp(shape, "Thickness")));
                    }
                    if (fill)
                    {
                        uint fc = Convert.ToUInt32(GetProp(shape, "FillColor"));
                        pathElem.SetAttribute("fill", "#" + fc.ToString("x8"));
                    }
                    pathElem.SetAttribute("d", sb.ToString());
                    svgElem.AppendChild(pathElem);
                }

                xmlDoc.Save(path);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["width"] = width,
                    ["height"] = height,
                    ["size"] = new FileInfo(path).Length
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "EXPORT_FAILED");
            }
        }

        // =====================================================================
        // SVG import (mirrors SvgImagePlugin's path parser)
        // =====================================================================

        public static object ImportSvg(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("SvgImage EBX not found", "NOT_FOUND");

            string path = p.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return McpHandlers.Error("path (.svg) is required", "INVALID_PARAMS");

            Type svgType = FindType("SvgImagePlugin.SvgImage");
            Type shapeType = FindType("SvgImagePlugin.SvgShape");
            Type pathType = FindType("SvgImagePlugin.SvgPath");
            if (svgType == null || shapeType == null || pathType == null)
                return McpHandlers.Error("SvgImagePlugin not loaded", "PLUGIN_MISSING");

            XmlDocument xmlDoc = new XmlDocument();
            try
            {
                xmlDoc.Load(path);
            }
            catch (Exception ex)
            {
                return McpHandlers.Error("SVG is not valid XML: " + ex.Message, "INVALID_PARAMS");
            }

            XmlElement root = xmlDoc["svg"];
            if (root == null)
                return McpHandlers.Error("SVG has no svg element", "INVALID_PARAMS");

            float width, height;
            if (root.HasAttribute("viewBox"))
            {
                string[] values = root.Attributes["viewBox"].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < 4 ||
                    !float.TryParse(values[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out width) ||
                    !float.TryParse(values[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out height))
                    return McpHandlers.Error("viewBox must contain four numbers", "INVALID_PARAMS");
            }
            else
            {
                if (!root.HasAttribute("width") || !root.HasAttribute("height"))
                    return McpHandlers.Error("SVG needs a viewBox or width and height", "INVALID_PARAMS");
                if (!float.TryParse(root.Attributes["width"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out width) ||
                    !float.TryParse(root.Attributes["height"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out height))
                    return McpHandlers.Error("width/height must be numbers", "INVALID_PARAMS");
            }

            object image = Activator.CreateInstance(svgType, new object[] { width, height });
            ConstructorInfo shapeConstructor = shapeType.GetConstructor(Type.EmptyTypes);
            ConstructorInfo pathConstructor = pathType.GetConstructor(Type.EmptyTypes);
            MethodInfo addPoint = pathType.GetMethod("AddPoint");
            MethodInfo addPaths = shapeType.GetMethod("AddPaths");
            MethodInfo addShape = svgType.GetMethod("AddShape");
            if (shapeConstructor == null || pathConstructor == null || addPoint == null || addPaths == null || addShape == null)
                return McpHandlers.Error("SvgImagePlugin API changed", "PLUGIN_MISSING");

            var warnings = new List<string>();
            try
            {
                CollectSvgShapes(root, image, shapeConstructor, pathType, pathConstructor,
                    addPoint, addPaths, addShape, warnings);
            }
            catch (Exception ex)
            {
                return McpHandlers.Error("SVG could not be parsed: " + Unwrap(ex), "IMPORT_FAILED");
            }

            try
            {
                dynamic rootObject = App.AssetManager.GetEbx(entry).RootObject;
                ulong resRid = Convert.ToUInt64(rootObject.Resource);
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                if (resEntry == null)
                    return McpHandlers.Error("SvgImage resource not found", "NOT_FOUND");

                App.AssetManager.ModifyRes(resRid, (FrostySdk.Resources.Resource)image);
                entry.LinkAsset(resEntry);

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["res"] = resEntry.Name,
                    ["path"] = Path.GetFullPath(path),
                    ["width"] = width,
                    ["height"] = height,
                    ["shape_count"] = ((IEnumerable)GetProp(image, "Shapes")).Cast<object>().Count(),
                    ["warnings"] = warnings,
                    ["warning_count"] = warnings.Count
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "IMPORT_FAILED");
            }
        }

        private static void CollectSvgShapes(XmlNode parentNode, object image, ConstructorInfo shapeConstructor,
            Type pathType, ConstructorInfo pathConstructor, MethodInfo addPoint, MethodInfo addPaths,
            MethodInfo addShape, List<string> warnings)
        {
            foreach (XmlNode node in parentNode)
            {
                if (!(node is XmlElement subElem))
                    continue;

                if (subElem.Name == "g")
                {
                    CollectSvgShapes(subElem, image, shapeConstructor, pathType, pathConstructor,
                        addPoint, addPaths, addShape, warnings);
                    continue;
                }

                if (subElem.Name != "path")
                {
                    warnings.Add("Unsupported element: " + subElem.Name);
                    continue;
                }

                float thickness = 1.0f;
                uint fillColor = 0;
                uint strokeColor = 0;
                if (subElem.HasAttribute("stroke-width"))
                    float.TryParse(subElem.Attributes["stroke-width"].Value,
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out thickness);
                if (subElem.HasAttribute("fill") && subElem.Attributes["fill"].Value != "none")
                    fillColor = SvgColor(subElem.Attributes["fill"].Value);
                if (subElem.HasAttribute("stroke") && subElem.Attributes["stroke"].Value != "none")
                    strokeColor = SvgColor(subElem.Attributes["stroke"].Value);

                string dataText = subElem.HasAttribute("d") ? subElem.Attributes["d"].Value : "";
                List<object> paths = ParseSvgPathData(dataText, pathType, pathConstructor, addPoint, warnings);
                if (paths.Count == 0)
                    continue;

                object shape = shapeConstructor.Invoke(null);
                SetIfWritable(shape, "Fill", fillColor != 0);
                SetIfWritable(shape, "FillColor", fillColor);
                SetIfWritable(shape, "Stroke", strokeColor != 0);
                SetIfWritable(shape, "StrokeColor", strokeColor);
                SetIfWritable(shape, "Thickness", thickness);

                Type listType = typeof(List<>).MakeGenericType(pathType);
                IList typedPaths = (IList)Activator.CreateInstance(listType);
                foreach (object item in paths)
                    typedPaths.Add(item);
                addPaths.Invoke(shape, new object[] { typedPaths });

                addShape.Invoke(image, new[] { shape });
            }
        }

        private static void SetIfWritable(object target, string property, object value)
        {
            PropertyInfo pi = target.GetType().GetProperty(property);
            if (pi != null && pi.CanWrite)
                pi.SetValue(target, value);
        }

        private static uint SvgColor(string colorString)
        {
            if (string.IsNullOrEmpty(colorString) || colorString[0] != '#')
                return 0;

            string hex = colorString.Substring(1);
            int r, g, b, a = 0xFF;
            try
            {
                if (hex.Length <= 4)
                {
                    r = Convert.ToInt32("0" + hex.Substring(0, 1), 16) * 17;
                    g = Convert.ToInt32("0" + hex.Substring(1, 1), 16) * 17;
                    b = Convert.ToInt32("0" + hex.Substring(2, 1), 16) * 17;
                    if (hex.Length == 4)
                        a = Convert.ToInt32("0" + hex.Substring(3, 1), 16) * 17;
                }
                else
                {
                    r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    b = Convert.ToInt32(hex.Substring(4, 2), 16);
                    if (hex.Length == 8)
                        a = Convert.ToInt32(hex.Substring(6, 2), 16);
                }
            }
            catch
            {
                return 0;
            }
            return (uint)((a << 24) | (b << 16) | (g << 8) | r);
        }

        private static readonly Regex s_svgNumber = new Regex(
            "-*[0-9]+(([e]{1}[-]*([0-9]{0,12}))|([.]*(([0-9]{0,12}[ ,]{1})|([0-9]{0,3}))))",
            RegexOptions.Compiled);

        private static float AsFloat(object host, string property)
        {
            object value = host?.GetType().GetProperty(property)?.GetValue(host);
            return value == null ? 0f : Convert.ToSingle(value);
        }
        private static float SvgFloat(string text)
        {
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;
        }

        private static List<object> ParseSvgPathData(string dataText, Type pathType,
            ConstructorInfo pathConstructor, MethodInfo addPoint, List<string> warnings)
        {
            var result = new List<object>();
            if (string.IsNullOrWhiteSpace(dataText))
                return result;

            const string supported = "MCZmcZLlVvHhSsAa";
            var tokens = new List<char>();
            var bodies = new List<string>();
            foreach (char c in dataText)
            {
                if (supported.IndexOf(c) >= 0)
                {
                    tokens.Add(c);
                    bodies.Add("");
                    continue;
                }
                if (bodies.Count == 0)
                    continue;
                bodies[bodies.Count - 1] += c;
            }

            object currentPath = pathConstructor.Invoke(null);
            bool hasPoints = false;
            SharpDX.Vector2 relPos = new SharpDX.Vector2();

            Action<float, float> setAbsolute = (x, y) => relPos = new SharpDX.Vector2(x, y);
            Action closePath = null;
            Action add = null;
            closePath = () =>
            {
                if (hasPoints)
                    result.Add(currentPath);
                currentPath = pathConstructor.Invoke(null);
                hasPoints = false;
            };
            add = () =>
            {
                addPoint.Invoke(currentPath, new object[] { relPos });
                hasPoints = true;
            };

            for (int i = 0; i < tokens.Count; i++)
            {
                char token = tokens[i];
                var data = new List<float>();
                foreach (Match m in s_svgNumber.Matches(bodies[i] + " "))
                {
                    string text = m.Value.Trim(' ', ',');
                    if (text.Length > 0)
                        data.Add(SvgFloat(text));
                }

                if (token == 'M' || token == 'm')
                {
                    closePath();
                    if (data.Count < 2) continue;
                    float x = token == 'm' ? relPos.X + data[0] : data[0];
                    float y = token == 'm' ? relPos.Y + data[1] : data[1];
                    setAbsolute(x, y);
                    add();
                }
                else if (token == 'C' || token == 'c')
                {
                    for (int j = 0; j + 5 < data.Count; j += 6)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            float x = data[j + k * 2];
                            float y = data[j + k * 2 + 1];
                            if (token == 'c')
                            {
                                x += relPos.X;
                                y += relPos.Y;
                            }
                            setAbsolute(x, y);
                            add();
                        }
                    }
                }
                else if (token == 'L' || token == 'l')
                {
                    // SvgPath stores one cubic segment per three points, so an
                    // on-curve command contributes three identical points, exactly
                    // like the editor's importer.
                    for (int j = 0; j + 1 < data.Count; j += 2)
                    {
                        float x = data[j], y = data[j + 1];
                        if (token == 'l')
                        {
                            x += relPos.X;
                            y += relPos.Y;
                        }
                        setAbsolute(x, y);
                        add();
                        add();
                        add();
                    }
                }
                else if (token == 'V' || token == 'v')
                {
                    foreach (float y in data)
                    {
                        setAbsolute(relPos.X, token == 'v' ? relPos.Y + y : y);
                        add();
                        add();
                        add();
                    }
                }
                else if (token == 'H' || token == 'h')
                {
                    foreach (float x in data)
                    {
                        setAbsolute(token == 'h' ? relPos.X + x : x, relPos.Y);
                        add();
                        add();
                        add();
                    }
                }
                else if (token == 'S' || token == 's')
                {
                    for (int j = 0; j + 3 < data.Count; j += 4)
                    {
                        // The first point is the previous control point reflected
                        // about the current position, same as the editor.
                        float rx = relPos.X, ry = relPos.Y;
                        float cx = data[j], cy = data[j + 1];
                        if (token == 's')
                        {
                            cx += relPos.X;
                            cy += relPos.Y;
                        }
                        float ex = data[j + 2], ey = data[j + 3];
                        if (token == 's')
                        {
                            ex += relPos.X;
                            ey += relPos.Y;
                        }
                        IList existing = (IList)pathType.GetProperty("Points").GetValue(currentPath);
                        if (existing.Count >= 2)
                        {
                            object prev = existing[existing.Count - 2];
                            rx = rx - AsFloat(prev, "X") + rx;
                            ry = ry - AsFloat(prev, "Y") + ry;
                        }
                        setAbsolute(rx, ry);
                        add();
                        setAbsolute(cx, cy);
                        add();
                        setAbsolute(ex, ey);
                        add();
                    }
                }
                else if (token == 'Z' || token == 'z')
                {
                    if (hasPoints)
                    {
                        pathType.GetProperty("Closed")?.SetValue(currentPath, true);
                        IList closed = (IList)pathType.GetProperty("Points").GetValue(currentPath);
                        if (closed.Count > 0)
                        {
                            object first = closed[0];
                            setAbsolute(AsFloat(first, "X"), AsFloat(first, "Y"));
                        }
                    }
                }
                else if (token == 'A' || token == 'a')
                {
                    warnings.Add("ArcTo (A/a) is not supported");
                }
                else
                {
                    warnings.Add("Unsupported token: " + token);
                }
            }

            closePath();
            return result;
        }

        // =====================================================================
        // Sound info
        // =====================================================================

        public static object GetSoundInfo(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("Sound EBX not found", "NOT_FOUND");

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;

                Type rootType = ((object)root).GetType();
                bool hasChunks = rootType.GetProperty("Chunks") != null;
                bool hasVariations = rootType.GetProperty("RuntimeVariations") != null;
                bool hasSegments = rootType.GetProperty("Segments") != null;
                if (!hasChunks && !hasVariations && !hasSegments)
                    return McpHandlers.Error("Asset is not a SoundWave (no Chunks/RuntimeVariations/Segments)", "NOT_A_SOUND");

                var scanErrors = new List<object>();
                var chunks = new List<object>();
                try
                {
                    for (int i = 0; i < root.Chunks.Count; i++)
                    {
                        dynamic c = root.Chunks[i];
                        Guid cid = (Guid)c.ChunkId;
                        ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(cid);
                        chunks.Add(new Dictionary<string, object>
                        {
                            ["index"] = i,
                            ["chunk_id"] = cid.ToString(),
                            ["chunk_size"] = ce?.Size,
                            ["exists"] = ce != null
                        });
                    }
                }
                catch (Exception ex) { scanErrors.Add(new Dictionary<string, object> { ["section"] = "chunks", ["error"] = Unwrap(ex) }); }

                var variations = new List<object>();
                try
                {
                    for (int i = 0; i < root.RuntimeVariations.Count; i++)
                    {
                        dynamic v = root.RuntimeVariations[i];
                        variations.Add(new Dictionary<string, object>
                        {
                            ["index"] = i,
                            ["chunk_index"] = (int)(byte)v.ChunkIndex,
                            ["first_segment_index"] = (int)(ushort)v.FirstSegmentIndex,
                            ["segment_count"] = (int)(ushort)v.SegmentCount
                        });
                    }
                }
                catch (Exception ex) { scanErrors.Add(new Dictionary<string, object> { ["section"] = "runtime_variations", ["error"] = Unwrap(ex) }); }

                var segments = new List<object>();
                try
                {
                    for (int i = 0; i < root.Segments.Count; i++)
                    {
                        dynamic s = root.Segments[i];
                        segments.Add(new Dictionary<string, object>
                        {
                            ["index"] = i,
                            ["samples_offset"] = (uint)s.SamplesOffset,
                            ["seek_table_offset"] = TryDyn(s, "SeekTableOffset"),
                            ["segment_length"] = TryDyn(s, "SegmentLength")
                        });
                    }
                }
                catch (Exception ex) { scanErrors.Add(new Dictionary<string, object> { ["section"] = "segments", ["error"] = Unwrap(ex) }); }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["name"] = entry.Name,
                    ["type"] = entry.Type,
                    ["chunk_count"] = chunks.Count,
                    ["variation_count"] = variations.Count,
                    ["segment_count"] = segments.Count,
                    ["chunks"] = chunks,
                    ["runtime_variations"] = variations,
                    ["segments"] = segments,
                    ["scan_error_count"] = scanErrors.Count,
                    ["scan_errors"] = scanErrors,
                    ["bundles"] = entry.EnumerateBundles()
                        .Select(id => App.AssetManager.GetBundleEntry(id)?.Name)
                        .Where(n => n != null).ToList()
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "INFO_FAILED");
            }
        }

        // =====================================================================
        // Deep duplicate (DuplicationPlugin)
        // =====================================================================

        public static object DuplicateDeep(JObject p)
        {
            if (App.AssetManager == null)
                return McpHandlers.Error("AssetManager not ready", "NOT_READY");

            EbxAssetEntry entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
                return McpHandlers.Error("EBX not found", "NOT_FOUND");

            string newName = (p.Value<string>("new_name") ?? "").Trim().Trim('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(newName))
                return McpHandlers.Error("new_name is required", "INVALID_PARAMS");

            if (App.AssetManager.GetEbxEntry(newName) != null)
                return McpHandlers.Error("Target already exists: " + newName, "EXISTS");

            Type extensionBase = FindType("DuplicationPlugin.DuplicateAssetExtension");
            if (extensionBase == null)
                return McpHandlers.Error("DuplicationPlugin not loaded", "PLUGIN_MISSING");

            try
            {
                // Mesh assets need MeshVariationDb
                if (TypeLibrary.IsSubClassOf(entry.Type, "MeshAsset"))
                {
                    Type mvdb = FindType("Frosty.Core.Viewport.MeshVariationDb");
                    if (mvdb != null)
                    {
                        PropertyInfo isLoaded = mvdb.GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Static);
                        if (isLoaded != null && !(bool)isLoaded.GetValue(null))
                        {
                            if (mvdb.GetMethod("LoadVariations", BindingFlags.Public | BindingFlags.Static) != null)
                                InvokeCompat(mvdb, "LoadVariations");
                        }
                    }
                }

                // The extension classes are top-level types in DuplicationPlugin
                // (TextureBaseExtension, MeshExtension, ...); there is no
                // "DuplicationTool" container type.
                object extension = null;
                string matchedKey = "null";
                object nullExt = null;
                var subclasses = new List<Type>();

                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm != extensionBase.Assembly)
                        continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
                    foreach (Type t in types)
                    {
                        if (t == null || t.IsAbstract || !extensionBase.IsAssignableFrom(t))
                            continue;
                        subclasses.Add(t);
                    }
                }

                foreach (Type t in subclasses)
                {
                    object inst;
                    try { inst = Activator.CreateInstance(t); }
                    catch { continue; }

                    PropertyInfo atProp = t.GetProperty("AssetType");
                    string at = atProp?.GetValue(inst) as string;
                    if (string.IsNullOrEmpty(at))
                    {
                        nullExt = inst;
                        continue;
                    }
                    if (string.Equals(entry.Type, at, StringComparison.OrdinalIgnoreCase))
                    {
                        extension = inst;
                        matchedKey = at;
                        break;
                    }
                }
                if (extension == null)
                {
                    foreach (Type t in subclasses)
                    {
                        object inst;
                        try { inst = Activator.CreateInstance(t); }
                        catch { continue; }
                        string at = t.GetProperty("AssetType")?.GetValue(inst) as string;
                        if (string.IsNullOrEmpty(at))
                            continue;
                        if (TypeLibrary.IsSubClassOf(entry.Type, at))
                        {
                            extension = inst;
                            matchedKey = at;
                            break;
                        }
                    }
                }
                if (extension == null)
                    extension = nullExt;
                if (extension == null)
                    return McpHandlers.Error("No DuplicateAssetExtension available", "PLUGIN_MISSING");

                Type newType = null;
                string newTypeName = (p.Value<string>("new_type") ?? "").Trim();
                bool createNew = false;
                if (!string.IsNullOrEmpty(newTypeName))
                {
                    newType = TypeLibrary.GetType(newTypeName);
                    if (newType == null)
                        return McpHandlers.Error("new_type not found: " + newTypeName, "TYPE_NOT_FOUND");
                    createNew = true;
                }
                if (newType == null)
                    newType = TypeLibrary.GetType(entry.Type);

                EbxAssetEntry newEntry = (EbxAssetEntry)InvokeCompat(
                    extension, "DuplicateAsset", entry, newName, createNew, newType);

                // DuplicationPlugin only renumbers the root object; renumber the
                // whole graph so the copy cannot collide with its source at load.
                EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
                if (newAsset != null)
                {
                    McpEditHandlers.RenumberAssetObjectGuids(newAsset);
                    newAsset.Update();
                    App.AssetManager.ModifyEbx(newEntry.Name, newAsset);
                }

                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["source"] = entry.Name,
                    ["source_type"] = entry.Type,
                    ["extension"] = matchedKey,
                    ["new_name"] = newEntry.Name,
                    ["new_guid"] = newEntry.Guid.ToString(),
                    ["new_type"] = newEntry.Type,
                    ["entry"] = McpHandlers.SerializeEbxEntry(newEntry)
                };
            }
            catch (Exception ex)
            {
                return McpHandlers.Error(Unwrap(ex), "DUPLICATE_FAILED");
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static bool TryLoadLua(JObject p, out EbxAssetEntry entry, out ResAssetEntry resEntry, out object luaRes, out object error)
        {
            entry = null;
            resEntry = null;
            luaRes = null;
            error = null;

            if (App.AssetManager == null)
            {
                error = McpHandlers.Error("AssetManager not ready", "NOT_READY");
                return false;
            }

            entry = McpHandlers.ResolveEbxEntry(p.Value<string>("name"), p.Value<string>("guid"));
            if (entry == null)
            {
                error = McpHandlers.Error("Lua EBX not found", "NOT_FOUND");
                return false;
            }

            Type luaType = FindType("LuaPlugin.CompiledLuaResource");
            if (luaType == null)
            {
                error = McpHandlers.Error("LuaPlugin not loaded", "PLUGIN_MISSING");
                return false;
            }

            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic root = asset.RootObject;
                ulong rid = Convert.ToUInt64(root.CompiledLuaResource);
                resEntry = App.AssetManager.GetResEntry(rid);
                if (resEntry == null)
                {
                    error = McpHandlers.Error("CompiledLuaResource RES not found", "NOT_FOUND");
                    return false;
                }
                luaRes = GetResAs(luaType, resEntry);
                return true;
            }
            catch (Exception ex)
            {
                error = McpHandlers.Error(Unwrap(ex), "LOAD_FAILED");
                return false;
            }
        }

        private static bool TryGetAtlasTexture(EbxAssetEntry entry, out object texture, out ResAssetEntry resEntry, out object error)
        {
            texture = null;
            resEntry = null;
            error = null;

            Type atlasType = FindType("AtlasTexturePlugin.AtlasTexture");
            if (atlasType == null)
            {
                error = McpHandlers.Error("AtlasTexturePlugin not loaded", "PLUGIN_MISSING");
                return false;
            }

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic root = asset.RootObject;
            ulong rid = Convert.ToUInt64(root.Resource);
            resEntry = App.AssetManager.GetResEntry(rid);
            if (resEntry == null)
            {
                error = McpHandlers.Error("Atlas RES not found", "NOT_FOUND");
                return false;
            }
            texture = GetResAs(atlasType, resEntry);
            return true;
        }

        private static object GetResAs(Type resourceType, ResAssetEntry resEntry)
        {
            MethodInfo getResAs = typeof(AssetManager).GetMethods()
                .First(m => m.Name == "GetResAs" && m.IsGenericMethodDefinition);
            MethodInfo closed = getResAs.MakeGenericMethod(resourceType);
            return closed.Invoke(App.AssetManager, PadArgs(closed, resEntry));
        }

        /// <summary>
        /// Builds an argument array matching the method's parameter count so optional
        /// parameters (which reflection does not fill in) never cause a count mismatch.
        /// </summary>
        private static object[] PadArgs(MethodBase method, params object[] supplied)
        {
            ParameterInfo[] ps = method.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (supplied != null && i < supplied.Length)
                {
                    args[i] = supplied[i];
                    continue;
                }
                if (ps[i].HasDefaultValue)
                    args[i] = ps[i].DefaultValue;
                else if (ps[i].ParameterType.IsValueType)
                    args[i] = Activator.CreateInstance(ps[i].ParameterType);
                else
                    args[i] = null;
            }
            return args;
        }

        /// <summary>
        /// Invokes a method by name, tolerating plugin version drift in optional parameters.
        /// </summary>
        private static object InvokeCompat(object target, string methodName, params object[] supplied)
        {
            Type t = target as Type ?? target.GetType();
            object instance = target is Type ? null : target;

            MethodInfo method = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == methodName)
                .OrderBy(m => Math.Abs(m.GetParameters().Length - (supplied?.Length ?? 0)))
                .FirstOrDefault();
            if (method == null)
                throw new MissingMethodException(t.FullName, methodName);

            return method.Invoke(instance, PadArgs(method, supplied));
        }

        /// <summary>
        /// Reads a public property or field. Frostbite resource classes (e.g.
        /// CompiledLuaResource.EntrypointName) expose fields, not properties.
        /// </summary>
        private static object GetProp(object obj, string name)
        {
            if (obj == null)
                return null;
            PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi != null)
                return pi.GetValue(obj);
            FieldInfo fi = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return fi?.GetValue(obj);
        }

        private static bool SetMember(object obj, string name, object value)
        {
            if (obj == null)
                return false;
            PropertyInfo pi = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi != null && pi.CanWrite)
            {
                pi.SetValue(obj, value);
                return true;
            }
            FieldInfo fi = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (fi != null)
            {
                fi.SetValue(obj, value);
                return true;
            }
            return false;
        }

        private static object TryDyn(dynamic obj, string name)
        {
            try { return obj.GetType().GetProperty(name)?.GetValue(obj); }
            catch { return null; }
        }

        private static string FormatLuaGuidName(Guid g)
        {
            return g.ToString("D").ToUpperInvariant().Replace('-', '_');
        }

        private static void AppendCStringList(object obj, string propName, JArray arr)
        {
            if (arr == null || arr.Count == 0)
                return;
            PropertyInfo pi = obj.GetType().GetProperty(propName);
            if (pi == null)
                return;
            object listObj = pi.GetValue(obj);
            if (!(listObj is IList list))
                return;
            foreach (JToken t in arr)
            {
                string s = t?.ToString();
                if (!string.IsNullOrEmpty(s))
                    list.Add((CString)s);
            }
        }

        private static void SetBoolProp(object obj, string name, bool value)
        {
            try
            {
                PropertyInfo pi = obj.GetType().GetProperty(name);
                if (pi != null && pi.CanWrite)
                    pi.SetValue(obj, value);
            }
            catch { }
        }

        private static bool TryAddPointer(object root, string arrayName, PointerRef pref)
        {
            PropertyInfo pi = root.GetType().GetProperty(arrayName);
            if (pi == null) return false;
            if (pi.GetValue(root) is IList list)
            {
                list.Add(pref);
                return true;
            }
            return false;
        }

        private static void CopyArrayParam(JObject src, JObject dst, string key)
        {
            if (src[key] != null)
                dst[key] = src[key];
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null) return t;
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
