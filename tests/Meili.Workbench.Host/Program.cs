using System.Text.Json;
using System.Threading.Channels;
using Meili.Shared.Tests;
using Meili.Shared.ViewModels;
using Meili.Testing;

// Explicit, test-only fixture host. Production CLI/desktop never load this project.
var queue=Channel.CreateUnbounded<Action>(new(){SingleReader=true,SingleWriter=false});
Ui.Platform=new QueuedUi(queue);_ =new LocalizationService();
var directory=Path.Combine(Path.GetTempPath(),"meili-workbench-host-"+Guid.NewGuid().ToString("N"));
var chartMode=args.Contains("--chart");
var vm=chartMode?null:await WorkbenchFixture.CreateAsync(directory);
var chartVm=chartMode?await WorkbenchFixture.CreateChartAsync(directory,args.Contains("--sessions")):null;
var palette=WorkbenchFixture.Palette(true);
using var session=chartMode?new WebChartSession(chartVm!):new WebChartSession(vm!);
if(vm is not null){vm.ChartChanged+=(_,_)=>session.Update(palette);vm.WhatIfChanged+=(_,_)=>session.Update(palette);}
if(chartVm is not null)chartVm.PlotChanged+=(_,_)=>session.Update(palette);
session.Update(palette);
Console.WriteLine(JsonSerializer.Serialize(new{url=session.PageUri.AbsoluteUri}));
var pump=Pump();
while(await Console.In.ReadLineAsync() is { } line)
{
    if(line=="quit")break;
    var command=line;
    if(command is "draw-horizontal" or "draw-trend" && chartVm is not null)queue.Writer.TryWrite(()=>{chartVm.StartDrawingCommand.Execute(command=="draw-horizontal"?Meili.Core.Settings.DrawingKind.HorizontalLine:Meili.Core.Settings.DrawingKind.TrendLine);session.Update(palette);});
    if(command=="opened")queue.Writer.TryWrite(()=>Console.WriteLine(JsonSerializer.Serialize(((QueuedUi)Ui.Platform).OpenedArticles)));
    if(command is "light" or "dark")queue.Writer.TryWrite(()=>{palette=WorkbenchFixture.Palette(command=="dark");session.Update(palette);});
    if(command is "en-US" or "zh-CN")queue.Writer.TryWrite(()=>LocalizationService.Current.Apply(command));
}
queue.Writer.TryComplete();await pump;
vm?.Deactivate();chartVm?.Deactivate();session.Dispose();Directory.Delete(directory,true);
async Task Pump(){await foreach(var action in queue.Reader.ReadAllAsync())action();}
sealed class QueuedUi(Channel<Action> queue):IUiPlatform
{
    private readonly FakeUiPlatform _fake=new();
    public void Run(Action action)=>queue.Writer.TryWrite(action);
    public IUiTimer CreateTimer(TimeSpan interval)=>_fake.CreateTimer(interval);
    public object? Resource(string key)=>key;
    public event EventHandler? ColorsChanged{add{}remove{}}
    public Task<bool> ConfirmAsync(string title,string message)=>Task.FromResult(false);
    public void OpenUrl(string url)=>throw new NotSupportedException();
    public List<object> OpenedArticles {get;}=[];
    public void OpenNewsArticle(string url,string title)=>OpenedArticles.Add(new{url,title});
    public void OpenFolder(string path)=>throw new NotSupportedException();
    public Task<string?> PickSavePathAsync(string title,string suggestedName)=>Task.FromResult<string?>(null);
}
