using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Collections.Concurrent;
using MCP_Client_UI.Models;

namespace MCP_Client_UI.Services
{
    public class McpAgentService(IConfiguration _config, ILogger<McpAgentService> _logger) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _initializationLock = new(1, 1);
        private readonly ConcurrentDictionary<string, AgentThread> _threadCache = new();

        private McpClient? _mcpClient;
        private AIAgent? _agent;
        private AgentThread? _currentThread;
        private IChatClient? _chatClient;
        private bool _isDisposed;

        public bool IsInitialized { get; private set; }

        // Expose available tools for UI or debugging
        public IReadOnlyList<string> AvailableTools { get; private set; } = Array.Empty<string>();

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (IsInitialized) return;

            await _initializationLock.WaitAsync(cancellationToken);
            try
            {
                if (IsInitialized) return; // Double-check after acquiring lock

                _logger.LogInformation("Initializing MCP Agent Service...");

                var settings = _config.GetSection("McpSettings");

                ValidateConfiguration(settings);

                // 1. Setup MCP Client with retry logic
                _mcpClient = await InitializeMcpClientAsync(settings, cancellationToken);

                // 2. Fetch and process tools
                var (agentTools, toolArgsDetails) = await LoadToolsAsync(cancellationToken);
                AvailableTools = agentTools.Select(t => t.Name).ToList();

                // 3. Setup AI Client
                _chatClient = InitializeChatClient(settings);

                // 4. Create the Agent with enhanced instructions
                _agent = CreateAgent(agentTools, toolArgsDetails);

                // 5. Initialize default thread
                _currentThread = _agent.GetNewThread();

                IsInitialized = true;
                _logger.LogInformation("MCP Agent Service initialized successfully with {ToolCount} tools", agentTools.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MCP Agent Service");
                throw new InvalidOperationException("MCP Agent Service initialization failed. See inner exception for details.", ex);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        private void ValidateConfiguration(IConfigurationSection settings)
        {
            var requiredKeys = new[] { "ServerCommand", "ServerPath", "ApiKey", "ApiUrl", "ModelId" };
            var missingKeys = requiredKeys.Where(key => string.IsNullOrWhiteSpace(settings[key])).ToList();

            if (missingKeys.Any())
            {
                throw new InvalidOperationException(
                    $"Missing required configuration keys: {string.Join(", ", missingKeys)}");
            }
        }

        private async Task<McpClient> InitializeMcpClientAsync(IConfigurationSection settings, CancellationToken cancellationToken)
        {
            var transportOptions = new StdioClientTransportOptions
            {
                Command = settings["ServerCommand"]!,
                Arguments = [settings["ServerPath"]!]
            };

            var transport = new StdioClientTransport(transportOptions);

            _logger.LogDebug("Creating MCP client with command: {Command} {Arguments}",
                transportOptions.Command,
                string.Join(" ", transportOptions.Arguments));

            var mcpClientOptions = new McpClientOptions();
            return await McpClient.CreateAsync(transport, mcpClientOptions);
        }

        private async Task<(List<AITool> Tools, Dictionary<string, Dictionary<string, ToolParameter>> ToolArgs)> LoadToolsAsync(CancellationToken cancellationToken)
        {
            var mcpTools  = await _mcpClient!.ListToolsAsync();
            var agentTools = new List<AITool>();
            var toolArgsDetails = new Dictionary<string, Dictionary<string, ToolParameter>>();

            _logger.LogInformation("Retrieved {ToolCount} tools from MCP server", mcpTools.Count);

            foreach (var tool in mcpTools)
            {
                _logger.LogDebug("Processing tool: {ToolName} - {Description}", tool.Name, tool.Description);

                var parameters = ExtractToolParameters(tool);
                toolArgsDetails[tool.Name] = parameters;

                var aiFunction = CreateAIFunction(tool);
                agentTools.Add(aiFunction);
            }

            return (agentTools, toolArgsDetails);
        }

        private Dictionary<string, ToolParameter> ExtractToolParameters(McpClientTool tool)
        {
            var parameters = new Dictionary<string, ToolParameter>();

            if (tool.JsonSchema.ValueKind != JsonValueKind.Object ||
                !tool.JsonSchema.TryGetProperty("properties", out JsonElement propertiesElement))
            {
                return parameters;
            }

            var requiredParams = new HashSet<string>();
            if (tool.JsonSchema.TryGetProperty("required", out JsonElement requiredElement) &&
                requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement req in requiredElement.EnumerateArray())
                {
                    if (req.ValueKind == JsonValueKind.String)
                    {
                        requiredParams.Add(req.GetString()!);
                    }
                }
            }

            foreach (JsonProperty prop in propertiesElement.EnumerateObject())
            {
                var param = new ToolParameter
                {
                    Type = prop.Value.TryGetProperty("type", out var typeProp)
                        ? typeProp.GetString() ?? "unknown"
                        : "unknown",
                    Description = prop.Value.TryGetProperty("description", out var descProp)
                        ? descProp.GetString() ?? ""
                        : "",
                    IsRequired = requiredParams.Contains(prop.Name)
                };

                if (prop.Value.TryGetProperty("default", out JsonElement defaultProp))
                {
                    param.DefaultValue = param.Type == "string"
                        ? defaultProp.GetString()
                        : defaultProp.GetRawText();
                }

                parameters[prop.Name] = param;

                _logger.LogTrace("  Parameter: {ParamName} ({Type}, Required: {Required})",
                    prop.Name, param.Type, param.IsRequired);
            }

            return parameters;
        }

        private AITool CreateAIFunction(McpClientTool tool)
        {
            return AIFunctionFactory.Create(async (JsonElement args) =>
            {
                try
                {
                    _logger.LogInformation($"Invoking MCP tool: {tool.Name} - args {args}");
                    _logger.LogDebug("Tool arguments: {Args}", args.GetRawText());

                    var argsDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText());

                    var result = await _mcpClient!.CallToolAsync(tool.Name, argsDict);

                    if (result?.Content == null || result.Content.Count == 0)
                    {
                        _logger.LogWarning("Tool {ToolName} returned empty result", tool.Name);
                        return $"Tool '{tool.Name}' executed but returned no content.";
                    }

                    // Handle multiple content blocks if present
                    var textResults = result.Content
                        .OfType<TextContentBlock>()
                        .Select(block => block.Text)
                        .ToList();

                    var combinedResult = string.Join("\n", textResults);

                    _logger.LogDebug("Tool {ToolName} result: {Result}",
                        tool.Name,
                        combinedResult.Length > 200 ? $"{combinedResult[..200]}..." : combinedResult);

                    return combinedResult;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error invoking tool {ToolName}", tool.Name);
                    return $"Error invoking tool {tool.Name}: {ex.Message}";
                }
            },
            name: tool.Name,
            description: tool.Description);
        }

        private IChatClient InitializeChatClient(IConfigurationSection settings)
        {
            var apiKey = new ApiKeyCredential(settings["ApiKey"]!);
            var openAiOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(settings["ApiUrl"]!)
            };

            _logger.LogDebug("Initializing chat client with model: {ModelId}", settings["ModelId"]);

            return new ChatClient(settings["ModelId"], apiKey, openAiOptions).AsIChatClient();
        }

        private AIAgent CreateAgent(List<AITool> agentTools,Dictionary<string, Dictionary<string, ToolParameter>> toolArgsDetails, string agentInstructions = "")
        {
            var toolArgsJson = JsonSerializer.Serialize(toolArgsDetails, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            // Keep the original agent instructions as requested
            return _chatClient!.CreateAIAgent(new ChatClientAgentOptions
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

                    CRITICAL: Parameter names come from toolArgsDetails dictionary ONLY. User provides VALUES only." + agentInstructions,
                ChatOptions = new() { Tools = agentTools }
            });
        }

        public async Task<string> SendMessageAsync(string userMessage,CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (string.IsNullOrWhiteSpace(userMessage))
            {
                throw new ArgumentException("User message cannot be null or empty", nameof(userMessage));
            }

            if (!IsInitialized)
            {
                await InitializeAsync(cancellationToken);
            }

            try
            {
                _logger.LogInformation("Processing user message: {Message}",
                    userMessage.Length > 100 ? $"{userMessage[..100]}..." : userMessage);

                var response = await _agent!.RunAsync(userMessage, _currentThread!);
                var responseText = response.ToString();

                _logger.LogDebug("Agent response: {Response}",
                    responseText.Length > 200 ? $"{responseText[..200]}..." : responseText);

                return responseText;
            }
            catch (ClientResultException ex)
            {
                _logger.LogError(ex, "API error occurred with status: {Status}", ex.Status);
                var rawResponse = ex.GetRawResponse();
                var errorBody = rawResponse?.Content.ToString() ?? "No details available";
                return $"API Error: {ex.Status} - {errorBody}";
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Message processing was cancelled");
                return "Operation was cancelled.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing message");
                return $"Internal Error: {ex.Message}";
            }
        }

        // Create a new thread for isolated conversations
        public AgentThread CreateNewThread(string? threadId = null)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (!IsInitialized || _agent == null)
            {
                throw new InvalidOperationException("Service must be initialized before creating threads");
            }

            var thread = _agent.GetNewThread();

            if (!string.IsNullOrEmpty(threadId))
            {
                _threadCache[threadId] = thread;
            }

            _logger.LogDebug("Created new agent thread: {ThreadId}", threadId ?? "default");
            return thread;
        }

        // Switch to a different thread
        public bool SwitchThread(string threadId)
        {
            if (_threadCache.TryGetValue(threadId, out var thread))
            {
                _currentThread = thread;
                _logger.LogInformation("Switched to thread: {ThreadId}", threadId);
                return true;
            }

            _logger.LogWarning("Thread not found: {ThreadId}", threadId);
            return false;
        }

        // Reset current thread
        public void ResetCurrentThread()
        {
            if (_agent != null)
            {
                _currentThread = _agent.GetNewThread();
                _logger.LogInformation("Reset current thread");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;

            _logger.LogInformation("Disposing MCP Agent Service...");

            try
            {
                if (_mcpClient != null)
                {
                    await _mcpClient.DisposeAsync();
                }

                if (_chatClient is IDisposable disposableChat)
                {
                    disposableChat.Dispose();
                }

                _initializationLock.Dispose();
                _threadCache.Clear();

                _isDisposed = true;
                _logger.LogInformation("MCP Agent Service disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disposal");
            }
        }
    }
}