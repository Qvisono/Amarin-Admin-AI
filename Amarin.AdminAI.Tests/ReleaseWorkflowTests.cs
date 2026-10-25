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

    [Fact]
    public void The_policy_templates_ship_with_every_release_but_stay_out_of_the_checksums()
    {
        // Шаблон ADMX (1.37.0) — вложение релиза: и при первой выкладке, и при повторной. В сумме
        // его нет: SHA256SUMS — одна строка про exe, её сверяет шаг перед выкладкой и читает программа.
        var text = Workflow();

        var templates = text.IndexOf("- name: Policy templates", StringComparison.Ordinal);
        Assert.True(templates > text.IndexOf("- name: Publish", StringComparison.Ordinal));
        Assert.True(templates < text.IndexOf("- name: Checksums", StringComparison.Ordinal));
        Assert.Contains("Compress-Archive -Path policies/*", text, StringComparison.Ordinal);
        Assert.Contains("gh release upload $env:TAG $env:ASSET $env:SUMS $env:POLICIES --clobber", text, StringComparison.Ordinal);
        Assert.Contains("gh release create $env:TAG $env:ASSET $env:SUMS $env:POLICIES --target", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(SourceTree.RepositoryRoot, "policies", "AmarinAdminAI.admx")));
    }

    [Fact]
    public void Update_patches_and_winget_come_after_the_release_and_never_stop_it()
    {
        // Заплатки (1.37.0) делает сам новый exe, и ему нужен уже выложенный релиз прошлой версии;
        // winget нужна сумма из SHA256SUMS. Ни то ни другое не должно валить выпуск: без заплатки
        // программа качает весь файл, а без winget её качают со страницы релизов.
        var text = Workflow();
        string[] steps = ["- name: Checksums", "- name: Release", "- name: Update patches", "- name: Winget manifests", "- name: Winget submit"];

        var positions = steps.Select(step => text.IndexOf(step, StringComparison.Ordinal)).ToList();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);

        foreach (var step in steps[2..])
        {
            var body = text[text.IndexOf(step, StringComparison.Ordinal)..];
            var next = body.IndexOf("- name:", step.Length, StringComparison.Ordinal);
            Assert.Contains("continue-on-error: true", next > 0 ? body[..next] : body, StringComparison.Ordinal);
        }

        // Имя заплатки — контракт с ReleaseInfo.PatchFrom.
        Assert.Contains("--make-update-patch", text, StringComparison.Ordinal);
        Assert.Contains("-from-$base-win-x64.patch", text, StringComparison.Ordinal);
        Assert.Contains("!contains(steps.version.outputs.version, '-')", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_winget_token_is_seen_only_by_the_submit_step()
    {
        // Токен долгоживущий и пишет во все публичные репозитории владельца. В окружении задания,
        // как секреты подписи, его видели бы тесты, пакеты NuGet и свежий exe.
        var text = Workflow();
        var token = text.IndexOf("secrets.WINGET_TOKEN", StringComparison.Ordinal);

        Assert.Equal(token, text.LastIndexOf("secrets.WINGET_TOKEN", StringComparison.Ordinal));
        Assert.True(token > text.IndexOf("- name: Winget submit", StringComparison.Ordinal));
    }

    [Fact]
    public void The_winget_templates_describe_one_portable_package()
    {
        var folder = Path.Combine(SourceTree.RepositoryRoot, "packaging", "winget");
        var files = Directory.GetFiles(folder, "*.yaml");

        Assert.Equal(3, files.Length);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.Contains("PackageIdentifier: Qvisono.AmarinAdminAI", text, StringComparison.Ordinal);
            Assert.Contains("PackageVersion: {{Version}}", text, StringComparison.Ordinal);
            Assert.Contains("ManifestVersion: 1.10.0", text, StringComparison.Ordinal);
        }

        var installer = File.ReadAllText(Path.Combine(folder, "Qvisono.AmarinAdminAI.installer.yaml"));
        Assert.Contains("InstallerType: portable", installer, StringComparison.Ordinal);
        Assert.Contains("{{InstallerUrl}}", installer, StringComparison.Ordinal);
        Assert.Contains("{{InstallerSha256}}", installer, StringComparison.Ordinal);
    }
}
