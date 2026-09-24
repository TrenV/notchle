using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

public class IslandSnapshotTests
{
    [Fact]
    public void RenderAllWritesEveryScenarioAsPng()
    {
        var dir = Path.Combine(Path.GetTempPath(), "notchle-island-" + Guid.NewGuid().ToString("N"));
        try
        {
            IslandSnapshots.RenderAll(dir); // from an MTA test thread: it must hop to STA itself
            foreach (var scenario in IslandSnapshots.Scenarios)
            {
                var path = Path.Combine(dir, scenario.Name + ".png");
                Assert.True(File.Exists(path), path);
                var bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 2000, $"{path}: {bytes.Length} bytes");
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static Color Pixel(BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        b.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    private static bool NearBlack(Color c) => c.R < 16 && c.G < 16 && c.B < 16;

    [Fact]
    public void TheIslandIsBlackAndTheDesktopShowsAroundIt()
    {
        IslandSta.Run(() =>
        {
            var expanded = IslandSnapshots.Render(IslandSnapshots.Scenarios.Single(s => s.Name == "13-revealed-gave-up"));
            // 2× DPI: centre of the 460×190 island, a bit below its header (clear of text).
            Assert.True(NearBlack(Pixel(expanded, 760 / 2 * 2 - 150, 150 * 2)));
            // Far below it: the mock wallpaper, not black.
            Assert.False(NearBlack(Pixel(expanded, 30, 280 * 2)));

            var lip = IslandSnapshots.Render(IslandSnapshots.Scenarios.Single(s => s.Name == "01-lip-idle"));
            Assert.True(NearBlack(Pixel(lip, 760, 4 * 2)));          // inside the 8 px lip
            Assert.False(NearBlack(Pixel(lip, 760, 40 * 2)));        // just below it: desktop
        });
    }
}
