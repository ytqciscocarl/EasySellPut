using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Meili.App.Services;
using Meili.Core.Models;

namespace Meili.App.Views.Controls;

/// <summary>
/// D3: the one symbol input used by every page. Typing shows matching watch-list symbols first, then search hits
/// (debounced); the chevron lists the whole watch list. Enter or a click commits through <see cref="CommitCommand"/>.
/// </summary>
public sealed class SymbolBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SymbolBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SymbolBox)d).OnTextPropertyChanged((string?)e.NewValue))
        { DefaultUpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
    public static readonly DependencyProperty WatchSymbolsProperty = DependencyProperty.Register(nameof(WatchSymbols), typeof(IEnumerable), typeof(SymbolBox));
    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(nameof(CommitCommand), typeof(ICommand), typeof(SymbolBox));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IEnumerable? WatchSymbols { get => (IEnumerable?)GetValue(WatchSymbolsProperty); set => SetValue(WatchSymbolsProperty, value); }
    public ICommand? CommitCommand { get => (ICommand?)GetValue(CommitCommandProperty); set => SetValue(CommitCommandProperty, value); }

    public ObservableCollection<SymbolSuggestion> Items { get; } = [];
    /// <summary>True when the last refresh found nothing for a non-empty query (or the search failed).</summary>
    public bool NoMatch { get; private set; }
    /// <summary>True when the last search failed: only watch-list matches are shown.</summary>
    public bool SearchFailed { get; private set; }

    private readonly Wpf.Ui.Controls.TextBox _box;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _seq;
    private bool _syncing;

    public SymbolBox()
    {
        _box = new Wpf.Ui.Controls.TextBox { FontSize = 14, MinHeight = 32, Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4, 8, 4) };
        _box.SetResourceReference(Wpf.Ui.Controls.TextBox.PlaceholderTextProperty, "Sb_Placeholder");
        _box.TextChanged += OnBoxTextChanged;
        _box.PreviewKeyDown += OnBoxKeyDown;

        var chevron = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Width = 32, Height = 32, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            Content = new LucideIcon { Kind = "ChevronDown", Width = 14, Height = 14 },
            Focusable = false,
        };
        chevron.SetResourceReference(ToolTipProperty, "Sb_ShowWatchlist");
        AutomationProperties.SetAutomationId(chevron, "SymbolBoxDropDown");
        chevron.Click += (_, _) => { _box.Focus(); _ = RefreshAsync(showAll: true, open: true); };

        _list = new ListBox { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, ItemTemplate = BuildTemplate() };
        _list.ItemsSource = Items;
        _list.PreviewKeyDown += OnListKeyDown;
        _list.MouseLeftButtonUp += (_, _) => { if (_list.SelectedItem is SymbolSuggestion s) Pick(s); };
        AutomationProperties.SetAutomationId(_list, "SymbolBoxList");

        _empty = new TextBlock { Margin = new Thickness(12, 6, 12, 6), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var panel = new DockPanel();
        DockPanel.SetDock(_empty, Dock.Bottom);
        panel.Children.Add(_empty);
        panel.Children.Add(_list);
        var border = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 4, 0, 4),
            MinWidth = 320, MaxHeight = 320, Margin = new Thickness(0, 4, 0, 0), Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
        _popup = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = border,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(_box);
        grid.Children.Add(chevron);
        grid.Children.Add(_popup);
        Content = grid;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(showAll: false, open: true); };
        Loaded += (_, _) =>
        {
            var id = AutomationProperties.GetAutomationId(this);
            if (!string.IsNullOrEmpty(id)) AutomationProperties.SetAutomationId(_box, id + "Input");
        };
    }

    /// <summary>Moves keyboard focus into the text box and selects its text (used by the keyboard wizard, D2).</summary>
    public void FocusInput()
    {
        _box.Focus();
        _box.SelectAll();
    }

    private static DataTemplate BuildTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(MarginProperty, new Thickness(4, 2, 4, 2));
        var sym = new FrameworkElementFactory(typeof(TextBlock));
        sym.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SymbolSuggestion.Symbol)));
        sym.SetValue(TextBlock.FontSizeProperty, 14.0);
        sym.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        sym.SetValue(WidthProperty, 88.0);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SymbolSuggestion.Name)));
        name.SetValue(TextBlock.FontSizeProperty, 14.0);
        name.SetValue(MaxWidthProperty, 200.0);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var tag = new FrameworkElementFactory(typeof(TextBlock));
        tag.SetResourceReference(TextBlock.TextProperty, "Sb_WatchTag");
        tag.SetValue(TextBlock.FontSizeProperty, 12.0);
        tag.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        tag.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        tag.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(SymbolSuggestion.FromWatchlist)) { Converter = new BooleanToVisibilityConverter() });
        var exch = new FrameworkElementFactory(typeof(TextBlock));
        exch.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SymbolSuggestion.Exchange)));
        exch.SetValue(TextBlock.FontSizeProperty, 12.0);
        exch.SetValue(MarginProperty, new Thickness(12, 0, 0, 0));
        exch.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.AppendChild(sym);
        panel.AppendChild(name);
        panel.AppendChild(tag);
        panel.AppendChild(exch);
        return new DataTemplate(typeof(SymbolSuggestion)) { VisualTree = panel };
    }

    private void OnTextPropertyChanged(string? value)
    {
        if (_box.Text == (value ?? "")) return;
        _syncing = true;
        try
        {
            _box.Text = value ?? "";
            _box.CaretIndex = _box.Text.Length;
        }
        finally { _syncing = false; }
    }

    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        Text = _box.Text;
        if (!_box.IsKeyboardFocusWithin) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
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
                    (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as UIElement ?? _list).Focus();
                }
                else _ = RefreshAsync(showAll: true, open: true);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_popup.IsOpen) { Close(); e.Handled = true; }
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
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
        _empty.SetResourceReference(TextBlock.TextProperty, SearchFailed ? "Sb_SearchFailed" : "Sb_NoMatch");
        _empty.Visibility = NoMatch || SearchFailed ? Visibility.Visible : Visibility.Collapsed;
        // Never pop up for a box that does not have focus (e.g. text set by navigation, or tests off-screen).
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
