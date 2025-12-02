using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;

// If GeminiOpenAIClient is not available in your project or referenced packages, you need to:
// 1. Install the NuGet package that provides GeminiOpenAIClient.
// 2. Or, provide the implementation for GeminiOpenAIClient.
// If you are unsure which package or namespace contains GeminiOpenAIClient, please provide more information about where it should come from.

var _apiKey = "AIzaSyAaD5JA-obTJLAPpkz7e9QZfWOYO2vLjAE";
var _apiUrl = "https://generativelanguage.googleapis.com/v1beta/openai/";
var _apiModel = "gemini-2.5-flash";

var geminiApiKey = new ApiKeyCredential(_apiKey);

var openAIOptions = new OpenAIClientOptions
{
    Endpoint = new Uri(_apiUrl)
};

// Create an MCPClient for Server_V1
await using var mcpClient = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Name = "Server_V1", 
    Command = "dotnet", // assuming the server is a .NET application
    Arguments = [@"E:\\Mario\\TrialProjects\\MCPForRealCases\\MCP_Servers\\Server_V1\\bin\\Debug\\net10.0\\win-x64\\Server_V1.dll"], // server path
}));

// Retrieve the list of tools available on Sever_V1
var mcpTools = await mcpClient.ListToolsAsync().ConfigureAwait(false);

var agentTools = new List<AITool>();

// store tool argument details in a dictionary like this args[toolName][argumentName] = (argumentType, argumentDescription, argDefaultValue)
var toolArgsDetails = new Dictionary<string, Dictionary<string, (string Type, string Description, dynamic? DefaultValue, bool isRequired)>>();

// Display the retrieved tools
foreach (var tool in mcpTools)
{
    Console.WriteLine($"Tool: [{tool.Name}] - {tool.Description} \n");
    
    // Look for the "properties" field
    // The structure is usually: { "type": "object", "properties": { ... } }
    if (tool.JsonSchema.ValueKind == JsonValueKind.Object && tool.JsonSchema.TryGetProperty("properties", out JsonElement propertiesElement))
    {
        // 4. Iterate over the properties (The Keys are the argument names)
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
            var result = await mcpClient.CallToolAsync(tool.Name, argsDict);

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

Console.WriteLine("Enter your query:");
string userQuery = Console.ReadLine() ?? string.Empty;
while (string.IsNullOrWhiteSpace(userQuery))
{
    Console.WriteLine("Query cannot be empty. Please enter your query:");
    userQuery = Console.ReadLine() ?? string.Empty;
}

IChatClient chatClient = new ChatClient(_apiModel, geminiApiKey, openAIOptions).AsIChatClient();

var toolArgsJson = JsonSerializer.Serialize(toolArgsDetails, new JsonSerializerOptions{ WriteIndented = true });

AIAgent agent = chatClient
    .CreateAIAgent(new ChatClientAgentOptions
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
// Try running the agent first; if it fails, fallback to sending a corrected raw HTTP request
try
{
    AgentThread thread = agent.GetNewThread();

    while (!userQuery.Contains("exit") || userQuery != string.Empty)
    {
        var response = await agent.RunAsync(userQuery,thread);
        Console.WriteLine($"Agent Response: {response}");
        Console.WriteLine("Enter your query:");
        userQuery = Console.ReadLine() ?? string.Empty;
    }
}
catch (ClientResultException ex)
{
    Console.WriteLine("Agent invocation failed: " + ex.ToString());
    Console.WriteLine("Falling back to direct HTTP request to the OpenAI-compatible endpoint with a Google-compatible message shape.");

    Console.WriteLine("!!! OPENAI API ERROR !!!");
    Console.WriteLine($"Status: {ex.Status}");

    // This is the secret sauce: decoding the actual error message from OpenAI
    var rawResponse = ex.GetRawResponse();
    if (rawResponse != null)
    {
        string errorBody = rawResponse.Content.ToString();
        Console.WriteLine($"Body: {errorBody}");
    }
}
