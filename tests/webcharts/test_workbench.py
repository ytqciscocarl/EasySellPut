#!/usr/bin/env python3
"""Actual browser -> private HTTP -> C# strategy model -> rendered payoff, with explicit offline fixture data."""
import json
import os
import pathlib
import select
import subprocess
import unittest
import time
from playwright.sync_api import sync_playwright

ROOT=pathlib.Path(__file__).resolve().parents[2]

class WorkbenchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dll=ROOT/'tests/Meili.Workbench.Host/bin/Release/net10.0/Meili.Workbench.Host.dll'
        cls.process=subprocess.Popen(['dotnet',str(dll)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd=ROOT)
        ready,_,_=select.select([cls.process.stdout],[],[],45)
        if not ready: cls.process.kill();raise RuntimeError('Fixture host did not start')
        line=cls.process.stdout.readline()
        if not line: raise RuntimeError(cls.process.stderr.read())
        cls.url=json.loads(line)['url'];cls.errors=[]
        cls.playwright=sync_playwright().start()
        executable=os.environ.get('MEILI_CHROMIUM')
        cls.browser=cls.playwright.chromium.launch(headless=True,**({'executable_path':executable} if executable else {}))
        cls.page=cls.browser.new_page(viewport=dict(width=1440,height=900));cls.page.on('pageerror',lambda e:cls.errors.append(str(e)))
        cls.page.goto(cls.url);cls.wait(cls.page,'window.meiliWorkbench?.inspect().state?.contracts.length === 18')
    @staticmethod
    def wait(page,expression,arg=None):
        # CDP function calls can inspect the page without weakening its production CSP.
        function=expression if '=>' in expression else '()=>('+expression+')'
        deadline=time.monotonic()+30
        while not page.evaluate(function,arg):
            if time.monotonic()>deadline: raise TimeoutError(expression)
            page.wait_for_timeout(100)
    @classmethod
    def tearDownClass(cls):
        cls.browser.close();cls.playwright.stop()
        cls.process.stdin.write('quit\n');cls.process.stdin.flush();cls.process.communicate(timeout=15)
        if cls.process.returncode: raise RuntimeError('Fixture host exit '+str(cls.process.returncode))
    def setUp(self):
        if self.page.evaluate('meiliWorkbench.inspect().state.legs.length'):
            self.page.locator('#clear').click();self.wait(self.page,'meiliWorkbench.inspect().state.legs.length === 0')
    def command(self,value): self.process.stdin.write(value+'\n');self.process.stdin.flush()
    def test_add_spread_edit_quantity_remove_and_what_if_reach_real_backend(self):
        p=self.page
        p.locator('tr[data-contract="Call100"] button.buy').click();self.wait(p,'meiliWorkbench.inspect().state.legs.length === 1')
        p.locator('tr[data-contract="Call110"] button.sell').click();self.wait(p,'meiliWorkbench.inspect().state.legs.length === 2')
        data=p.evaluate('meiliWorkbench.inspect().state')
        self.assertEqual(data['legs'][0]['premium'],'3');self.assertEqual(data['legs'][1]['premium'],'1.50')
        self.assertEqual(data['curve'][-1]['expiry'],850)
        self.assertEqual(data['curve'][0]['expiry'],-150)
        self.assertEqual(p.locator('svg path[data-series="expiry"]').count(),1)
        qty=p.locator('#legs input').first;qty.fill('2');qty.press('Tab')
        self.wait(p,'meiliWorkbench.inspect().state.legs[0].quantity === "2"')
        self.assertEqual(p.evaluate('meiliWorkbench.inspect().state.legs[0].premium'),'3')
        self.assertEqual(p.locator('#legs tbody tr').first.evaluate('(e)=>e.getAnimations().length'),0)
        span=p.evaluate('meiliWorkbench.inspect().domain.high-meiliWorkbench.inspect().domain.low')
        plot_bounds=p.locator('#payoff').bounding_box();p.mouse.move(plot_bounds['x']+plot_bounds['width']/2,plot_bounds['y']+plot_bounds['height']/2);p.mouse.wheel(0,-300)
        self.assertLess(p.evaluate('meiliWorkbench.inspect().domain.high-meiliWorkbench.inspect().domain.low'),span)
        p.locator('#fit').click()
        previous=p.evaluate('meiliWorkbench.inspect().state.whatIf.price')
        bounds=p.locator('#payoff').bounding_box();p.mouse.move(bounds['x']+bounds['width']*.7,bounds['y']+bounds['height']*.5)
        self.wait(p,'(previous)=>meiliWorkbench.inspect().state.whatIf.price !== previous',arg=previous)
        self.assertTrue(p.evaluate('Number.isFinite(meiliWorkbench.inspect().state.greeks.delta)'))
        p.locator('#legs tbody tr').first.locator('button').click();self.wait(p,'meiliWorkbench.inspect().state.legs.length === 1')
        p.locator('#clear').click();self.wait(p,'meiliWorkbench.inspect().state.legs.length === 0')
        self.assertEqual(p.evaluate('meiliWorkbench.inspect().state.curve.length'),0);self.assertFalse(self.errors)
    def test_filters_freeze_headers_and_retain_provenance(self):
        p=self.page
        p.locator('#right').select_option('Put');self.assertEqual(p.locator('#chain tbody tr').count(),9)
        p.locator('#search').fill('110');self.assertEqual(p.locator('#chain tbody tr').count(),1)
        self.assertEqual(p.locator('#chain tbody tr td').nth(1).inner_text(),'认沽')
        self.assertEqual(p.locator('#chain th').first.evaluate('(e)=>getComputedStyle(e).position'),'sticky')
        self.assertEqual(p.locator('#chain td').first.evaluate('(e)=>getComputedStyle(e).position'),'sticky')
        data=p.evaluate('meiliWorkbench.inspect().state');self.assertEqual(data['source'],'fixture');self.assertEqual(data['delayMinutes'],15)
        self.assertIn('不会下单',p.locator('#draft').inner_text());self.assertTrue(data['asOf'])
        p.locator('#search').fill('');p.locator('#right').select_option('All')
    def test_live_theme_language_and_narrow_layout(self):
        p=self.page
        p.locator('tr[data-contract="Call100"] button.buy').click();self.wait(p,'meiliWorkbench.inspect().state.legs.length === 1')
        for command,expected in [('dark','rgb(31, 30, 29)'),('light','rgb(250, 249, 245)')]:
            self.command(command);self.wait(p,'(dark)=>meiliWorkbench.inspect().state.theme.dark === dark',arg=command=='dark')
            self.assertEqual(p.locator('body').evaluate('(e)=>getComputedStyle(e).backgroundColor'),expected)
            output=os.environ.get('MEILI_RENDER_DIR')
            if output:
                pathlib.Path(output).mkdir(parents=True,exist_ok=True);p.screenshot(path=str(pathlib.Path(output)/('workbench-'+command+'.png')))
        self.command('en-US');self.wait(p,'meiliWorkbench.inspect().state.language === "en-US"')
        self.assertIn('Paper draft',p.locator('#draft').inner_text())
        p.set_viewport_size(dict(width=480,height=900))
        self.assertEqual(p.locator('#workspace').evaluate('(e)=>getComputedStyle(e).gridTemplateColumns').count(' '),0)
        self.assertLessEqual(p.locator('body').evaluate('(e)=>e.scrollWidth'),480)
        p.set_viewport_size(dict(width=1440,height=900));self.command('zh-CN');self.command('dark');self.wait(p,'meiliWorkbench.inspect().state.language === "zh-CN"')
        self.assertFalse(self.errors)

if __name__=='__main__':unittest.main()
