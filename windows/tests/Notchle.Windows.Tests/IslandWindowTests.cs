using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Notchle.Windows.Island;

namespace Notchle.Windows.Tests;

public class IslandWindowTests
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [Fact]
    public void WindowIsABorderlessTopmostToolWindowThatNeverActivatesOnShow()
    {
        IslandSta.Run(() =>
        {
            var window = new IslandWindow(new NotchViewModel());
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.True(window.AllowsTransparency);
            Assert.True(window.Topmost);
            Assert.False(window.ShowInTaskbar);
            Assert.False(window.ShowActivated);
            window.Show();
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var ex = GetWindowLongPtr(hwnd, -20).ToInt64();
                Assert.True((ex & 0x00000080) != 0, "WS_EX_TOOLWINDOW");
                Assert.True((ex & 0x08000000) != 0, "WS_EX_NOACTIVATE until clicked");
                Assert.True((ex & 0x00000020) != 0, "WS_EX_TRANSPARENT (click-through) while the pointer is away");
                Assert.True((ex & 0x00080000) != 0, "WS_EX_LAYERED");
                Assert.False(window.IsActive);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
