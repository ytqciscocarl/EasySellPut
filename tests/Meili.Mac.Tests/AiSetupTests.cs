using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Meili.Automation;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Shared.ViewModels;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Meili.Mac.Views;

namespace Meili.Mac.Tests;
public sealed class AiSetupTests
{
    [AvaloniaFact]
    public void Connection_entry_is_localized_and_alternatives_start_collapsed()
    {
        MacHost.EnsureComposed();var page=new SettingsPage();var window=new Window{Width=480,Height=800,Content=page};window.Show();MacHost.Pump();
        var loc=AppServices.Get<LocalizationService>();var original=loc.Language;
        try
        {
            foreach(var language in new[]{"zh-CN","en-US"})
            {
                loc.Apply(language);MacHost.Pump();
                var button=page.FindControl<Button>("ConnectCodex")!;
                Assert.False(page.GetVisualDescendants().OfType<Expander>().Single(e=>Equals(e.Header,loc["Set_AiMore"])).IsExpanded);
                var claude=page.FindControl<Button>("ConnectClaude")!;Assert.Equal(loc["Set_AiClaude"],claude.Content);Assert.False(claude.IsEnabled);
                Assert.Equal(loc["Set_AiCodex"],button.Content);Assert.False(button.IsEnabled); // source test host does not bundle CLI
                Assert.Equal(loc["Set_AiMissing"],((Meili.Shared.ViewModels.SettingsViewModel)page.DataContext!).AiConnectionStatus);
            }
        }
        finally{loc.Apply(original);window.Close();}
    }
    [AvaloniaTheory]
    [InlineData("zh-CN",true,1100)]
    [InlineData("en-US",false,980)]
    public async Task Setup_cards_and_live_status_fit_both_themes_and_languages(string language,bool dark,int width)
    {
        MacHost.EnsureComposed();var loc=AppServices.Get<LocalizationService>();var original=loc.Language;
        var theme=AppServices.Get<MacTheme>();var directory=Path.Combine(MacHost.DataDir,"ai-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var executable=Path.Combine(directory,"easysellput");File.WriteAllText(executable,"");
        var connection=new McpConnectionSetup(executable,directory,false);
        var shell=new ShellViewModel(loc,new(directory,DataDirectorySource.CommandLine),AppServices.Get<Meili.Data.DataServices>(),connection);
        loc.Apply(language);theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);
        var window=new MainWindow{Width=width,Height=800,DataContext=shell};
        try
        {
            window.Show();window.Navigate(AppPage.Settings);MacHost.Pump();
            var page=(SettingsPage)window.CurrentView!;
            page.DataContext=new SettingsViewModel(new(directory),loc,theme,new(directory,DataDirectorySource.CommandLine),AppServices.Provider!,connection);
            MacHost.Pump();var panel=page.FindControl<Border>("AiSetupPanel")!;panel.BringIntoView();MacHost.Pump();
            var codex=page.FindControl<Button>("ConnectCodex")!;var claude=page.FindControl<Button>("ConnectClaude")!;
            Assert.True(codex.IsEnabled);Assert.True(claude.IsEnabled);
            Assert.InRange(Math.Abs(codex.TranslatePoint(new Point(),panel)!.Value.Y-claude.TranslatePoint(new Point(),panel)!.Value.Y),0,1);
            Save(panel,$"ai-setup-{language}-{dark}");
            var status=window.FindControl<Border>("BottomStatusBar")!;
            shell.RefreshMcpStatus();Assert.Equal(loc["Status_McpIdle"],shell.McpStatusText);Save(status,$"mcp-idle-{language}-{dark}");
            using(var lease=new McpSessionLease(directory,false,()=>"UI fixture client"))
            {
                lease.Publish();shell.RefreshMcpStatus();MacHost.Pump();
                Assert.Equal(string.Format(loc["Status_McpConnected"],1),shell.McpStatusText);
                var button=window.FindControl<Button>("McpStatusIndicator")!;
                Assert.Same(shell.CopyMcpSetupCommand,button.Command);Assert.True(button.IsEffectivelyVisible);
                Assert.Contains(loc["Status_McpCopyHint"],shell.McpStatusTip);
                Save(status,$"mcp-connected-{language}-{dark}");
            }
            shell.RefreshMcpStatus();Assert.Equal(loc["Status_McpIdle"],shell.McpStatusText);
            await shell.CopyMcpSetupCommand.ExecuteAsync(null);MacHost.Pump();
            Assert.Equal(loc["Status_McpCopyFailed"],shell.McpStatusText); // headless has no desktop clipboard; native package checks verify success
            Save(status,$"mcp-copy-failed-{language}-{dark}");
        }
        finally{window.Close();loc.Apply(original);theme.Apply(AppThemeMode.System);Directory.Delete(directory,true);}
    }
    private static void Save(Control control,string name)
    {
        if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is not {Length:>0} dir)return;
        Directory.CreateDirectory(dir);MacHost.Pump();
        using var frame=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width),(int)Math.Ceiling(control.Bounds.Height)));
        frame.Render(control);frame.Save(Path.Combine(dir,name+".png"),new PngBitmapEncoderOptions());
    }
}
