using System.Net.Http.Json;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.Hosting;

using ModelContextProtocol.Client;



// ---------------------------------------------------------

// PROGRAM SETUP

// ---------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables().AddUserSecrets<Program>();



var (command, arguments) = GetCommandAndArguments(args);



// Start MCP server via stdio transport

var clientTransport = new StdioClientTransport(new()

{

    Name = "Server_V1",

    Command = command,

    Arguments = arguments,

});



Console.WriteLine($"Connecting to server: {command} {string.Join(" ", arguments)}...");

await using var mcpClient = await McpClientFactory.CreateAsync(clientTransport);



// List tools to verify connection

try

{

    var tools = await mcpClient.ListToolsAsync();

    foreach (var tool in tools)

        Console.WriteLine($"Connected to server with tool: {tool.Name}");

}

catch (Exception ex)

{

    Console.WriteLine($"Warning: Could not list tools. Server might be active but silent. ({ex.Message})");

}



// ---------------------------------------------------------

// CONFIGURATION

// ---------------------------------------------------------



// Use a valid model name (gemini-1.5-flash or gemini-2.0-flash-exp)

const string geminiModel = "gemini-2.5-flash";



// TODO: PASTE YOUR NEW API KEY HERE

// Your previous key was exposed online. Please generate a new one in Google AI Studio.

const string geminiApiKey = "AIzaSyDePZRZMqGqhqVr4B_6o1MdxDgBG3yY2a4";



if (geminiApiKey.StartsWith("INSERT"))

{

    Console.ForegroundColor = ConsoleColor.Red;

    Console.WriteLine("ERROR: You must update the 'geminiApiKey' variable in Program.cs with a valid key.");

    Console.ResetColor();

    return;

}



Console.ForegroundColor = ConsoleColor.Green;

Console.WriteLine("MCP Client Started — Gemini chat ready.");

Console.ResetColor();



// ---------------------------------------------------------

// MAIN LOOP

// ---------------------------------------------------------



PromptForInput();

while (Console.ReadLine() is string query && !"exit".Equals(query, StringComparison.OrdinalIgnoreCase))

{

    if (string.IsNullOrWhiteSpace(query))

    {

        PromptForInput();

        continue;

    }



    // OPTION A: Manual Tool Invocation via 'tool <name> [args]'

    if (query.StartsWith("tool ", StringComparison.OrdinalIgnoreCase))

    {

        await HandleManualToolCall(mcpClient, query);

        PromptForInput();

        continue;

    }



    // OPTION B: Chat with Gemini

    try

    {

        var responseText = await GenerateWithGeminiAsync(geminiApiKey, geminiModel, query);



        Console.ForegroundColor = ConsoleColor.Yellow;

        if (!string.IsNullOrEmpty(responseText))

            Console.WriteLine(responseText);

        else

            Console.WriteLine("No response from Gemini.");

        Console.ResetColor();

    }

    catch (Exception ex)

    {

        Console.ForegroundColor = ConsoleColor.Red;

        Console.WriteLine($"Chat failed: {ex.Message}");

        Console.ResetColor();

    }



    PromptForInput();

}



// ---------------------------------------------------------

// METHODS

// ---------------------------------------------------------



static async Task HandleManualToolCall(IMcpClient mcpClient, string query)

{

    var parts = query.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);

    if (parts.Length >= 2)

    {

        var toolName = parts[1];

        IReadOnlyDictionary<string, object?>? toolArgs = null;

        if (parts.Length == 3)

        {

            try { var tmp = JsonSerializer.Deserialize<Dictionary<string, object?>>(parts[2]); toolArgs = tmp; } catch { toolArgs = null; }

        }



        try

        {

            var resp = await mcpClient.CallToolAsync(toolName, toolArgs);

            Console.WriteLine("Tool response:");

            Console.WriteLine(JsonSerializer.Serialize(resp, new JsonSerializerOptions { WriteIndented = true }));

        }

        catch (Exception ex)

        {

            Console.WriteLine($"Tool call failed: {ex.Message}");

        }

    }

    else

    {

        Console.WriteLine("Usage: tool <name> [jsonArguments]");

    }

}



static async Task<string?> GenerateWithGeminiAsync(string apiKey, string model, string prompt)

{

    using var http = new HttpClient();



    // 1. Use the correct v1beta/generateContent endpoint for Gemini

    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";



    // 2. Construct the Payload

    var payload = new

    {

        contents = new[]

        {

            new

            {

                parts = new[]

                {

                    new { text = prompt }

                }

            }

        }

    };



    try

    {

        var httpResponse = await http.PostAsJsonAsync(url, payload);

        var body = await httpResponse.Content.ReadAsStringAsync();



        if (!httpResponse.IsSuccessStatusCode)

        {

            return $"Gemini API Error ({(int)httpResponse.StatusCode}): {body}";

        }



        using var doc = JsonDocument.Parse(body);

        var root = doc.RootElement;



        // 3. Parse standard Gemini response structure

        if (root.TryGetProperty("candidates", out var candidates) &&

            candidates.ValueKind == JsonValueKind.Array &&

            candidates.GetArrayLength() > 0)

        {

            var firstCandidate = candidates[0];

            if (firstCandidate.TryGetProperty("content", out var content) &&

                content.TryGetProperty("parts", out var parts) &&

                parts.ValueKind == JsonValueKind.Array)

            {

                foreach (var part in parts.EnumerateArray())

                {

                    if (part.TryGetProperty("text", out var text))

                    {

                        return text.GetString();

                    }

                }

            }

        }



        return "No text content found in response.";

    }

    catch (Exception ex)

    {

        return $"Exception during API call: {ex.Message}";

    }

}



static (string command, string[] arguments) GetCommandAndArguments(string[] args)

{

    if (args is null || args.Length == 0)

        throw new NotSupportedException("No server argument provided. Pass a .py, .js, .csproj, directory, or executable path.");



    var script = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? string.Empty;

    script = script.Trim('"', '\'');



    // If csproj specified, return dotnet run arguments

    if (script.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))

    {

        return ("dotnet", new[] { "run", "--project", script, "--no-build" });

    }



    if (script.EndsWith(".py", StringComparison.OrdinalIgnoreCase))

        return ("python", new[] { script });

    if (script.EndsWith(".js", StringComparison.OrdinalIgnoreCase))

        return ("node", new[] { script });

    if (script.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))

        return (script, Array.Empty<string>());

    if (Directory.Exists(script))

        return ("dotnet", new[] { "run", "--project", script, "--no-build" });



    // Fallback: try to resolve full path

    try { script = Path.GetFullPath(script); } catch { }

    if (File.Exists(script) && script.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))

        return (script, Array.Empty<string>());



    throw new NotSupportedException($"Unsupported server argument: '{script}'. Use a .py, .js, .csproj, directory, or executable path.");

}



static void PromptForInput()
{

    Console.WriteLine("Enter a command (or 'exit' to quit):");

    Console.ForegroundColor = ConsoleColor.Cyan;

    Console.Write("> ");

    Console.ResetColor();

}