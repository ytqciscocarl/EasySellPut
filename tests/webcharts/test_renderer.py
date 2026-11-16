#!/usr/bin/env python3
"""Real Chromium renderer regression with explicit deterministic fixture data (no live market claims)."""
import copy
import hashlib
import json
import math
import os
import pathlib
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'src/Meili.Shared/WebCharts'


def fixture():
    rows = []
    for i in range(120):
        close = 100 + i * .1 + math.sin(i / 5)
        rows.append(dict(time=1736173800 + i * 300, index=i, open=close-.2, high=close+.5, low=close-.5, close=close,
                         incomplete=False, volume=1000+i*10, ma=None if i < 19 else close-.4,
                         ema=None if i < 19 else close-.2, bollMiddle=close, bollUpper=close+2, bollLower=close-2,
                         macd=.5, signal=.4, histogram=.1, rsi=55, k=60, d=50, j=80))
    rows[70].update(open=None, high=None, low=None, incomplete=True, k=None, d=None, j=None)
    return dict(schemaVersion=1, key='FIXTURE|Minute5|test|Split', symbol='FIXTURE', preserveView=False, language='en-US', intraday=True,
                rows=rows, compare=[dict(time=r['time'],value=r['close']*1.01) for r in rows], compareLabel='COMPARE',
                show=dict(ma=True,ema=True,boll=True,volume=True,macd=True,rsi=True,kdj=True),
                labels=dict(ma='MA(20)',ema='EMA(20)',boll='BOLL(20,2)',macd='MACD(12,26,9)',rsi='RSI(14)',kdj='KDJ(9,3,3)',volume='Volume',empty='No data'),
                theme=dict(dark=True,control='#32302C',border='#433F3A',accent='#C4B3D4',errorBackground='#3A2A28',errorText='#C4A46A',background='#201E1C',text='#C9C6C1',muted='#A09B92',grid='#C9C6C11F',up='#CC8E8A',down='#6EAA8A',colors=['#7BA8C0','#C9A06A','#BAA8C6','#6AADC0','#C4909E','#B4A072']),
                drawingMode=None,drawings=[dict(kind='HorizontalLine',price1=105,price2=0,time1=None,time2=None),dict(kind='TrendLine',price1=102,price2=110,time1=rows[10]['time'],time2=rows[90]['time'])])


class RendererTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.state = fixture(); cls.version = 1; cls.events = []; cls.errors = []
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_): pass
            def do_GET(self):
                route = self.path.split('?')[0]
                if route == '/state':
                    if self.path.endswith('?after='+str(cls.version)): self.send_response(204); self.end_headers(); return
                    body=json.dumps(dict(version=cls.version,data=cls.state)).encode(); mime='application/json'
                else:
                    file={'/index.html':'index.html','/chart.js':'chart.js','/icons.js':'icons.js','/motion.js':'motion.js','/design-tokens.css':'design-tokens.css','/components.css':'components.css',**{'/fonts/'+name:'../Assets/Fonts/'+name for name in ['SourceSans3-Regular.ttf','SourceSans3-Semibold.ttf','SourceSerif4-Regular.ttf','SourceCodePro-Regular.ttf']},'/library.js':'vendor/lightweight-charts.js'}.get(route)
                    if file is None: self.send_error(404); return
                    body=(ASSETS/file).read_bytes(); mime='font/ttf' if route.endswith('.ttf') else 'text/css' if route.endswith('.css') else 'text/html' if route.endswith('.html') else 'text/javascript'
                self.send_response(200);self.send_header('Content-Type',mime);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
            def do_POST(self):
                cls.events.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))));self.send_response(204);self.end_headers()
        cls.server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
        cls.thread=threading.Thread(target=cls.server.serve_forever,daemon=True);cls.thread.start()
        cls.playwright=sync_playwright().start()
        executable=os.environ.get('MEILI_CHROMIUM')
        cls.browser=cls.playwright.chromium.launch(headless=True,**({'executable_path':executable} if executable else {}))
        cls.page=cls.browser.new_page(viewport=dict(width=1280,height=800),device_scale_factor=1)
        cls.page.on('pageerror',lambda e:cls.errors.append(str(e)))
        cls.requests=[];cls.page.on('request',lambda r:cls.requests.append(r.url))
        cls.origin=f'http://127.0.0.1:{cls.server.server_port}'
        cls.page.goto(cls.origin+'/index.html')
        cls.page.wait_for_function('window.meiliChart?.inspect().state?.rows.length === 120 || document.getElementById("error").style.display === "block"')
        assert cls.page.locator('#error').inner_text() == '', cls.events[-2:]
    @classmethod
    def tearDownClass(cls):
        cls.browser.close();cls.playwright.stop();cls.server.shutdown();cls.server.server_close()
    def test_pinned_icons_keep_canvas_and_native_svg_primitives(self):
        manifest=json.loads((ROOT/'src/Meili.Shared/Assets/NavigationIcons/provenance.json').read_text())
        self.assertIn(manifest['version'],(ASSETS/'icons.js').read_text())
        import importlib.util
        spec=importlib.util.spec_from_file_location('icon_importer',ROOT/'build/import-navigation-icons.py')
        importer=importlib.util.module_from_spec(spec);spec.loader.exec_module(importer)
        result=self.page.evaluate("""names=>names.map(name=>{
            const e=meiliIcons.svg(name,18);document.body.append(e);const b=e.getBBox(),r=e.getBoundingClientRect();
            const result={name,box:[b.x,b.y,b.width,b.height],frame:[r.width,r.height],stroke:e.getAttribute('stroke-width'),view:e.getAttribute('viewBox')};e.remove();return result;
        })""",list(importer.MAP))
        for icon in result:
            self.assertEqual(icon['frame'],[18,18],icon['name']);self.assertEqual(icon['view'],'0 0 24 24');self.assertEqual(icon['stroke'],'2')
            x,y,w,h=icon['box'];self.assertGreaterEqual(x,-.1);self.assertGreaterEqual(y,-.1);self.assertLessEqual(x+w,24.1);self.assertLessEqual(y+h,24.1)
        self.assertEqual(self.page.locator('#right-tools svg[data-lucide]').count(),3)
        self.assertEqual(self.page.locator('#right-tools button > span').count(),3)

    def test_full_chart_indicators_gaps_and_attribution(self):
        p=self.page
        result=p.evaluate('''() => {const {chart,series}=meiliChart.inspect();return {panes:chart.panes().length,candles:series.candles.data().filter(p=>Number.isFinite(p.open)).length,gap:series.candles.data().find(p=>p.time===meiliChart.inspect().state.rows[70].time)??null,incomplete:series.incomplete.data().find(p=>p.time===meiliChart.inspect().state.rows[70].time),warmup:series.ma.data().find(p=>p.time===meiliChart.inspect().state.rows[0].time)??null,ma:series.ma.data().at(-1).value,rsi:series.rsi.data().at(-1).value};}''')
        self.assertEqual(result['panes'],5);self.assertEqual(result['candles'],119)
        self.assertTrue(all(height > 60 for height in p.evaluate('meiliChart.inspect().chart.panes().map(p=>p.getHeight())')), 'Indicator panes collapsed after autoSize')
        self.assertIsNone(result['gap']);self.assertIn('value',result['incomplete']);self.assertIsNone(result['warmup'])
        self.assertAlmostEqual(result['ma'],fixture()['rows'][-1]['ma']);self.assertEqual(result['rsi'],55)
        self.assertTrue(p.locator('#attribution').is_visible());p.locator('#attribution').click()
        p.wait_for_timeout(150);self.assertTrue(any(e['kind']=='attribution' for e in self.events))
        self.assertFalse(self.errors);self.assertTrue(all(url.startswith(self.origin+'/') for url in self.requests))
    def test_interaction_theme_refresh_and_panes(self):
        p=self.page
        p.mouse.move(600,250);p.mouse.wheel(0,-500);p.wait_for_timeout(250)
        before=p.evaluate('meiliChart.inspect().chart.timeScale().getVisibleLogicalRange()')
        next_state=copy.deepcopy(self.state);next_state['preserveView']=True;next_state['theme'].update(dark=False,muted='#5F594F',grid='#2F2B261F',colors=['#1C648E','#8E4E00','#5E3F8A','#0C6262','#8E3F68','#6A4E10'],control='#FBF7F0',border='#E0D6C8',accent='#6A458E',errorBackground='#F4E4DC',errorText='#7A5500',background='#F6F0E3',text='#2F2B26',up='#1A7050',down='#A83E38');next_state['language']='zh-CN'
        p.evaluate('(s)=>meiliChart.render(s)',next_state)
        after=p.evaluate('meiliChart.inspect().chart.timeScale().getVisibleLogicalRange()')
        self.assertAlmostEqual(before['from'],after['from'],places=5);self.assertAlmostEqual(before['to'],after['to'],places=5)
        self.assertEqual(p.evaluate('meiliChart.inspect().series.candles.options().upColor'),'#1A7050')
        self.assertEqual(p.locator('#fit').inner_text(),'适应')
        next_state['show']['rsi']=False;p.evaluate('(s)=>meiliChart.render(s)',next_state)
        self.assertEqual(p.evaluate('meiliChart.inspect().chart.panes().length'),4)
        next_state['drawingMode']='HorizontalLine';p.evaluate('(s)=>meiliChart.render(s)',next_state)
        p.mouse.move(650,200);p.wait_for_timeout(150);p.mouse.click(650,200);p.wait_for_timeout(250)
        self.assertTrue(any(e['kind']=='drawing' and math.isfinite(e['price']) for e in self.events))
        self.assertTrue(any(e['kind']=='crosshair' and e['index']>=0 for e in self.events))
        p.locator('#fit').click();self.assertFalse(self.errors)
        output=os.environ.get('MEILI_RENDER_DIR')
        if output: pathlib.Path(output).mkdir(parents=True,exist_ok=True);p.screenshot(path=str(pathlib.Path(output)/'tradingview-webchart.png'))
        p.evaluate('(s)=>meiliChart.render(s)',fixture())
    def test_close_only_and_empty_data_do_not_invent_candles(self):
        p=self.page
        data=fixture()
        for row in data['rows']: row.update(open=None,high=None,low=None,incomplete=True)
        p.evaluate('(s)=>meiliChart.render(s)',data)
        self.assertEqual(p.evaluate('meiliChart.inspect().series.candles.data().length'),0)
        self.assertTrue(p.evaluate('meiliChart.inspect().series.incomplete.options().lineVisible'))
        data['rows']=[];data['compare']=[];data['drawings']=[]
        p.evaluate('(s)=>meiliChart.render(s)',data)
        self.assertEqual(p.locator('#empty').inner_text(),'No data')
        self.assertFalse(self.errors)
        p.evaluate('(s)=>meiliChart.render(s)',fixture())
    def test_theme_tokens_keyboard_focus_and_compact_attribution_icon(self):
        p=self.page
        for dark in [True,False]:
            data=fixture()
            if not dark: data['theme'].update(dark=False,muted='#5F594F',grid='#2F2B261F',colors=['#1C648E','#8E4E00','#5E3F8A','#0C6262','#8E3F68','#6A4E10'],control='#FBF7F0',border='#E0D6C8',accent='#6A458E',errorBackground='#F4E4DC',errorText='#7A5500',background='#F6F0E3',text='#2F2B26',up='#A83E38',down='#1A7050')
            p.evaluate('(s)=>meiliChart.render(s)',data)
            p.mouse.move(1,1);p.wait_for_timeout(150)
            style=p.locator('#fit').evaluate('(e)=>{const s=getComputedStyle(e);return {background:s.backgroundColor,radius:s.borderRadius,size:s.fontSize,height:e.getBoundingClientRect().height}}')
            self.assertEqual(style['background'],'rgb(50, 48, 44)' if dark else 'rgb(251, 247, 240)')
            self.assertEqual(p.evaluate('meiliChart.inspect().chart.options().layout.panes.separatorColor'),data['theme']['border'])
            self.assertEqual(p.evaluate('meiliChart.inspect().chart.options().crosshair.vertLine.labelBackgroundColor'),data['theme']['accent'])
            self.assertEqual(style['radius'],'8px');self.assertEqual(style['size'],'14px');self.assertGreaterEqual(style['height'],32)
            p.keyboard.press('Tab');p.locator('#fit').focus()
            self.assertEqual(p.locator('#fit').evaluate('(e)=>getComputedStyle(e).outlineWidth'),'2px')
            self.assertEqual(p.evaluate('getComputedStyle(document.documentElement).colorScheme'),'dark' if dark else 'light')
            output=os.environ.get('MEILI_RENDER_DIR')
            if output: p.screenshot(path=str(pathlib.Path(output)/('webchart-unified-dark.png' if dark else 'webchart-unified-light.png')))
        self.assertEqual(p.locator('footer').count(),0)
        self.assertEqual(p.locator('#attribution').inner_text(),'')
        self.assertIn('TradingView',p.locator('#attribution').get_attribute('title'))
        p.locator('#attribution').focus()
        self.assertEqual(p.locator('#attribution').evaluate('(e)=>getComputedStyle(e).outlineWidth'),'2px')
        p.set_viewport_size(dict(width=480,height=800))
        self.assertTrue(p.locator('#attribution').is_visible())
        bounds=p.locator('#attribution').bounding_box();self.assertLessEqual(bounds['x']+bounds['width'],480)
        self.assertLessEqual(bounds['width'],32)
        self.assertLessEqual(p.locator('#toolbar').evaluate('(e)=>e.scrollWidth'),480)
        p.set_viewport_size(dict(width=1280,height=800));p.evaluate('(s)=>meiliChart.render(s)',fixture())
    def test_vendor_integrity(self):
        provenance=json.loads((ASSETS/'vendor/provenance.json').read_text())
        self.assertEqual(hashlib.sha256((ASSETS/'vendor/lightweight-charts.js').read_bytes()).hexdigest(),provenance['sha256'])
        self.assertIn('Apache License',(ASSETS/'vendor/LICENSE').read_text())

if __name__ == '__main__': unittest.main()
