using Frosty.Core;
using FrostyMcpPlugin.Bridge;
using FrostySdk.Interfaces;
using System;

namespace FrostyMcpPlugin.Extensions
{
    public class McpStartupAction : StartupAction
    {
        public override Action<ILogger> Action => (logger) =>
        {
            try
            {
                McpBridgeServer.Instance.Start();
                logger.Log($"[MCP v{McpBridgeServer.Version}] Listening on: {McpBridgeServer.Instance.PipeName}");
            }
            catch (Exception ex)
            {
                logger.Log($"[MCP] Failed to start bridge: {ex.Message}");
            }
        };
    }
}
