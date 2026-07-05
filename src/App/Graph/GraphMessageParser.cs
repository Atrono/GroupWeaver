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
                "ready" => ParseReady(root),
                "loaded" => ParseLoaded(root, json),
                "nodeClick" => ParseNodeClick(root, json),
                "nodeExpand" => ParseNodeExpand(root, json),
                "focused" => new FocusedMessage(),
                "jsError" => ParseJsError(root, json),
                "pngExported" => ParsePngExported(root, json),
                "stateReport" => ParseStateReport(root, json),
                _ => new UnknownMessage(json, $"unknown message type '{type}'"),
            };
        }
    }

    // Both fields are OPTIONAL (ADR-037 D6): graph.js sends webglRenderer:null when the WebGL
    // context/extension is unavailable (JSON null => TryGetString false => C# null), and a bare
    // {"type":"ready"} must keep parsing — ready is never demoted to UnknownMessage.
    private static GraphMessage ParseReady(JsonElement root) =>
        new ReadyMessage(
            TryGetString(root, "webglRenderer", out var webglRenderer) ? webglRenderer : null,
            TryGetString(root, "userAgent", out var userAgent) ? userAgent : null);

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

    // `data` (the base64 image) is the sole required field — without it the message
    // carries no bytes. `width`/`height` are diagnostics: optional, default 0 (never
    // demoting a valid payload to Unknown, which would trip RendererError per ADR-013 F1).
    private static GraphMessage ParsePngExported(JsonElement root, string raw)
    {
        if (!TryGetString(root, "data", out var data))
        {
            return new UnknownMessage(raw, "pngExported: string 'data' is required");
        }

        TryGetInt(root, "width", out var width);
        TryGetInt(root, "height", out var height);
        return new PngExportedMessage(data, width, height);
    }

    // ADR-038 D3.2 (WP6, #245): the `--e2e` page-truth reply. `selected` is the ONE genuinely
    // OPTIONAL field (JSON null or absent both mean "nothing selected" — GetNullableString);
    // every other field is required (a malformed reply must not silently report zeroed state).
    private static GraphMessage ParseStateReport(JsonElement root, string raw)
    {
        if (!TryGetInt(root, "seq", out var seq)
            || !TryGetInt(root, "nodes", out var nodes)
            || !TryGetInt(root, "edges", out var edges)
            || !TryGetDouble(root, "zoom", out var zoom)
            || !TryGetDouble(root, "panX", out var panX)
            || !TryGetDouble(root, "panY", out var panY)
            || !TryGetBool(root, "animated", out var animated))
        {
            return new UnknownMessage(
                raw,
                "stateReport: integer 'seq'/'nodes'/'edges', number 'zoom'/'panX'/'panY', "
                    + "and boolean 'animated' are required");
        }

        return new StateReportMessage(
            seq, nodes, edges, zoom, panX, panY, GetNullableString(root, "selected"), animated);
    }

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

    // OPTIONAL string field (ADR-037 D6 idiom, e.g. ReadyMessage's webglRenderer/userAgent):
    // absent, JSON null, or the wrong type all map to null — no UnknownMessage demotion.
    private static string? GetNullableString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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

    private static bool TryGetDouble(JsonElement obj, string name, out double value)
    {
        if (obj.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetBool(JsonElement obj, string name, out bool value)
    {
        if (obj.TryGetProperty(name, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }
}
