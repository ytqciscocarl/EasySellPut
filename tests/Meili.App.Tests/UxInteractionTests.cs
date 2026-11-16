using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using Meili.App.Services;
using Meili.App.Views.Controls;
using Meili.App.Views.Pages;

namespace Meili.App.Tests;

public sealed class UxInteractionTests
{
    [Fact]
    public void Advanced_fields_keep_values_and_busy_actions_cannot_reenter()
    {
        WpfHost.Run(async()=>
        {
            ScreenRenderTests.Apply("en-US",false);var page=new OptionsPage();using var host=new OffscreenHost(page,1200,800);await ScreenRenderTests.Settle();
            var vm=(OptionsViewModel)page.DataContext;var old=vm.ShowGamma;
            try
            {
                var disclosure=(AppPopover)page.FindName("OptionControlsPopover");Assert.False(disclosure.IsExpanded);vm.ShowGamma=!old;disclosure.IsExpanded=true;await WpfHost.Settle();Assert.Equal(13,UiAudit.Descendants<CheckBox>((DependencyObject)disclosure.PopupContent!).Count());disclosure.IsExpanded=false;Assert.Equal(!old,vm.ShowGamma);
                var refresh=UiAudit.Descendants<System.Windows.Controls.Primitives.ButtonBase>(page).First(b=>b.Command==vm.RefreshCommand);vm.IsBusy=true;await WpfHost.Settle();Assert.False(refresh.IsEnabled);vm.IsBusy=false;
                var actions=UiAudit.Descendants<System.Windows.Controls.Primitives.ButtonBase>(page).Where(b=>b.Command==vm.BuildStrategyCommand).ToArray();Assert.Equal(4,actions.Length);vm.SelectedRow=null;await WpfHost.Settle();Assert.All(actions,b=>Assert.False(b.IsEnabled));
            }
            finally{vm.ShowGamma=old;vm.IsBusy=false;}
        });
    }
    [Fact]
    public void Help_opens_with_automation_and_escape_returns_focus()
    {
        WpfHost.Run(async()=>
        {
            var help=new AppHelp{HelpText="Read full details with the keyboard"};using var host=new OffscreenHost(help,400,300);await WpfHost.Settle();help.Focus();
            var peer=new ButtonAutomationPeer(help);((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();await Task.Delay(250);await WpfHost.Settle();Assert.True(help.IsHelpOpen);
            help.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(help),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});Assert.False(help.IsHelpOpen);
            DesignMotion.SetReduced(true);try{var panel=new StackPanel();ComponentMotion.Enter(panel);Assert.Equal(1,panel.Opacity);}finally{DesignMotion.SetReduced(false);}
        });
    }
    [Fact]
    public void Invalid_hidden_cooldown_is_revealed_without_saving_a_rule()
    {
        WpfHost.Run(async()=>
        {
            var page=new AlertsPage();using var host=new OffscreenHost(page,1200,800);await WpfHost.Settle();var vm=(AlertsViewModel)page.DataContext;
            var old=(vm.SymbolText,vm.ThresholdText,vm.CooldownText,vm.NoteText);var count=vm.Rows.Count;
            try{vm.SymbolText="SPY";vm.ThresholdText="100";vm.CooldownText="invalid";vm.AdvancedExpanded=false;vm.AddCommand.Execute(null);await WpfHost.Settle();Assert.True(vm.AdvancedExpanded);Assert.Equal(count,vm.Rows.Count);Assert.Contains("1440",vm.StatusText);}
            finally{(vm.SymbolText,vm.ThresholdText,vm.CooldownText,vm.NoteText)=old;vm.AdvancedExpanded=false;}
        });
    }
}
