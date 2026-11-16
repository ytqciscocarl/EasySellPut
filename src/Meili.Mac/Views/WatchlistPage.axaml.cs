using Avalonia.Controls;
using Avalonia.Input;
using Meili.Core.Models;

namespace Meili.Mac.Views;

public partial class WatchlistPage : UserControl
{
    public WatchlistPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<WatchlistViewModel>();
        DataContext = vm;
        AttachedToVisualTree+=(_,_)=>{vm.PropertyChanged+=OnGroupEditorChanged;FocusGroupEditor();};
        DetachedFromVisualTree+=(_,_)=>vm.PropertyChanged-=OnGroupEditorChanged;

        PageLife.Bind(this, vm.ActivateAsync, vm.Deactivate);
        QueryBox.KeyDown += OnQueryKeyDown;
        SuggestionList.KeyDown += OnSuggestionKeyDown;
        SuggestionList.Tapped += (_, _) =>
        {
            if (SuggestionList.SelectedItem is SymbolMatch m) _ = Vm.AddMatchCommand.ExecuteAsync(m);
        };
        QueryBox.LostFocus += (_, _) => { if (!SuggestionList.IsKeyboardFocusWithin && !SuggestionList.IsPointerOver) vm.HideSuggestions(); };
        // D1: double-click a row → its K-line chart.
        QuoteList.DoubleTapped += (_, _) => { if (vm.SelectedRow is not null) vm.OpenChartCommand.Execute(null); };
    }

    private void OnGroupEditorChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(WatchlistViewModel.IsAddingGroup) or nameof(WatchlistViewModel.IsRenamingGroup))FocusGroupEditor();
    }
    private void FocusGroupEditor()=>Avalonia.Threading.Dispatcher.UIThread.Post(()=>
    {
        if(!IsEffectivelyVisible)return;
        if(Vm.IsAddingGroup||Vm.IsRenamingGroup){GroupDraftInput.Focus();GroupDraftInput.SelectAll();}
    });
    private void OnGroupDraftKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter){Vm.ConfirmGroupEditCommand.Execute(null);e.Handled=true;}
        if(e.Key==Key.Escape){Vm.CancelGroupEditCommand.Execute(null);QueryBox.Focus();e.Handled=true;}
    }

    private WatchlistViewModel Vm => (WatchlistViewModel)DataContext!;

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (SuggestionList.SelectedItem is SymbolMatch match) _ = Vm.AddMatchCommand.ExecuteAsync(match);
                else _ = Vm.AddCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.HideSuggestions();
                e.Handled = true;
                break;
            case Key.Down when Vm.Suggestions.Count > 0:
                if (SuggestionList.SelectedIndex < 0) SuggestionList.SelectedIndex = 0;
                SuggestionList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionList.SelectedItem is SymbolMatch match)
        {
            _ = Vm.AddMatchCommand.ExecuteAsync(match);
            QueryBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.HideSuggestions();
            QueryBox.Focus();
            e.Handled = true;
        }
    }
}
