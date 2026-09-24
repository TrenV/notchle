using Microsoft.Win32;
using Notchle.Windows.Shell;

namespace Notchle.Windows.Tests;

// Windows only (CI).

public class ShellStartupRegistrationTests
{
    [Fact]
    public void TogglesAQuotedRunValue()
    {
        var keyPath = $@"Software\Notchle.Tests\{Guid.NewGuid():N}";
        try
        {
            var startup = new StartupRegistration(@"C:\Program Files\Notchle\Notchle.Windows.exe", "Notchle", keyPath);
            Assert.False(startup.IsEnabled);

            startup.SetEnabled(true);
            Assert.True(startup.IsEnabled);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert.Equal("\"C:\\Program Files\\Notchle\\Notchle.Windows.exe\"", key!.GetValue("Notchle"));

            // A value left by a copy elsewhere doesn't count as "on".
            Assert.False(new StartupRegistration(@"D:\Other\Notchle.Windows.exe", "Notchle", keyPath).IsEnabled);

            startup.SetEnabled(false);
            Assert.False(startup.IsEnabled);
            startup.SetEnabled(false); // idempotent
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Notchle.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void UsesTheCurrentUserRunKey() =>
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKey);
}

public class ShellSingleInstanceTests
{
    [Fact]
    public void SecondInstanceIsNotFirstAndCanActivateTheFirst()
    {
        var name = $"Notchle.Tests.{Guid.NewGuid():N}";
        using var activated = new ManualResetEventSlim();
        using var first = new SingleInstance(name, activated.Set);
        Assert.True(first.IsFirst);

        // The mutex is thread-affine (and released on Dispose by its owner thread, as the app
        // does from OnExit); a real second launch is another process, so use another thread.
        bool? second = null;
        var thread = new Thread(() =>
        {
            using var instance = new SingleInstance(name, () => { });
            if (!instance.IsFirst) instance.ActivateFirst();
            second = instance.IsFirst;
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        Assert.False(second);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }
}
