using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Meili.Core.Settings;
using Meili.Mac.Platform;

namespace Meili.Mac.Tests;

public sealed class ButtonAlignmentTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN",false)]
    [InlineData("en-US",true)]
    public void Chart_tool_families_have_equal_bounds(string language,bool dark)
    {
        MacHost.EnsureComposed();var loc=LocalizationService.Current;var theme=AppServices.Get<MacTheme>();loc.Apply(language);theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);
        var window=new MainWindow();window.Show();window.Navigate(AppPage.Charts);MacHost.Pump();
        try
        {
            var page=window.CurrentView!;
            var tools=page.GetVisualDescendants().OfType<Button>().Where(b=>b.Classes.Contains("DrawTool")).ToArray();
            Assert.Equal(3,tools.Length);
            Assert.All(tools,b=>{Assert.Equal(56,b.Bounds.Width);Assert.Equal(56,b.Bounds.Height);});
            var symbol=page.GetVisualDescendants().OfType<Meili.Mac.Views.Controls.SymbolBox>().First();
            var dropdown=symbol.GetVisualDescendants().OfType<Button>().Single();
            var input=symbol.GetVisualDescendants().OfType<TextBox>().First();
            Assert.Equal(32,dropdown.Bounds.Width);Assert.Equal(32,dropdown.Bounds.Height);Assert.Equal(32,input.Bounds.Height);
            Assert.InRange(Math.Abs(dropdown.TranslatePoint(new Point(),page)!.Value.Y-input.TranslatePoint(new Point(),page)!.Value.Y),0,1);
            MacHost.Save(window,"button-families-"+language+"-"+dark);
            if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is {Length:>0} directory)
            {
                var group=(StackPanel)tools[0].Parent!;var bottom=tools.Max(b=>b.TranslatePoint(new Point(),group)!.Value.Y+b.Bounds.Height);
                using var frame=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(group.Bounds.Width)*3,(int)Math.Ceiling(bottom)*3),new Vector(288,288));
                frame.Render(group);frame.Save(Path.Combine(directory,"drawing-buttons-"+language+".png"),new PngBitmapEncoderOptions());
            }
        }
        finally{window.Close();loc.Apply("zh-CN");theme.Apply(AppThemeMode.System);}
    }
    [AvaloniaTheory]
    [InlineData("zh-CN",false)]
    [InlineData("en-US",true)]
    public void Buttons_keep_content_centered_across_pages(string language,bool dark)
    {
        MacHost.EnsureComposed();var loc=AppServices.Get<LocalizationService>();var original=loc.Language;
        var theme=AppServices.Get<MacTheme>();loc.Apply(language);theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);
        var window=new MainWindow{Width=1100,Height=720};
        try
        {
            window.Show();MacHost.Pump();
            foreach(var page in MainWindow.PageOrder)
            {
                window.Navigate(page);MacHost.Pump();
                foreach(var button in window.GetVisualDescendants().OfType<Button>().Where(b=>(b.GetType()==typeof(Button)||b.GetType()==typeof(ToggleButton)) && b.TemplatedParent is null && b.IsEffectivelyVisible && b.Bounds.Width>0))
                {
                    var presenter=button.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p=>p.Name=="PART_ContentPresenter");
                    if(presenter is null)continue;
                    Assert.Equal(HorizontalAlignment.Center,button.HorizontalContentAlignment);
                    Assert.Equal(VerticalAlignment.Center,button.VerticalContentAlignment);
                    var content=presenter.GetVisualChildren().OfType<Control>().FirstOrDefault();
                    if(content is null)continue;
                    var center=content.TranslatePoint(new Point(content.Bounds.Width/2,content.Bounds.Height/2),button)!.Value;
                    Assert.True(Math.Abs(center.X-button.Bounds.Width/2)<=1.1 && Math.Abs(center.Y-button.Bounds.Height/2)<=1.1,$"{page}: {button.Name}/{button.Content} center {center} in {button.Bounds}");
                    foreach(var path in button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p=>p.Bounds.Width==20 && p.Bounds.Height==20))
                    {
                        var ink=path.RenderedGeometry!.Bounds.Center;
                        var position=path.TranslatePoint(ink,button)!.Value;
                        Assert.InRange(Math.Abs(position.X-button.Bounds.Width/2),0,1.1);
                        Assert.InRange(Math.Abs(ink.Y-path.Bounds.Height/2),0,1.1);
                    }
                }
                if(page is AppPage.Charts or AppPage.Settings)MacHost.Save(window,$"buttons-{page}-{language}-{dark}");
            }
            var languageButton=window.FindControl<Button>("LanguageButton")!;
            var themeButton=window.FindControl<Button>("ThemeButton")!;
            Assert.Equal(new Size(28,24),languageButton.Bounds.Size);Assert.Equal(languageButton.Bounds.Size,themeButton.Bounds.Size);
            var utilities=window.FindControl<StackPanel>("ShellUtilities")!;
            window.MouseMove(languageButton.TranslatePoint(new Point(20,16),window)!.Value);MacHost.Pump();
            if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is {Length:>0} dir)
            {
                Directory.CreateDirectory(dir);
                using var frame=new RenderTargetBitmap(new PixelSize((int)utilities.Bounds.Width*3,(int)utilities.Bounds.Height*3),new Vector(288,288));
                frame.Render(utilities);frame.Save(Path.Combine(dir,$"shell-buttons-{language}-{dark}.png"),new PngBitmapEncoderOptions());
            }
        }
        finally{window.Close();loc.Apply(original);theme.Apply(AppThemeMode.System);}
    }
}
