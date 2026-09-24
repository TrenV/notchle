using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Notchle.Windows.Tests;

internal static class IslandSta
{
    /// WPF needs an STA thread; xunit's are MTA.
    public static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// Every piece of text anywhere in the tree (visible or not): text blocks, text boxes,
    /// automation names.
    public static List<string> AllText(DependencyObject root)
    {
        var found = new List<string>();
        void Walk(DependencyObject d)
        {
            switch (d)
            {
                case TextBlock t: found.Add(t.Text); break;
                case TextBox b: found.Add(b.Text); break;
            }
            if (AutomationProperties.GetName(d) is { Length: > 0 } name) found.Add(name);
            var count = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(d, i));
            // Logical children too: collapsed parts may not be in the visual tree yet.
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
                if (child is not Visual || VisualTreeHelper.GetParent(child) != d) Walk(child);
        }
        Walk(root);
        return found;
    }
}
