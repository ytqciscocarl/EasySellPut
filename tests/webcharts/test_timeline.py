#!/usr/bin/env python3
"""Real Chromium -> production private C# channel and timeline, with test-only providers."""
import json
import os
import pathlib
import select
import subprocess
import time
import unittest
from playwright.sync_api import sync_playwright

ROOT=pathlib.Path(__file__).resolve().parents[2]

class TimelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dll=ROOT/'tests/Meili.Workbench.Host/bin/Release/net10.0/Meili.Workbench.Host.dll'
        cls.process=subprocess.Popen(['dotnet',str(dll),'--chart'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd=ROOT)
        ready,_,_=select.select([cls.process.stdout],[],[],45)
        if not ready: cls.process.kill();raise RuntimeError('Timeline fixture host did not start')
        line=cls.process.stdout.readline()
        if not line: raise RuntimeError(cls.process.stderr.read())
        cls.url=json.loads(line)['url'];cls.errors=[]
        cls.playwright=sync_playwright().start()
        executable=os.environ.get('MEILI_CHROMIUM')
        cls.browser=cls.playwright.chromium.launch(headless=True,**({'executable_path':executable} if executable else {}))
        cls.page=cls.browser.new_page(viewport=dict(width=1280,height=900));cls.page.on('pageerror',lambda e:cls.errors.append(str(e)))
        cls.page.goto(cls.url);cls.wait('window.meiliChart?.inspect().state?.events.length === 4')
    @classmethod
    def wait(cls,expression):
        deadline=time.monotonic()+30
        while not cls.page.evaluate('()=>('+expression+')'):
            if time.monotonic()>deadline: raise TimeoutError(expression)
            cls.page.wait_for_timeout(100)
    @classmethod
    def tearDownClass(cls):
        cls.browser.close();cls.playwright.stop();cls.process.stdin.write('quit\n');cls.process.stdin.flush();cls.process.communicate(timeout=15)
        if cls.process.returncode: raise RuntimeError('Timeline host exit '+str(cls.process.returncode))
    def capture(self,name):
        output=os.environ.get('MEILI_RENDER_DIR')
        if output:
            pathlib.Path(output).mkdir(parents=True,exist_ok=True)
            self.page.screenshot(path=str(pathlib.Path(output)/(name+'.png')))
            if name.startswith('news-history'):
                self.page.locator('#timeline').screenshot(path=str(pathlib.Path(output)/(name+'-panel.png')))
    def test_aggregated_marker_opens_all_28_events_with_matching_filters(self):
        p=self.page
        original=p.evaluate('structuredClone(meiliChart.inspect().state)')
        try:
            p.set_viewport_size(dict(width=1440,height=1000))
            p.evaluate("""()=>{const s=structuredClone(meiliChart.inspect().state),template=s.events.find(x=>x.event.type==='news'&&x.time);
                s.key+='|group-repro';s.events=Array.from({length:28},(_,n)=>({...template,event:{...template.event,id:'bucket-'+n,title:'聚合新闻 '+String(n+1).padStart(2,'0')+' · 测试数据',published:new Date(Date.UTC(2025,0,6,20)-n*60000).toISOString()}}));
                s.events.push({...template,time:s.rows.at(-1).time,event:{...template.event,id:'outside-bucket',title:'另一根 K 线的新闻 · 测试数据'}});meiliChart.render(s);}""")
            def click_bucket():
                p.locator('#fit').click();p.wait_for_timeout(200)
                point=p.evaluate("""()=>{const {chart,series,state}=meiliChart.inspect();return {x:chart.timeScale().timeToCoordinate(state.events[0].time),y:series.candles.priceToCoordinate(118.5)}}""")
                bounds=p.locator('#chart').bounding_box()
                p.mouse.move(bounds['x']+point['x'],bounds['y']+point['y']);p.wait_for_timeout(100)
                self.assertIn('28',p.locator('.tooltip-heading').inner_text())
                p.mouse.click(bounds['x']+point['x'],bounds['y']+point['y']);p.wait_for_timeout(100)
            click_bucket()
            self.assertIn('28',p.locator('#event-group-label').inner_text())
            self.assertEqual(p.locator('#event-list [data-event-id]').count(),28)
            self.assertFalse(p.locator('#event-detail').is_visible())
            self.assertEqual(p.locator('[data-event-id="outside-bucket"]').count(),0)
            self.capture('event-group-28-dark')
            p.locator('[data-event-id="bucket-27"]').click()
            self.assertIn('聚合新闻 28',p.locator('#event-detail').inner_text())
            self.assertEqual(p.locator('#event-list [data-event-id]').count(),28)
            before=p.locator('#event-list').evaluate('(e)=>e.scrollTop')
            p.evaluate('meiliChart.render(structuredClone(meiliChart.inspect().state))')
            self.assertAlmostEqual(p.locator('#event-list').evaluate('(e)=>e.scrollTop'),before,delta=2)
            p.locator('#event-close').click()
            self.assertEqual(p.locator('#event-list [data-event-id]').count(),28)
            p.evaluate("""()=>{const s=structuredClone(meiliChart.inspect().state);s.events.push({...s.events[0],event:{...s.events[0].event,id:'mixed-earnings',type:'earnings'}});meiliChart.render(s);}""")
            self.assertIn('29',p.locator('#event-group-label').inner_text())
            p.locator('input[data-type="news"]').uncheck()
            self.assertEqual(p.locator('#event-list [data-event-id]').count(),1)
            self.assertIn('1',p.locator('#event-group-label').inner_text())
            p.locator('input[data-type="news"]').check()
            p.locator('#event-group-clear').click()
            self.assertFalse(p.locator('#event-group').is_visible())
            self.assertEqual(p.locator('#event-list [data-event-id]').count(),30)
        finally:
            p.locator('input[data-type="news"]').check()
            p.evaluate('(s)=>meiliChart.render(s)',original)

    def test_local_detail_motion_reduced_motion_and_refresh_focus(self):
        p=self.page
        p.emulate_media(reduced_motion='no-preference')
        p.evaluate("document.documentElement.dataset.reducedMotion='false'")
        p.locator('#event-list button[data-event-id]').first.click()
        self.assertTrue(p.locator('#event-detail').is_visible())
        p.locator('#event-close').focus()
        p.evaluate('meiliChart.render(structuredClone(meiliChart.inspect().state))')
        self.assertEqual(p.evaluate('document.activeElement.id'),'event-close')
        p.evaluate("meiliMotion.enter(document.querySelector('#event-detail'))")
        self.assertTrue(p.locator('#event-detail').evaluate('(e)=>e.getAnimations().length>0'))
        p.evaluate("document.documentElement.dataset.reducedMotion='true'")
        self.wait("document.querySelector('#event-detail').getAnimations().length === 0")
        p.evaluate("meiliMotion.enter(document.querySelector('#event-detail'))")
        self.assertEqual(p.locator('#event-detail').evaluate('(e)=>e.getAnimations().length'),0)
        self.assertEqual(p.locator('canvas').first.evaluate('(e)=>e.getAnimations().length'),0)
        self.capture('ux-event-detail-reduced')
        p.locator('#event-close').click();p.evaluate("document.documentElement.dataset.reducedMotion='false'")

    def test_history_groups_original_dates_scroll_navigation_and_live_updates(self):
        p=self.page
        original=p.evaluate('structuredClone(meiliChart.inspect().state)')
        try:
            p.set_viewport_size(dict(width=1280,height=900))
            p.evaluate("""()=>{const s=structuredClone(meiliChart.inspect().state),template=s.events.find(e=>e.event.type==='news');
                s.events=[];
                for(let day=0;day<12;day++)for(let n=0;n<3;n++){
                    const published=new Date(Date.UTC(2026,10,14-day,20-n)).toISOString();
                    s.events.push({...template,event:{...template.event,id:`history-${day}-${n}`,published,date:'2099-01-01',title:['市场焦点：科技股盘中走势与资金流向','公司动态：最新业绩展望及分析师观点','行业观察：半导体板块与大盘表现'][n]+' · 测试新闻',publisher:'Fixture publisher'}});
                }
                // 02:00 UTC is the previous New York calendar day, not the UTC day or aligned candle.
                s.events[0].event.published='2026-11-15T02:00:00Z';
                const calendar=s.events[0];
                s.events.push({...calendar,event:{...calendar.event,id:'undated',published:null,date:null,title:'未提供发布时间 · 测试新闻'}});
                s.events.push({...calendar,event:{...calendar.event,id:'date-range',type:'earnings',published:null,date:'2026-12-31',endDate:'2027-01-03',precision:'date_range',confirmation:'estimated'}});
                meiliChart.render(s);}""")
            p.wait_for_timeout(100)
            self.assertEqual(p.locator('.event-day').count(),14)
            self.assertEqual(p.locator('#event-history button').count(),14)
            self.assertEqual(p.locator('.event-day[data-day="2026-11-14"] [data-event-id]').count(),3)
            self.assertEqual(p.locator('.event-day[data-day="2026-11-14"] [data-event-id]').first.get_attribute('data-event-id'),'history-0-0')
            self.assertIn('21:00 ET',p.locator('[data-event-id="history-0-0"]').inner_text())
            self.assertIn('2027-01-03',p.locator('[data-event-id="date-range"]').inner_text())
            self.assertEqual(p.locator('.event-day').last.get_attribute('data-day'),'')
            self.capture('news-history-dark')
            target=p.locator('#event-history button[data-day="2026-11-08"]')
            target.click();p.wait_for_timeout(100)
            self.assertEqual(p.locator('#event-history [aria-current]').get_attribute('data-day'),'2026-11-08')
            self.assertGreater(p.locator('#event-list').evaluate('(e)=>e.scrollTop'),500)
            # The latest quote/theme payload must not reset the user's reading position.
            before=p.locator('#event-list').evaluate('(e)=>e.scrollTop')
            p.evaluate('meiliChart.render(structuredClone(meiliChart.inspect().state))');p.wait_for_timeout(80)
            self.assertAlmostEqual(p.locator('#event-list').evaluate('(e)=>e.scrollTop'),before,delta=2)
            p.locator('#event-history button[data-day="2026-11-08"]').focus()
            p.keyboard.press('ArrowDown');p.wait_for_timeout(80)
            self.assertEqual(p.locator('#event-history [aria-current]').get_attribute('data-day'),'2026-11-07')
            self.capture('news-history-navigated-dark')
            p.locator('#event-list').evaluate('(e)=>e.scrollTop=0');p.wait_for_timeout(80)
            self.assertEqual(p.locator('#event-history [aria-current]').get_attribute('data-day'),'2026-12-31')
            p.locator('input[data-type="news"]').uncheck()
            self.assertEqual(p.locator('#event-history button').count(),1)
            p.locator('input[data-type="earnings"]').uncheck();p.locator('input[data-type="ex_dividend"]').uncheck()
            self.assertFalse(p.locator('#event-history').is_visible())
            for kind in ['news','earnings','ex_dividend']:p.locator('input[data-type="'+kind+'"]').check()
            # Change locale/theme using real host settings while retaining the fixture timeline payload.
            history_state=p.evaluate('structuredClone(meiliChart.inspect().state)')
            self.process.stdin.write('light\nen-US\n');self.process.stdin.flush()
            self.wait('meiliChart.inspect().state.language === "en-US" && !meiliChart.inspect().state.theme.dark')
            p.evaluate('(s)=>{s.language="en-US";s.theme=meiliChart.inspect().state.theme;meiliChart.render(s)}',history_state)
            p.wait_for_timeout(80)
            rows=p.locator('#event-filters label').evaluate_all('(nodes)=>nodes.map(n=>n.getBoundingClientRect().top)')
            self.assertLessEqual(max(rows)-min(rows),1)
            self.capture('news-history-light')
            self.assertEqual(p.locator('#event-history').get_attribute('aria-label'),'History by date')
            p.set_viewport_size(dict(width=480,height=800));p.wait_for_timeout(100)
            self.assertLessEqual(p.locator('body').evaluate('(e)=>e.scrollWidth'),480)
            self.assertTrue(p.locator('#attribution').is_visible())
            p.locator('#event-history button').nth(6).click();p.wait_for_timeout(80)
            self.assertEqual(p.locator('#event-history [aria-current]').get_attribute('data-day'),p.locator('#event-history button').nth(6).get_attribute('data-day'))
            self.capture('news-history-compact')
            self.assertFalse(self.errors)
        finally:
            p.evaluate('(s)=>meiliChart.render(s)',original)
            self.process.stdin.write('dark\nzh-CN\n');self.process.stdin.flush()
            self.wait('meiliChart.inspect().state.language === "zh-CN" && meiliChart.inspect().state.theme.dark')
            p.set_viewport_size(dict(width=1280,height=900))

    def test_timeline_filters_marker_click_metadata_reader_theme_and_compact_layout(self):
        p=self.page
        self.assertEqual(p.evaluate('meiliChart.inspect().eventMarkers.markers().length'),2)
        self.assertEqual(p.evaluate('meiliChart.inspect().series.candles.data().length'),120)
        self.assertEqual(p.locator('#timeline').evaluate('(e)=>e.tagName'),'ASIDE')
        p.locator('#event-heading').click()
        self.assertEqual(p.locator('#event-list button').count(),4)
        chart_bounds=p.locator('#chart').bounding_box()
        event_bounds=p.locator('#timeline').bounding_box()
        self.assertGreater(chart_bounds['height'],700)
        self.assertGreater(p.evaluate('meiliChart.inspect().chart.panes()[0].getHeight()'),2*p.evaluate('meiliChart.inspect().chart.panes()[1].getHeight()'))
        self.assertGreaterEqual(event_bounds['x'],chart_bounds['x']+chart_bounds['width']-1)
        self.assertLessEqual(event_bounds['width'],341)
        self.assertFalse(p.locator('#event-detail').is_visible())
        self.assertFalse(p.locator('#event-status').is_visible())
        self.assertFalse(p.locator('#event-scope').is_visible())
        self.assertIn('预计',p.locator('#event-list button',has_text='财报').inner_text())
        p.locator('[data-view="news"]').click()
        self.assertEqual(p.locator('#event-list button').count(),2)
        self.assertEqual(p.locator('[data-view="news"]').get_attribute('aria-pressed'),'true')
        p.locator('[data-view="calendar"]').focus();p.keyboard.press('Enter')
        self.assertEqual(p.locator('#event-list button').count(),2)
        self.assertEqual(p.locator('#event-heading').inner_text(),'日历')
        p.locator('[data-view="all"]').click()
        self.capture('timeline-default-dark')
        # Fit after opening the sidebar so the fixture's early news bar is visible.
        p.locator('#fit').click();p.wait_for_timeout(200)
        # Hover the real chart column: tooltip includes original time, title and provider.
        point=p.evaluate("""()=>{const {chart,series,state}=meiliChart.inspect();const item=state.events.find(e=>e.event.type==='news'&&e.time);return {x:chart.timeScale().timeToCoordinate(item.time),y:series.candles.priceToCoordinate(118.5)}}""")
        bounds=p.locator('#chart').bounding_box()
        p.mouse.move(bounds['x']+point['x'],bounds['y']+point['y']);p.wait_for_timeout(200)
        tooltip=p.locator('#event-tooltip')
        self.assertTrue(tooltip.is_visible())
        self.assertIn('新闻',tooltip.inner_text());self.assertIn('Fixture news <script>literal</script>',tooltip.inner_text())
        self.assertIn('ET',tooltip.inner_text());self.assertIn('Test publisher',tooltip.inner_text())
        self.assertEqual(tooltip.locator('script').count(),0)
        self.capture('event-tooltip-dark')
        p.keyboard.press('Escape');self.assertFalse(tooltip.is_visible())
        # Same-bar events retain their types and count, rather than a bare unexplained number.
        p.evaluate("""()=>{const s=structuredClone(meiliChart.inspect().state);window.originalTimelineState=structuredClone(s);const item=s.events.find(e=>e.event.type==='news'&&e.time);s.events=[...s.events,{...item,event:{...item.event,id:'hover-extra',type:'earnings',title:'Same-bar earnings',confirmation:'estimated'}}];meiliChart.render(s)}""")
        marker=p.evaluate("meiliChart.inspect().eventMarkers.markers().find(m=>m.text.includes('2'))")
        self.assertIn('新闻',marker['text']);self.assertIn('财报',marker['text'])
        p.mouse.move(bounds['x']+point['x']+2,bounds['y']+point['y']);p.wait_for_timeout(150)
        self.assertIn('2 项事件',tooltip.inner_text());self.assertIn('预计',tooltip.inner_text())
        self.capture('event-tooltip-grouped-dark')
        p.evaluate('meiliChart.render(window.originalTimelineState)')
        p.mouse.move(10,10);self.assertFalse(tooltip.is_visible())

        p.locator('#event-info-heading').click()
        self.assertIn('历史',p.locator('#event-scope').inner_text())
        p.locator('#event-info-heading').click()
        p.locator('#event-list button',has_text='Fixture news').click()
        self.assertIn('<script>literal</script>',p.locator('#event-detail').inner_text())
        self.assertIn('Test publisher',p.locator('#event-detail').inner_text())
        self.capture('timeline-selected-dark')
        self.assertFalse(p.locator('.event-metadata p').first.is_visible())
        p.locator('.event-metadata summary').click()
        self.assertIn('本次读取',p.locator('.event-metadata').inner_text())
        p.locator('.event-metadata summary').click()
        p.locator('#event-read').click();p.wait_for_timeout(300)
        self.process.stdin.write('opened\n');self.process.stdin.flush()
        ready,_,_=select.select([self.process.stdout],[],[],10);self.assertTrue(ready)
        opened=json.loads(self.process.stdout.readline());self.assertEqual(opened,[dict(url='https://example.com/news',title='Fixture news <script>literal</script>')])
        p.locator('#event-close').click()
        self.assertFalse(p.locator('#event-detail').is_visible())
        self.assertEqual(p.locator('#event-list button:focus').count(),1)
        p.locator('#event-list button:focus').press('Enter')
        self.assertTrue(p.locator('#event-detail').is_visible())
        p.locator('input[data-type="news"]').uncheck()
        self.assertNotIn('active',p.locator('#event-filters label').filter(has=p.locator('input[data-type="news"]')).get_attribute('class') or '')
        p.locator('input[data-type="news"]').focus();p.keyboard.press('Space')
        self.assertTrue(p.locator('input[data-type="news"]').is_checked())
        self.assertIn('active',p.locator('#event-filters label').filter(has=p.locator('input[data-type="news"]')).get_attribute('class'))
        p.keyboard.press('Space');self.assertFalse(p.locator('input[data-type="news"]').is_checked())
        self.assertEqual(p.locator('#event-list button').count(),2)
        self.assertEqual(p.evaluate('meiliChart.inspect().eventMarkers.markers().length'),1)
        self.assertFalse(p.locator('#event-detail').is_visible())
        p.locator('input[data-type="earnings"]').uncheck()
        p.locator('input[data-type="ex_dividend"]').uncheck()
        self.assertEqual(p.locator('#event-list button').count(),0)
        self.assertIn('暂无匹配事件',p.locator('#event-list').inner_text())
        self.assertEqual(p.evaluate('meiliChart.inspect().eventMarkers.markers().length'),0)
        p.locator('input[data-type="earnings"]').check()
        p.locator('input[data-type="ex_dividend"]').check()
        p.locator('input[data-type="news"]').check()
        p.locator('#event-list button',has_text='财报').click()
        self.assertNotIn('Fixture news',p.locator('#event-detail').inner_text())
        p.locator('#fit').click();p.wait_for_timeout(200)
        # Click the real price pane at the news time; drawing mode is off.
        point=p.evaluate('''()=>{const {chart,series,state}=meiliChart.inspect();const t=state.events.find(e=>e.event.type==='news'&&e.time).time;return {x:chart.timeScale().timeToCoordinate(t),y:series.candles.priceToCoordinate(118.5)}}''')
        bounds=p.locator('#chart').bounding_box();p.mouse.click(bounds['x']+point['x'],bounds['y']+point['y']);p.wait_for_timeout(150)
        self.assertIn('Fixture news',p.locator('#event-detail').inner_text())
        self.assertEqual(p.evaluate("meiliChart.inspect().eventMarkers.markers().filter(m=>m.text.startsWith('› ')).length"),1)
        for command in ['light','en-US']:
            self.process.stdin.write(command+'\n');self.process.stdin.flush()
        self.wait('meiliChart.inspect().state.language === "en-US" && !meiliChart.inspect().state.theme.dark')
        self.assertEqual(p.locator('#event-heading').inner_text(),'Activity')
        point=p.evaluate("""()=>{const {chart,series,state}=meiliChart.inspect();return {x:chart.timeScale().timeToCoordinate(state.events.find(e=>e.event.type==='news'&&e.time).time),y:series.candles.priceToCoordinate(118.5)}}""")
        bounds=p.locator('#chart').bounding_box();p.mouse.move(bounds['x']+point['x'],bounds['y']+point['y']);p.wait_for_timeout(150)
        self.assertTrue(p.locator('#event-tooltip').is_visible());self.assertIn('News',p.locator('#event-tooltip').inner_text())
        self.capture('event-tooltip-light')

        p.set_viewport_size(dict(width=480,height=800));p.wait_for_timeout(150)
        self.assertLessEqual(p.locator('body').evaluate('(e)=>e.scrollWidth'),480)
        self.assertTrue(p.locator('#attribution').is_visible());self.assertFalse(self.errors)
        self.assertGreaterEqual(p.locator('#timeline').bounding_box()['x'],p.locator('#chart').bounding_box()['x']+p.locator('#chart').bounding_box()['width']-1)
        self.assertTrue(p.locator('#event-close').is_visible())
        self.capture('timeline-compact')
        p.locator('#fit').click();p.wait_for_timeout(200)
        point=p.evaluate("""()=>{const {chart,series,state}=meiliChart.inspect();return {x:chart.timeScale().timeToCoordinate(state.events.find(e=>e.event.type==='news'&&e.time).time),y:series.candles.priceToCoordinate(118.5)}}""")
        bounds=p.locator('#chart').bounding_box();p.mouse.move(bounds['x']+point['x'],bounds['y']+point['y']);p.wait_for_timeout(150)
        self.assertTrue(tooltip.is_visible())
        tip=tooltip.bounding_box()
        self.assertGreaterEqual(tip['x'],bounds['x']);self.assertLessEqual(tip['x']+tip['width'],bounds['x']+bounds['width'])
        self.assertGreaterEqual(tip['y'],bounds['y']);self.assertLessEqual(tip['y']+tip['height'],bounds['y']+bounds['height'])
        self.capture('event-tooltip-compact')


if __name__=='__main__':unittest.main()
