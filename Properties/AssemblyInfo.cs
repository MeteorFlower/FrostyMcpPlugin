using Frosty.Core.Attributes;
using FrostyMcpPlugin.Extensions;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: ComVisible(false)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]

[assembly: Guid("A7C3E91F-2B48-4D6A-9F15-8E0D3C1B7A24")]

[assembly: PluginDisplayName("Frosty MCP Bridge")]
[assembly: PluginAuthor("Frosty2000")]
[assembly: PluginVersion("2.0.1.0")]

[assembly: RegisterStartupAction(typeof(McpStartupAction))]
[assembly: RegisterMenuExtension(typeof(McpStatusMenuExtension))]
