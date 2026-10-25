using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace FrostyMcpPlugin.Bridge
{
    /// <summary>
    /// Two-phase gate for destructive tools. The first call stores the resolved plan
    /// and returns would_change; only the immediately following call of the same tool
    /// with deep-equal parameters (ignoring the commit flag) executes the stored plan.
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
        private static Pending s_pending;
        private static long s_callCounter;
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        /// <summary>Called once per tool request before dispatch.</summary>
        internal static long BeginCall(string tool)
        {
            lock (s_lock)
            {
                long call = ++s_callCounter;
                if (s_pending != null &&
                    (call != s_pending.PreviewCall + 1 ||
                     !string.Equals(tool, s_pending.Tool, StringComparison.Ordinal) ||
                     DateTime.UtcNow > s_pending.ExpiresUtc))
                    s_pending = null;
                return call;
            }
        }

        /// <summary>Called when a client disconnects; a plan cannot span connections.</summary>
        internal static void ClearPending()
        {
            lock (s_lock)
                s_pending = null;
        }

        /// <summary>
        /// Preview or execute. <paramref name="plan"/> is what the call would change;
        /// <paramref name="execute"/> performs the change using the already-resolved
        /// targets collected by the caller.
        /// </summary>
        internal static object Require(string tool, JObject p, Dictionary<string, object> plan, Func<object> execute)
        {
            JObject normalized = Normalize(p);

            if (!(p.Value<bool?>("commit") ?? false))
            {
                lock (s_lock)
                {
                    s_pending = new Pending
                    {
                        Tool = tool,
                        Parameters = normalized,
                        Execute = execute,
                        PreviewCall = s_callCounter,
                        ExpiresUtc = DateTime.UtcNow + Lifetime
                    };
                }

                var response = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["would_change"] = true,
                    ["commit_required"] = true,
                    ["tool"] = tool,
                    ["plan"] = plan
                };
                return response;
            }

            Func<object> pendingExecute = null;
            lock (s_lock)
            {
                if (s_pending != null &&
                    string.Equals(s_pending.Tool, tool, StringComparison.Ordinal) &&
                    JToken.DeepEquals(s_pending.Parameters, normalized))
                {
                    pendingExecute = s_pending.Execute;
                    s_pending = null;
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
