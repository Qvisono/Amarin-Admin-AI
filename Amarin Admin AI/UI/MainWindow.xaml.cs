using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Amarin.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = $"Amarin Admin AI v{RuntimeContext.AppVersion}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ResizeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string direction } && e.LeftButton == MouseButtonState.Pressed)
        {
            ResizeWindowLogic.ResizeWindow(direction, this);
        }
    }
}
