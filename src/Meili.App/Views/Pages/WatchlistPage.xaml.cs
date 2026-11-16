using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meili.App.Services;
using Meili.Core.Models;

namespace Meili.App.Views.Pages;

public partial class WatchlistPage : Page
{
    private Point _dragStart;
    private WatchlistRowViewModel? _dragRow;
    private bool _dragging;

    public WatchlistPage()
    {
        InitializeComponent();
        var vm = AppServices.Get<WatchlistViewModel>();
        DataContext = vm;
        Loaded+=(_,_)=>{vm.PropertyChanged+=OnGroupEditorChanged;FocusGroupEditor();};
        Unloaded+=(_,_)=>vm.PropertyChanged-=OnGroupEditorChanged;

        Loaded += async (_, _) => await vm.ActivateAsync();
        Unloaded += (_, _) => vm.Deactivate();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && !IsInside(d, QueryBox) && !IsInside(d, SuggestionList))
                vm.HideSuggestions();
        };
    }

    private void OnGroupEditorChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(WatchlistViewModel.IsAddingGroup) or nameof(WatchlistViewModel.IsRenamingGroup))FocusGroupEditor();
    }
    private void FocusGroupEditor()=>Dispatcher.BeginInvoke(new Action(()=>
    {
        if(!IsVisible)return;
        var input=Vm.IsAddingGroup?GroupAddInput:Vm.IsRenamingGroup?GroupRenameInput:null;
        if(input is not null){input.Focus();input.SelectAll();}
    }));
    private void OnGroupDraftKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter){Vm.ConfirmGroupEditCommand.Execute(null);e.Handled=true;}
        if(e.Key==Key.Escape){Vm.CancelGroupEditCommand.Execute(null);QueryBox.Focus();e.Handled=true;}
    }

    private WatchlistViewModel Vm => (WatchlistViewModel)DataContext;

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (SuggestionList.SelectedItem is SymbolMatch match)
                _ = Vm.AddMatchCommand.ExecuteAsync(match);
            else
                _ = Vm.AddCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.HideSuggestions();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Vm.Suggestions.Count > 0)
        {
            SuggestionList.Focus();
            if (SuggestionList.SelectedIndex < 0) SuggestionList.SelectedIndex = 0;
            e.Handled = true;
        }
    }

    private void OnSuggestionKeyDown(object sender, KeyEventArgs e)
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

    private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SymbolMatch match)
        {
            _ = Vm.AddMatchCommand.ExecuteAsync(match);
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragRow = (e.OriginalSource as FrameworkElement)?.DataContext as WatchlistRowViewModel;
        _dragging = false;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null || _dragging) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 12) return;
        _dragging = true;
        DragDrop.DoDragDrop(QuoteList, _dragRow, DragDropEffects.Move);
        _dragging = false;
        _dragRow = null;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(WatchlistRowViewModel)) is not WatchlistRowViewModel from) return;
        var to = (e.OriginalSource as FrameworkElement)?.DataContext as WatchlistRowViewModel;
        if (to is not null && !ReferenceEquals(from, to))
            Vm.ReorderRows(from.Symbol, to.Symbol);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // D1: double-click a row → its K-line chart.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is WatchlistRowViewModel row)
        {
            Vm.SelectedRow = row;
            Vm.OpenChartCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}