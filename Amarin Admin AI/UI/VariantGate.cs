using System.ComponentModel;

namespace Amarin.UI;

/// <summary>
/// Можно ли сейчас листать варианты ответа — одно значение на все переключатели ленты.
/// </summary>
/// <remarks>
/// Кнопки старых сообщений остаются нажимаемыми, пока чат отвечает, а переключение посреди
/// хода вырезало бы из-под движка ответ, который он дописывает. Привязкой, а не обходом ленты:
/// хостов сотни, половина ещё не построена, а построенная позже обязана сразу знать правду.
/// </remarks>
internal sealed class VariantGate : INotifyPropertyChanged
{
    private bool _isOpen = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
            {
                return;
            }

            _isOpen = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
        }
    }
}
