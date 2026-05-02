using System.Text.Json;
using System.Text.Json.Nodes;

namespace InfraMapper.Services.Agent;

public sealed class MessagesRequest
{
    public string Model { get; set; } = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-6";
    public int MaxTokens { get; set; } = 8192;
    public string? System { get; set; }
    public List<MessageParam> Messages { get; set; } = new();
    public List<ToolDefinition>? Tools { get; set; }
}

public sealed class MessageParam
{
    public string Role { get; set; } = "";
    public JsonNode Content { get; set; } = JsonValue.Create("")!;

    public static MessageParam User(string text) =>
        new() { Role = "user", Content = JsonValue.Create(text)! };

    public static MessageParam UserWithBlocks(JsonArray blocks) =>
        new() { Role = "user", Content = blocks };

    public static MessageParam Assistant(JsonArray blocks) =>
        new() { Role = "assistant", Content = blocks };
}

public sealed class MessagesResponse
{
    public string Id { get; set; } = "";
    public string StopReason { get; set; } = "";
    public List<ContentBlock> Content { get; set; } = new();
    public AnthropicUsage? Usage { get; set; }
}

public sealed class AnthropicUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}

public sealed class ContentBlock
{
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement Input { get; set; }

    public JsonNode ToNode()
    {
        var obj = new JsonObject { ["type"] = Type };
        if (Type == "text") obj["text"] = Text;
        if (Type == "tool_use")
        {
            obj["id"] = Id;
            obj["name"] = Name;
            obj["input"] = JsonNode.Parse(Input.GetRawText());
        }
        return obj;
    }
}

public sealed class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonNode InputSchema { get; set; } = new JsonObject();
}
