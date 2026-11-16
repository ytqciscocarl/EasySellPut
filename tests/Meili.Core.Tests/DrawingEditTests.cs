using Meili.Core.Settings;

namespace Meili.Core.Tests;

public sealed class DrawingEditTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "meili-drawing-edit-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Edit_delete_and_stale_revisions_preserve_other_drawings()
    {
        var store = new ChartDrawingStore(_path);store.Add("AAPL",new(DrawingKind.HorizontalLine,100));store.Add("AAPL",new(DrawingKind.HorizontalLine,200));
        var stale = new ChartDrawingStore(_path);var revision = store.Revision("AAPL");
        store.Edit("AAPL",0,new(DrawingKind.HorizontalLine,110),revision);
        Assert.Equal(200,store.For("AAPL")[1].Price1);
        Assert.Throws<DataConflictException>(()=>store.Edit("AAPL",1,null,revision));
        Assert.Throws<DataConflictException>(()=>stale.Edit("AAPL",0,null,stale.Revision("AAPL")));
        Assert.Equal(2,stale.For("AAPL").Count);
        store.Edit("AAPL",0,null,store.Revision("AAPL"));
        Assert.Equal(200,Assert.Single(new ChartDrawingStore(_path).For("AAPL")).Price1);
        Assert.Throws<ArgumentOutOfRangeException>(()=>store.Edit("AAPL",1,null,store.Revision("AAPL")));
    }
    [Fact]
    public void Rejects_nonfinite_and_same_time_anchors_before_writing()
    {
        var store=new ChartDrawingStore(_path);var time=DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(()=>store.Add("AAPL",new(DrawingKind.TrendLine,10,time,20,time)));
        Assert.Throws<ArgumentException>(()=>store.Add("AAPL",new(DrawingKind.HorizontalLine,double.NaN)));
        Assert.Empty(store.For("AAPL"));Assert.False(File.Exists(store.FilePath));
    }
    public void Dispose(){if(Directory.Exists(_path))Directory.Delete(_path,true);}
}
