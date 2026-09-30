namespace Amarin.AdminAI.Tests;

/// <summary>
/// Порядок шагов выпуска (H3): подпись — до суммы, проверка подписи — сразу после неё, сверка
/// суммы с файлом — последней перед выкладкой. Переставь их — и релиз уедет с суммой не того
/// файла или с битой подписью, а заметят это только люди, у которых обновление не встанет.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static string Workflow()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "release.yml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("release.yml");
    }

    [Fact]
    public void Signing_is_verified_and_the_checksum_is_rechecked_right_before_upload()
    {
        var text = Workflow();
        string[] steps = ["- name: Publish", "- name: Sign", "- name: Verify signature", "- name: Checksums", "- name: Verify checksums", "- name: Release"];

        var positions = steps.Select(step => text.IndexOf(step, StringComparison.Ordinal)).ToList();

        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("Get-AuthenticodeSignature", text, StringComparison.Ordinal);
    }
}
