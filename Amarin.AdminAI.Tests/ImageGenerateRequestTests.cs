using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Venice sizes the diffusion models by pixels and the Gemini-backed nano-banana line by aspect
/// ratio; sending the wrong pair is rejected by the API. These pin the serialized shape, and
/// they also catch a DTO change that was never registered in the source-generated JSON context.
/// </summary>
public sealed class ImageGenerateRequestTests
{
    private static string Serialize(ImageGenerateRequest request) =>
        JsonSerializer.Serialize(request, VeniceJsonContext.Default.ImageGenerateRequest);

    [Fact]
    public void Nano_banana_is_sized_by_ratio_and_carries_no_pixel_dimensions()
    {
        var json = Serialize(new ImageGenerateRequest
        {
            Model = "nano-banana-pro",
            Prompt = "infographic",
            AspectRatio = "3:4",
            Resolution = "2K"
        });

        Assert.Contains("\"aspect_ratio\":\"3:4\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolution\":\"2K\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"width\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"height\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pixel_model_is_sized_by_pixels_and_carries_no_ratio()
    {
        var json = Serialize(new ImageGenerateRequest
        {
            Model = "venice-sd35",
            Prompt = "a cat",
            Width = 1024,
            Height = 1024
        });

        Assert.Contains("\"width\":1024", json, StringComparison.Ordinal);
        Assert.Contains("\"height\":1024", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"aspect_ratio\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"resolution\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_image_model_is_nano_banana()
    {
        // The infographic and the chat tool both lean on this constant.
        Assert.Equal("nano-banana-pro", VeniceClient.DefaultImageModel);
    }

    [Fact]
    public void Response_reads_back_the_base64_list()
    {
        const string Body = """{"id":"abc","images":["QUJD"],"timing":{"total":1200}}""";
        var parsed = JsonSerializer.Deserialize(Body, VeniceJsonContext.Default.ImageGenerateResponse);

        Assert.NotNull(parsed);
        Assert.Equal("abc", parsed!.Id);
        Assert.Equal("QUJD", Assert.Single(parsed.Images));
    }
}
