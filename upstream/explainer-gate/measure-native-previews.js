function measureNativePreviews() {
  const rect=e=>{const r=e.getBoundingClientRect();return {left:r.left,right:r.right,top:r.top,bottom:r.bottom,width:r.width,height:r.height};};
  const visible=e=>{const c=getComputedStyle(e),r=e.getBoundingClientRect();return c.display!=='none'&&c.visibility!=='hidden'&&Number(c.opacity)!==0&&r.width>0&&r.height>0;};
  return Array.from(document.querySelectorAll('.diagram-preview svg')).map(svg=>{
    const block=svg.closest('[data-block-type]'),panel=block.closest('.panel'),preview=svg.closest('.diagram-preview');
    const prose=panel.querySelector('.block-prose > p')||document.querySelector('.block-prose > p'),body=parseFloat(getComputedStyle(prose||panel.querySelector('.panel-body')||block).fontSize);
    const texts=Array.from(svg.querySelectorAll('text')).filter(e=>e.textContent.trim()&&visible(e)).map(e=>{const m=e.getScreenCTM(),sum=m.a*m.a+m.b*m.b+m.c*m.c+m.d*m.d,det=m.a*m.d-m.b*m.c,scale=Math.sqrt(Math.max(0,(sum-Math.sqrt(Math.max(0,sum*sum-4*det*det)))/2));return {text:e.textContent.trim(),size:parseFloat(getComputedStyle(e).fontSize)*scale/(e.closest('[data-edge-id]')?.7:1)};});
    const minimum=texts.length?Math.min(...texts.map(t=>t.size)):null,bounds=rect(preview),svgBounds=rect(svg);
    const nodes=Array.from(svg.querySelectorAll('[data-node-id]')).filter(visible).map(e=>({id:e.dataset.nodeId,...rect(e)}));
    const outside=nodes.filter(n=>n.left<bounds.left-1||n.right>bounds.right+1||n.top<bounds.top-1||n.bottom>bounds.bottom+1);
    return {component:block.id,minimum,body,ratio:minimum===null?null:minimum/body,smallest:texts.find(t=>t.size===minimum),rect:{width:svgBounds.width,height:svgBounds.height},computed:{width:getComputedStyle(svg).width,height:getComputedStyle(svg).height},fit:preview.dataset.diagramFit,requiredWidth:Number(preview.dataset.diagramRequiredWidth)||null,minimumText:Number(preview.dataset.diagramMinimumText)||null,minWidth:svg.style.minWidth,viewBox:svg.getAttribute('viewBox'),preview:bounds,scrollWidth:preview.scrollWidth,clientWidth:preview.clientWidth,nodes,outsidePreview:outside};
  });
}