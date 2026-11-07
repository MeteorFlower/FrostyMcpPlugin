using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Two-phase gate for destructive tools. The first call stores the resolved plan
    /// and returns would_change; only the immediately following call of the same tool
    /// with deep-equal parameters (ignoring the commit flag) executes the stored plan.
    ///
    /// Pending plans are kept per connection. A single shared slot meant any other
    /// client calling any tool between a preview and its commit discarded the plan,
    /// so a second client (a subagent's MCP server, for instance) made commits fail
    /// with "No matching preview" at random.
    /// </summary>
    internal static class McpCommit
    {
        private sealed class Pending
        {
            internal string Tool;
            internal JObject Parameters;
            internal Func<object> Execute;
            internal long PreviewCall;
            internal DateTime ExpiresUtc;
        }

        private static readonly object s_lock = new object();
        private static readonly Dictionary<long, Pending> s_pending = new Dictionary<long, Pending>();
        private static readonly Dictionary<long, long> s_callCounters = new Dictionary<long, long>();
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        // HandleClient serves a connection start to finish on one thread, so the
        // connection id can ride along as thread state instead of being threaded
        // through every handler signature.
        [ThreadStatic] private static long t_connection;

        /// <summary>Called once when a client connects.</summary>
        internal static void EnterConnection(long connection)
        {
            t_connection = connection;
        }

        /// <summary>Called when a client disconnects; drops only that connection's plan.</summary>
        internal static void ExitConnection()
        {
            ClearPending(t_connection);
        }

        private static long CurrentConnection => t_connection;

        /// <summary>Called once per tool request before dispatch.</summary>
        internal static long BeginCall(string tool)
        {
            long connection = CurrentConnection;
            lock (s_lock)
            {
                long call = s_callCounters.TryGetValue(connection, out long previous) ? previous + 1 : 1;
                s_callCounters[connection] = call;

                // The plan is only valid for the immediately following call on the
                // same connection; anything else means the caller moved on.
                if (s_pending.TryGetValue(connection, out Pending pending) &&
                    (call != pending.PreviewCall + 1 || DateTime.UtcNow > pending.ExpiresUtc))
                    s_pending.Remove(connection);

                return call;
            }
        }

        private static void ClearPending(long connection)
        {
            lock (s_lock)
            {
                s_pending.Remove(connection);
                s_callCounters.Remove(connection);
            }
        }

        /// <summary>
        /// Preview or execute. <paramref name="plan"/> is what the call would change;
        /// <paramref name="execute"/> performs the change using the already-resolved
        /// targets collected by the caller.
        /// </summary>
        internal static object Require(string tool, JObject p, Dictionary<string, object> plan, Func<object> execute)
        {
            long connection = CurrentConnection;
            JObject normalized = Normalize(p);

            if (!(p.Value<bool?>("commit") ?? false))
            {
                lock (s_lock)
                {
                    s_pending[connection] = new Pending
                    {
                        Tool = tool,
                        Parameters = normalized,
                        Execute = execute,
                        PreviewCall = s_callCounters.TryGetValue(connection, out long preview) ? preview : 0,
                        ExpiresUtc = DateTime.UtcNow + Lifetime
                    };
                }

                // Nothing was applied yet, so this is not a success. Treating the
                // preview as success is how a caller ends up believing the change
                // happened when only the plan exists.
                return new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "Preview only; repeat the same call with commit=true to apply it",
                    ["error_code"] = "PREVIEW_ONLY",
                    ["would_change"] = true,
                    ["commit_required"] = true,
                    ["tool"] = tool,
                    ["plan"] = plan
                };
            }

            Func<object> pendingExecute = null;
            lock (s_lock)
            {
                if (s_pending.TryGetValue(connection, out Pending pending) &&
                    string.Equals(pending.Tool, tool, StringComparison.Ordinal) &&
                    JToken.DeepEquals(pending.Parameters, normalized))
                {
                    pendingExecute = pending.Execute;
                    s_pending.Remove(connection);
                }
            }

            if (pendingExecute == null)
                return McpHandlers.Error(
                    "No matching preview for this call; run the same call without commit first",
                    "COMMIT_REQUIRED");

            return pendingExecute();
        }

        private static JObject Normalize(JObject p)
        {
            JObject clone = (JObject)p.DeepClone();
            clone.Remove("commit");
            return clone;
        }
    }
}
