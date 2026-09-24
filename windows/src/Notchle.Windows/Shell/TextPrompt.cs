using System.Windows;
using System.Windows.Controls;

namespace Notchle.Windows.Shell;

/// A one-field dialog (the Spotify client id). Code-only, no XAML.
internal static class TextPrompt
{
    public static string? Ask(string title, string message, string initial = "")
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 360 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
