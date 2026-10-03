using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выход с обновлением: окно прячется, процесс без окна доводит проверку и загрузку, подменяет
/// файл и выходит. До 1.30.0 это проверялось только оконными тестами через отражение в поля окна.
/// </summary>
public sealed class UpdateExitTests
{
    private static readonly ReleaseInfo V130 = UpdateTestKit.Release("1.30.0");
    private static readonly ReleaseInfo V131 = UpdateTestKit.Release("1.31.0");

    [Fact]
    public void With_nothing_to_install_the_program_just_closes()
    {
        using var rig = new UpdateRig();
        rig.Controller.Start();
        rig.Source.Answer(new UpdateCheckResult { Latest = UpdateTestKit.Release("1.29.0") });
        rig.Queue.Run();

        Assert.False(rig.Exit.ShouldDeferClose);
        _ = rig.Exit.BeginAsync();
        rig.Queue.Run();

        Assert.Equal(0, rig.Host.Hidden);
        Assert.Empty(rig.Files.Swaps);
        Assert.Equal(1, rig.Host.Shutdowns);
    }

    [Fact]
    public void A_downloaded_build_is_installed_after_the_window_is_gone()
    {
        using var rig = new UpdateRig();
        SeedFresh(rig, state => state with { Latest = V130, Staged = UpdateTestKit.Staged(V130) });

        Assert.True(rig.Exit.ShouldDeferClose);
        var exit = rig.Exit.BeginAsync();
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.Equal(1, rig.Host.Hidden);
        Assert.Equal(V130.Release, Assert.Single(rig.Files.Swaps).Version);
        Assert.Empty(rig.Host.Successors);
        Assert.Equal(1, rig.Host.Shutdowns);
        Assert.True(rig.State.SwapDone);

        // Новая версия сама не запускается: человек просил закрыть программу.
        Assert.Empty(rig.App.Restarts);
    }

    [Fact]
    public void Closing_during_the_check_waits_for_it_and_for_the_download()
    {
        using var rig = new UpdateRig();
        rig.Controller.Start();

        Assert.True(rig.Exit.ShouldDeferClose);
        var exit = rig.Exit.BeginAsync();
        Assert.Equal(1, rig.Host.Hidden);

        rig.Source.Answer(UpdateTestKit.Found(V130));
        rig.Queue.Run();
        Assert.Empty(rig.Files.Swaps);

        Assert.Single(rig.Files.Downloads).Finish();
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.Equal(V130.Release, Assert.Single(rig.Files.Swaps).Version);
        Assert.Equal(1, rig.Host.Shutdowns);
    }

    [Fact]
    public void Closing_with_an_old_answer_asks_github_again_first()
    {
        // Программа бывает открыта сутками: ставить при закрытии надо то, что лежит на GitHub
        // сейчас, а не то, что было там утром.
        using var rig = new UpdateRig();
        rig.Controller.Start();
        rig.Source.Answer(new UpdateCheckResult { Latest = UpdateTestKit.Release("1.29.0") });
        rig.Queue.Run();
        rig.Time.Advance(UpdateSchedule.RecheckOnExit);
        rig.Queue.Run();

        var exit = rig.Exit.BeginAsync();
        Assert.Equal(2, rig.Source.Calls.Count);

        rig.Source.Answer(UpdateTestKit.Found(V130));
        rig.Queue.Run();
        Assert.Single(rig.Files.Downloads).Finish();
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.Single(rig.Files.Swaps);
    }

    [Fact]
    public void Closing_with_v1_downloaded_and_v2_downloading_waits_for_v2()
    {
        // Гонка 4. Выход ставил уже скачанную v1, не дождавшись v2, которая докачивалась рядом.
        using var rig = new UpdateRig();
        var v2 = StagedThenNewer(rig);

        var exit = rig.Exit.BeginAsync();
        rig.Queue.Run();
        Assert.Empty(rig.Files.Swaps);

        v2.Finish();
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.Equal(V131.Release, Assert.Single(rig.Files.Swaps).Version);
    }

    [Fact]
    public void Waiting_for_v2_ends_at_the_limit_and_v1_is_installed_instead()
    {
        using var rig = new UpdateRig();
        var v2 = StagedThenNewer(rig);

        var exit = rig.Exit.BeginAsync();
        rig.Queue.Run();
        rig.Time.Advance(UpdateSchedule.ExitLimit);
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.True(v2.Token.IsCancellationRequested);
        Assert.Equal(V130.Release, Assert.Single(rig.Files.Swaps).Version);
        Assert.Equal(1, rig.Host.Shutdowns);
    }

    [Fact]
    public void A_download_the_person_started_is_put_aside_when_the_program_closes()
    {
        // Человек нажал «Обновить» и закрыл программу, не дождавшись: перезапускать её нельзя —
        // он просил закрыть. Скачанное ставит сам выход.
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V130);
        rig.Controller.Confirm(Assert.IsType<UpdateOffer>(rig.Controller.Prepare()));

        var exit = rig.Exit.BeginAsync();
        download.Finish();
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.Empty(rig.App.Restarts);
        Assert.Single(rig.Files.Swaps);
        Assert.Equal(1, rig.Host.Shutdowns);
    }

    [Fact]
    public void A_download_that_failed_is_tried_once_more_without_the_window()
    {
        using var rig = new UpdateRig();
        rig.StartBackgroundDownload(V130).Fail("обрыв");
        rig.Queue.Run();
        Assert.NotNull(rig.State.Failure);

        var exit = rig.Exit.BeginAsync();
        rig.Queue.Run();
        Assert.Equal(2, rig.Files.Downloads.Count);

        rig.Files.Downloads[1].Finish();
        rig.Queue.RunUntil(() => exit.IsCompleted);
        Assert.Single(rig.Files.Swaps);
    }

    [Fact]
    public void A_second_launch_during_the_background_update_brings_the_window_back()
    {
        // Человек закрыл программу, пока докачивалось обновление, и тут же открыл снова. Второй
        // запуск отдаёт запрос этому процессу, и тот обязан показать окно, а не молча выйти —
        // иначе оба процесса исчезнут, и человек останется ни с чем.
        using var rig = new UpdateRig();
        rig.Controller.Start();
        var exit = rig.Exit.BeginAsync();
        Assert.True(rig.Exit.HiddenForExit);
        var generation = rig.Exit.Generation;

        Assert.True(rig.Exit.Revive());

        Assert.False(rig.Exit.Exiting);
        Assert.False(rig.Exit.HiddenForExit);
        Assert.False(rig.State.Exiting);
        Assert.Equal(1, rig.Host.Shown);

        // Новый номер попытки: прежний выход, дождавшись загрузки, обязан отступить.
        Assert.Equal(generation + 1, rig.Exit.Generation);
        rig.Source.Answer(UpdateTestKit.Found(V130));
        rig.Queue.Run();
        Assert.Single(rig.Files.Downloads).Finish();
        rig.Queue.RunUntil(() => exit.IsCompleted);

        Assert.Empty(rig.Files.Swaps);
        Assert.Equal(0, rig.Host.Shutdowns);
        Assert.NotNull(rig.State.Staged);
    }

    [Fact]
    public void A_second_launch_after_the_swap_began_starts_the_new_version_after_it()
    {
        using var rig = new UpdateRig();
        SeedFresh(rig, state => state with { Staged = UpdateTestKit.Staged(V130) });

        var exit = rig.Exit.BeginAsync();
        Assert.True(rig.Exit.SwapStarted);

        Assert.False(rig.Exit.Revive());
        Assert.True(rig.Exit.RelaunchAfterExit);

        rig.Queue.RunUntil(() => exit.IsCompleted);
        Assert.Equal("/apps/Amarin Admin AI.exe", Assert.Single(rig.Host.Successors));
        Assert.Equal(1, rig.Host.Shutdowns);
        Assert.Equal(0, rig.Host.Shown);
    }

    [Fact]
    public void Reviving_a_window_that_was_never_hidden_changes_nothing()
    {
        using var rig = new UpdateRig();

        Assert.True(rig.Exit.Revive());
        Assert.Equal(0, rig.Host.Shown);
        Assert.Equal(0, rig.Exit.Generation);
    }

    [Fact]
    public void A_wipe_or_an_admin_restart_closes_without_finishing_the_update()
    {
        // Преемник ждёт этот процесс не дольше полуминуты, а загрузка без окна длилась бы до
        // пятнадцати минут — преемник отдал бы запрос живому владельцу и вышел.
        using var rig = new UpdateRig();
        rig.Host.UpdateForbidden = true;
        SeedFresh(rig, state => state with { Staged = UpdateTestKit.Staged(V130) });

        _ = rig.Exit.BeginAsync();
        rig.Queue.Run();

        Assert.Equal(0, rig.Host.Hidden);
        Assert.Empty(rig.Files.Swaps);
        Assert.Equal(1, rig.Host.Shutdowns);
    }

    [Fact]
    public void A_window_beside_the_main_one_never_holds_its_close()
    {
        // Оконные тесты поднимают свои окна рядом с общим: закрытие такого окна не должно гасить
        // всё приложение через несколько секунд.
        using var rig = new UpdateRig();
        rig.Host.OwnsApplication = false;
        SeedFresh(rig, state => state with { Staged = UpdateTestKit.Staged(V130) });

        Assert.False(rig.Exit.ShouldDeferClose);
    }

    [Fact]
    public void Without_auto_update_only_a_downloaded_build_holds_the_close()
    {
        // Снятая галка значит «ничего не делай сам»: процесс, оставшийся жить после закрытия
        // окна ради проверки, был бы именно этим.
        using var rig = new UpdateRig();
        rig.App.AutoUpdate = false;
        rig.Controller.Seed(state => state with { Latest = V130 });

        Assert.False(rig.Exit.PendingForExit);
        Assert.False(rig.Exit.ShouldDeferClose);

        rig.Controller.Seed(state => state with { Staged = UpdateTestKit.Staged(V130) });
        Assert.True(rig.Exit.ShouldDeferClose);

        var exit = rig.Exit.BeginAsync();
        rig.Queue.RunUntil(() => exit.IsCompleted);
        Assert.Empty(rig.Source.Calls);
        Assert.Single(rig.Files.Swaps);
    }

    [Fact]
    public void The_end_of_the_windows_session_swaps_a_downloaded_build_at_once()
    {
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V131);
        rig.Controller.Seed(state => state with { Staged = UpdateTestKit.Staged(V130) });

        rig.Exit.OnSessionEnding();
        rig.Exit.OnSessionEnding();

        // Ждать загрузку некогда, а скачанное — два переименования.
        Assert.True(download.Token.IsCancellationRequested);
        Assert.Equal(V130.Release, Assert.Single(rig.Files.Swaps).Version);
        Assert.True(rig.State.SwapDone);
    }

    [Fact]
    public void The_end_of_the_windows_session_never_asks_for_admin_rights()
    {
        using var rig = new UpdateRig();
        rig.Controller.Seed(state => state with { Staged = UpdateTestKit.Staged(V130, elevated: true) });

        rig.Exit.OnSessionEnding();

        Assert.Empty(rig.Files.Swaps);
        Assert.Empty(rig.Files.ElevatedSwaps);
        Assert.False(rig.State.SwapDone);
    }

    /// <summary>Состояние со свежей удачной проверкой: выход не пойдёт в GitHub ещё раз.</summary>
    private static void SeedFresh(UpdateRig rig, Func<UpdateState, UpdateState> change) =>
        rig.Controller.Seed(state => change(state) with { LastSuccessUtc = rig.Time.GetUtcNow().UtcDateTime });

    /// <summary>Скачана v1.30, а проверка нашла v1.31 и качает её в фоне.</summary>
    private static FakeUpdateFiles.Download StagedThenNewer(UpdateRig rig)
    {
        rig.Controller.Seed(state => state with { Staged = UpdateTestKit.Staged(V130) });
        rig.Controller.CheckNow();
        rig.Source.Answer(UpdateTestKit.Found(V131));
        rig.Queue.Run();
        return Assert.Single(rig.Files.Downloads);
    }
}
