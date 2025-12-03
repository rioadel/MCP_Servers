namespace MCP_Client_UI.DTOs
{
    public class ToolParameter
    {
        public string Type { get; set; } = "unknown";
        public string Description { get; set; } = "";
        public dynamic? DefaultValue { get; set; }
        public bool IsRequired { get; set; }
    }
}
