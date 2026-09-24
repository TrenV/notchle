using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notchle.Windows.Shell;

/// Stand-in for W2's IslandWindow until the branches merge (see UiHooks). Shows the phase and
/// a link box, so the shell and the audio can be exercised end to end on Windows.
internal sealed class PlaceholderIslandWindow : Window
{
    public PlaceholderIslandWindow(NotchViewModel model)
    {
        Title = "Notchle";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        DataContext = model;

        var phase = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        phase.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("State.Phase"));
        var link = new TextBox();
        var load = new Button { Content = "Load link", Margin = new Thickness(0, 8, 0, 0) };
        load.Click += (_, _) =>
        {
            if (Notchle.Core.SourceRefParser.TryParse(link.Text) is { } source)
                model.Send(new Notchle.Core.GameAction.Load(source));
        };
        Content = new StackPanel { Margin = new Thickness(16), Children = { phase, link, load } };
    }

    public static void RenderPlaceholderSnapshot(string directory)
    {
        Directory.CreateDirectory(directory);
        var text = new TextBlock { Text = "Island UI not merged yet (placeholder snapshot)", Padding = new Thickness(12), Background = Brushes.Black, Foreground = Brushes.White };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.Arrange(new Rect(text.DesiredSize));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(text.ActualWidth), (int)Math.Ceiling(text.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(text);
        var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } };
        using var file = File.Create(Path.Combine(directory, "placeholder.png"));
        encoder.Save(file);
    }
}
