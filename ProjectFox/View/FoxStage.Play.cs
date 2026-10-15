using System.Windows;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>
/// Занятия воли (<see cref="FoxWill"/>) и игры с человеком: прилечь и повилять хвостом, сменить
/// место, catwalk, поймать свой хвост, подразнить, побесить; мячик, прятки, догонялки, драка с
/// курсором.
/// </summary>
/// <remarks>
/// Что делать, решает мозг; здесь только как. Игра одна за раз (<see cref="_game"/>), Esc
/// заканчивает её (<see cref="Holding"/>), взятый на руки Роза бросает игру сам: всё отложенное
/// игрой живёт в <see cref="After"/> и отменяется новым планом.
/// </remarks>
internal sealed partial class FoxStage
{
    /// <summary>Сколько раз выглядывает в прятках.</summary>
    private const int SeekRounds = 3;

    /// <summary>Сколько ждёт, что мячик бросят, прежде чем обидеться.</summary>
    private static readonly TimeSpan FetchWait = TimeSpan.FromSeconds(12);

    /// <summary>Не чаще этого затевает драку с дразнящим курсором.</summary>
    private static readonly TimeSpan TeaseEvery = TimeSpan.FromSeconds(20);

    /// <summary>Идущая игра: <c>fetch</c>, <c>hideseek</c>, <c>catch</c>, <c>fight</c>; null — не играет.</summary>
    private string? _game;
    private DateTime _gameUntil;
    private int _seekLeft;
    private int _swipes;
    private DateTime _nextSwipe;
    private bool _ballWaiting;
    private DateTime _pettedAt;
    private DateTime _lastTeased;
    private readonly List<(DateTime At, int Direction)> _wiggles = [];
    private double _lastWiggleX;

    /// <summary>Идущая игра — тестам.</summary>
    internal string? Game => _game;

    /// <summary>Нынешняя анимация — тестам.</summary>
    internal string Anim => _anim;

    /// <summary>Курсор над Розой без нажатия — тестам, без настоящей мыши.</summary>
    internal void MovedOver(Point onSprite, Point onLayer) => Hover(onSprite, onLayer);

    /// <summary>
    /// Идёт игра: своя, указка или мячик. Пока идёт, воля своего не затевает, а курсор над Розой —
    /// часть игры: за указкой он бегает прямо под курсором, и махание ею читалось как дразнилка —
    /// драка с курсором, а через 20 с новая, и погоня не кончалась.
    /// </summary>
    private bool Playing => _game is not null || _laser is not null || _ball is not null;

    /// <summary>Принесённый мячик лежит и ждёт броска — тестам.</summary>
    internal bool BallWaiting => _ballWaiting;

    /// <summary>Щелчок по точке рисунка без мыши — тестам.</summary>
    internal void ClickAt(Point onSprite) => Click(onSprite);

    /// <summary>Игра из меню: «Прятки» и «Догонялки». Сменяет прежнюю игру, а не ждёт её конца.</summary>
    public void PlayGame(string game)
    {
        EndPlay();
        if (_mode == FoxMode.Sleep)
        {
            Wake();
        }

        if (_mode == FoxMode.Peek)
        {
            EndPeek();
        }

        Do(game);
        if (game == FoxWill.HideSeek || game == FoxWill.Catch)
        {
            Bubble(Loc.Get(FoxPhrases.Pick(game == FoxWill.HideSeek ? "HideSeek" : "Catch", _random)), 1800);
        }
    }

    /// <summary>Занятие от воли или из меню. Занятой (в руках, в игре, на ходу) ничего не начинает.</summary>
    private void Do(string activity)
    {
        if (_mode != FoxMode.Idle || Playing || Talking)
        {
            return;
        }

        Replan();
        switch (activity)
        {
            case FoxWill.Lie:
                LieDown(wag: true);
                After(TimeSpan.FromSeconds(8 + _random.Next(8)), () =>
                {
                    if (_lying && _mode == FoxMode.Idle)
                    {
                        _lying = false;
                        Play("stretch", "sit");
                    }
                });
                break;
            case FoxWill.Wander:
                Wander();
                break;
            case FoxWill.Strut:
                Strut();
                break;
            case FoxWill.Chase:
                ChaseTail();
                break;
            case FoxWill.Tease:
                Tease();
                break;
            case FoxWill.Pester:
                Pester();
                break;
            case FoxWill.Fetch:
                StartFetch();
                break;
            case FoxWill.HideSeek:
                StartHideSeek();
                break;
            case FoxWill.Catch:
                StartGame(FoxWill.Catch, TimeSpan.FromSeconds(15));
                Feel("sly", 1800);
                break;
            case FoxWill.Fight:
                StartGame(FoxWill.Fight, TimeSpan.FromSeconds(8));
                _swipes = 0;
                _nextSwipe = _clock();
                Feel("sly", 1200);
                break;
        }
    }

    private void StartGame(string game, TimeSpan length)
    {
        _game = game;
        _gameUntil = _clock() + length;
        StartRendering();
    }

    /// <summary>
    /// Новая игра из меню сменяет любую прежнюю: две сразу тянули бы Розу к курсору и от него
    /// (указка и догонялки).
    /// </summary>
    private void EndPlay()
    {
        StopGame();
        StopLaser();
        RemoveBall();
    }

    /// <summary>Игра кончилась: время вышло, Esc, взяли на руки, окно свернули.</summary>
    private void StopGame()
    {
        if (_game == FoxWill.Fetch)
        {
            RemoveBall();
        }

        _game = null;
        _ballWaiting = false;
    }

    /// <summary>Мордочка, значок и поза эмоции — той же таблицей, что у мозга.</summary>
    private void Feel(string emotion, int ms) =>
        Apply(FoxEmotions.Feel(emotion, ms, pose: false));

    // ---------------- занятия ----------------

    /// <summary>
    /// Сменить место: другая точка той же кромки, пол между кнопками, край колонки или любимое
    /// место справа на поле ввода — оно весит вдвое.
    /// </summary>
    private void Wander()
    {
        if (!_host.Motion())
        {
            return;
        }

        List<Action> options = [() => WalkTo(RandomFoot(EdgeOf(_spot)), run: false)];
        if (_spot != "home" || Math.Abs(_foot - HomeFoot(_host.Home())) > Px(20))
        {
            options.Add(() => GoHomeEdge(HomeFoot(_host.Home()), run: false));
            options.Add(() => GoHomeEdge(HomeFoot(_host.Home()), run: false));
        }

        if (_spot != "floor" && _host.FloorSpans().Where(span => span.Right - span.Left >= Px(34)).ToList() is { Count: > 0 } spans)
        {
            options.Add(() => JumpTo("floor", RandomFoot(spans[_random.Next(spans.Count)])));
        }

        if (_spot != "column" && _host.Column() is { } column)
        {
            options.Add(() => JumpTo("column", RandomFoot(column)));
        }

        options[_random.Next(options.Count)]();
    }

    private double RandomFoot(FoxEdge edge) => ClampFoot(edge, edge.Left + (_random.NextDouble() * (edge.Right - edge.Left)));

    /// <summary>Catwalk через всю кромку, а в конце — поворот и искры.</summary>
    private void Strut()
    {
        if (!_host.Motion())
        {
            return;
        }

        var edge = EdgeOf(_spot);
        var target = _foot > (edge.Left + edge.Right) / 2 ? edge.Left : edge.Right;
        Feel("proud", 6000);
        WalkTo(target, "strut", () =>
        {
            _flip = !_flip;
            Feel("proud", 1800);
            Burst("spark", 2);
        });
    }

    /// <summary>
    /// Ловит свой хвост: оглянулся, крутится на месте, поднимая пыль, и кружится голова. Отдельной
    /// позой на 48 точках это не читается, а бег с разворотами — читается.
    /// </summary>
    private void ChaseTail()
    {
        if (!_host.Motion())
        {
            return;
        }

        Play("lookback", "sit");
        _reactUntil = _clock().AddSeconds(3);
        After(TimeSpan.FromMilliseconds(700), () =>
        {
            Play("run", "run");
            _reactUntil = _clock().AddSeconds(3);
            Feel("excited", 2000);
        });
        for (var i = 1; i <= 14; i++)
        {
            var last = i == 14;
            After(TimeSpan.FromMilliseconds(700 + (i * 120)), () =>
            {
                _flip = !_flip;
                Burst("puff", 1);
                if (last)
                {
                    Play("sit", "sit");
                    _face = "dizzy";
                    _faceUntil = _clock().AddSeconds(2.4);
                    Burst("star", 3);
                }
            });
        }
    }

    /// <summary>Дразнит: поворачивается задом к курсору, виляет и оглядывается с языком.</summary>
    private void Tease()
    {
        var center = _move.X + (_sprite.Side / 2);
        if (_host.Pointer() is { } pointer)
        {
            // Лицом прочь от курсора — то есть задом к нему.
            _flip = pointer.X < center;
        }

        Play("tease", "tease");
        _reactUntil = _clock().AddSeconds(3);
        ShowEmote("scheme", 1800);
        After(TimeSpan.FromSeconds(3), () =>
        {
            if (_anim == "tease")
            {
                Play("sit", "sit");
                _flip = !_flip;
                _face = "sly";
                _faceUntil = _clock().AddSeconds(1.4);
            }
        });
    }

    /// <summary>
    /// Бесит: подбегает к курсору и выпрашивает ласку. Не погладили — топает, обижается и дразнит.
    /// </summary>
    private void Pester()
    {
        var started = _clock();
        void Beg()
        {
            if (_host.Pointer() is { } pointer)
            {
                _flip = pointer.X > _move.X + (_sprite.Side / 2);
            }

            Play("beg", "beg");
            _reactUntil = _clock().AddSeconds(3);
            Apply(FoxEmotions.Feel("puppy", 3000, pose: false));
            After(TimeSpan.FromSeconds(3), () =>
            {
                if (_pettedAt >= started || _mode != FoxMode.Idle)
                {
                    return;
                }

                Play("stomp", "sit");
                Feel("offended", 1600);
                After(TimeSpan.FromSeconds(1.6), () =>
                {
                    if (_pettedAt < started && _mode == FoxMode.Idle)
                    {
                        Tease();
                    }
                });
            });
        }

        if (_host.Motion() && _host.Pointer() is { } at && EdgeOf(_spot).Contains(at.X))
        {
            WalkTo(at.X, run: true, then: Beg);
        }
        else
        {
            Beg();
        }
    }

    // ---------------- игры ----------------

    /// <summary>Сам приносит мячик, кладёт у курсора и просит бросить.</summary>
    private void StartFetch()
    {
        if (!_host.Motion())
        {
            return;
        }

        StartGame(FoxWill.Fetch, FetchWait + TimeSpan.FromSeconds(10));
        _item = "ball";
        _itemUntil = _clock().AddSeconds(8);
        var edge = EdgeOf(_spot);
        var target = _host.Pointer() is { } pointer && edge.Contains(pointer.X) ? pointer.X : RandomFoot(edge);
        WalkTo(target, run: false, then: DropBall);
    }

    /// <summary>Мячик выпал из пасти перед носом и ждёт: щёлкнуть по нему — бросить.</summary>
    private void DropBall()
    {
        if (_game != FoxWill.Fetch)
        {
            return;
        }

        _item = null;
        RemoveBall();
        var head = HeadPoint();
        _ball = Pixels(_bitmaps.Item("ball"));
        _ball.IsHitTestVisible = true;
        _ball.Cursor = Cursors.Hand;
        _ball.MouseLeftButtonDown += KickBall;
        _ballX = head.X + (_flip ? Px(8) : -Px(14));
        _ballY = head.Y + Px(4);
        _ballVx = (_flip ? 1 : -1) * 0.08;
        _ballVy = -0.15;
        _ballSpot = null;
        _ballWaiting = true;
        _host.Layer.Children.Add(_ball);
        Apply(FoxEmotions.Feel("puppy", 3000, pose: false));
        Bubble(Loc.Get(FoxPhrases.Pick("FetchAsk", _random)), 2200);
        StartRendering();
        After(FetchWait, () =>
        {
            if (_game == FoxWill.Fetch && _ballWaiting)
            {
                StopGame();
                Signal(FoxSignalKind.GameIgnored);
            }
        });
    }

    /// <summary>Мячик бросили: летит прочь от Розы, а тот бежит за ним, как за брошенным из меню.</summary>
    private void KickBall(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_ball is null || !_ballWaiting)
        {
            return;
        }

        _ballWaiting = false;
        _ball.IsHitTestVisible = false;
        _game = null;
        var away = _ballX + (_ball.Width / 2) > _move.X + (_sprite.Side / 2) ? 1 : -1;
        _ballVx = away * (0.35 + (_random.NextDouble() * 0.3));
        _ballVy = -0.7;
        _ballSpot = null;
        StartRendering();
    }

    /// <summary>Прятки: прячется за поле ввода и выглядывает в разных местах; щелчок — нашёл.</summary>
    private void StartHideSeek()
    {
        if (_spot != "home")
        {
            GoHomeEdge(HomeFoot(_host.Home()), run: true, then: StartHideSeek);
            return;
        }

        _game = FoxWill.HideSeek;
        _gameUntil = _clock().AddSeconds(40);
        _seekLeft = SeekRounds;
        Peek(TimeSpan.FromSeconds(2.2 + _random.NextDouble() * 1.5));
    }

    /// <summary>Спрятался после выглядывания: в прятках — перебегает и выглядывает снова.</summary>
    private bool NextSeekRound()
    {
        if (_game != FoxWill.HideSeek || --_seekLeft <= 0)
        {
            if (_game == FoxWill.HideSeek)
            {
                StopGame();
            }

            return false;
        }

        _foot = RandomFoot(_host.Home());
        _flip = _random.Next(2) == 0;
        _peekUntil = _clock().AddSeconds(2 + _random.NextDouble() * 1.5);
        Play("peekup", "peek");
        return true;
    }

    /// <summary>Каждый кадр игры: догонялки и драка смотрят на курсор, у прочих — только срок.</summary>
    private void StepGame()
    {
        if (_game is null)
        {
            return;
        }

        var now = _clock();
        if (now >= _gameUntil)
        {
            var fought = _game == FoxWill.Fight;
            StopGame();
            if (fought && _mode == FoxMode.Idle)
            {
                Feel("proud", 1800);
                Bubble(Loc.Get(FoxPhrases.Pick("FightWin", _random)), 1600);
            }

            return;
        }

        if (_host.Pointer() is not { } pointer)
        {
            return;
        }

        switch (_game)
        {
            case FoxWill.Catch:
                RunFromPointer(pointer);
                break;
            case FoxWill.Fight:
                Fight(pointer, now);
                break;
        }
    }

    /// <summary>Догонялки: курсор ближе полутора кадров — удирает к дальнему концу кромки.</summary>
    private void RunFromPointer(Point pointer)
    {
        if (_mode != FoxMode.Idle)
        {
            return;
        }

        var center = new Point(_move.X + (_sprite.Side / 2), _move.Y + (_sprite.Side / 2));
        if ((pointer - center).Length > _sprite.Side * 1.6)
        {
            return;
        }

        var edge = EdgeOf(_spot);
        var target = pointer.X < center.X ? edge.Right : edge.Left;
        WalkTo(target, run: true, then: () =>
        {
            _flip = pointer.X > _move.X;
            _face = "sly";
            _faceUntil = _clock().AddSeconds(1.2);
        });
    }

    /// <summary>
    /// Драка: подкрадывается к курсору, а дотянувшись — бьёт лапой и оставляет следы когтей; после
    /// трёх ударов может большой кусь.
    /// </summary>
    private void Fight(Point pointer, DateTime now)
    {
        if (_mode != FoxMode.Idle || now < _nextSwipe)
        {
            return;
        }

        var head = HeadPoint();
        _flip = pointer.X > _move.X + (_sprite.Side / 2);
        _nextSwipe = now.AddMilliseconds(450 + _random.Next(350));
        if ((pointer - head).Length < _sprite.Side * 0.9)
        {
            if (_swipes >= 3 && _random.Next(2) == 0)
            {
                _swipes = 0;
                _nextSwipe = now.AddMilliseconds(1100);
                Play("lunge", "sit");
                _face = "bite";
                _faceUntil = now.AddSeconds(1);
                After(TimeSpan.FromMilliseconds(230), () => StartChomp(pointer));
                return;
            }

            _swipes++;
            Play("poke", "sit");
            _face = "angry";
            _faceUntil = now.AddMilliseconds(700);
            After(TimeSpan.FromMilliseconds(160), () => MarkAt("claw", pointer, 650));
            return;
        }

        var edge = EdgeOf(_spot);
        if (edge.Contains(pointer.X) && Math.Abs(pointer.X - head.X) < _sprite.Side * 6)
        {
            WalkTo(pointer.X - (Math.Sign(pointer.X - head.X) * _sprite.Side * 0.4), "sneak");
        }
        else
        {
            Play("crouch", "sit");
        }
    }

    /// <summary>
    /// Курсор мечется туда-сюда рядом с Розой — дразнят: четыре смены направления быстрым движением
    /// за секунду. Медленное поглаживание сюда не попадает — оно короткими шагами.
    /// </summary>
    private void Wiggle(Point onLayer)
    {
        var now = _clock();
        var dx = onLayer.X - _lastWiggleX;
        _lastWiggleX = onLayer.X;
        if (Math.Abs(dx) < Px(10) || _mode != FoxMode.Idle || _game is not null || now - _lastTeased < TeaseEvery)
        {
            return;
        }

        var direction = Math.Sign(dx);
        _wiggles.RemoveAll(wiggle => now - wiggle.At > TimeSpan.FromSeconds(1));
        if (_wiggles.Count == 0 || _wiggles[^1].Direction != direction)
        {
            _wiggles.Add((now, direction));
        }

        if (_wiggles.Count >= 5)
        {
            _wiggles.Clear();
            _lastTeased = now;
            Signal(FoxSignalKind.Teased);
        }
    }
}
