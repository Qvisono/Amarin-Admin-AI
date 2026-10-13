using System.Windows;
using System.Windows.Controls;

namespace Amarin.Fox;

/// <summary>Кромка, на которой можно сидеть: от Left до Right на высоте Y, в координатах слоя Розы.</summary>
internal readonly record struct FoxEdge(double Left, double Right, double Y)
{
    public bool Contains(double x) => x >= Left && x <= Right;
}

/// <summary>
/// Что сцена Розы знает об окне. Реализует переходник окна (<c>FoxWindowHost</c> в приложении).
/// </summary>
/// <remarks>
/// Узкий и на простых типах: сцена не знает ни <c>MainWindow</c>, ни его разметки — только где
/// кромки, что сейчас в окне и какие две шалости можно сделать по-настоящему.
/// </remarks>
internal interface IFoxHost
{
    /// <summary>Слой поверх окна, в котором живёт Роза: выше настроек, ниже всех вопросов.</summary>
    Canvas Layer { get; }

    /// <summary>Что сейчас в окне: печатает ли человек, идёт ли ход, лежит ли что-то поверх.</summary>
    FoxSituation Situation();

    /// <summary>Верхняя кромка поля ввода — дом Розы.</summary>
    FoxEdge Home();

    /// <summary>Верх карточки настроек (мимо крестика), если настройки открыты.</summary>
    FoxEdge? Card();

    /// <summary>Нижний край окна — «пол», куда падает брошенный мимо кромок.</summary>
    double Floor();

    /// <summary>Ширина слоя.</summary>
    double Width();

    /// <summary>Размер Розы из настроек: 48 или 96 точек.</summary>
    int Size();

    /// <summary>Можно двигаться плавно: анимации включены, сеанс не удалённый, отрисовка аппаратная.</summary>
    bool Motion();

    /// <summary>Шалость: ткнуть «Новый чат» по-настоящему.</summary>
    void NewChat();

    /// <summary>Шалость: подтолкнуть колонку чатов.</summary>
    void NudgeChatList();

    /// <summary>Где курсор на слое; <c>null</c> — не над окном.</summary>
    Point? Pointer();

    /// <summary>Кнопка отправки на слое — шалость «сесть на неё». Пустой прямоугольник — её не видно.</summary>
    Rect SendButton();

    /// <summary>Плашка модели в поле ввода на слое — шалость «погрызть».</summary>
    Rect ModelChip();

    /// <summary>Меню Розы по правому щелчку: собирает окно, его стилями.</summary>
    void ShowMenu(FoxStage stage);

    /// <summary>Поле разговора закрылось: фокус — обратно туда, где человек печатает.</summary>
    void TalkClosed();
}
