    using System.Text.Json;
    using Microsoft.Agents.AI;
    using Microsoft.Extensions.AI;
    using ModelContextProtocol.Client;
    using ModelContextProtocol.Protocol;
    using OpenAI;
    using OpenAI.Chat;
    using System.ClientModel;

namespace MCP_Client_UI.Services
{
    public class McpAgentService : IAsyncDisposable
    {
        private readonly IConfiguration _config;
        private McpClient? _mcpClient;
        private AIAgent? _agent;
        private AgentThread? _currentThread;
        private IChatClient? _chatClient;

        // Track if we are initialized to prevent double loading
        public bool IsInitialized { get; private set; }

        public McpAgentService(IConfiguration config)
        {
            _config = config;
        }

        public async Task InitializeAsync()
        {
            if (IsInitialized) return;

            var settings = _config.GetSection("McpSettings");

            // 1. Setup MCP Client (Stdio)
            var transportOptions = new StdioClientTransportOptions
            {
                Command = settings["ServerCommand"] ?? "dotnet",
                Arguments = [settings["ServerPath"]!]
            };

            var transport = new StdioClientTransport(transportOptions);

            _mcpClient = await McpClient.CreateAsync(transport);

            // 2. Fetch Tools from MCP Server
            var mcpTools = await _mcpClient.ListToolsAsync();
            var agentTools = new List<AITool>();

            var toolArgsDetails = new Dictionary<string, Dictionary<string, (string Type, string Description, dynamic? DefaultValue, bool isRequired)>>();

            // Display the retrieved tools
            foreach (var tool in mcpTools)
            {
                Console.WriteLine($"Tool: [{tool.Name}] - {tool.Description} \n");

                // Look for the "properties" field
                // The structure is usually: { "type": "object", "properties": { ... } }
                if (tool.JsonSchema.ValueKind == JsonValueKind.Object && tool.JsonSchema.TryGetProperty("properties", out JsonElement propertiesElement))
                {
                    // Iterate over the properties (The Keys are the argument names)
                    foreach (JsonProperty prop in propertiesElement.EnumerateObject())
                    {
                        string argName = prop.Name;
                        string argType = "unknown";
                        string argDescription = "";
                        dynamic? argDefaultValue = null!;
                        bool isRequired = false;

                        // Get the argument type (e.g., "integer", "string")
                        if (prop.Value.TryGetProperty("type", out JsonElement typeProp))
                        {
                            argType = typeProp.GetString() ?? "unknown";
                        }

                        // Get the argument description
                        if (prop.Value.TryGetProperty("description", out JsonElement descProp))
                        {
                            argDescription = descProp.GetString() ?? "";
                        }

                        if (prop.Value.TryGetProperty("default", out JsonElement defaultProp))
                        {
                            argDefaultValue = defaultProp.GetRawText();
                            argDefaultValue = argType == "string" ? defaultProp.GetString() : argDefaultValue;
                        }

                        // Get the required status if the key is in the "required" array make it true else false
                        if (tool.JsonSchema.TryGetProperty("required", out JsonElement requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement requiredProp in requiredElement.EnumerateArray())
                            {
                                if (requiredProp.GetString() == argName)
                                {
                                    isRequired = true;
                                    break;
                                }
                            }
                        }

                        // Store the argument details
                        if (!toolArgsDetails.ContainsKey(tool.Name))
                        {
                            toolArgsDetails[tool.Name] = new Dictionary<string, (string Type, string Description, dynamic? DefaultValue, bool isRequired)>();
                        }
                        toolArgsDetails[tool.Name][prop.Name] = (argType, argDescription, argDefaultValue, isRequired);
                    }
                }
                // We create an AIFunction that, when called by the LLM, 
                // executes the specific tool on the MCP Client.
                var aiFunction = AIFunctionFactory.Create(async (JsonElement args) =>
                {
                    try
                    {
                        // This callback runs when the Agent decides to use the tool
                        Console.WriteLine($"\n[Agent] Invoking MCP Tool: {tool.Name} with args: {args}\n");

                        // Call the MCP Server
                        var argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText());
                        var result = await _mcpClient.CallToolAsync(tool.Name, argsDict);

                        // get Text Content
                        TextContentBlock textContentBlock = (TextContentBlock)result.Content[0];
                        var textResult = textContentBlock.Text;
                        return textResult;

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"aiFunction Conversion Error : {ex.Message}");
                        return $"Error invoking tool {tool.Name}: {ex.Message}";
                    }
                },
                name: tool.Name,
                description: tool.Description
                );
                agentTools.Add(aiFunction);
            }

            // 3. Setup OpenAI/Gemini Client
            var apiKey = new ApiKeyCredential(settings["ApiKey"]!);
            var openAiOptions = new OpenAIClientOptions { Endpoint = new Uri(settings["ApiUrl"]!) };

            // Note: Using OpenAI SDK directly as per your snippet
            _chatClient = new ChatClient(settings["ModelId"], apiKey, openAiOptions).AsIChatClient();

            // 4. Create the Agent
            var toolArgsJson = JsonSerializer.Serialize(toolArgsDetails, new JsonSerializerOptions { WriteIndented = true });

            _agent = _chatClient.CreateAIAgent(new ChatClientAgentOptions
            {
                Name = "DatabaseRetrievalAgent",
                Instructions = $@"You are a database retrieval agent. Follow these strict rules for tool invocation:
                    AVAILABLE TOOLS AND THEIR PARAMETERS: {toolArgsJson}

                    PARAMETER BINDING RULES:
                    1. NEVER invent or assume parameter names - they MUST come from the toolArgsDetails dictionary
                    2. Extract ONLY the parameter VALUES from the user's query
                    3. Match extracted values to parameter names from toolArgsDetails[toolName]

                    WORKFLOW:
                    When the user makes a request:
                    1. Identify which tool to use
                    2. Get parameter definitions from toolArgsDetails[toolName] which contains:
                       - Parameter name (the key)
                       - Type, description, default value, and isRequired flag (the value tuple)
                    3. Extract VALUES from user query that match the parameter descriptions
                    4. Bind each extracted value to its corresponding parameter name from the dictionary
                    5. For parameters NOT provided by user:
                       - If NOT required AND has default value: use the default value
                       - If required: Ask user to provide the missing value

                    EXAMPLE:
                    toolArgsDetails = {{
                        ""GetCustomer"": {{
                            ""customerId"": (""int"", ""The customer ID"", null, true),
                            ""includeOrders"": (""bool"", ""Include order history"", false, false)
                        }}
                    }}

                    User query: ""Get customer 12345""
                    - Tool: GetCustomer
                    - Extract value: 12345
                    - Bind: customerId = 12345 (from dictionary key)
                    - includeOrders = false (use default, not required)

                    User query: ""Get customer with orders""
                    - Missing required customerId
                    - Response: ""Please provide the customer ID to retrieve""

                    CRITICAL: Parameter names come from toolArgsDetails dictionary ONLY. User provides VALUES only.",
                ChatOptions = new() { Tools = agentTools }
            });

            // 5. Start a Thread
            _currentThread = _agent.GetNewThread(); // Or GetNewThread() depending on version

            IsInitialized = true;
        }

        public async Task<string> SendMessageAsync(string userMessage)
        {
            if (!IsInitialized || _agent == null || _currentThread == null)
            {
                await InitializeAsync();
            }

            try
            {
                // Execute the run
                var response = await _agent!.RunAsync(userMessage, _currentThread!);
                return response.ToString();
            }
            catch (ClientResultException ex)
            {
                // Handle OpenAI/Gemini specific errors
                var rawResponse = ex.GetRawResponse();
                var errorBody = rawResponse?.Content.ToString() ?? "No details";
                return $"API Error: {ex.Status} - {errorBody}";
            }
            catch (Exception ex)
            {
                return $"Internal Error: {ex.Message}";
            }
        }

        // Cleanup is critical for Stdio processes!
        public async ValueTask DisposeAsync()
        {
            if (_mcpClient != null)
            {
                await _mcpClient.DisposeAsync();
            }

            // If ChatClient needs disposal (depending on implementation), do it here
            if (_chatClient is IDisposable disposableChat)
            {
                disposableChat.Dispose();
            }
        }
    }
}
