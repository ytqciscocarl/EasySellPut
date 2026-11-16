using Meili.Shared.Services;
using Meili.Core.Abstractions;
using Meili.Core.Models;
using Meili.Core.Quotes;
using Meili.Core.Settings;
using Meili.Core.Diagnostics;
namespace Meili.Shared.ViewModels;

public sealed class PanelQuoteRow(string symbol)
{
    public string Symbol{get;}=symbol;
    public Quote? Quote{get;internal set;}
    public bool Failed{get;internal set;}
    public IntradaySparkline Graph{get;internal set;}=IntradaySparkline.Empty;
    public BarSeries? ChartMeta{get;internal set;}
    public bool GraphFailed{get;internal set;}
    internal DateTimeOffset GraphRequested;
    public string Name=>Quote?.Name??Symbol;
    public string Price=>Quote is {Price:>0} q && double.IsFinite(q.Price)?NumberDisplay.Price(q.Price):"—";
    public string Change=>Quote is { } q?QuoteMovement.Percent(QuoteMovement.Values(q).Percent):"—";
    public string BrushKey=>Failed||Quote is null||QuoteStaleness.Check(Quote,DateTimeOffset.UtcNow)!=StaleReason.None?"UnknownBrush":QuoteMovement.Values(Quote).Percent is { } p?p>=0?"UpBrush":"DownBrush":"UnknownBrush";
}

/// <summary>Visible-only quote subscriber; the existing QuotePump drives updates. No new market timer.</summary>
public sealed class QuotePanelViewModel : IDisposable
{
    public const int PageSize=8;
    private readonly QuoteHub _hub;private readonly IMarketDataService _data;private readonly WatchlistStore _watch;private readonly SymbolContext _context;
    private readonly LocalizationService _loc;private readonly string _owner="quote-panel-"+Guid.NewGuid();
    private readonly Dictionary<string,PanelQuoteRow> _cache=new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _request;private bool _visible,_disposed;private int _generation;private DateTimeOffset _lastAutomatic;
    public event EventHandler? Changed;
    public IReadOnlyList<PanelQuoteRow> Rows{get;private set;}=[];
    public PanelQuoteRow? Selected=>Rows.FirstOrDefault(r=>r.Symbol==_context.Current)??Rows.FirstOrDefault();
    public int Page{get;private set;}public int Pages{get;private set;}=1;public bool IsBusy{get;private set;}
    public string Status{get;private set;}="";
    public QuotePanelViewModel(QuoteHub hub,IMarketDataService data,WatchlistStore watch,SymbolContext context,LocalizationService loc)
    {_hub=hub;_data=data;_watch=watch;_context=context;_loc=loc;hub.Updated+=OnBatch;watch.Changed+=OnWatch;context.Changed+=OnSymbol;loc.LanguageChanged+=OnLabels;Ui.Platform.ColorsChanged+=OnLabels;}
    public async Task ActivateAsync(){if(_disposed)return;_visible=true;Build();await RefreshAsync(false);}
    public void Deactivate(){if(_disposed)return;_visible=false;_generation++;_request?.Cancel();_hub.Unsubscribe(_owner);IsBusy=false;}
    private void Notify()=>Changed?.Invoke(this,EventArgs.Empty);
    private void Build()
    {
        var watch=_watch.AllSymbols.ToList();if(!watch.Contains(_context.Current??"SPY",StringComparer.OrdinalIgnoreCase))watch.Insert(0,_context.Current??"SPY");var symbols=watch.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();Pages=Math.Max(1,(symbols.Length+PageSize-1)/PageSize);Page=Math.Clamp(Page,0,Pages-1);
        Rows=symbols.Skip(Page*PageSize).Take(PageSize).Select(s=>{if(!_cache.TryGetValue(s,out var row))_cache[s]=row=new(s);if(_hub.TryGetCached(s,out var q))row.Quote=q;return row;}).ToArray();
        foreach(var s in _cache.Keys.Where(s=>!symbols.Contains(s,StringComparer.OrdinalIgnoreCase)).ToArray())_cache.Remove(s);
        if(_visible)_hub.Subscribe(_owner,Rows.Select(r=>r.Symbol));Notify();
    }
    private void CancelRefresh(){_request?.Cancel();_generation++;IsBusy=false;}
    public async Task MoveAsync(int delta){CancelRefresh();Page+=delta;Build();await RefreshAsync(false);}
    public void Select(string symbol){if(Rows.Any(r=>r.Symbol==symbol))_context.Set(symbol);}
    private void OnSymbol(object? sender,string symbol){if(!_visible)return;if(!Rows.Any(r=>r.Symbol==symbol)){CancelRefresh();Page=0;Build();_ = RefreshAsync(false);}Notify();}
    private void OnWatch(object? sender,EventArgs e){if(!_visible)return;CancelRefresh();Build();_ = RefreshAsync(false);}
    private void OnLabels(object? sender,EventArgs e)=>Ui.Platform.Run(()=>{if(_visible)Notify();});
    private void OnBatch(object? sender,QuoteBatch batch)=>Ui.Platform.Run(()=>
    {
        if(!_visible||_disposed)return;foreach(var row in Rows.Where(r=>batch.Covers(r.Symbol))){var q=batch.Find(row.Symbol);row.Failed=batch.Error is not null||q is null;if(q is not null)row.Quote=q;}Notify();
        if(!IsBusy && DateTimeOffset.UtcNow-_lastAutomatic>TimeSpan.FromMinutes(1))_ = RefreshAsync(false);
    });
    public async Task RefreshAsync(bool force=true)
    {
        if(!_visible||_disposed||IsBusy)return;_request?.Dispose();_request=new CancellationTokenSource(TimeSpan.FromSeconds(35));var ct=_request.Token;var generation=++_generation;
        IsBusy=true;Status="";_lastAutomatic=DateTimeOffset.UtcNow;Notify();var rows=Rows.ToArray();
        try
        {
            try{await _hub.GetQuotesAsync(rows.Select(r=>r.Symbol).ToArray(),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){AppLog.Warn("quote-panel","Quote refresh failed",ex);Status=_loc["QP_Retry"];}
            using var gate=new SemaphoreSlim(2);
            await Task.WhenAll(rows.Select(async row=>
            {
                if(!force && DateTimeOffset.UtcNow-row.GraphRequested<TimeSpan.FromMinutes(1))return;
                await gate.WaitAsync(ct);try
                {
                    var now=DateTimeOffset.UtcNow;var series=await _data.GetBarsAsync(new(row.Symbol,BarInterval.Minute5,now.AddDays(-10),now),ct);
                    if(ct.IsCancellationRequested||generation!=_generation||!_visible)return;
                    if(series.Interval!=BarInterval.Minute5 || !string.Equals(series.Symbol,row.Symbol,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Mismatched chart symbol");
                    row.GraphRequested=DateTimeOffset.UtcNow;row.Graph=IntradaySparkline.Create(series,row.Quote?.PreviousClose,now);row.ChartMeta=series with{Bars=[]};row.GraphFailed=false;Notify();
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception ex){AppLog.Warn("quote-panel","Intraday unavailable for "+row.Symbol,ex);if(generation!=_generation||!_visible)return;row.GraphFailed=true;Status=_loc["QP_Retry"];}
                finally{gate.Release();}
            }));
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){if(generation==_generation&&_visible)Status=_loc["QP_Retry"];}
        finally{if(generation==_generation){IsBusy=false;Notify();}}
    }
    public void Dispose(){if(_disposed)return;Deactivate();_disposed=true;_request?.Dispose();_hub.Updated-=OnBatch;_watch.Changed-=OnWatch;_context.Changed-=OnSymbol;_loc.LanguageChanged-=OnLabels;Ui.Platform.ColorsChanged-=OnLabels;}
}
