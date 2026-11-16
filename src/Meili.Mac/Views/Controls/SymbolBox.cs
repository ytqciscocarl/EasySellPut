using System.Collections;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Meili.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Meili.Mac.Views.Controls;

/// <summary>
/// D3: the one symbol input used by every page (port of the Windows control). Typing shows matching watch-list
/// symbols first, then search hits (debounced 300 ms, searched off the UI thread); the chevron lists the whole
/// watch list. Return or a click commits through <see cref="CommitCommand"/>.
/// </summary>
public sealed class SymbolBox : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SymbolBox, string?>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IEnumerable?> WatchSymbolsProperty = AvaloniaProperty.Register<SymbolBox, IEnumerable?>(nameof(WatchSymbols));
    public static readonly StyledProperty<ICommand?> CommitCommandProperty = AvaloniaProperty.Register<SymbolBox, ICommand?>(nameof(CommitCommand));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IEnumerable? WatchSymbols { get => GetValue(WatchSymbolsProperty); set => SetValue(WatchSymbolsProperty, value); }
    public ICommand? CommitCommand { get => GetValue(CommitCommandProperty); set => SetValue(CommitCommandProperty, value); }

    public ObservableCollection<SymbolSuggestion> Items { get; } = [];
    public bool NoMatch { get; private set; }
    public bool SearchFailed { get; private set; }
    public bool IsDropDownOpen => _popup.IsOpen;

    private readonly TextBox _box;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _seq;
    private bool _syncing;

    public SymbolBox()
    {
        _box = new TextBox { Height = 32, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4) };
        _box[!TextBox.PlaceholderTextProperty] = _box.GetResourceObservable("Sb_Placeholder").ToBinding();
        _box.TextChanged += OnBoxTextChanged;
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var chevron = new Button { Content = new LucideIcon { Kind = "ChevronDown", Width = 14, Height = 14 }, Classes = { "Subtle", "Icon" }, Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Focusable = false };
        chevron[!ToolTip.TipProperty] = chevron.GetResourceObservable("Sb_ShowWatchlist").ToBinding();
        chevron.Click += (_, _) => { _box.Focus(); _ = RefreshAsync(showAll: true, open: true); };

        _list = new ListBox { Background = Brushes.Transparent, ItemsSource = Items, ItemTemplate = BuildTemplate() };
        _list.KeyDown += OnListKeyDown;
        _list.Tapped += (_, _) => { if (_list.SelectedItem is SymbolSuggestion s) Pick(s); };

        _empty = new TextBlock { Margin = new Thickness(12, 6), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        _empty[!TextBlock.ForegroundProperty] = _empty.GetResourceObservable("TextFillColorSecondaryBrush").ToBinding();

        var panel = new DockPanel();
        DockPanel.SetDock(_empty, Dock.Bottom);
        panel.Children.Add(_empty);
        panel.Children.Add(_list);
        var border = new Border { Classes = { "Popup" }, MinWidth = 320, MaxHeight = 320, Margin = new Thickness(0, 4, 0, 0), Child = panel };
        _popup = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, Child = border,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(_box);
        grid.Children.Add(chevron);
        grid.Children.Add(_popup);
        Content = grid;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(showAll: false, open: true); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;
        var value = change.GetNewValue<string?>() ?? "";
        if (_box.Text == value) return;
        _syncing = true;
        _box.Text = value;
        _box.CaretIndex = value.Length;
        _syncing = false;
    }

    /// <summary>Moves keyboard focus into the text box and selects its text (used by the keyboard wizard, D2).</summary>
    public void FocusInput() => Dispatcher.UIThread.Post(() =>
    {
        _box.Focus();
        _box.SelectAll();
    }, DispatcherPriority.Loaded);   // the wizard was hidden until now: focus once it has been laid out

    /// <summary>Puts the caret after <paramref name="text"/> (the wizard opens with the first typed letter).</summary>
    public void Begin(string text)
    {
        Text = text;
        Dispatcher.UIThread.Post(() =>
        {
            _box.Focus();
            _box.CaretIndex = text.Length;
        }, DispatcherPriority.Loaded);
    }

    private static FuncDataTemplate<SymbolSuggestion> BuildTemplate() => new((s, _) =>
    {
        var name = new TextBlock { Text = s?.Name, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis };
        name[!TextBlock.ForegroundProperty] = name.GetResourceObservable("TextFillColorSecondaryBrush").ToBinding();
        var tag = new TextBlock { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), IsVisible = s?.FromWatchlist == true };
        tag[!TextBlock.TextProperty] = tag.GetResourceObservable("Sb_WatchTag").ToBinding();
        tag[!TextBlock.ForegroundProperty] = tag.GetResourceObservable("AccentFillColorDefaultBrush").ToBinding();
        var exch = new TextBlock { Text = s?.Exchange, FontSize = 12, Margin = new Thickness(12, 0, 0, 0) };
        exch[!TextBlock.ForegroundProperty] = exch.GetResourceObservable("TextFillColorSecondaryBrush").ToBinding();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2),
            Children = { new TextBlock { Text = s?.Symbol, FontWeight = FontWeight.SemiBold, Width = 88 }, name, tag, exch },
        };
    });

    private void OnBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Text = _box.Text;
        if (!_box.IsKeyboardFocusWithin) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (_popup.IsOpen && _list.SelectedItem is SymbolSuggestion s) Pick(s);
                else Commit();
                e.Handled = true;
                break;
            case Key.Down:
                if (_popup.IsOpen && Items.Count > 0)
                {
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex);
                    (_list.ContainerFromIndex(_list.SelectedIndex) ?? (Control)_list).Focus();
                }
                else _ = RefreshAsync(showAll: true, open: true);
                e.Handled = true;
                break;
            case Key.Escape when _popup.IsOpen:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (_list.SelectedItem is SymbolSuggestion s) Pick(s);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                _box.Focus();
                e.Handled = true;
                break;
            case Key.Up when _list.SelectedIndex <= 0:
                _box.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Rebuilds the suggestion list for the current text (or the whole watch list).</summary>
    internal async Task RefreshAsync(bool showAll, bool open)
    {
        var seq = ++_seq;
        var q = showAll ? "" : (Text ?? "").Trim();
        var watch = WatchSymbols?.Cast<string>().ToList() ?? [];
        IReadOnlyList<SymbolMatch>? hits = null;
        SearchFailed = false;
        if (q.Length > 0 && AppServices.Provider?.GetService<SymbolSearchService>() is { } search)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { hits = await search.SearchAsync(q, _cts.Token); }
            catch (OperationCanceledException) { return; }
            if (hits is null) SearchFailed = true;
        }
        if (seq != _seq) return;
        Items.Clear();
        foreach (var s in SymbolSuggestions.Build(q, watch, hits)) Items.Add(s);
        NoMatch = q.Length > 0 && Items.Count == 0;
        _empty.Text = LocalizationService.Current[SearchFailed ? "Sb_SearchFailed" : "Sb_NoMatch"];
        _empty.IsVisible = NoMatch || SearchFailed;
        // Never pop up for a box that does not have focus (text set by navigation, or headless tests).
        _popup.IsOpen = open && IsKeyboardFocusWithin && (Items.Count > 0 || q.Length > 0);
    }

    internal void Pick(SymbolSuggestion s)
    {
        Text = s.Symbol;
        Commit();
    }

    private void Commit()
    {
        Close();
        _debounce.Stop();
        if (CommitCommand?.CanExecute(null) == true) CommitCommand.Execute(null);
    }

    private void Close()
    {
        _popup.IsOpen = false;
        _seq++;
    }
}
