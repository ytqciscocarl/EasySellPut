using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
namespace Meili.App.Tests;
// Local publisher fixture. Never a production data/provider or browser route.
internal sealed class NewsReaderFixture:IDisposable
{
    private readonly TcpListener _server=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    public string Url{get;}
    public NewsReaderFixture(){_server.Start();Url=$"http://127.0.0.1:{((IPEndPoint)_server.LocalEndpoint).Port}/story";_ = Serve();}
    private async Task Serve()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                using var client=await _server.AcceptTcpClientAsync(_stop.Token);using var stream=client.GetStream();var request=new byte[8192];var read=await stream.ReadAsync(request,_stop.Token);if(read==0)continue;
                var html="""
                <!doctype html><html lang="zh"><meta charset="utf-8"><title>本地演示新闻</title><meta name="viewport" content="width=device-width,initial-scale=1"><style>body{margin:0;background:#faf9f5;color:#30302e;font:16px/1.8 system-ui}article{max-width:620px;padding:28px;margin:auto}small{color:#77756f}h1{font-size:28px;line-height:1.4;font-weight:550}hr{border:0;border-top:1px solid #dedbd2}p{margin:20px 0}.tag{color:#b2674c}</style><article><small class="tag">本地演示新闻 · 测试数据</small><h1>在同一个工作区，连接行情与新闻</h1><small>2026 年 11 月 15 日 · 阅读体验演示</small><hr><p>图表留在一侧，原文在另一侧展开。阅读时仍然可以查看价格变化、选择其他新闻，并拖动分隔线调整空间。</p><p>切换新闻会复用当前阅读区域。关闭阅读分屏后，原来的页面恢复完整空间。</p><p>这个页面来自测试进程内的本地 HTTP 服务，仅用于验证原生浏览器、分屏布局和截图，不代表真实新闻或行情。</p></article></html>
                """;
                var body=Encoding.UTF8.GetBytes(html);var header=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(header,_stop.Token);await stream.WriteAsync(body,_stop.Token);
            }
        }
        catch(OperationCanceledException){}catch(SocketException)when(_stop.IsCancellationRequested){}
    }
    public void Dispose(){_stop.Cancel();_server.Stop();_stop.Dispose();}
    // Actual desktop pixels, including child HWND WebView2 surfaces (RenderTargetBitmap cannot capture them).
    public static bool Capture(Window window,string path,bool entireDesktop=false)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        // Hosted Windows ARM images can still have the OOBE privacy screen above the app.
        // Never label an obscuring system desktop as an application screenshot.
        if(GetForegroundWindow()!=hwnd)return false;
        GetWindowRect(hwnd,out var rect);
        if(entireDesktop)rect=new Rect{Left=0,Top=0,Right=GetSystemMetrics(0),Bottom=GetSystemMetrics(1)};
        var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
        var screen=GetDC(IntPtr.Zero);var memory=CreateCompatibleDC(screen);var bitmap=CreateCompatibleBitmap(screen,width,height);var old=SelectObject(memory,bitmap);
        try
        {
            if(!BitBlt(memory,0,0,width,height,screen,rect.Left,rect.Top,0x00CC0020))throw new InvalidOperationException("Native screenshot failed");
            var source=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var output=File.Create(path);encoder.Save(output);return true;
        }
        finally{SelectObject(memory,old);DeleteObject(bitmap);DeleteDC(memory);ReleaseDC(IntPtr.Zero,screen);}
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int w,int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target,int x,int y,int w,int h,IntPtr source,int sx,int sy,int operation);
}
