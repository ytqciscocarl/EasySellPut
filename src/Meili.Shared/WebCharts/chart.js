/* Local renderer only. All market data and indicator calculations stay in .NET. */
'use strict';
(() => {
  const root = document.getElementById('chart');
  let byTime = new Map(), eventMarkers, selectedEvent, selectedEventTime=null, filters = new Set(["news","earnings","ex_dividend"]);
  let sessionBands=[],sessionLayers=[];
  let chart, state, version = -1, signature = '', key = '', series = {}, priceLines = [], drawingSeries = [];
  let posting = false, pendingHover = null, userRange = null, applying = false;
  let editorPreview=null, sameBarWarning = false, draft = null, drawingHover = null, selectedDrawing = -1, drawingRevision = '', drawingBusy = false;
  const finite = Number.isFinite;
  const post = body => fetch('event', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)});
  const diagnostic = body => post({kind: 'diagnostic', ...body}).catch(() => {});
  const error = value => {
    const box = document.getElementById('error');
    box.textContent = state?.language === 'zh-CN' ? '图表加载失败，请重新打开图表页。' : 'Could not load the chart. Reopen the chart page.';
    box.style.display = 'block';
    diagnostic({status: 'error', message: String(value).slice(0, 300)});
  };
  window.addEventListener('error', e => error(e.message));
  window.addEventListener('unhandledrejection', e => error(e.reason));
  let selectionDown=null, drawingDown=null, drawingRequests=Promise.resolve(), finishingDrawing=false;
  function mouseDrawingPoint(event){const rect=root.getBoundingClientRect(),point={x:event.clientX-rect.left,y:event.clientY-rect.top};return drawingPoint({point,time:chart.timeScale().coordinateToTime(point.x),paneIndex:0});}
  root.addEventListener('mousedown',event=>{
    if(!chart || !state)return;
    if(event.button!==0)return;
    if(!state?.drawingMode){if(!event.target.closest('#drawing-panel'))selectionDown={x:event.clientX,y:event.clientY};return;}
    const point=mouseDrawingPoint(event);if(!point)return;
    drawingDown={x:event.clientX,y:event.clientY};event.preventDefault();event.stopPropagation();
  },true);
  root.addEventListener('mouseup',event=>{
    if(!chart || !state)return;
    if(!state.drawingMode){
      const down=selectionDown;selectionDown=null;
      if(!down || event.button!==0 || Math.hypot(event.clientX-down.x,event.clientY-down.y)>5)return;
      const rect=root.getBoundingClientRect(),point={x:event.clientX-rect.left,y:event.clientY-rect.top};
      if(point.x<0 || point.x>=chart.timeScale().width())return;
      const hit=drawingHit(point);if(hit>=0){selectDrawing(hit);root.focus();}return;
    }
    if(!drawingDown || event.button!==0)return;
    const down=drawingDown;drawingDown=null;event.preventDefault();event.stopPropagation();
    if(Math.hypot(event.clientX-down.x,event.clientY-down.y)>5 || finishingDrawing)return;
    const point=mouseDrawingPoint(event);if(!point)return;root.focus();
    if(state.drawingMode==='TrendLine' && draft?.time===point.time){sameBarWarning=true;drawOverlay();return;}
    finishingDrawing=state.drawingMode==='HorizontalLine'||!!draft;
    if(state.drawingMode==='TrendLine'&&!draft)draft=point;
    const body={kind:'drawing',key:state.key,mode:state.drawingMode,epoch:state.drawingEpoch,index:point.index,price:point.price};
    drawingRequests=drawingRequests.then(()=>post(body)).then(response=>{if(!response.ok)throw new Error('Drawing request rejected');}).catch(e=>{finishingDrawing=false;error(e);});drawOverlay();
  },true);
  window.addEventListener('mouseup',()=>{drawingDown=null;selectionDown=null;});
  const overlay = document.getElementById('drawing-overlay');
  const drawingPanel = document.getElementById('drawing-panel');
  function drawingPoint(param) {
    if (!param.point || param.paneIndex !== undefined && param.paneIndex !== 0 || param.point.y < 0 || param.point.y >= chart.panes()[0].getHeight() || param.point.x < 0 || param.point.x >= chart.timeScale().width()) return null;
    const row = byTime.get(param.time), price = series.candles.coordinateToPrice(param.point.y);
    return row && finite(price) && price >= 0 && price <= 1e12 ? {time:row.time,index:row.index,price:Number(price.toFixed(6))} : null;
  }
  function anchors(drawing) {
    const width=chart.timeScale().width();
    if(drawing.kind==='HorizontalLine')return [{x:0,y:series.candles.priceToCoordinate(drawing.price1)},{x:width,y:series.candles.priceToCoordinate(drawing.price1)}];
    return [{x:chart.timeScale().timeToCoordinate(drawing.time1),y:series.candles.priceToCoordinate(drawing.price1)},{x:chart.timeScale().timeToCoordinate(drawing.time2),y:series.candles.priceToCoordinate(drawing.price2)}];
  }
  function drawOverlay() {
    overlay.replaceChildren(); if(!chart || !state)return;
    const width=chart.timeScale().width(),height=chart.panes()[0].getHeight();
    overlay.style.width=width+'px';overlay.style.height=height+'px';
    function shape(tag,attrs){const node=document.createElementNS('http://www.w3.org/2000/svg',tag);for(const [key,value] of Object.entries(attrs))node.setAttribute(key,value);overlay.append(node);}
    function segment(points,preview){
      if(!points.every(p=>finite(p.x)&&finite(p.y)))return;
      shape('line',{x1:points[0].x,y1:points[0].y,x2:points[1].x,y2:points[1].y,stroke:state.theme.accent,'stroke-width':2,...(preview?{'stroke-dasharray':'5 4'}:{})});
      for(const p of points)shape('circle',{cx:p.x,cy:p.y,r:4,fill:state.theme.background,stroke:state.theme.accent,'stroke-width':2});
    }
    const drawing=editorPreview||state.drawings[selectedDrawing];if(drawing && !state.drawingMode)segment(anchors(drawing),!!editorPreview);
    if(state.drawingMode && drawingHover){
      if(state.drawingMode==='HorizontalLine')segment([{x:0,y:series.candles.priceToCoordinate(drawingHover.price)},{x:width,y:series.candles.priceToCoordinate(drawingHover.price)}],true);
      else if(draft)segment(anchors({kind:'TrendLine',time1:draft.time,price1:draft.price,time2:drawingHover.time,price2:drawingHover.price}),true);
      else shape('circle',{cx:chart.timeScale().timeToCoordinate(drawingHover.time),cy:series.candles.priceToCoordinate(drawingHover.price),r:4,fill:state.theme.accent});
    }else if(state.drawingMode && draft)shape('circle',{cx:chart.timeScale().timeToCoordinate(draft.time),cy:series.candles.priceToCoordinate(draft.price),r:4,fill:state.theme.accent});
    const hint=document.getElementById('drawing-hint'),zh=state.language==='zh-CN';
    hint.hidden=!state.drawingMode;
    hint.textContent=sameBarWarning?(zh?'请选择另一根 K 线':'Choose a different candle'):state.drawingMode==='HorizontalLine'?(zh?'点击放置水平线 · Esc 取消':'Click to place · Esc cancel'):draft?(zh?'选择终点 · Esc 取消':'Choose end · Esc cancel'):(zh?'选择起点 · Esc 取消':'Choose start · Esc cancel');
  }
  function selectDrawing(index,focus=false) {
    editorPreview=null;selectedDrawing=index; drawingRevision=state.drawingRevision||'';
    drawingPanel.hidden=index<0 || !!state.drawingMode;
    const zh=state.language==='zh-CN';
    const select=document.getElementById('drawing-select');select.replaceChildren();
    select.setAttribute('aria-label',zh?'已保存画线':'Saved drawings');
    state.drawings.forEach((d,i)=>{const option=document.createElement('option');option.value=i;option.textContent=(zh?(d.kind==='HorizontalLine'?'水平线':'趋势线'):(d.kind==='HorizontalLine'?'Horizontal':'Trend'))+' '+(i+1);select.append(option);});
    select.value=index;
    const drawing=state.drawings[index];
    if(drawing){document.getElementById('drawing-first').value=drawing.price1;document.getElementById('drawing-second').value=drawing.price2;document.getElementById('drawing-second-field').hidden=drawing.kind==='HorizontalLine';}
    document.getElementById('drawing-first-label').textContent=drawing?.kind==='TrendLine'?(zh?'起点':'Start'):(zh?'价格':'Price');document.getElementById('drawing-second-label').textContent=zh?'终点':'End';
    document.getElementById('drawing-save').textContent=zh?'保存':'Save';document.getElementById('drawing-delete').textContent=zh?'删除':'Delete';
    document.getElementById('drawing-close').setAttribute('aria-label',zh?'收起画线编辑':'Close drawing editor');
    drawOverlay();if(focus){document.getElementById('drawing-first').focus();document.getElementById('drawing-first').select();}
  }
  function drawingHit(point){
    if(!point || point.y<0 || point.y>=chart.panes()[0].getHeight())return -1;
    let hit=-1,best=7;
    state.drawings.forEach((d,i)=>{const [a,b]=anchors(d);if(![a.x,a.y,b.x,b.y].every(finite))return;const dx=b.x-a.x,dy=b.y-a.y,t=Math.max(0,Math.min(1,((point.x-a.x)*dx+(point.y-a.y)*dy)/(dx*dx+dy*dy||1))),distance=Math.hypot(point.x-a.x-t*dx,point.y-a.y-t*dy);if(distance<best){best=distance;hit=i;}});return hit;
  }
  async function changeDrawing(remove){
    if(selectedDrawing<0 || drawingBusy)return;
    const first=document.getElementById('drawing-first'),second=document.getElementById('drawing-second');
    if(!remove && (!first.reportValidity() || !second.reportValidity() || first.value==='' || second.value===''))return;
    drawingBusy=true;
    try{const response=await post({kind:remove?'drawing-delete':'drawing-edit',key:state.key,index:selectedDrawing,revision:drawingRevision,price1:Number(first.value),price2:Number(second.value)});if(!response.ok)throw new Error('Drawing request rejected');if(remove)selectDrawing(-1);}
    catch(e){error(e);}finally{drawingBusy=false;}
  }
  for(const id of ['drawing-first','drawing-second'])document.getElementById(id).addEventListener('input',()=>{
    const drawing=state.drawings[selectedDrawing],first=document.getElementById('drawing-first'),second=document.getElementById('drawing-second');
    editorPreview=drawing && first.validity.valid && second.validity.valid && first.value!=='' && second.value!==''?{...drawing,price1:Number(first.value),price2:Number(second.value)}:null;drawOverlay();
  });
  drawingPanel.addEventListener('keydown',event=>{if(event.key==='Enter'&&event.target.tagName==='INPUT'){event.preventDefault();changeDrawing(false);}});
  document.getElementById('drawing-select').addEventListener('change',e=>selectDrawing(Number(e.target.value)));
  document.getElementById('drawing-save').addEventListener('click',()=>changeDrawing(false));
  document.getElementById('drawing-delete').addEventListener('click',()=>changeDrawing(true));
  document.getElementById('drawing-close').addEventListener('click',()=>{selectDrawing(-1);root.focus();});
  root.tabIndex=0;root.setAttribute('aria-label','Chart');
  let overlayFrame=0;
  function scheduleOverlay(){if(!overlayFrame)overlayFrame=requestAnimationFrame(()=>{overlayFrame=0;drawOverlay();});}
  root.addEventListener('pointermove',scheduleOverlay);root.addEventListener('wheel',scheduleOverlay,{passive:true});
  root.addEventListener('keydown',e=>{
    if(e.target!==root || state.drawingMode || !state.drawings.length)return;
    if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();selectDrawing((selectedDrawing+(e.key==='ArrowDown'?1:-1)+state.drawings.length)%state.drawings.length);}
    if(e.key==='Enter'&&selectedDrawing>=0){e.preventDefault();selectDrawing(selectedDrawing,true);}
  });
  root.addEventListener('mouseleave',()=>{drawingHover=null;drawOverlay();});
  window.addEventListener('keydown',e=>{
    if(e.key==='Escape'){
      if(state?.drawingMode){const mode=state.drawingMode;state.drawingMode=null;draft=null;drawingHover=null;root.style.cursor='';chart.applyOptions({handleScroll:{pressedMouseMove:true},handleScale:{axisPressedMouseMove:true}});drawOverlay();post({kind:'drawing-cancel',key:state.key,mode,epoch:state.drawingEpoch}).catch(error);}
      selectDrawing(-1);
    }
    if((e.key==='Delete'||e.key==='Backspace') && selectedDrawing>=0 && e.target===root){e.preventDefault();changeDrawing(true);}
  });
  new ResizeObserver(()=>requestAnimationFrame(drawOverlay)).observe(root);
  function point(row, field, color) {
    return finite(row[field]) ? {time: row.time, value: row[field], ...(color ? {color} : {})} : {time: row.time};
  }
  const eastern = (time, full = false) => {
    if (typeof time !== 'number') return '';
    return new Intl.DateTimeFormat(state?.language || 'en-US', {timeZone: 'America/New_York', ...(full ? {year: 'numeric', month: '2-digit', day: '2-digit'} : {}), ...(state?.intraday ? {hour: '2-digit', minute: '2-digit', hour12: false} : {month: '2-digit', day: '2-digit'})}).format(new Date(time * 1000));
  };
  function line(id, field, title, color, pane = 0, options = {}) {
    if (!series[id]) series[id] = chart.addSeries(LightweightCharts.LineSeries, {title, color, lineWidth: 1, priceLineVisible: false, lastValueVisible: false, ...options}, pane);
    series[id].applyOptions({title, color});
    series[id].setData(state.rows.map(r => point(r, field)));
  }
  const eventNames = () => state.language === 'zh-CN'
    ? {news:'新闻',earnings:'财报',ex_dividend:'除息'}
    : {news:'News',earnings:'Earnings',ex_dividend:'Ex-dividend'};
  const eventStyle = type => ({news:{shape:'circle',color:state.theme.colors[0],icon:'News'},
    earnings:{shape:'square',color:state.theme.colors[2],icon:'CalendarClock'},
    ex_dividend:{shape:'arrowDown',color:state.theme.colors[1],icon:'Dividend'}}[type]);
  const hideEventTooltip = () => {document.getElementById('event-tooltip').hidden = true;};
  // Tooltip and click must resolve exactly the same filtered candle bucket.
  function eventHitItems(param) {
    const visible=(state.events||[]).filter(item=>filters.has(item.event.type)&&finite(item.time));
    const marker=visible.find(item=>item.event.id===param.hoveredObjectId);
    const time=marker?.time??param.time;
    return visible.filter(item=>item.time===time);
  }
  function showEventTooltip(param) {
    const items = eventHitItems(param);
    if (!param.point || state.drawingMode || !items.length) {hideEventTooltip();return;}
    const box = document.getElementById('event-tooltip'), names = eventNames(), zh = state.language === 'zh-CN';
    box.replaceChildren();
    const append = (parent,tag,value,css) => {const node=document.createElement(tag);node.textContent=value;if(css)node.className=css;parent.append(node);return node;};
    append(box,'div',zh ? `${items.length} 项事件` : `${items.length} event${items.length === 1 ? '' : 's'}`,'tooltip-heading');
    const limit=Math.max(1,Math.min(5,Math.floor((root.clientHeight-80)/150)));
    for (const item of items.slice(0,limit)) {
      const e=item.event, row=append(box,'div','','tooltip-event');
      const heading=append(row,'div',names[e.type],'tooltip-type');heading.prepend(meiliIcons.svg(eventStyle(e.type).icon,14),document.createTextNode(' '));heading.style.color=eventStyle(e.type).color;
      const when=e.published ? new Intl.DateTimeFormat(state.language,{timeZone:'America/New_York',dateStyle:'medium',timeStyle:'short'}).format(new Date(e.published))+' ET'
        : e.date ? e.date+(e.endDate?' – '+e.endDate:'') : (zh?'时间未知':'Time unknown');
      append(row,'div',when,'tooltip-meta');
      if (e.title) append(row,'div',e.title,'tooltip-title');
      const confirmation=zh ? {estimated:'预计',source_confirmed:'来源已确认',unverified:'未核实'} : {estimated:'Estimated',source_confirmed:'Source confirmed',unverified:'Unverified'};
      const meta=[e.publisher||e.source,e.type!=='news'?confirmation[e.confirmation]:null].filter(Boolean);
      if(meta.length)append(row,'div',meta.join(' · '),'tooltip-meta');
      const alignment=zh ? {next_bar:'后续 K 线对齐 · 原始事件时间',same_day:'同日 K 线 · 原始事件时间',period_bucket:'周期汇总 · 原始事件时间'}
        : {next_bar:'Next-bar marker · Original time above',same_day:'Same-day marker · Original time above',period_bucket:'Period marker · Original time above'};
      if(alignment[item.alignment])append(row,'div',alignment[item.alignment],'tooltip-meta');
    }
    append(box,'div',items.length>limit ? (zh?`另有 ${items.length-limit} 项 · 点击图表查看详情`:`${items.length-limit} more · Click chart for details`)
      : (zh?'点击展开':'Click to expand'),'tooltip-meta');
    box.hidden=false;
    const width=box.offsetWidth, height=box.offsetHeight;
    box.style.left=Math.max(8,Math.min(param.point.x+16,root.clientWidth-width-8))+'px';
    box.style.top=Math.max(8,Math.min(param.point.y+16,root.clientHeight-height-8))+'px';
  }
  root.addEventListener('mouseleave',hideEventTooltip);
  root.addEventListener('wheel',hideEventTooltip,{passive:true});
  document.addEventListener('keydown',e=>{if(e.key==='Escape')hideEventTooltip();});
  // Pane primitive: paints in chart coordinates, below candles/indicators, and follows native zoom/pan/DPI.
  class SessionBackground {
    attached({chart,requestUpdate}) {this.chart=chart;this.requestUpdate=requestUpdate;}
    detached() {this.chart=null;this.requestUpdate=null;}
    paneViews() {return [{zOrder:()=> 'bottom',renderer:()=>({draw:target=>target.useBitmapCoordinateSpace(scope=>{
      if(!this.chart)return;
      const {context,horizontalPixelRatio,bitmapSize}=scope;
      for(const band of sessionBands){
        const scale=this.chart.timeScale(),start=scale.timeToCoordinate(state.rows[band.from].time),finish=scale.timeToCoordinate(state.rows[band.to].time);
        if(!finite(start)||!finite(finish))continue;
        const half=scale.options().barSpacing/2,left=start-half,right=finish+half;
        const x=Math.max(0,Math.round(left*horizontalPixelRatio)),end=Math.min(bitmapSize.width,Math.round(right*horizontalPixelRatio));
        if(end<=x)continue;
        context.fillStyle=band.session==='PreMarket'?state.theme.sessionPre:state.theme.sessionPost;
        context.fillRect(x,0,end-x,bitmapSize.height);
      }
    })})}];}
  }
  function updateSessions() {
    sessionBands=[];
    if(state.intraday)state.rows.forEach((row,index)=>{
      if(row.session!=='PreMarket'&&row.session!=='AfterHours')return;
      const previous=sessionBands.at(-1);
      if(previous?.session===row.session && previous.to===index-1)previous.to=index;
      else sessionBands.push({session:row.session,from:index,to:index});
    });
    for(const owner of [series.candles,series.volume,series.histogram,series.rsi,series.k].filter(Boolean)){
      if(!sessionLayers.some(layer=>layer.owner===owner)){
        const primitive=new SessionBackground();owner.attachPrimitive(primitive);sessionLayers.push({owner,primitive});
      }
    }
    for(const layer of sessionLayers)layer.primitive.requestUpdate?.();
    const legend=document.getElementById('session-key');legend.replaceChildren();legend.hidden=!state.intraday||!state.rows.some(row=>row.session);
    const zh=state.language==='zh-CN';
    legend.title=zh?'按 K 线起始时间 · 美东标准时段：盘前 04:00–09:30，盘后 16:00–20:00；提前收市日未调整':'Bar start · Standard Eastern hours: pre-market 04:00–09:30, after-hours 16:00–20:00; early closes are not adjusted';
    const summary=document.createElement('summary');summary.setAttribute('aria-label',zh?'交易时段详情':'Trading session details');legend.append(summary);
    for(const [color,label] of [[state.theme.sessionPre,zh?'盘前':'Pre'],[state.theme.sessionPost,zh?'盘后':'Post']]){
      const item=document.createElement('span'),swatch=document.createElement('i');swatch.style.background=color;swatch.setAttribute('aria-hidden','true');item.append(swatch,document.createTextNode(label));summary.append(item);
    }
    summary.append(meiliIcons.svg('Info',14));
    const note=document.createElement('div');note.className='session-note';note.textContent=legend.title;legend.append(note);
  }
  function build() {
    if (chart) chart.remove();
    series = {}; priceLines = []; drawingSeries = [];sessionLayers=[];
    chart = LightweightCharts.createChart(root, {
      autoSize: true,
      layout: {fontFamily:getComputedStyle(document.body).fontFamily,fontSize:12,background: {type: 'solid', color: state.theme.background}, textColor: state.theme.text, attributionLogo: true, panes: {enableResize: true,separatorColor:state.theme.border,separatorHoverColor:state.theme.accent}},
      rightPriceScale: {borderVisible: false, minimumWidth: 70},
      timeScale: {borderVisible: false, timeVisible: state.intraday, secondsVisible: false, rightOffset: 5, tickMarkFormatter: time => eastern(time)},
      localization: {locale: state.language, timeFormatter: time => eastern(time, true)},
      crosshair: {mode: LightweightCharts.CrosshairMode.Normal,vertLine:{color:state.theme.muted,labelBackgroundColor:state.theme.accent},horzLine:{color:state.theme.muted,labelBackgroundColor:state.theme.accent}},
      grid: {vertLines: {color: state.theme.grid}, horzLines: {color: state.theme.grid}},
    });
    series.candles = chart.addSeries(LightweightCharts.CandlestickSeries, {priceLineVisible: true, borderVisible: false});
    series.eventAnchor = chart.addSeries(LightweightCharts.LineSeries, {lineVisible:false,pointMarkersVisible:false,crosshairMarkerVisible:false,priceLineVisible:false,lastValueVisible:false});
    eventMarkers = LightweightCharts.createSeriesMarkers(series.eventAnchor, []);
    series.incomplete = chart.addSeries(LightweightCharts.LineSeries, {color: state.theme.muted, lineVisible: false, pointMarkersVisible: true, pointMarkersRadius: 3, priceLineVisible: false, lastValueVisible: false});
    chart.subscribeCrosshairMove(param => {
      if (applying) return;
      const row = byTime.get(param.time);
      pendingHover = {kind: 'crosshair', key: state.key, index: row ? row.index : -1};
      if (row) showLegend(row);
      showEventTooltip(param);
      drawingHover=drawingPoint(param);if(drawingHover && drawingHover.time!==draft?.time)sameBarWarning=false;root.style.cursor=state.drawingMode?'crosshair':(param.paneIndex===undefined||param.paneIndex===0)&&drawingHit(param.point)>=0?'pointer':'';drawOverlay();
    });
    chart.subscribeClick(param => {
      if (!state.drawingMode) {
        const drawing=param.paneIndex===undefined||param.paneIndex===0?drawingHit(param.point):-1;if(drawing>=0){if(drawing!==selectedDrawing)selectDrawing(drawing);root.focus();return;}
        if(selectedDrawing>=0)selectDrawing(-1);
        const hits=eventHitItems(param);
        hideEventTooltip();
        if (hits.length) {
          selectedEventTime=hits[0].time;
          selectedEvent=hits.length===1?hits[0].event.id:null;
          renderEvents();
        }
        return;
      }
      // Drawing gestures use their own mouse lifecycle, including rapid second clicks.

    });
    chart.timeScale().subscribeVisibleLogicalRangeChange(range => {if (!applying && range) userRange = range;drawOverlay();});
  }
  // Group by original publication day in New York, never by an aligned candle or observation time.
  function eventDay(event) {
    if (!event.published) return event.date || '';
    const parts = new Intl.DateTimeFormat('en-US', {timeZone:'America/New_York',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date(event.published));
    const value = type => parts.find(p => p.type === type).value;
    return `${value('year')}-${value('month')}-${value('day')}`;
  }
  function historyPosition() {
    const list=document.getElementById('event-list'), nav=document.getElementById('event-history');
    const days=[...list.querySelectorAll('.event-day')];
    if (!days.length || !list.clientHeight) return;
    const top=list.getBoundingClientRect().top;
    const atEnd=list.scrollHeight>list.clientHeight+1 && list.scrollTop+list.clientHeight>=list.scrollHeight-1;
    const current=atEnd?days.at(-1):days.find(day=>day.getBoundingClientRect().bottom>top+32)||days.at(-1);
    for (const button of nav.children) {
      if (button.dataset.day===current.dataset.day) button.setAttribute('aria-current','date');
      else button.removeAttribute('aria-current');
    }
    const active=nav.querySelector('[aria-current]');
    if(active){const a=active.getBoundingClientRect(),n=nav.getBoundingClientRect();if(a.top<n.top)nav.scrollTop+=a.top-n.top;else if(a.bottom>n.bottom)nav.scrollTop+=a.bottom-n.bottom;}
  }
  let historyFrame=0;
  const scheduleHistory=()=>{if(!historyFrame)historyFrame=requestAnimationFrame(()=>{historyFrame=0;historyPosition();});};
  document.getElementById('event-list').addEventListener('scroll',scheduleHistory,{passive:true});
  for(const button of document.querySelectorAll('#right-tools button')) button.addEventListener('click',()=>{
    filters.clear();
    for(const type of button.dataset.view==='news'?['news']:button.dataset.view==='calendar'?['earnings','ex_dividend']:['news','earnings','ex_dividend'])filters.add(type);
    for(const input of document.querySelectorAll('#event-filters input'))input.checked=filters.has(input.dataset.type);
    selectedEvent=null;selectedEventTime=null;renderEvents();document.getElementById('event-list').scrollTop=0;
  });
  document.getElementById('event-group-clear').addEventListener('click',()=>{
    selectedEventTime=null;selectedEvent=null;renderEvents();document.getElementById('event-group-clear').blur();
    document.getElementById('event-heading').focus();
  });
  window.addEventListener('resize',scheduleHistory);
  const historySize=new ResizeObserver(scheduleHistory);
  historySize.observe(document.getElementById('timeline'));historySize.observe(document.getElementById('event-tools'));
  function renderEvents() {
    hideEventTooltip();
    const zh = state.language === 'zh-CN';
    const names = eventNames();
    const labels = zh ? {estimated:'预计',source_confirmed:'来源已确认',unverified:'未核实',reported_published:'来源发布时间',timestamp:'精确时间',date:'仅日期',date_range:'日期范围',unknown:'时间未知',outside_chart:'图表范围外',next_bar:'对齐后续 K 线',same_day:'同日 K 线',period_bucket:'周期汇总',exact:'精确对齐',available:'可用',empty:'暂无新闻',not_reported:'来源未提供',unavailable:'不可用'} :
      {estimated:'Estimated',source_confirmed:'Source confirmed',unverified:'Unverified',reported_published:'Reported publication',timestamp:'Timestamp',date:'Date only',date_range:'Date range',unknown:'Time unknown',outside_chart:'Outside chart',next_bar:'Aligned to next bar',same_day:'Same day bar',period_bucket:'Period bucket',exact:'Exact',available:'Available',empty:'No recent news',not_reported:'Not reported',unavailable:'Unavailable'};
    const text = (tag,value,css) => {const node=document.createElement(tag);node.textContent=value;if(css)node.className=css;return node;};
    const date = (e,compact=false) => e.published ? new Intl.DateTimeFormat(state.language,{timeZone:'America/New_York',dateStyle:compact?'short':'medium',timeStyle:'short'}).format(new Date(e.published))+' ET' : e.date ? e.date+(e.endDate ? ' – '+e.endDate : '') : labels.unknown;
    const view=filters.size===3?'all':filters.size===1&&filters.has('news')?'news':filters.size===2&&!filters.has('news')?'calendar':null;
    const viewNames=zh?{all:'动态',news:'新闻',calendar:'日历'}:{all:'Activity',news:'News',calendar:'Calendar'};
    document.getElementById('event-heading').textContent=viewNames[view]||(zh?'筛选动态':'Filtered activity');
    document.getElementById('event-symbol').textContent=state.symbol;
    document.getElementById('timeline').setAttribute('aria-label',zh?'市场侧栏':'Market sidebar');
    document.getElementById('right-tools').setAttribute('aria-label',zh?'侧栏视图':'Sidebar views');
    for(const button of document.querySelectorAll('#right-tools button')){
      button.setAttribute('aria-pressed',String(button.dataset.view===view));
      button.title=viewNames[button.dataset.view];button.setAttribute('aria-label',button.title);button.querySelector('span').textContent=button.title;
    }
    document.getElementById('event-heading').setAttribute('aria-label',zh?'事件与新闻':'Events and news');
    const box = document.getElementById('event-filters');
    if (box.dataset.language !== state.language) {
      box.dataset.language=state.language;box.replaceChildren();
      for (const type of Object.keys(names)) {
        const label = text('label',''), input=document.createElement('input');input.type='checkbox';input.checked=filters.has(type);input.dataset.type=type;
        input.addEventListener('change',()=>{input.checked?filters.add(type):filters.delete(type);renderEvents();});label.append(input);const glyph=text('span','');glyph.append(meiliIcons.svg(eventStyle(type).icon,12));glyph.style.color=eventStyle(type).color;glyph.setAttribute('aria-hidden','true');const tick=text('span','','filter-check');tick.append(meiliIcons.svg('Check',12));label.append(glyph,document.createTextNode(names[type]),tick);box.append(label);
      }
    }
    for(const label of box.querySelectorAll('label')) {const input=label.querySelector('input');label.classList.toggle('active',input.checked);label.querySelector('span').style.color=eventStyle(input.dataset.type).color;}
    const status=document.getElementById('event-status');
    status.textContent=state.timelineStatus==='loading' ? (zh?'加载中…':'Loading…') : (state.coverage||[]).filter(c=>c.status==='unavailable').map(c=>names[c.type]+': '+labels.unavailable).join(' · ');
    status.hidden=!status.textContent;
    document.getElementById('event-info-heading').textContent=zh?'数据范围':'Coverage';
    document.getElementById('event-coverage').textContent=(state.coverage||[]).map(c=>names[c.type]+': '+(labels[c.status]||c.status)).join(' · ');
    document.getElementById('event-scope').textContent=zh?'最近新闻与当前日历快照，不含完整历史。':'Recent news and current calendar snapshot; historical coverage is limited.';
    const visible=(state.events||[]).filter(e=>filters.has(e.event.type));
    if(!visible.some(e=>e.event.id===selectedEvent)) selectedEvent=null;
    if(selectedEventTime!==null&&!visible.some(e=>e.time===selectedEventTime))selectedEventTime=null;
    const feed=selectedEventTime===null?visible:visible.filter(e=>e.time===selectedEventTime);
    const groupHeader=document.getElementById('event-group');groupHeader.hidden=selectedEventTime===null;
    const groupLabel=document.getElementById('event-group-label'),groupText=(zh?'所选 K 线':'Selected candle')+' · '+feed.length;
    if(groupLabel.textContent!==groupText)groupLabel.textContent=groupText;
    const clearGroup=document.getElementById('event-group-clear');clearGroup.title=zh?'返回全部事件':'Show all events';clearGroup.setAttribute('aria-label',clearGroup.title);
    if(!clearGroup.childElementCount)clearGroup.append(meiliIcons.svg('ArrowLeft',16));
    const groups=new Map();for(const item of visible) if(finite(item.time)){if(!groups.has(item.time))groups.set(item.time,[]);groups.get(item.time).push(item);}
    eventMarkers.setMarkers([...groups].sort((a,b)=>a[0]-b[0]).map(([time,items])=>{
      const types=[...new Set(items.map(i=>i.event.type))], selected=selectedEventTime===time||items.some(i=>i.event.id===selectedEvent);
      const style=eventStyle(items[0].event.type);
      return {time,id:items[0].event.id,position:'aboveBar',shape:style.shape,color:selected?state.theme.accent:style.color,
        size:selected?1.5:1,text:(selected?'› ':'')+types.map(type=>names[type]).join(' / ')+(items.length>1?' · '+items.length:'')};
    }));
    const focusedId=document.activeElement?.dataset.eventId;
    const list=document.getElementById('event-list'), nav=document.getElementById('event-history');
    const groupKey=selectedEventTime===null?'all':String(selectedEventTime);
    const scroll=list.dataset.key===state.key&&list.dataset.group===groupKey?list.scrollTop:0, focusedDay=document.activeElement?.dataset.day;
    list.dataset.key=state.key;list.dataset.group=groupKey;list.replaceChildren();nav.replaceChildren();
    nav.setAttribute('aria-label',zh?'历史日期导航':'History by date');
    const days=new Map();
    for(const item of feed){const day=eventDay(item.event);if(!days.has(day))days.set(day,[]);days.get(day).push(item);}
    const dayLabel=day=>day?new Intl.DateTimeFormat(state.language,{timeZone:'UTC',month:'short',day:'numeric',weekday:'short',year:'numeric'}).format(new Date(day+'T12:00:00Z')):labels.unknown;
    let dayIndex=0;
    for(const [day,items] of [...days].sort(([a],[b])=>b.localeCompare(a))) {
      const section=text('section','','event-day');section.dataset.day=day;section.id='event-day-'+dayIndex++;
      const heading=text('h3',dayLabel(day),'event-day-heading');heading.append(text('span',String(items.length),'event-day-count'));section.append(heading);list.append(section);
      const jump=text('button','');jump.type='button';jump.dataset.day=day;jump.title=dayLabel(day)+' · '+items.length+(zh?' 项事件':' events');jump.setAttribute('aria-label',(zh?'跳至 ':'Jump to ')+jump.title);jump.setAttribute('aria-controls',section.id);
      jump.addEventListener('click',()=>{list.scrollTop+=section.getBoundingClientRect().top-list.getBoundingClientRect().top;historyPosition();});
      jump.addEventListener('keydown',e=>{const buttons=[...nav.children],index=buttons.indexOf(jump);const target=e.key==='ArrowDown'?buttons[index+1]:e.key==='ArrowUp'?buttons[index-1]:e.key==='Home'?buttons[0]:e.key==='End'?buttons.at(-1):null;if(target){e.preventDefault();target.focus({preventScroll:true});target.click();}});
      nav.append(jump);if(focusedDay===day)jump.focus({preventScroll:true});
      for(const item of items.sort((a,b)=>(b.event.published?Date.parse(b.event.published):-Infinity)-(a.event.published?Date.parse(a.event.published):-Infinity))) {
      const e=item.event,b=text('button','');b.type='button';b.dataset.eventId=e.id;b.setAttribute('aria-pressed',String(e.id===selectedEvent));b.setAttribute('aria-controls','event-detail');
      const icon=text('span','','event-type-icon');icon.append(meiliIcons.svg(eventStyle(e.type).icon,16));icon.style.color=eventStyle(e.type).color;icon.setAttribute('aria-hidden','true');b.append(icon);
      b.append(text('span',e.type==='news'?e.title:names[e.type],'event-title'));
      b.setAttribute('aria-label',[names[e.type],e.title,date(e)].filter(Boolean).join(' · '));
      const meta=text('span','','event-row-meta');meta.append(text('span',e.published?new Intl.DateTimeFormat(state.language,{timeZone:'America/New_York',hour:'2-digit',minute:'2-digit',hour12:false}).format(new Date(e.published))+' ET':date(e,true)));
      if(e.type!=='news')meta.append(text('span',labels[e.confirmation],'event-badge'));
      if(item.alignment==='outside_chart' && e.date)meta.append(text('span',labels.outside_chart,'event-badge'));
      b.append(meta);b.addEventListener('click',()=>{selectedEvent=e.id;renderEvents();});section.append(b);if(focusedId===e.id)b.focus({preventScroll:true});
    }
    }
    nav.hidden=!days.size;document.getElementById('event-feed').classList.toggle('no-history',!days.size);
    list.scrollTop=scroll;scheduleHistory();
    if(selectedEvent && document.getElementById('event-detail').dataset.eventId!==selectedEvent){const selected=[...list.querySelectorAll('[data-event-id]')].find(b=>b.dataset.eventId===selectedEvent);if(selected)list.scrollTop+=selected.getBoundingClientRect().top-list.getBoundingClientRect().top-34;}
    if(!feed.length && state.timelineStatus!=='loading')list.append(text('p',zh?'暂无匹配事件':'No matching events','event-empty'));
    const detail=document.getElementById('event-detail');
    const changedDetail=detail.dataset.eventId!==(selectedEvent||'');
    const focusedDetail=!changedDetail&&detail.contains(document.activeElement)?document.activeElement.id:null;
    const keepMetadataOpen=detail.dataset.eventId===selectedEvent && detail.querySelector('details')?.open;
    detail.dataset.eventId=selectedEvent||'';detail.replaceChildren();detail.hidden=!selectedEvent;
    document.getElementById('event-content').classList.toggle('has-selection',!!selectedEvent);
    const item=visible.find(e=>e.event.id===selectedEvent);if(!item)return;
    const e=item.event;
    const close=text('button','','event-close');close.append(meiliIcons.svg('Close',18));close.setAttribute('aria-label',zh?'收起详情':'Close details');close.title=zh?'收起详情':'Close details';close.type='button';close.id='event-close';close.addEventListener('click',()=>{const id=selectedEvent;selectedEvent=null;renderEvents();list.querySelector('[data-event-id="'+id+'"]')?.focus({preventScroll:true});});
    detail.append(close,text('p',e.type==='news'?e.title:names[e.type],'event-detail-title'),text('p',date(e)+(e.type!=='news'?' · '+labels[e.confirmation]:''),'event-caption'));
    if(e.publisher||e.source)detail.append(text('p',e.publisher||e.source,'event-caption'));
    if(e.summary)detail.append(text('p',e.summary,'event-summary'));
    if(e.type==='news' && e.url){const b=text('button',zh?'原文':'Original','event-read');b.type='button';b.id='event-read';b.append(meiliIcons.svg('ExternalLink',14));b.addEventListener('click',()=>post({kind:'open-event',key:state.key,id:e.id}).catch(error));detail.append(b);}
    const metadata=document.createElement('details');metadata.className='event-metadata';metadata.open=!!keepMetadataOpen;
    metadata.append(text('summary',zh?'来源与时间':'Source & time'),text('p',labels[e.precision]+' · '+labels[e.confirmation]),text('p',(zh?'来源：':'Source: ')+(e.publisher?e.publisher+' / ':'')+e.source),text('p',labels[item.alignment]));
    if(e.published)metadata.append(text('p',(zh?'原始时间：':'Original time: ')+e.published));
    if(e.providerFetchedAt)metadata.append(text('p',(zh?'来源下载时间：':'Provider fetched: ')+e.providerFetchedAt));
    if(e.providerAsOf)metadata.append(text('p',(zh?'来源数据时间：':'Provider as of: ')+e.providerAsOf));
    metadata.append(text('p',(zh?'本次读取：':'Observed: ')+e.observedAt));detail.append(metadata);
    if(changedDetail)meiliMotion.enter(detail);
    if(focusedDetail)detail.querySelector('#'+focusedDetail)?.focus({preventScroll:true});
  }
  function showLegend(row) {
    const fields = [state.show.ma && ['MA', row.ma], state.show.ema && ['EMA', row.ema], state.show.macd && ['MACD', row.macd], state.show.rsi && ['RSI', row.rsi], state.show.kdj && ['K', row.k]];
    const sessionNames=state.language==='zh-CN'?{PreMarket:'盘前',Regular:'常规',AfterHours:'盘后',Closed:'休市'}:{PreMarket:'Pre-market',Regular:'Regular',AfterHours:'After-hours',Closed:'Closed'};
    const values=fields.filter(Boolean).map(([label,value])=>label+' '+(finite(value)?value.toFixed(2):'—'));
    if(state.intraday&&row.session)values.unshift(sessionNames[row.session]);
    document.getElementById('legend').textContent=values.join('   ');
  }
  function render(next) {
    const previousKey = key, saved = userRange, previousMode=state?.drawingMode, previousRevision=state?.drawingRevision,previousEpoch=state?.drawingEpoch;
    state = next;
    if(previousKey!==state.key || previousMode!==state.drawingMode || previousEpoch!==state.drawingEpoch){finishingDrawing=false;sameBarWarning=false;draft=null;drawingHover=null;selectedDrawing=-1;}
    if(state.drawingStart)draft=state.drawingStart;
    if(previousRevision && previousRevision!==state.drawingRevision)selectedDrawing=-1;
    byTime = new Map(state.rows.map(r => [r.time, r]));
    document.documentElement.lang = state.language;
    const shape = JSON.stringify(state.show);
    const rebuild = !chart || signature !== shape;
    applying = true;
    try {
      if (rebuild) {signature = shape; build();}
      key = state.key;
      if (previousKey !== key) {selectedEvent = null;selectedEventTime=null;}
      const t = state.theme, c = t.colors;
      document.body.style.background = t.background; document.body.style.color = t.text;
      document.documentElement.dataset.reducedMotion=String(state.theme.reducedMotion===true);document.documentElement.style.colorScheme = t.dark ? 'dark' : 'light';
      for (const [name,value] of Object.entries({brand:t.brand,focus:t.focus,link:t.link,input:t.input,hover:t.hover,pressed:t.pressed,danger:t.danger,background:t.background,text:t.text,muted:t.muted,control:t.control,border:t.border,accent:t.accent,'error-background':t.errorBackground,'error-text':t.errorText}))
        if (value) document.documentElement.style.setProperty('--'+name,value);
      document.getElementById('symbol').textContent = state.symbol;
      document.getElementById('fit').textContent = state.language === 'zh-CN' ? '适应' : 'Fit';
      document.getElementById('fit').setAttribute('aria-label',state.language === 'zh-CN' ? '适应图表窗口' : 'Fit chart to window');
      const attribution=document.getElementById('attribution');
      attribution.replaceChildren(meiliIcons.svg('Info',16));
      attribution.title='TradingView Lightweight Charts™ · Copyright © 2025 TradingView, Inc. · https://www.tradingview.com/';
      attribution.setAttribute('aria-label',state.language==='zh-CN'?'图表组件信息':'Chart component information');
      document.getElementById('empty').textContent = state.rows.length ? '' : state.labels.empty;
      document.getElementById('error').style.display = 'none';
      chart.applyOptions({layout: {background: {type: 'solid', color: t.background}, textColor: t.text,panes:{separatorColor:t.border,separatorHoverColor:t.accent}}, crosshair:{vertLine:{color:t.muted,labelBackgroundColor:t.accent},horzLine:{color:t.muted,labelBackgroundColor:t.accent}}, grid: {vertLines: {color: t.grid}, horzLines: {color: t.grid}}, localization: {locale: state.language}, timeScale: {timeVisible: state.intraday}});
      series.candles.applyOptions({upColor: t.up, downColor: t.down, wickUpColor: t.up, wickDownColor: t.down, borderUpColor: t.up, borderDownColor: t.down});
      series.candles.setData(state.rows.map(r => !r.incomplete && [r.open,r.high,r.low,r.close].every(finite) ? {time:r.time,open:r.open,high:r.high,low:r.low,close:r.close} : {time:r.time}));
      series.incomplete.applyOptions({color:t.muted,lineVisible:state.rows.length > 0 && state.rows.every(r=>r.incomplete),pointMarkersVisible:!state.rows.every(r=>r.incomplete)});
      series.incomplete.setData(state.rows.map(r => r.incomplete ? point(r, 'close') : {time: r.time}));
      series.eventAnchor.setData(state.rows.map(r => point(r, finite(r.high) ? 'high' : 'close')));
      renderEvents();
      if (state.show.ma) line('ma', 'ma', state.labels.ma, c[0]);
      if (state.show.ema) line('ema', 'ema', state.labels.ema, c[1]);
      if (state.show.boll) {line('bollMiddle','bollMiddle',state.labels.boll,c[2]);line('bollUpper','bollUpper','BOLL ↑',c[2]);line('bollLower','bollLower','BOLL ↓',c[2]);}
      let pane = 0;
      if (state.show.volume) {
        pane++;
        if (!series.volume) series.volume = chart.addSeries(LightweightCharts.HistogramSeries, {title: state.labels.volume, priceFormat: {type: 'volume'}, lastValueVisible: false, priceLineVisible: false}, pane);
        series.volume.applyOptions({title:state.labels.volume});
        series.volume.setData(state.rows.map((r,i) => point(r, 'volume', r.close >= (finite(r.open) ? r.open : state.rows[i-1]?.close ?? r.close) ? t.up : t.down)));
      }
      if (state.show.macd) {
        pane++;
        if (!series.histogram) series.histogram = chart.addSeries(LightweightCharts.HistogramSeries, {title: 'MACD H', priceLineVisible: false, lastValueVisible: false}, pane);
        series.histogram.setData(state.rows.map(r => point(r, 'histogram', r.histogram >= 0 ? t.up : t.down)));
        line('macd','macd',state.labels.macd,c[0],pane);line('signal','signal','Signal',c[1],pane);
      }
      if (state.show.rsi) {
        pane++;line('rsi','rsi',state.labels.rsi,c[2],pane,{autoscaleInfoProvider:()=>({priceRange:{minValue:0,maxValue:100}})});
        if (rebuild) for (const price of [30,70]) series.rsi.createPriceLine({price, color:t.muted, lineWidth:1, lineStyle:2, axisLabelVisible:false});
      }
      if (state.show.kdj) {pane++;line('k','k','K',c[0],pane);line('d','d','D',c[1],pane);line('j','j',state.labels.kdj,c[2],pane);}
      if (!series.compare) series.compare = chart.addSeries(LightweightCharts.LineSeries, {color:c[3], lineWidth:2, priceLineVisible:false, lastValueVisible:false});
      series.compare.applyOptions({color:c[3],title:state.compareLabel});
      series.compare.setData(state.compare.map(p => finite(p.value) ? p : {time:p.time}));
      for (const l of priceLines) series.candles.removePriceLine(l);
      for (const s of drawingSeries) chart.removeSeries(s);
      priceLines = [];drawingSeries = [];
      for (const d of state.drawings) {
        if (d.kind === 'HorizontalLine') priceLines.push(series.candles.createPriceLine({price:d.price1,color:c[4],lineWidth:1,lineStyle:2,axisLabelVisible:true}));
        else if (finite(d.time1) && finite(d.time2) && d.time1 !== d.time2) {
          const s = chart.addSeries(LightweightCharts.LineSeries,{color:c[4],lineWidth:2,priceLineVisible:false,lastValueVisible:false});
          s.setData([{time:d.time1,value:d.price1},{time:d.time2,value:d.price2}].sort((a,b)=>a.time-b.time));drawingSeries.push(s);
        }
      }
      updateSessions();
      root.style.cursor=state.drawingMode?'crosshair':'';
      chart.applyOptions({handleScroll:{pressedMouseMove:!state.drawingMode},handleScale:{axisPressedMouseMove:!state.drawingMode}});
      if(document.activeElement?.closest('#drawing-panel') && !drawingPanel.hidden && previousRevision===state.drawingRevision)drawOverlay();else selectDrawing(selectedDrawing);
      if (rebuild) {
        const panes = chart.panes();
        // Relative weights remain stable when autoSize lays out the window after the first frame.
        panes.forEach((p,i) => p.setStretchFactor(i === 0 ? Math.max(4, (panes.length - 1) * 1.5) : 1));
      }
      if (saved && previousKey === key && state.preserveView) {chart.timeScale().setVisibleLogicalRange(saved);userRange = saved;}
      else {chart.timeScale().fitContent();userRange = null;}
      if (state.rows.length) showLegend(state.rows[state.rows.length - 1]);
      document.title = 'EasySellPut Charts · ' + state.symbol;
      diagnostic({status:'rendered',drawingPreview:!!draft,drawingHandles:overlay.querySelectorAll("circle").length,drawingSelection:selectedDrawing,sidebarPermanent:document.getElementById('timeline').tagName==='ASIDE',sidebarViews:document.querySelectorAll('#right-tools button').length,sessionBands:sessionBands.length,sessionPanes:sessionLayers.length,historyGroups:document.querySelectorAll('.event-day').length,historyStops:document.getElementById('event-history').children.length,events:(state.events||[]).length,eventMarkers:eventMarkers.markers().length,key:state.key,rows:state.rows.length,candles:series.candles.data().filter(p=>finite(p.open)).length,panes:chart.panes().length,lastMa:series.ma?.data().at(-1)?.value ?? null,background:t.background,up:series.candles.options().upColor,range:chart.timeScale().getVisibleLogicalRange()});
    } finally {applying = false;}
  }
  document.getElementById('fit').addEventListener('click',()=>{if(chart){userRange=null;chart.timeScale().fitContent();chart.priceScale('right').applyOptions({autoScale:true});}});
  document.getElementById('attribution').addEventListener('click',e=>{e.preventDefault();post({kind:'attribution'}).catch(error);});
  setInterval(()=>{
    if (!pendingHover || posting) return;
    const body = pendingHover;pendingHover = null;posting = true;
    post(body).catch(()=>{}).finally(()=>posting = false);
  },100);
  async function poll() {
    try {
      const response = await fetch('state?after=' + version, {cache:'no-store'});
      if (response.status === 200) {const update=await response.json();if(update.version>0){render(update.data);version=update.version;}}
      else if (response.status !== 204) throw new Error('Chart state HTTP ' + response.status);
    } catch (e) {error(e);}
    setTimeout(poll,250);
  }
  // Read-only diagnostics for renderer tests; it grants no native capabilities.
  window.meiliChart = {inspect:()=>({chart,series,state,eventMarkers,sessionBands,sessionLayers}),render};
  poll();
})();
