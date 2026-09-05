using System.Text.Json;

namespace Amarin.Tools;

/// <summary>
/// Draws a picture from a description. The model calls this wherever an illustration belongs in
/// its answer, and the image is rendered at exactly that point in the reply.
/// </summary>
public sealed class GenerateImageTool : ITool
{
    /// <summary>Signature matches VeniceClient.GenerateImageAsync (prompt, width, height, model, ct).</summary>
    private readonly Func<string, int, int, string?, CancellationToken, Task<string>> _generate;

    public GenerateImageTool(Func<string, int, int, string?, CancellationToken, Task<string>> generate) =>
        _generate = generate;

    public string Name => "generate_image";

    public string Description =>
        "Generate an image from a text description and place it in the reply. Use it when a " +
        "picture explains better than words: diagrams, infographics, illustrations, mock-ups. " +
        "Describe the whole picture in one detailed English prompt — including any text that " +
        "must appear inside it. The image is inserted where this tool is called, so call it at " +
        "the point in the answer where the picture belongs.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Detailed English description of the image, including any text to render inside it"
            },
            "orientation": {
              "type": "string",
              "enum": ["square", "portrait", "landscape"],
              "description": "Shape of the image. Default square. Use portrait for infographics."
            }
          },
          "required": ["prompt"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("prompt", out var promptProp) ||
            string.IsNullOrWhiteSpace(promptProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: prompt");
        }

        var prompt = promptProp.GetString()!;
        var orientation = arguments.TryGetProperty("orientation", out var shapeProp)
            ? shapeProp.GetString()
            : null;

        var (width, height) = orientation?.ToLowerInvariant() switch
        {
            "portrait" => (768, 1280),
            "landscape" => (1280, 768),
            _ => (1024, 1024)
        };

        try
        {
            var base64 = await _generate(prompt, width, height, null, cancellationToken);

            // The caller mints a handle for the image and tells the model to write it as a
            // markdown link — that is what lets the picture land mid-answer rather than after it.
            return ToolResult.WithImage("Изображение создано.", base64);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Image generation error: {ex.Message}");
        }
    }
}
