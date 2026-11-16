using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views.Controls;

namespace Meili.Mac.Tests;

public sealed class UxInteractionTests
{
    private static async Task Settle()
    {
        for(var i=0;i<6;i++){await Task.Delay(40);MacHost.Pump();}
    }
    [AvaloniaTheory]
    [InlineData(980,620,"zh-CN",false)]
    [InlineData(1320,840,"en-US",true)]
    [InlineData(980,620,"en-US",true)]
    [InlineData(1320,840,"zh-CN",false)]
    public async Task Every_page_keeps_actions_readable_and_captures_default_and_expanded_states(int width,int height,string language,bool dark)
    {
        MacHost.EnsureComposed();var theme=AppServices.Get<MacTheme>();var loc=LocalizationService.Current;theme.Apply(dark?AppThemeMode.Dark:AppThemeMode.Light);loc.Apply(language);
        var w=new MainWindow();w.Show();w.Width=width;w.Height=height;MacHost.Pump();var metrics=new List<object>();
        try
        {
            foreach(var page in MainWindow.PageOrder)
            {
                w.Navigate(page);await Settle();var view=w.CurrentView!;
                if(page==AppPage.Options)Assert.True(view.FindControl<DataGrid>("ChainList")!.Bounds.Height>=180,"Compact option chain needs usable row space");
                var disclosures=view.GetVisualDescendants().OfType<AppDisclosure>().ToArray();
                // Fields survive collapsed state; no default full-page tutorial is introduced.
                var text=view.GetVisualDescendants().OfType<TextBlock>().Where(t=>t.IsEffectivelyVisible).Select(t=>t.Text??"").ToArray();
                Assert.DoesNotContain(text,t=>t==loc["Dash_WelcomeBody"]||t==loc["St_Hint"]);
                Assert.All(view.GetVisualDescendants().OfType<AppHelp>().Where(b=>b.IsEffectivelyVisible),b=>{Assert.Equal(28,b.Bounds.Width);Assert.Equal(28,b.Bounds.Height);Assert.False(string.IsNullOrWhiteSpace(b.HelpText));});
                foreach(var button in view.GetVisualDescendants().OfType<Button>().Where(b=>b.IsEffectivelyVisible && b.Bounds.Width>0 && b.TemplatedParent is null))
                    Assert.True(double.IsFinite(button.Bounds.Width)&&double.IsFinite(button.Bounds.Height),page+": invalid control bounds");
                metrics.Add(new{page=page.ToString(),width,height,language,dark,visibleTextCharacters=text.Sum(x=>x.Length),visibleButtons=view.GetVisualDescendants().OfType<Button>().Count(x=>x.IsEffectivelyVisible),collapsed=disclosures.Count(x=>!x.IsExpanded)});
                MacHost.Save(w,$"ux-{page}-{width}-{language}-{dark}");
                var popover=view.GetVisualDescendants().OfType<AppPopover>().FirstOrDefault(x=>x is not AppHelp && x.IsEffectivelyVisible);
                if(popover is not null){popover.IsExpanded=true;await Settle();MacHost.Save(w,$"ux-{page}-popover-{width}-{language}-{dark}");popover.IsExpanded=false;}
                var disclosure=disclosures.FirstOrDefault(x=>x.IsEffectivelyVisible);
                if(disclosure is not null){disclosure.IsExpanded=true;await Settle();MacHost.Save(w,$"ux-{page}-expanded-{width}-{language}-{dark}");disclosure.IsExpanded=false;}
            }
            if(Environment.GetEnvironmentVariable("MEILI_RENDER_DIR") is {Length:>0} directory)File.WriteAllText(Path.Combine(directory,$"ux-metrics-{width}-{language}-{dark}.json"),JsonSerializer.Serialize(metrics,new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{w.Close();theme.Apply(AppThemeMode.System);loc.Apply("zh-CN");}
    }
    [AvaloniaFact]
    public async Task Selected_actions_group_keyboard_and_hidden_fields_keep_the_same_state()
    {
        MacHost.EnsureComposed();var w=new MainWindow();w.Show();w.Width=1320;w.Height=840;
        try
        {
            w.Navigate(AppPage.Dashboard);await Settle();var dashboard=(DashboardViewModel)w.CurrentView!.DataContext!;
            var quote=w.CurrentView!.GetVisualDescendants().OfType<Button>().First(b=>b.Command==dashboard.OpenChartCommand);
            Assert.False(string.IsNullOrEmpty(quote.CommandParameter as string));quote.Command!.Execute(quote.CommandParameter);MacHost.Pump();Assert.Equal(AppPage.Charts,w.CurrentPage);Assert.Equal(quote.CommandParameter,AppServices.Get<SymbolContext>().Current);
            w.Navigate(AppPage.Watchlist);await Settle();var vm=AppServices.Get<WatchlistViewModel>();var page=w.CurrentView!;
            vm.SelectedRow=null;MacHost.Pump();var remove=page.GetVisualDescendants().OfType<Button>().First(b=>b.Command==vm.RemoveSelectedCommand);Assert.False(remove.IsEnabled);
            vm.SelectedRow=vm.Rows.FirstOrDefault();MacHost.Pump();if(vm.SelectedRow is not null)Assert.True(remove.IsEnabled);
            vm.BeginAddGroupCommand.Execute(null);await Settle();var draft=page.FindControl<TextBox>("GroupDraftInput")!;Assert.True(draft.IsFocused);
            vm.GroupNameDraft="UX unsaved test";w.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null);MacHost.Pump();Assert.False(vm.IsAddingGroup);
            w.Navigate(AppPage.Options);await Settle();var options=AppServices.Get<OptionsViewModel>();var columns=w.CurrentView!.FindControl<AppPopover>("OptionControlsPopover")!;
            var old=options.ShowGamma;options.ShowGamma=!old;columns.IsExpanded=true;await Settle();
            var gamma=((Control)columns.PopupContent!).GetVisualDescendants().OfType<CheckBox>().First(x=>Equals(x.Content,LocalizationService.Current["Op_Gamma"]));
            Assert.Equal(!old,gamma.IsChecked);gamma.SetCurrentValue(ToggleButton.IsCheckedProperty,old);MacHost.Pump();Assert.Equal(old,options.ShowGamma);
            columns.IsExpanded=false;MacHost.Pump();Assert.Equal(old,options.ShowGamma);
            var refresh=w.CurrentView!.GetVisualDescendants().OfType<Button>().First(b=>b.Command==options.RefreshCommand);options.IsBusy=true;MacHost.Pump();Assert.False(refresh.IsEnabled);options.IsBusy=false;
            w.Navigate(AppPage.Alerts);await Settle();var alerts=AppServices.Get<AlertsViewModel>();var advanced=w.CurrentView!.FindControl<AppPopover>("AlertAdvanced")!;var cooldown=alerts.CooldownText;var note=alerts.NoteText;var symbol=alerts.SymbolText;var threshold=alerts.ThresholdText;
            try
            {
                alerts.CooldownText="45";alerts.NoteText="UX draft";advanced.IsExpanded=true;advanced.IsExpanded=false;MacHost.Pump();Assert.Equal("45",alerts.CooldownText);Assert.Equal("UX draft",alerts.NoteText);
                alerts.SymbolText="SPY";alerts.ThresholdText="100";alerts.CooldownText="invalid";alerts.AddCommand.Execute(null);MacHost.Pump();Assert.True(advanced.IsExpanded);Assert.Contains("1440",alerts.StatusText);
            }
            finally{alerts.CooldownText=cooldown;alerts.NoteText=note;alerts.SymbolText=symbol;alerts.ThresholdText=threshold;alerts.AdvancedExpanded=false;}
        }
        finally{w.Close();}
    }
    [AvaloniaFact]
    public async Task Help_is_clickable_and_reduced_motion_cancels_enter_feedback_without_touching_native_hosts()
    {
        MacHost.EnsureComposed();var help=new AppHelp{HelpText="A complete help message"};var panel=new StackPanel{Children={help,new TextBox{Text="Keep this input"}}};var window=new Window{Content=panel,Width=500,Height=300};window.Show();MacHost.Pump();
        try
        {
            help.Focus(NavigationMethod.Tab);await Settle();Assert.True(ToolTip.GetIsOpen(help));MacHost.Save(window,"ux-help-tooltip");
            help.IsExpanded=true;await Settle();Assert.True(help.IsHelpOpen);Assert.False(ToolTip.GetIsOpen(help));Assert.IsType<TextBlock>(ToolTip.GetTip(help));MacHost.Save(window,"ux-help-open");help.IsExpanded=false;
            DesignMotion.SetReduced(false);AppMotion.Enter(panel);Assert.NotNull(panel.Transitions);
            DesignMotion.SetReduced(true);Assert.Null(panel.Transitions);Assert.Equal(1,panel.Opacity);
            var native=new NativeControlHost{Opacity=.7};AppMotion.Enter(native);Assert.Equal(.7,native.Opacity);
        }
        finally{window.Close();DesignMotion.SetReduced(false);}
    }
}
