using System.Text.Json;

namespace GroupWeaver.App.Graph;

/// <summary>
/// Parses raw JS→.NET bridge JSON into <see cref="GraphMessage"/>s. Total function:
/// malformed JSON, unknown message types, and missing/invalid required fields all
/// map to <see cref="UnknownMessage"/> — <see cref="Parse"/> NEVER throws (the
/// bridge callback has no sane place to catch).
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/Graph/GraphMessageParserTests.cs</c>.
/// </summary>
public static class GraphMessageParser
{
    /// <summary>Parses one bridge message; unparseable input becomes <see cref="UnknownMessage"/>.</summary>
    public static GraphMessage Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new UnknownMessage(json, $"malformed JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new UnknownMessage(json, $"expected a JSON object, got {root.ValueKind}");
            }

            if (!TryGetString(root, "type", out var type))
            {
                return new UnknownMessage(json, "missing string 'type' field");
            }

            // Extra fields (graph.js sends e.g. userAgent, label) are tolerated:
            // each case reads only what its record models.
            return type switch
            {
                "ready" => new ReadyMessage(),
                "loaded" => ParseLoaded(root, json),
                "nodeClick" => ParseNodeClick(root, json),
                "nodeExpand" => ParseNodeExpand(root, json),
                "jsError" => ParseJsError(root, json),
                _ => new UnknownMessage(json, $"unknown message type '{type}'"),
            };
        }
    }

    private static GraphMessage ParseLoaded(JsonElement root, string raw) =>
        TryGetInt(root, "nodeCount", out var nodeCount) && TryGetInt(root, "edgeCount", out var edgeCount)
            ? new LoadedMessage(nodeCount, edgeCount)
            : new UnknownMessage(raw, "loaded: integer 'nodeCount' and 'edgeCount' are required");

    private static GraphMessage ParseNodeClick(JsonElement root, string raw) =>
        TryGetString(root, "id", out var id) && TryGetString(root, "kind", out var kind)
            ? new NodeClickMessage(id, kind)
            : new UnknownMessage(raw, "nodeClick: string 'id' and 'kind' are required");

    private static GraphMessage ParseNodeExpand(JsonElement root, string raw) =>
        TryGetString(root, "id", out var id)
            ? new NodeExpandMessage(id)
            : new UnknownMessage(raw, "nodeExpand: string 'id' is required");

    private static GraphMessage ParseJsError(JsonElement root, string raw) =>
        TryGetString(root, "source", out var source) && TryGetString(root, "message", out var message)
            ? new JsErrorMessage(source, message)
            : new UnknownMessage(raw, "jsError: string 'source' and 'message' are required");

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        if (obj.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
