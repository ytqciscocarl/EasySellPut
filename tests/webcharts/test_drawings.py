#!/usr/bin/env python3
"""Real pointer gestures -> private C# channel -> persistent drawing store."""
import json
import os
import pathlib
import select
import subprocess
import unittest
from playwright.sync_api import sync_playwright

ROOT = pathlib.Path(__file__).resolve().parents[2]

class DrawingTests(unittest.TestCase):
    def test_preview_cancel_pane_guards_edit_and_single_delete(self):
        process = subprocess.Popen(['dotnet',str(ROOT/'tests/Meili.Workbench.Host/bin/Release/net10.0/Meili.Workbench.Host.dll'),'--chart'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd=ROOT)
        try:
            ready,_,_=select.select([process.stdout],[],[],45)
            self.assertTrue(ready)
            url=json.loads(process.stdout.readline())['url']
            with sync_playwright() as pw:
                browser=pw.chromium.launch(headless=True,**({'executable_path':os.environ['MEILI_CHROMIUM']} if os.environ.get('MEILI_CHROMIUM') else {}))
                p=browser.new_page(viewport=dict(width=1320,height=850));errors=[];p.on('pageerror',lambda e:errors.append(str(e)))
                requests=[];p.on('request',lambda req:requests.append(req.post_data) if req.method=='POST' and req.post_data and json.loads(req.post_data).get('kind','').startswith('drawing') else None)
                p.goto(url);p.wait_for_function('()=>window.meiliChart?.inspect().state?.rows.length===120')
                def wait(expression):
                    try:p.wait_for_function('()=>('+expression+')',timeout=10000)
                    except Exception:
                        print('STATE',p.evaluate('(()=>{const s=meiliChart.inspect().state;return {mode:s.drawingMode,start:s.drawingStart,drawings:s.drawings}})()'),'ERRORS',errors,'REQUESTS',requests)
                        raise
                def mode(command,kind):
                    process.stdin.write(command+'\n');process.stdin.flush();wait('meiliChart.inspect().state.drawingMode==='+json.dumps(kind))
                def point(index,price):
                    return p.evaluate('''([index,price])=>{const {chart,series,state}=meiliChart.inspect();return {x:chart.timeScale().timeToCoordinate(state.rows[index].time),y:series.candles.priceToCoordinate(price)}}''',[index,price])
                def move(index,price,click=False):
                    at=point(index,price);bounds=p.locator('#chart').bounding_box();p.mouse.move(bounds['x']+at['x'],bounds['y']+at['y'])
                    if click:p.mouse.click(bounds['x']+at['x'],bounds['y']+at['y'])
                def capture(name):
                    if directory:=os.environ.get('MEILI_RENDER_DIR'):
                        pathlib.Path(directory).mkdir(parents=True,exist_ok=True);p.screenshot(path=str(pathlib.Path(directory)/(name+'.png')))
                p.locator('#fit').click();p.wait_for_timeout(100)
                mode('draw-horizontal','HorizontalLine');move(30,140);p.wait_for_timeout(100)
                self.assertEqual(p.locator('#drawing-overlay line').count(),1)
                self.assertFalse(p.evaluate('meiliChart.inspect().chart.options().handleScroll.pressedMouseMove'))
                # A click on volume must never turn its pane-relative y into a stock price.
                bounds=p.locator('#chart').bounding_box();height=p.evaluate('meiliChart.inspect().chart.panes()[0].getHeight()')
                p.mouse.click(bounds['x']+200,bounds['y']+height+20);p.wait_for_timeout(350)
                self.assertEqual(p.evaluate('meiliChart.inspect().state.drawings.length'),0)
                p.keyboard.press('Escape');wait('meiliChart.inspect().state.drawingMode===null');p.wait_for_timeout(300)
                self.assertTrue(p.evaluate('meiliChart.inspect().chart.options().handleScroll.pressedMouseMove'))
                mode('draw-trend','TrendLine');move(30,140,True);wait('!!meiliChart.inspect().state.drawingStart')
                move(75,180);p.wait_for_timeout(120)
                self.assertEqual(p.locator('#drawing-overlay circle').count(),2);capture('drawing-trend-preview-dark')
                # First point survives live re-render without creating a hidden/incomplete stored line.
                p.evaluate('meiliChart.render(structuredClone(meiliChart.inspect().state))');move(30,145,True);p.wait_for_timeout(100)
                self.assertEqual(p.evaluate('meiliChart.inspect().state.drawings.length'),0)
                self.assertIn('另一根',p.locator('#drawing-hint').inner_text())
                p.keyboard.press('Escape');wait('!meiliChart.inspect().state.drawingMode');p.wait_for_timeout(300);self.assertEqual(p.locator('#drawing-overlay circle').count(),0)
                mode('draw-trend','TrendLine');move(30,140,True);wait('!!meiliChart.inspect().state.drawingStart');move(75,180,True)
                wait('meiliChart.inspect().state.drawings.length===1 && !meiliChart.inspect().state.drawingMode')
                mode('draw-horizontal','HorizontalLine');move(90,250,True);wait('meiliChart.inspect().state.drawings.length===2')
                bounds=p.locator('#chart').bounding_box();height=p.evaluate('meiliChart.inspect().chart.panes()[0].getHeight()')
                p.mouse.click(bounds['x']+200,bounds['y']+height+point(90,250)['y']);p.wait_for_timeout(80)
                self.assertFalse(p.locator('#drawing-panel').is_visible())
                # Select near the segment midpoint, edit exactly, then delete only that line.
                def select_trend():
                    a=point(30,p.evaluate('meiliChart.inspect().state.drawings[0].price1'));b=point(75,180);bounds=p.locator('#chart').bounding_box()
                    p.mouse.click(bounds['x']+(a['x']+b['x'])/2,bounds['y']+(a['y']+b['y'])/2)
                    self.assertTrue(p.locator('#drawing-panel').is_visible())
                    return bounds['x']+(a['x']+b['x'])/2,bounds['y']+(a['y']+b['y'])/2
                target=select_trend();p.keyboard.press('Escape');p.mouse.dblclick(*target,delay=20);self.assertTrue(p.locator('#drawing-panel').is_visible())
                p.locator('#drawing-first').fill('150');capture('drawing-selected-dark');p.locator('#drawing-save').click()
                wait('meiliChart.inspect().state.drawings[0].price1===150')
                process.stdin.write('light\nen-US\n');process.stdin.flush();wait('!meiliChart.inspect().state.theme.dark && meiliChart.inspect().state.language==="en-US"')
                select_trend();self.assertEqual(p.locator('#drawing-save').inner_text(),'Save');capture('drawing-selected-light')
                p.locator('#drawing-delete').click();wait('meiliChart.inspect().state.drawings.length===1')
                self.assertEqual(p.evaluate('meiliChart.inspect().state.drawings[0].kind'),'HorizontalLine')
                p.locator('#chart').focus();p.keyboard.press('ArrowDown');self.assertTrue(p.locator('#drawing-panel').is_visible())
                p.keyboard.press('Enter');self.assertTrue(p.locator('#drawing-first').evaluate('(e)=>e===document.activeElement'))
                p.locator('#drawing-first').fill('196');p.keyboard.press('Enter');wait('meiliChart.inspect().state.drawings[0].price1===196')
                p.set_viewport_size(dict(width=480,height=800));p.locator('#chart').focus();p.keyboard.press('ArrowDown');p.wait_for_timeout(100)
                self.assertLessEqual(p.locator('body').evaluate('(e)=>e.scrollWidth'),480)
                panel=p.locator('#drawing-panel').bounding_box();self.assertGreaterEqual(panel['x'],0);self.assertLessEqual(panel['x']+panel['width'],480)
                capture('drawing-selected-compact');p.locator('#chart').focus();p.keyboard.press('Delete');wait('meiliChart.inspect().state.drawings.length===0')
                self.assertFalse(errors);browser.close()
        finally:
            process.stdin.write('quit\n');process.stdin.flush();process.communicate(timeout=15)
            self.assertEqual(process.returncode,0)

if __name__=='__main__':unittest.main()
