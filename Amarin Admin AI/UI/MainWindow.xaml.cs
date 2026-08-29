using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Amarin.UI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title = $"Amarin Admin AI v{RuntimeContext.AppVersion}";
            TitleText.Text = Title;
            SettingsVersionText.Text = $"v{RuntimeContext.AppVersion}";

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);

            new PerformanceOptimizer(this);

            SmoothScroll.SetIsEnabled(SideBarScrollViewer, true);
            SmoothScroll.SetIsEnabled(ChatScrollViewer, true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void ResizeBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string direction } && e.LeftButton == MouseButtonState.Pressed)
            {
                ResizeWindowLogic.ResizeWindow(direction, this);
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            Application.Current?.Shutdown();
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current?.MainWindow;
            if (mw != null)
                mw.WindowState = WindowState.Minimized;
        }

        private void Button_Click_2(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current?.MainWindow;
            if (mw == null) return;
            mw.WindowState = mw.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            WindowMoveBehavior.HandleMouseLeftButtonDownForMove(this, e);
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void Button_Click_3(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Visible;
        }
    }
}
