/* App-owned chrome feedback only; data, Canvas, SVG plots and remote pages never enter this channel. */
'use strict';
(()=>{
  const preference=matchMedia('(prefers-reduced-motion: reduce)'),active=new Set();
  const reduced=()=>preference.matches||document.documentElement.dataset.reducedMotion==='true';
  function stop(){for(const a of active)a.cancel();active.clear();}
  function enter(node){
    if(!node||reduced()||node.querySelector('canvas,iframe,object'))return;
    for(const a of node.getAnimations())a.cancel();
    const duration=parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--motion-standard-ms'))||180;
    const a=node.animate([{opacity:0},{opacity:1}],{duration,easing:'cubic-bezier(.215,.61,.355,1)'});active.add(a);a.finished.catch(()=>{}).finally(()=>active.delete(a));
  }
  preference.addEventListener('change',()=>{if(reduced())stop();});
  new MutationObserver(()=>{if(reduced())stop();}).observe(document.documentElement,{attributes:true,attributeFilter:['data-reduced-motion']});
  document.addEventListener('keydown',e=>{if(!['Enter',' '].includes(e.key))return;const b=e.target.closest('button');if(b&&!b.disabled)b.classList.add('keyboard-pressed');});
  document.addEventListener('keyup',()=>document.querySelectorAll('.keyboard-pressed').forEach(b=>b.classList.remove('keyboard-pressed')));
  document.addEventListener('focusout',e=>e.target.classList?.remove('keyboard-pressed'));
  window.addEventListener('blur',()=>document.querySelectorAll('.keyboard-pressed').forEach(b=>b.classList.remove('keyboard-pressed')));
  window.meiliMotion={enter,reduced};
})();
