using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Meili.Core.Models;
using Meili.Shared.Services;
using Meili.Shared.ViewModels;
using Meili.Mac.Views.Controls;
namespace Meili.Mac.Platform;

/// <summary>Native, cached market glance surface shared with the Windows tray through QuotePanelViewModel.</summary>
public sealed class QuotePanelWindow : Window
{
    private readonly QuotePanelViewModel _model;private readonly Action<string> _open;
    private readonly LocalizationService _loc;private readonly StackPanel _rows=new(){Spacing=3};
    private readonly Dictionary<string,RowView> _views=new();
    private readonly TextBlock _title=new(),_name=new(),_price=new(),_change=new(),_source=new(),_asof=new(),_extended=new(),_date=new(),_status=new(),_page=new(),_empty=new();
    private readonly QuoteSparkline _chart=new(){Height=96};private readonly TextBlock[] _metrics=[new(),new(),new(),new()];
    private readonly Button _refresh,_previous,_next,_chartButton;private readonly ToggleButton _pin;
    private readonly DesktopActivity _activity=AppServices.Get<DesktopActivity>();
    private readonly string _activityOwner="quote-panel-window-"+Guid.NewGuid();
    public QuotePanelViewModel Model=>_model;
    public QuotePanelWindow(QuotePanelViewModel model,Action<string> open,Action watchlist,Action quit)
    {
        _model=model;_open=open;_loc=LocalizationService.Current;Width=540;Height=760;CanResize=false;WindowDecorations=Avalonia.Controls.WindowDecorations.None;ShowInTaskbar=false;Topmost=true;
        this[!BackgroundProperty]=this.GetResourceObservable("ApplicationBackgroundBrush").ToBinding();this[!ForegroundProperty]=this.GetResourceObservable("TextFillColorPrimaryBrush").ToBinding();this[!FontFamilyProperty]=this.GetResourceObservable("AppFont").ToBinding();
        var header=new DockPanel{LastChildFill=true};var tools=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};DockPanel.SetDock(tools,Dock.Right);header.Children.Add(tools);
        _pin=new ToggleButton{Content=_loc["QP_Pin"],FontSize=12,MinHeight=32,Padding=new Thickness(8,4)};tools.Children.Add(_pin);
        _refresh=IconButton("Refresh","QP_Refresh",async()=>await _model.RefreshAsync());tools.Children.Add(_refresh);tools.Children.Add(IconButton("Close","QP_Close",HidePanel));
        _title.FontSize=18;_title.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(_title);
        header.PointerPressed+=(_,e)=>{if(_pin.IsChecked==true && e.Source==_title && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)BeginMoveDrag(e);};
        var hero=new StackPanel{Spacing=5,Margin=new Thickness(0,12,0,12)};_name.FontSize=14;hero.Children.Add(_name);
        var prices=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};_price.FontSize=32;_price.FontWeight=FontWeight.SemiBold;_change.FontSize=18;_change.VerticalAlignment=VerticalAlignment.Center;prices.Children.Add(_price);prices.Children.Add(_change);hero.Children.Add(prices);
        Caption(_source);Caption(_asof);Caption(_extended);Caption(_date);Caption(_empty);_source.TextWrapping=_asof.TextWrapping=_date.TextWrapping=_extended.TextWrapping=TextWrapping.Wrap;hero.Children.Add(_source);hero.Children.Add(_asof);hero.Children.Add(_extended);hero.Children.Add(_chart);var times=new UniformGrid{Columns=3};foreach(var (time,align) in new[]{("09:30",HorizontalAlignment.Left),("12:00",HorizontalAlignment.Center),("16:00",HorizontalAlignment.Right)}){var tick=new TextBlock{Text=time,HorizontalAlignment=align};Caption(tick);times.Children.Add(tick);}hero.Children.Add(times);hero.Children.Add(_empty);hero.Children.Add(_date);
        var metrics=new UniformGrid{Columns=4,Margin=new Thickness(0,4,0,0)};foreach(var t in _metrics){Caption(t);metrics.Children.Add(t);}hero.Children.Add(metrics);
        var list=new ScrollViewer{Content=_rows,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var footer=new DockPanel{LastChildFill=true,Margin=new Thickness(0,10,0,0)};var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=5};DockPanel.SetDock(actions,Dock.Right);footer.Children.Add(actions);
        _chartButton=new Button{Content=_loc["QP_Open"],FontSize=13};_chartButton.Click+=(_,_)=>OpenSelected();actions.Children.Add(_chartButton);
        var watch=new Button{FontSize=13};watch[!Button.ContentProperty]=this.GetResourceObservable("QP_Watchlist").ToBinding();watch.Click+=(_,_)=>{HidePanel();watchlist();};actions.Children.Add(watch);
        var more=IconButton("More","QP_More",()=>{});var quitItem=new MenuItem{Header=_loc["Menu_Quit"]};quitItem.Click+=(_,_)=>quit();more.ContextMenu=new ContextMenu{Items={quitItem}};more.Click+=(_,_)=>more.ContextMenu.Open(more);actions.Children.Add(more);
        var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4};_previous=IconButton("ChevronLeft","QP_Previous",async()=>await _model.MoveAsync(-1));_next=IconButton("ChevronRight","QP_Next",async()=>await _model.MoveAsync(1));paging.Children.Add(_previous);_page.VerticalAlignment=VerticalAlignment.Center;paging.Children.Add(_page);paging.Children.Add(_next);footer.Children.Add(paging);
        var content=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,*,Auto,Auto")};content.Children.Add(header);Grid.SetRow(hero,1);content.Children.Add(hero);Grid.SetRow(list,2);content.Children.Add(list);Grid.SetRow(_status,3);content.Children.Add(_status);Grid.SetRow(footer,4);content.Children.Add(footer);Caption(_status);
        Content=new AppCard{Padding=new Thickness(16),Child=content,CornerRadius=new CornerRadius(16)};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){HidePanel();e.Handled=true;}};Deactivated+=(_,_)=>{if(_pin.IsChecked!=true && !more.ContextMenu.IsOpen)HidePanel();};
        Closed+=(_,_)=>{_activity.SetVisible(_activityOwner,false);_model.Changed-=OnChanged;_model.Dispose();};_model.Changed+=OnChanged;UpdateView();
    }
    public async Task ShowPanelAsync(double? anchorX=null,double? anchorY=null)
    {
        var area=Screens.Primary?.WorkingArea;var scale=Screens.Primary?.Scaling??1;
        if(area is { } a){Height=Math.Min(760,a.Height/scale-20);var x=anchorX is { } ax?(int)(ax*scale-Width*scale):a.Right-(int)(Width*scale)-12;var y=anchorY is { } ay?(int)(ay*scale)+8:a.Y+8;Position=new PixelPoint(Math.Clamp(x,a.X+8,Math.Max(a.X+8,a.Right-(int)(Width*scale)-8)),Math.Clamp(y,a.Y+8,Math.Max(a.Y+8,a.Bottom-(int)(Height*scale)-8)));}
        _activity.SetVisible(_activityOwner,true);Show();Activate();AppMotion.Enter(_title);await _model.ActivateAsync();
    }
    public void HidePanel(){_activity.SetVisible(_activityOwner,false);Hide();_model.Deactivate();}
    private void OpenSelected(){if(_model.Selected is { } row){HidePanel();_open(row.Symbol);}}
    private void OnChanged(object? sender,EventArgs e)=>UpdateView();
    private IBrush Brush(string key)=>this.TryFindResource(key,out var value)&&value is IBrush b?b:Brushes.Gray;
    private void Caption(TextBlock text){text.FontSize=12;text[!TextBlock.ForegroundProperty]=this.GetResourceObservable("TextFillColorSecondaryBrush").ToBinding();}
    private Button IconButton(string kind,string label,Action action){var b=new Button{Width=32,Height=32,MinWidth=32,MinHeight=32,Padding=default,Content=new LucideIcon{Kind=kind,Width=18,Height=18}};b.Classes.Add("Subtle");b[!ToolTip.TipProperty]=this.GetResourceObservable(label).ToBinding();b[!Avalonia.Automation.AutomationProperties.NameProperty]=this.GetResourceObservable(label).ToBinding();b.Click+=(_,_)=>action();return b;}
    private static string Value(double? v)=>v is { } n&&double.IsFinite(n)?NumberDisplay.Price(n):"—";
    private void UpdateView()
    {
        Title=_title.Text=_loc["QP_Title"];_refresh.IsEnabled=!_model.IsBusy;_status.Text=_model.IsBusy?_loc["QP_Refresh"]+"…":_model.Status;_page.Text=$"{_model.Page+1}/{_model.Pages}";_previous.IsEnabled=_model.Page>0;_next.IsEnabled=_model.Page+1<_model.Pages;
        _pin.Content=_loc["QP_Pin"];_chartButton.Content=_loc["QP_Open"];
        if(!_views.Keys.SequenceEqual(_model.Rows.Select(r=>r.Symbol))){_rows.Children.Clear();_views.Clear();foreach(var row in _model.Rows){var view=new RowView(this,row);_views.Add(row.Symbol,view);_rows.Children.Add(view.Button);}}
        foreach(var row in _model.Rows)_views[row.Symbol].Update(row,_model.Selected==row);
        var selected=_model.Selected;_chartButton.IsEnabled=selected is not null;if(selected is null)return;
        _name.Text=selected.Symbol+" · "+selected.Name;_price.Text=selected.Price;_change.Text=(selected.Quote is { } movementQuote && QuoteMovement.Values(movementQuote).Change is { } movement?NumberDisplay.SignedChange(movement,movementQuote.Price)+"  ":"")+selected.Change;_price.Foreground=_change.Foreground=Brush(selected.BrushKey);
        var q=selected.Quote;_source.Text=q is null?_loc["Mb_Unavailable"]:_loc["Mb_Regular"]+" · "+q.Currency+" · "+FreshnessText.SourceAndTiming(q,_loc,DateTimeOffset.UtcNow)+(selected.Failed?" · "+_loc["Mb_Stale"]:"");_asof.Text=FreshnessText.AsOf(q?.AsOf,_loc);
        _extended.Text=q is {MarketState:MarketState.PreMarket or MarketState.AfterHours,ExtendedPrice:>0} && double.IsFinite(q.ExtendedPrice.Value)?_loc[q.MarketState==MarketState.PreMarket?"Quote_Pre":"Quote_Post"]+"  "+Value(q.ExtendedPrice)+" · "+FreshnessText.AsOf(q.ExtendedTime,_loc):"";_extended.IsVisible=_extended.Text.Length>0;
        _chart.Data=selected.Graph;_chart.LineBrush=Brush(selected.BrushKey);_chart.InvalidateVisual();_empty.Text=_loc["QP_NoChart"];_empty.IsVisible=selected.Graph.Points.Count<2;_chart.IsVisible=!_empty.IsVisible;
        _date.Text=GraphLabel(selected);_metrics[0].Text=_loc["QP_OpenPrice"]+"  "+Value(q?.Open);_metrics[1].Text=_loc["QP_High"]+"  "+Value(q?.High);_metrics[2].Text=_loc["QP_Low"]+"  "+Value(q?.Low);_metrics[3].Text=_loc["QP_Volume"]+"  "+NumberDisplay.Compact(q?.Volume);
    }
    private string GraphLabel(PanelQuoteRow row)=>row.Graph.Day is { } day?day.ToString("yyyy-MM-dd")+" · "+_loc["QP_Intraday"]+(row.ChartMeta is { } m?" · "+FreshnessText.SourceAndTiming(m,_loc,DateTimeOffset.UtcNow):"")+(row.Graph.Offline||row.GraphFailed?" · "+_loc["Mb_Stale"]:""):_loc["QP_NoChart"];
    private sealed class RowView
    {
        public readonly Button Button;private readonly QuotePanelWindow _owner;private readonly TextBlock _name=new(),_price=new(),_change=new(),_date=new();private readonly QuoteSparkline _spark=new(){Width=100,Height=24};
        public RowView(QuotePanelWindow owner,PanelQuoteRow row)
        {
            _owner=owner;Button=new Button{HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(8,6)};
            var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("*,108,90,80")};var symbol=new TextBlock{Text=row.Symbol,FontSize=15,FontWeight=FontWeight.SemiBold};owner.Caption(_name);_name.TextTrimming=TextTrimming.CharacterEllipsis;var identity=new StackPanel{Children={symbol,_name}};grid.Children.Add(identity);
            owner.Caption(_date);var mini=new StackPanel{Children={_spark,_date}};Grid.SetColumn(mini,1);grid.Children.Add(mini);_price.FontSize=16;_price.FontWeight=FontWeight.SemiBold;_price.VerticalAlignment=VerticalAlignment.Center;_price.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(_price,2);grid.Children.Add(_price);_change.FontSize=14;_change.VerticalAlignment=VerticalAlignment.Center;_change.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(_change,3);grid.Children.Add(_change);Button.Content=grid;Button.Click+=(_,_)=>owner._model.Select(row.Symbol);Button.DoubleTapped+=(_,_)=>{owner._model.Select(row.Symbol);owner.OpenSelected();};
        }
        public void Update(PanelQuoteRow row,bool selected){_name.Text=row.Name;_price.Text=row.Price;_price.FontSize=row.Price.Length>10?13:16;_change.Text=row.Change;_price.Foreground=_change.Foreground=_owner.Brush(row.BrushKey);_spark.Data=row.Graph;_spark.LineBrush=_owner.Brush(row.BrushKey);_spark.InvalidateVisual();_date.Text=(row.Graph.Day?.ToString("MM-dd")??"—")+(row.Failed||row.GraphFailed?" · !":row.Quote?.DelayMinutes is >0?" · "+row.Quote.DelayMinutes+"m":row.Quote?.IsRealtime==true?" · RT":" · ?");Button.Background=selected?_owner.Brush("RowHighlightBrush"):Brushes.Transparent;Button.BorderBrush=selected?_owner.Brush("ControlStrokeColorDefaultBrush"):Brushes.Transparent;ToolTip.SetTip(Button,row.Name+"\n"+(row.Quote is { } q?FreshnessText.SourceAndTiming(q,_owner._loc,DateTimeOffset.UtcNow)+" · "+FreshnessText.AsOf(q.AsOf,_owner._loc):_owner._loc["Mb_Unavailable"])+"\n"+_owner.GraphLabel(row));}
    }
}
