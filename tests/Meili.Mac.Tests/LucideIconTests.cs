using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Meili.Mac.Views.Controls;
using Meili.Shared.Services;

namespace Meili.Mac.Tests;

public sealed class LucideIconTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_pinned_icon_renders_in_its_original_canvas(bool dark)
    {
        MacHost.EnsureComposed();
        var panel=new WrapPanel{Width=600};
        foreach(var field in typeof(NavigationIcons).GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static))
        {
            var icon=new LucideIcon{Kind=field.Name,Width=24,Height=24,Foreground=dark?Brushes.White:Brushes.Black};
            panel.Children.Add(new StackPanel{Width=100,Height=68,Spacing=6,HorizontalAlignment=HorizontalAlignment.Center,Children={icon,new TextBlock{Text=field.Name,FontSize=10,Foreground=icon.Foreground,HorizontalAlignment=HorizontalAlignment.Center}}});
        }
        var window=new Window{Width=620,Height=600,Background=dark?new SolidColorBrush(Color.Parse("#141720")):Brushes.White,Content=panel};window.Show();MacHost.Pump();
        try
        {
            foreach(var icon in panel.GetVisualDescendants().OfType<LucideIcon>())
            {
                Assert.Equal(new Size(24,24),icon.Bounds.Size);
                var path=icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                Assert.NotNull(path.Data);Assert.Equal(new Size(24,24),path.Bounds.Size);
                var bounds=path.Data!.Bounds;
                Assert.True(bounds.Left>=0 && bounds.Top>=0 && bounds.Right<=24.1 && bounds.Bottom<=24.1,$"{icon.Kind}: {bounds}");
                var center=path.TranslatePoint(new Point(12,12),icon)!.Value;Assert.InRange(Math.Abs(center.X-12),0,.1);Assert.InRange(Math.Abs(center.Y-12),0,.1);
            }
            MacHost.Save(window,"lucide-gallery-"+dark);
        }
        finally{window.Close();}
    }
}
