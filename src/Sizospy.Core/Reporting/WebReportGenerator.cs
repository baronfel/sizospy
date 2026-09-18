using System.Text.Json;

namespace Sizospy.Reporting;

internal static class WebReportGenerator
{
    public static async Task WriteAsync(
        string outputPath,
        WebReportModel model,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(fullPath) || string.IsNullOrEmpty(Path.GetExtension(fullPath)))
        {
            Directory.CreateDirectory(fullPath);
            fullPath = Path.Combine(fullPath, "index.html");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }

        var json = JsonSerializer.Serialize(model, WebJsonContext.Default.WebReportModel)
            .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        var html = Template.Replace("__SIZOSPY_DATA__", json, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fullPath, html, cancellationToken);
    }

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Sizospy NativeAOT size report</title>
        <style>
        :root{color-scheme:dark;--bg:#0d1117;--panel:#161b22;--border:#30363d;--text:#e6edf3;--muted:#8b949e;--accent:#58a6ff;--selected:#f2cc60}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px system-ui,-apple-system,"Segoe UI",sans-serif}
        header{position:sticky;top:0;z-index:4;background:#0d1117ee;border-bottom:1px solid var(--border);padding:14px 20px}
        h1{font-size:20px;margin:0 0 10px}.summary{display:flex;gap:20px;flex-wrap:wrap;color:var(--muted)}
        .controls{display:flex;gap:8px;margin-top:12px}.controls input,.controls select{background:var(--panel);color:var(--text);border:1px solid var(--border);border-radius:6px;padding:8px}
        #search{min-width:300px;flex:1}.caveat{margin:14px 20px;padding:10px 12px;border:1px solid #9e6a03;background:#3b2d0b;border-radius:6px}
        main{padding:0 20px 24px;display:grid;grid-template-columns:1fr 1fr;gap:14px}.panel{background:var(--panel);border:1px solid var(--border);border-radius:8px;min-width:0}
        .panel h2{font-size:16px;margin:0;padding:12px;border-bottom:1px solid var(--border)}.wide{grid-column:1/-1}
        svg{display:block;width:100%;height:360px}.shape{stroke:#0d1117;stroke-width:1;cursor:pointer}.shape:hover,.shape.selected{stroke:var(--selected);stroke-width:3}
        .shape-label{fill:white;font-size:11px;pointer-events:none;text-shadow:0 1px 2px #000}.empty{padding:24px;color:var(--muted)}
        #breadcrumbs{padding:10px 12px;color:var(--muted);border-bottom:1px solid var(--border);min-height:40px}
        .table-wrap{overflow:auto;max-height:520px}table{width:100%;border-collapse:collapse}th{position:sticky;top:0;background:#21262d;cursor:pointer;text-align:left}
        th,td{padding:8px 10px;border-bottom:1px solid var(--border);white-space:nowrap}tbody tr{cursor:pointer}tbody tr:hover,tbody tr.selected{background:#263341}
        td.name{white-space:normal;min-width:320px}.number{text-align:right;font-variant-numeric:tabular-nums}
        #dependency{padding:12px;display:grid;grid-template-columns:1fr 1fr;gap:12px}#dependency h3{font-size:14px;margin:0 0 8px}
        #dependency ul{margin:0;padding-left:20px}button.link{border:0;background:none;color:var(--accent);padding:2px;cursor:pointer;text-align:left}
        .sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}
        @media(max-width:900px){main{grid-template-columns:1fr}.wide{grid-column:auto}svg{height:300px}}
        </style>
        </head>
        <body>
        <header>
          <h1>Sizospy NativeAOT size report</h1>
          <div class="summary" id="summary"></div>
          <div class="controls">
            <label class="sr-only" for="search">Search nodes</label><input id="search" type="search" placeholder="Search artifact, member, type, namespace, or assembly">
            <label class="sr-only" for="kind">Kind</label><select id="kind"><option value="">All kinds</option></select>
          </div>
        </header>
        <aside class="caveat"><strong>Retained size is a graph-model estimate, not guaranteed savings.</strong> Conditional dependencies, inlining, generic sharing, folding, and changed code generation can change the recompiled binary.</aside>
        <main>
          <section class="panel"><h2>Ownership treemap</h2><div id="ownership-empty" class="empty" hidden>No positive-size nodes match the filter.</div><svg id="treemap" role="img" aria-label="Ownership treemap weighted by physical self size"></svg></section>
          <section class="panel"><h2>Dominator icicle</h2><div id="dominator-empty" class="empty" hidden>No dominator data matches the filter.</div><svg id="icicle" role="img" aria-label="Immediate-dominator tree weighted by retained size"></svg></section>
          <section class="panel wide"><h2>Selected node</h2><div id="breadcrumbs">Select an artifact in any view.</div><div id="dependency"></div></section>
          <section class="panel wide"><h2>Artifacts</h2><div class="table-wrap"><table><thead><tr>
            <th data-sort="displayName">Name</th><th data-sort="kind">Kind</th><th data-sort="selfSize" class="number">Self</th>
            <th data-sort="retainedSize" class="number">Retained</th><th data-sort="marginalRetainedSize" class="number">Marginal</th>
            <th data-sort="leverage" class="number">Leverage</th><th data-sort="dominatedNodeCount" class="number">Dominated</th>
          </tr></thead><tbody id="rows"></tbody></table></div></section>
        </main>
        <script id="sizospy-data" type="application/json">__SIZOSPY_DATA__</script>
        <script>
        "use strict";
        const data=JSON.parse(document.getElementById("sizospy-data").textContent);
        const nodes=data.nodes.map(n=>({nodeId:n.i,displayName:n.n,kind:n.k,assembly:n.a,namespace:n.ns,type:n.t,member:n.m,selfSize:n.s,retainedSize:n.r,marginalRetainedSize:n.g,leverage:n.l,dominatedNodeCount:n.c,rootDistance:n.d}));
        const edges=data.edges.map(e=>({source:e.s,target:e.t,reason:e.r,reasonKind:e.k,conditionalGroup:e.c}));
        const dominators=data.dominators.map(d=>({node:d.n,immediateDominator:d.p}));
        data.nodes=null;data.edges=null;data.dominators=null;
        const byId=new Map(nodes.map(n=>[n.nodeId,n]));
        const idom=new Map(dominators.map(d=>[d.node,d.immediateDominator])),children=new Map();
        for(const [n,p] of idom){const k=p??0;if(!children.has(k))children.set(k,[]);children.get(k).push(n)}
        let selected=null,sortKey="selfSize",sortDirection=-1,visible=nodes;
        const colors=["#1f6feb","#238636","#8957e5","#bf8700","#da3633","#0e8a9d","#8250df","#bc4c00"];
        const esc=s=>String(s??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c]));
        const bytes=n=>{if(n==null)return"-";let i=0,v=n,u=["B","KiB","MiB","GiB"];while(Math.abs(v)>=1024&&i<u.length-1){v/=1024;i++}return new Intl.NumberFormat("en-US",{maximumFractionDigits:i?1:0}).format(v)+" "+u[i]};
        document.getElementById("summary").innerHTML=`<span><strong>${data.summary.nodeCount.toLocaleString()}</strong> imported nodes</span><span><strong>${nodes.length.toLocaleString()}</strong> size-relevant nodes shown</span><span><strong>${data.summary.edgeCount.toLocaleString()}</strong> imported edges</span><span><strong>${bytes(data.summary.accountedSize)}</strong> accounted</span><span><strong>${data.summary.binarySize==null?"not supplied":bytes(data.summary.binarySize)}</strong> binary</span>`;
        const kind=document.getElementById("kind");for(const k of [...new Set(nodes.map(n=>n.kind))].sort())kind.insertAdjacentHTML("beforeend",`<option>${esc(k)}</option>`);
        function matches(n){const q=document.getElementById("search").value.trim().toLowerCase(),k=kind.value;return(!k||n.kind===k)&&(!q||[n.displayName,n.assembly,n.namespace,n.type,n.member].some(v=>v?.toLowerCase().includes(q)))}
        function update(){visible=nodes.filter(matches);renderTable();renderTreemap();renderIcicle();if(selected&&!visible.some(n=>n.nodeId===selected))select(null)}
        document.getElementById("search").addEventListener("input",update);kind.addEventListener("change",update);
        function select(id){selected=id;document.querySelectorAll(".selected").forEach(e=>e.classList.remove("selected"));if(id!=null)document.querySelectorAll(`[data-node="${id}"]`).forEach(e=>e.classList.add("selected"));renderFocus()}
        function hierarchy(rows){const root={name:"All",children:new Map(),weight:0};for(const n of rows.filter(n=>n.selfSize>0)){let p=root;for(const name of [n.assembly||"(compiler)",n.namespace||"(global)",n.type||"(artifact)",n.member||"(unowned)"]){if(!p.children.has(name))p.children.set(name,{name,children:new Map(),weight:0});p=p.children.get(name);p.weight+=n.selfSize}const key=`artifact:${n.nodeId}`;p.children.set(key,{name:n.displayName,children:new Map(),weight:n.selfSize,node:n});root.weight+=n.selfSize}return root}
        function treemapLayout(node,x,y,w,h,depth,out){if(node.node){out.push({node:node.node,x,y,w,h,depth,label:node.name});return}const list=[...node.children.values()].sort((a,b)=>b.weight-a.weight),total=list.reduce((s,c)=>s+c.weight,0);let cursor=depth%2?y:x;for(const c of list){const ratio=total?c.weight/total:0;if(depth%2){const ch=h*ratio;treemapLayout(c,x,cursor,w,ch,depth+1,out);cursor+=ch}else{const cw=w*ratio;treemapLayout(c,cursor,y,cw,h,depth+1,out);cursor+=cw}}}
        function renderTreemap(){const svg=document.getElementById("treemap"),empty=document.getElementById("ownership-empty"),candidates=visible.filter(n=>n.selfSize>0).sort((a,b)=>b.selfSize-a.selfSize||a.nodeId-b.nodeId).slice(0,5000),root=hierarchy(candidates),layout=[];svg.innerHTML="";treemapLayout(root,0,0,1000,360,0,layout);empty.hidden=layout.length>0;for(const r of layout){const color=colors[(r.node.assembly||r.node.kind).split("").reduce((a,c)=>a+c.charCodeAt(0),0)%colors.length],g=document.createElementNS("http://www.w3.org/2000/svg","g");g.innerHTML=`<rect class="shape" data-node="${r.node.nodeId}" x="${r.x}" y="${r.y}" width="${Math.max(0,r.w)}" height="${Math.max(0,r.h)}" fill="${color}"><title>${esc(r.node.displayName)} — ${bytes(r.node.selfSize)}</title></rect>${r.w>90&&r.h>20?`<text class="shape-label" x="${r.x+4}" y="${r.y+14}">${esc(r.node.displayName.slice(0,Math.floor(r.w/7)))}</text>`:""}`;g.addEventListener("click",()=>select(r.node.nodeId));svg.appendChild(g)}}
        function renderIcicle(){const svg=document.getElementById("icicle"),empty=document.getElementById("dominator-empty");svg.innerHTML="";const roots=(children.get(0)||[]).map(byId.get).filter(Boolean).filter(matches),maxDepth=Math.max(1,...visible.map(n=>n.rootDistance??0)),rowH=360/(maxDepth+1);let x=0,total=roots.reduce((s,n)=>s+(n.retainedSize??n.selfSize),0);function draw(n,nx,nw,depth){if(nw<.25||depth*rowH>=360)return;const y=depth*rowH,g=document.createElementNS("http://www.w3.org/2000/svg","g"),color=colors[depth%colors.length];g.innerHTML=`<rect class="shape" data-node="${n.nodeId}" x="${nx}" y="${y}" width="${Math.max(0,nw)}" height="${Math.max(2,rowH)}" fill="${color}"><title>${esc(n.displayName)} — retained ${bytes(n.retainedSize)}</title></rect>${nw>90?`<text class="shape-label" x="${nx+4}" y="${y+14}">${esc(n.displayName.slice(0,Math.floor(nw/7)))}</text>`:""}`;g.addEventListener("click",()=>select(n.nodeId));svg.appendChild(g);const kids=(children.get(n.nodeId)||[]).map(byId.get).filter(Boolean).filter(matches),sum=kids.reduce((s,c)=>s+(c.retainedSize??c.selfSize),0);let cx=nx;for(const c of kids){const cw=sum?nw*(c.retainedSize??c.selfSize)/sum:0;draw(c,cx,cw,depth+1);cx+=cw}}for(const r of roots){const w=total?1000*(r.retainedSize??r.selfSize)/total:0;draw(r,x,w,0);x+=w}empty.hidden=roots.length>0}
        function renderTable(){const rows=document.getElementById("rows"),sorted=[...visible].sort((a,b)=>{const av=a[sortKey]??-1,bv=b[sortKey]??-1;return(typeof av==="string"?av.localeCompare(bv):av-bv)*sortDirection||a.nodeId-b.nodeId}).slice(0,2000);rows.innerHTML=sorted.map(n=>`<tr data-node="${n.nodeId}" class="${selected===n.nodeId?"selected":""}"><td class="name"><strong>${esc(n.displayName)}</strong><br><small>${esc([n.assembly,n.namespace,n.type,n.member].filter(Boolean).join(" / "))}</small></td><td>${esc(n.kind)}</td><td class="number">${bytes(n.selfSize)}</td><td class="number">${bytes(n.retainedSize)}</td><td class="number">${bytes(n.marginalRetainedSize)}</td><td class="number">${n.leverage==null?"-":n.leverage.toFixed(2)+"x"}</td><td class="number">${n.dominatedNodeCount??"-"}</td></tr>`).join("");rows.querySelectorAll("tr").forEach(tr=>tr.addEventListener("click",()=>select(Number(tr.dataset.node))))}
        document.querySelectorAll("th[data-sort]").forEach(th=>th.addEventListener("click",()=>{const next=th.dataset.sort;if(sortKey===next)sortDirection*=-1;else{sortKey=next;sortDirection=typeof nodes[0]?.[next]==="string"?1:-1}renderTable()}));
        function renderFocus(){const crumb=document.getElementById("breadcrumbs"),dep=document.getElementById("dependency");if(selected==null){crumb.textContent="Select an artifact in any view.";dep.innerHTML="";return}const n=byId.get(selected);crumb.innerHTML=[n.assembly,n.namespace,n.type,n.member,n.displayName].filter(Boolean).map(esc).join(" › ")+` — self ${bytes(n.selfSize)}, retained ${bytes(n.retainedSize)}`;const incoming=edges.filter(e=>e.target===selected).slice(0,50),outgoing=edges.filter(e=>e.source===selected).slice(0,50);const list=(items,key)=>items.length?`<ul>${items.map(e=>{const other=byId.get(e[key]);return`<li><button class="link" data-focus="${other?.nodeId}">${esc(other?.displayName||e[key])}</button> — ${esc(e.reason||"dependency")}</li>`}).join("")}</ul>`:`<p class="empty">None in the imported graph.</p>`;dep.innerHTML=`<div><h3>Required by / paths toward roots (incoming)</h3>${list(incoming,"source")}</div><div><h3>Depends on (outgoing)</h3>${list(outgoing,"target")}</div>`;dep.querySelectorAll("[data-focus]").forEach(b=>b.addEventListener("click",()=>select(Number(b.dataset.focus))))}
        update();
        </script>
        </body></html>
        """;
}
