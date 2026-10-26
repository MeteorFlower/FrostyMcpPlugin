using Frosty.Controls;
using Frosty.Core;
using FrostyMcpPlugin.Bridge;
using System.Windows;

namespace FrostyMcpPlugin.Extensions
{
    /// <summary>
    /// Tools ▸ MCP Tool Log — turns the per-call log mirroring on/off.
    /// </summary>
    public class McpToolLogMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "MCP Tool Log (On/Off)";

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            McpLog.Enabled = !McpLog.Enabled;
            App.Logger?.Log("[MCP] tool log {0}", McpLog.Enabled ? "enabled" : "disabled");
        });
    }
}
