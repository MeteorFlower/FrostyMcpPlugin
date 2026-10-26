using Frosty.Core;
using FrostySdk.Interfaces;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Mirrors every MCP tool call into the Frosty Editor log pane so the user can
    /// follow what the AI is doing step by step.
    ///
    /// Line shapes:
    ///   [MCP] >> [READ]  search_ebx query=ger_base type=SkeletonAsset
    ///   [MCP] << [READ]  search_ebx 42ms OK total=0 returned=0
    ///   [MCP] << [WRITE] set_component_property 3ms OK value=100
    ///   [MCP] !! export_mesh 12ms EXPORT_FAILED EXCEPTION InvalidOperationException: ...
    /// </summary>
    internal static class McpLog
    {
        private const int MaxArgsChars = 700;
        private const int MaxValueChars = 140;
        private const int MaxResultChars = 400;

        private static readonly HashSet<string> ReadOnlyPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get_", "list_", "search_", "find_", "decode_", "validate_",
            "resolve_", "export_", "ping", "hash_string", "research_"
        };

        private static readonly string[] ResultKeys =
        {
            "name", "path", "type", "index", "class_guid",
            "total", "returned", "count", "remaining", "removed_count",
            "added_count", "value", "changed", "removed"
        };

        /// <summary>Toggle from the Tools menu when the log gets noisy.</summary>
        public static bool Enabled { get; set; } = true;

        public static void ToolBegin(string method, JObject args)
        {
            if (!Enabled)
                return;

            Write("[MCP] >> [{0}] {1} {2}", KindOf(method), method, SummarizeArgs(args));
        }

        public static void ToolEnd(string method, object result, long elapsedMs)
        {
            if (!Enabled)
                return;

            if (IsFailed(result))
                Write("[MCP] << [{0}] {1} {2}ms FAIL {3}", KindOf(method), method, elapsedMs, SummarizeResult(result));
            else
                Write("[MCP] << [{0}] {1} {2}ms OK {3}", KindOf(method), method, elapsedMs, SummarizeResult(result));
        }

        public static void ToolException(string method, Exception ex, long elapsedMs)
        {
            if (!Enabled)
                return;

            Exception cur = ex;
            while (cur is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                cur = tie.InnerException;

            Write("[MCP] !! [{0}] {1} {2}ms EXCEPTION {3}: {4}",
                KindOf(method), method, elapsedMs, cur.GetType().Name, OneLine(cur.Message));
        }

        /// <summary>Free-form bridge event (client connect / disconnect / parse errors).</summary>
        public static void Event(string text)
        {
            if (!Enabled)
                return;

            Write("[MCP] -- {0}", OneLine(text));
        }

        public static void Warn(string text)
        {
            if (!Enabled)
                return;

            Write("[MCP] !! {0}", OneLine(text));
        }

        private static string KindOf(string method)
        {
            foreach (string prefix in ReadOnlyPrefixes)
            {
                if (method.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return "READ";
            }
            return "WRITE";
        }

        private static bool IsFailed(object result)
        {
            if (result is IDictionary<string, object> dict &&
                dict.TryGetValue("success", out object ok) && ok is bool b)
                return !b;
            return false;
        }

        private static string SummarizeArgs(JObject args)
        {
            if (args == null || args.Count == 0)
                return "{}";

            System.Text.StringBuilder sb = new System.Text.StringBuilder("{");
            bool first = true;

            foreach (JProperty prop in args.Properties())
            {
                JToken tok = prop.Value;
                if (tok == null || tok.Type == JTokenType.Null)
                    continue;

                string text;
                bool quoted = false;
                if (tok.Type == JTokenType.String)
                {
                    text = (string)tok;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    text = OneLine(text);
                    quoted = true;
                }
                else
                {
                    text = tok.ToString(Formatting.None);
                }

                if (text.Length > MaxValueChars)
                    text = text.Substring(0, MaxValueChars) + "...";
                if (quoted)
                    text = "\"" + text + "\"";

                if (!first)
                    sb.Append(", ");
                sb.Append(prop.Name).Append('=').Append(text);
                first = false;

                if (sb.Length >= MaxArgsChars)
                {
                    sb.Append(", ...");
                    break;
                }
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static string SummarizeResult(object result)
        {
            if (result == null)
                return "";
            if (!(result is IDictionary<string, object> dict))
                return result is ICollection c ? "items=" + c.Count : "";

            List<string> parts = new List<string>();

            if (dict.TryGetValue("error_code", out object code) && code != null)
                parts.Add("code=" + ScalarText(code));
            if (dict.TryGetValue("error", out object err) && err != null)
                parts.Add("error=" + ScalarText(err));

            foreach (string key in ResultKeys)
            {
                if (parts.Count >= 6)
                    break;
                if (!dict.TryGetValue(key, out object v) || v == null || v is IDictionary || v is JObject)
                    continue;
                if (v is ICollection col)
                {
                    parts.Add(key + "[" + col.Count + "]");
                    continue;
                }
                if (IsScalar(v))
                    parts.Add(key + "=" + ScalarText(v));
            }

            string text = string.Join(" ", parts);
            if (text.Length > MaxResultChars)
                text = text.Substring(0, MaxResultChars) + "...";
            return text;
        }

        private static bool IsScalar(object v)
        {
            Type t = v.GetType();
            return t.IsPrimitive || t.IsEnum || v is string || v is decimal || v is Guid;
        }

        private static string ScalarText(object v)
        {
            string text = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            text = OneLine(text);
            if (text.Length > 160)
                text = text.Substring(0, 160) + "...";
            return text;
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        }

        /// <summary>
        /// Frosty's logger appends to a StringBuilder bound to the log pane, so it is
        /// marshalled onto the UI dispatcher when called from the pipe thread.
        /// Always goes through the "{0}" template because FrostyLogger runs string.Format.
        /// </summary>
        private static void Write(string format, params object[] args)
        {
            ILogger logger = App.Logger;
            if (logger == null)
                return;

            Action emit = () =>
            {
                try { logger.Log(format, args); }
                catch { }
            };

            Application app = Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                try { app.Dispatcher.BeginInvoke(emit); }
                catch { }
                return;
            }

            emit();
        }
    }
}
