using MCPSharp;
using MCPSharp.Model;

MCPClient mcpClient = new (
    name: "MCP Client 2",
    version: "1.0.0",
    server: "E:\\Mario\\TrialProjects\\MCPForRealCases\\MCP_Servers\\Server_V1\\bin\\Debug\\net10.0\\win-x64\\Server_V1.exe"
);

List<Tool> tools = await mcpClient.GetToolsAsync();
foreach (Tool tool in tools)
{
    Console.WriteLine($"Tool: {tool.Name} - {tool.Description}");
}
Tool selectedTool = tools.First(t => t.Name == "Vision Apps Categories");
Console.WriteLine($"Invoking tool: {selectedTool.Name} - {selectedTool.Description}");

var result = await mcpClient.CallToolAsync(
    name: selectedTool.Name,
    parameters: new Dictionary<string, object?>()
);

foreach (var category in result.Content!)
{
    Console.WriteLine($"Category: {category.Text}");
}



