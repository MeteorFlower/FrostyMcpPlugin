using Frosty.Controls;
using Frosty.Core;
using FrostyMcpPlugin.Bridge;
using System.Windows;

namespace FrostyMcpPlugin.Extensions
{
    public class McpStatusMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "MCP Bridge Status";

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            string status = McpBridgeServer.Instance.IsRunning
                ? $"Running\nPipe: \\\\.\\pipe\\{McpBridgeServer.Instance.PipeName}\nProfile: {McpBridgeServer.Instance.InstanceProfile}\nVersion: {McpBridgeServer.Version}\nClients served: {McpBridgeServer.Instance.ClientsServed}\nTool log: {(McpLog.Enabled ? "on" : "off")}"
                : "Not running";

            FrostyMessageBox.Show(status, "Frosty MCP Bridge", MessageBoxButton.OK);
        });
    }
}
