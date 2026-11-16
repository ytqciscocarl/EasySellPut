#!/usr/bin/env python3
"""Actual C# session classification and bundled chart primitives in Chromium; fixture prices only."""
import json, os, pathlib, select, subprocess, unittest
from playwright.sync_api import sync_playwright
ROOT=pathlib.Path(__file__).resolve().parents[2]

class SessionTests(unittest.TestCase):
    def test_sessions_across_panes_themes_zoom_and_daily_view(self):
        process=subprocess.Popen(['dotnet',str(ROOT/'tests/Meili.Workbench.Host/bin/Release/net10.0/Meili.Workbench.Host.dll'),'--chart','--sessions'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd=ROOT)
        try:
            self.assertTrue(select.select([process.stdout],[],[],30)[0])
            url=json.loads(process.stdout.readline())['url']
            with sync_playwright() as browser_tool:
                browser=browser_tool.chromium.launch(headless=True,**({'executable_path':os.environ['MEILI_CHROMIUM']} if os.environ.get('MEILI_CHROMIUM') else {}))
                page=browser.new_page(viewport=dict(width=1320,height=850));errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
                page.goto(url);page.wait_for_function('()=>meiliChart?.inspect().state?.rows.length===192 && meiliChart.inspect().sessionLayers.length===5')
                sessions=page.evaluate('meiliChart.inspect().state.rows.map(r=>r.session)')
                self.assertEqual(sessions.count('PreMarket'),66);self.assertEqual(sessions.count('Regular'),78);self.assertEqual(sessions.count('AfterHours'),48)
                self.assertEqual(page.evaluate('meiliChart.inspect().sessionBands.length'),2)
                self.assertTrue(page.locator('#session-key').is_visible());self.assertIn('盘前',page.locator('#session-key').inner_text())
                self.assertIn('提前收市',page.locator('#session-key').get_attribute('title'))
                page.locator('#session-key summary').focus();page.keyboard.press('Enter');self.assertTrue(page.locator('.session-note').is_visible())
                page.keyboard.press('Enter');self.assertFalse(page.locator('.session-note').is_visible())
                directory=os.environ.get('MEILI_RENDER_DIR')
                def capture(name):
                    if directory:
                        pathlib.Path(directory).mkdir(parents=True,exist_ok=True);page.screenshot(path=str(pathlib.Path(directory)/(name+'.png')))
                page.wait_for_timeout(180);capture('sessions-dark')
                pixels=page.evaluate("""()=>{const {chart,state}=meiliChart.inspect(),context=chart.takeScreenshot().getContext('2d');return [65,101,149].map(index=>[...context.getImageData(Math.round(chart.timeScale().timeToCoordinate(state.rows[index].time)),15,1,1).data])}""")
                self.assertEqual(len({tuple(pixel) for pixel in pixels}),3,pixels)

                # Verify the primitive draws a pane-wide vertical band at actual bar coordinates.
                result=page.evaluate('''()=>{const {sessionLayers,sessionBands,state}=meiliChart.inspect();return sessionLayers.map(layer=>{const fills=[];layer.primitive.paneViews()[0].renderer().draw({useBitmapCoordinateSpace:draw=>draw({horizontalPixelRatio:2,bitmapSize:{width:2400,height:160},context:{set fillStyle(v){this.color=v},fillRect(x,y,w,h){fills.push({x,y,w,h,color:this.color})}}})});return fills})}''')
                self.assertTrue(all(len(fills)==2 and all(f['h']==160 and f['w']>0 for f in fills) for fills in result),result)
                page.mouse.move(600,250);page.mouse.wheel(0,-120);page.wait_for_timeout(160)
                zoomed=page.evaluate("""()=>{const {chart,state}=meiliChart.inspect(),context=chart.takeScreenshot().getContext('2d');return [65,101,149].map(index=>[...context.getImageData(Math.round(chart.timeScale().timeToCoordinate(state.rows[index].time)),15,1,1).data])}""")
                self.assertEqual(zoomed,pixels)
                process.stdin.write('light\nen-US\n');process.stdin.flush()
                page.wait_for_function('()=>!meiliChart.inspect().state.theme.dark && meiliChart.inspect().state.language==="en-US"')
                self.assertIn('Pre',page.locator('#session-key').inner_text());page.locator('#fit').click();page.wait_for_timeout(180);capture('sessions-light')
                self.assertEqual(page.evaluate('meiliChart.inspect().sessionLayers.length'),5)
                page.set_viewport_size(dict(width=480,height=800));page.wait_for_timeout(120)
                self.assertLessEqual(page.locator('body').evaluate('(e)=>e.scrollWidth'),480);capture('sessions-compact')
                page.set_viewport_size(dict(width=1320,height=850));page.wait_for_timeout(100)
                # Series rebuilds must recreate one background layer per remaining pane, not duplicate them.
                page.evaluate('()=>{const s=structuredClone(meiliChart.inspect().state);s.show.rsi=false;meiliChart.render(s)}')
                self.assertEqual(page.evaluate('meiliChart.inspect().sessionLayers.length'),4)
                page.evaluate('()=>{const s=structuredClone(meiliChart.inspect().state);s.intraday=false;meiliChart.render(s)}')
                self.assertEqual(page.evaluate('meiliChart.inspect().sessionBands.length'),0);self.assertFalse(page.locator('#session-key').is_visible())
                self.assertFalse(errors);browser.close()
        finally:
            process.stdin.write('quit\n');process.stdin.flush();process.communicate(timeout=15)
            self.assertEqual(process.returncode,0)

if __name__=='__main__':unittest.main()
