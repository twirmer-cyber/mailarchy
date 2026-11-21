using MailVault.App.Localization;
using System.Net;
using System.Text;
using System.Text.Json;
using MailVault.Core.Ai;

namespace MailVault.App.Services;

/// <summary>HTML-Seiten für den KI-Bereich (Gesprächsverlauf und Beziehungsnetz), angezeigt in WebView2.</summary>
public static class AiHtml
{
    const string BaseCss = """
        :root { color-scheme: light dark; --paper:#faf9f7; --shelf:var(--shelf); --surface:#fff; --line:var(--line); --ink:#1f2328; --ink2:var(--ink2); --accent:#2c5b8e; --accent-soft:#e7edf4; --danger:var(--danger); --bar:#9db6d3; }
        @media (prefers-color-scheme: dark) { :root { --paper:#1b1c1e; --shelf:#232427; --surface:#26272a; --line:#36383c; --ink:#e8e6e2; --ink2:#a4a8af; --accent:#86aedb; --accent-soft:#2a3644; --danger:#f08a84; --bar:#4f6d90; } }
        body { font-family: 'Segoe UI Variable Text', 'Segoe UI', sans-serif; font-size: 14px; color: var(--ink); background: var(--paper); margin: 0; }
        ::selection { background: var(--accent-soft); }
        a { color: var(--accent); }
        """;

    public sealed record ChatEntry(bool IsUser, string Text, string? Meta = null);

    public static string Chat(IReadOnlyList<ChatEntry> entries, string? pending, Func<long, string?> citationTitle)
    {
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{Lang}\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\">");
        sb.Append("<style>").Append(BaseCss).Append("""
            .wrap { max-width: 780px; margin: 0 auto; padding: 20px 24px 40px; }
            .msg { margin: 0 0 22px; }
            .user .bubble { font-weight: 600; white-space: pre-wrap; padding: 0 0 8px; border-bottom: 1px solid var(--line); line-height: 1.45; }
            .ai .bubble { line-height: 1.6; padding-top: 6px; }
            .ai h2, .ai h3, .ai h4 { margin: 12px 0 6px; font-size: 14px; } .ai h2 { font-size: 16px; }
            .ai p { margin: 6px 0; } .ai ul, .ai ol { margin: 6px 0; padding-left: 22px; } .ai li { margin: 2px 0; }
            .ai table { border-collapse: collapse; margin: 8px 0; font-size: 13px; background: var(--surface); }
            .ai th, .ai td { border: 1px solid var(--line); padding: 4px 8px; text-align: left; vertical-align: top; font-variant-numeric: tabular-nums; }
            .ai th { background: var(--shelf); font-weight: 600; }
            .ai code { background: var(--shelf); padding: 1px 4px; border-radius: 3px; }
            sup { line-height: 0; }
            a.fn { color: var(--accent); text-decoration: none; font-size: 12px; font-weight: 600; padding: 0 2px; }
            a.fn:hover { text-decoration: underline; }
            sup.missing { color: var(--danger); font-size: 12px; }
            .sources { margin: 12px 0 0; padding: 8px 0 0 22px; border-top: 1px solid var(--line); font-size: 13px; color: var(--ink2); }
            .sources li { margin: 2px 0; }
            .sources a { color: var(--ink); text-decoration: none; }
            .sources a:hover { color: var(--accent); text-decoration: underline; }
            .sources .d { font-variant-numeric: tabular-nums; color: var(--ink2); }
            .meta { color: var(--ink2); font-size: 12px; margin-top: 8px; }
            .pending { color: var(--ink2); font-style: italic; }
            .empty { color: var(--ink2); margin-top: 56px; line-height: 1.7; }
            .empty b { color: var(--ink); font-size: 16px; font-weight: 600; display: block; margin-bottom: 4px; }
            """).Append("</style></head><body><div class=\"wrap\">");
        if (entries.Count == 0 && pending is null)
            sb.Append($"<div class=\"empty\"><b>{H("AiHtml_EmptyTitle")}</b>{H("AiHtml_EmptyExample")}<br>{H("AiHtml_EmptyHint")}</div>");
        foreach (var e in entries)
        {
            if (e.IsUser) sb.Append($"<div class=\"msg user\"><div class=\"bubble\">{WebUtility.HtmlEncode(e.Text)}</div></div>");
            else
            {
                var cited = new List<long>();
                sb.Append("<div class=\"msg ai\"><div class=\"bubble\">").Append(MiniMarkdown.ToHtml(e.Text, citationTitle, cited));
                if (cited.Count > 0)
                {
                    sb.Append("<ol class=\"sources\">");
                    foreach (var id in cited)
                        sb.Append($"<li><a href=\"mailvault://msg/{id}\">{WebUtility.HtmlEncode(citationTitle(id) ?? "")}</a></li>");
                    sb.Append("</ol>");
                }
                if (e.Meta is not null) sb.Append($"<div class=\"meta\">{WebUtility.HtmlEncode(e.Meta)}</div>");
                sb.Append("</div></div>");
            }
        }
        if (pending is not null)
            sb.Append($"<div class=\"msg ai\"><div class=\"bubble pending\">{WebUtility.HtmlEncode(pending)} …</div></div>");
        sb.Append("<div id=\"end\"></div></div><script>document.getElementById('end').scrollIntoView();</script></body></html>");
        return sb.ToString();
    }

    /// <summary>Sprachkürzel der Oberfläche für das lang-Attribut.</summary>
    static string Lang => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>Oberflächentext, HTML-kodiert.</summary>
    static string H(string key) => WebUtility.HtmlEncode(L.T(key));

    public static string Network(ContactNetwork net)
    {
        var data = new
        {
            nodes = net.Nodes.Select(n => new { id = n.Address, label = n.Label, domain = n.Domain, total = n.Total, from = n.FromThem, to = n.ToThem, first = n.FirstUtc.ToString("MM/yyyy"), last = n.LastUtc.ToString("MM/yyyy"), owner = n.IsOwner }),
            edges = net.Edges.Select(e => new { a = e.A, b = e.B, w = e.Weight }),
            volume = net.Volume.Select(v => new { m = v.Month, c = v.Count }),
            count = net.MessageCount,
        };
        // JSON in <script> sicher einbetten: "</" und HTML-Sonderzeichen werden als \u-Escape kodiert.
        var json = JsonSerializer.Serialize(data);
        // Texte für das Skript ebenso sicher einbetten.
        var texts = JsonSerializer.Serialize(new { me = L.T("AiHtml_Me"), tip = L.T("AiHtml_Tip"), range = L.T("AiHtml_Range") });
        return """
            <!doctype html><html lang="
            """ + Lang + """
            "><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <style>
            """ + BaseCss + """
            html, body { height: 100%; overflow: hidden; }
            #wrap { display: flex; height: 100%; }
            #graph { flex: 1; position: relative; }
            canvas { display: block; }
            #side { width: 280px; border-left: 1px solid var(--line); padding: 12px 14px; overflow: auto; background: var(--shelf); }
            h3 { font-size: 13px; font-weight: 600; color: var(--ink); margin: 16px 0 6px; }
            .c { display: flex; justify-content: space-between; padding: 3px 4px; border-radius: 4px; cursor: pointer; font-size: 13px; }
            .c:hover { background: var(--accent-soft); } .c span:last-child { color: var(--ink2); }
            #tip { position: absolute; pointer-events: none; background: var(--ink); color: var(--paper); padding: 6px 9px; border-radius: 6px; font-size: 12px;
                   display: none; max-width: 280px; line-height: 1.45; }
            #bars { display: flex; align-items: flex-end; height: 70px; gap: 1px; }
            #bars div { flex: 1; background: var(--bar); min-height: 1px; }
            .hint { color: var(--ink2); font-size: 12px; position: absolute; left: 12px; bottom: 10px; }
            </style></head><body><div id="wrap"><div id="graph"><canvas id="cv"></canvas><div id="tip"></div>
            <div class="hint">
            """ + H("AiHtml_NetHint") + """
            </div></div>
            <div id="side"><h3>
            """ + H("AiHtml_PerMonth") + """
            </h3><div id="bars"></div><div id="range" style="font-size:12px;color:var(--ink2)"></div>
            <h3>
            """ + H("AiHtml_TopContacts") + """
            </h3><div id="list"></div></div></div>
            <script>
            const D =
            """ + json + """
            ;
            const T =
            """ + texts + """
            ;
            const css = getComputedStyle(document.documentElement), V = n => css.getPropertyValue(n).trim();
            const cv = document.getElementById('cv'), ctx = cv.getContext('2d'), tip = document.getElementById('tip');
            let W = 0, H = 0, scale = 1, ox = 0, oy = 0;
            function resize() { const r = cv.parentElement.getBoundingClientRect(); W = r.width; H = r.height; cv.width = W * devicePixelRatio; cv.height = H * devicePixelRatio; cv.style.width = W + 'px'; cv.style.height = H + 'px'; }
            resize(); addEventListener('resize', () => { resize(); draw(); });
            const palette = ['#1f5fad','#d0572a','#2e8b57','#8e44ad','#c0392b','#16a085','#b7950b','#5d6d7e','#d35400','#2874a6'];
            const domains = {}; let di = 0;
            const maxT = Math.max(1, ...D.nodes.filter(n => !n.owner).map(n => n.total));
            const nodes = D.nodes.map((n, i) => {
              if (!(n.domain in domains)) domains[n.domain] = palette[di++ % palette.length];
              const a = i * 2.39996, rad = n.owner ? 0 : 60 + 12 * Math.sqrt(i);
              return { ...n, x: Math.cos(a) * rad, y: Math.sin(a) * rad, vx: 0, vy: 0, r: n.owner ? 16 : 5 + 13 * Math.sqrt(n.total / maxT), color: n.owner ? V('--ink') : domains[n.domain] };
            });
            const byId = Object.fromEntries(nodes.map(n => [n.id, n]));
            const edges = D.edges.filter(e => byId[e.a] && byId[e.b]).map(e => ({ s: byId[e.a], t: byId[e.b], w: e.w }));
            const maxW = Math.max(1, ...edges.map(e => e.w));
            function step() {
              for (const a of nodes) for (const b of nodes) { if (a === b) continue; let dx = a.x - b.x, dy = a.y - b.y, d2 = dx*dx + dy*dy + 0.01; const f = 500 / d2; a.vx += dx * f; a.vy += dy * f; }
              for (const e of edges) { const dx = e.t.x - e.s.x, dy = e.t.y - e.s.y, d = Math.sqrt(dx*dx + dy*dy) + 0.01; const owner = e.s.owner || e.t.owner;
                const k = (owner ? 0.002 : 0.006) * Math.log(1 + e.w), f = (d - (owner ? 140 : 70)) * k; e.s.vx += dx / d * f; e.s.vy += dy / d * f; e.t.vx -= dx / d * f; e.t.vy -= dy / d * f; }
              for (const n of nodes) { n.vx -= n.x * 0.012; n.vy -= n.y * 0.012; if (n.owner || n === drag) { n.vx = n.vy = 0; if (n.owner && n !== drag) { n.x *= 0.9; n.y *= 0.9; } continue; }
                n.vx *= 0.82; n.vy *= 0.82; n.x += Math.max(-8, Math.min(8, n.vx)); n.y += Math.max(-8, Math.min(8, n.vy)); }
            }
            function toScreen(n) { return [W / 2 + (n.x + ox) * scale, H / 2 + (n.y + oy) * scale]; }
            let hover = null, drag = null, pan = null;
            function draw() {
              ctx.setTransform(devicePixelRatio, 0, 0, devicePixelRatio, 0, 0); ctx.clearRect(0, 0, W, H);
              for (const e of edges) { const [x1, y1] = toScreen(e.s), [x2, y2] = toScreen(e.t); const hi = hover && (e.s === hover || e.t === hover);
                ctx.strokeStyle = hi ? V('--accent') : (e.s.owner || e.t.owner ? 'rgba(120,130,145,.13)' : 'rgba(120,130,145,.38)');
                ctx.lineWidth = (0.6 + 4 * e.w / maxW) * (hi ? 1.5 : 1); ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke(); }
              const ranked = [...nodes].sort((a, b) => b.total - a.total);
              const labelled = new Set(ranked.slice(0, 28));
              for (const n of nodes) { const [x, y] = toScreen(n); const r = n.r * Math.sqrt(scale);
                ctx.beginPath(); ctx.arc(x, y, r, 0, 7); ctx.fillStyle = n.color; ctx.globalAlpha = hover && hover !== n && !edges.some(e => (e.s === hover && e.t === n) || (e.t === hover && e.s === n)) ? 0.25 : 1; ctx.fill();
                ctx.lineWidth = n === hover ? 3 : 1.5; ctx.strokeStyle = V('--surface'); ctx.stroke();
                if (labelled.has(n) || n === hover || n.owner) { ctx.fillStyle = V('--ink'); ctx.font = (n.owner ? 'bold ' : '') + '12px Segoe UI'; ctx.textAlign = 'center'; ctx.fillText(n.owner ? T.me : n.label.slice(0, 26), x, y + r + 13); }
                ctx.globalAlpha = 1; }
            }
            let userView = false;
            function fit() { if (userView || !nodes.length) return; let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
              for (const n of nodes) { x0 = Math.min(x0, n.x - n.r); y0 = Math.min(y0, n.y - n.r); x1 = Math.max(x1, n.x + n.r); y1 = Math.max(y1, n.y + n.r + 16); }
              const target = Math.min(2.2, (W - 60) / Math.max(1, x1 - x0), (H - 60) / Math.max(1, y1 - y0));
              scale += (target - scale) * 0.2; ox += (-(x0 + x1) / 2 - ox) * 0.2; oy += (-(y0 + y1) / 2 - oy) * 0.2; }
            let ticks = 0; (function loop() { if (ticks++ < 420 || drag) step(); if (ticks < 480) fit(); draw(); requestAnimationFrame(loop); })();
            function pick(mx, my) { let best = null, bd = 1e9; for (const n of nodes) { const [x, y] = toScreen(n); const d = Math.hypot(x - mx, y - my); if (d < n.r * Math.sqrt(scale) + 4 && d < bd) { bd = d; best = n; } } return best; }
            cv.addEventListener('mousemove', ev => {
              const r = cv.getBoundingClientRect(), mx = ev.clientX - r.left, my = ev.clientY - r.top;
              if (drag) { drag.x = (mx - W / 2) / scale - ox; drag.y = (my - H / 2) / scale - oy; moved = true; return; }
              if (pan) { userView = true; ox += (ev.clientX - pan.x) / scale; oy += (ev.clientY - pan.y) / scale; pan = { x: ev.clientX, y: ev.clientY }; moved = true; return; }
              hover = pick(mx, my); cv.style.cursor = hover ? 'pointer' : 'grab';
              if (hover) { tip.style.display = 'block'; tip.style.left = (mx + 14) + 'px'; tip.style.top = (my + 10) + 'px';
                tip.textContent = ''; const t = document.createElement('b'); t.textContent = hover.owner ? T.me + ' (' + hover.id + ')' : hover.label; tip.appendChild(t);
                const l = document.createElement('div'); l.textContent = hover.id; tip.appendChild(l);
                const s = document.createElement('div'); s.textContent = T.tip.replace('{0}', hover.from).replace('{1}', hover.to) + ' · ' + hover.first + ' – ' + hover.last; tip.appendChild(s);
              } else tip.style.display = 'none';
            });
            let moved = false;
            cv.addEventListener('mousedown', ev => { moved = false; const r = cv.getBoundingClientRect(); const n = pick(ev.clientX - r.left, ev.clientY - r.top); if (n) drag = n; else pan = { x: ev.clientX, y: ev.clientY }; });
            addEventListener('mouseup', () => { if (drag && !moved) open(drag.id); drag = null; pan = null; });
            cv.addEventListener('wheel', ev => { ev.preventDefault(); userView = true; scale = Math.max(0.3, Math.min(4, scale * (ev.deltaY < 0 ? 1.12 : 0.89))); }, { passive: false });
            function open(id) { if (window.chrome && chrome.webview) chrome.webview.postMessage(JSON.stringify({ type: 'contact', address: id })); }
            const list = document.getElementById('list');
            for (const n of [...nodes].filter(n => !n.owner).sort((a, b) => b.total - a.total).slice(0, 40)) {
              const row = document.createElement('div'); row.className = 'c'; row.title = n.id;
              const a = document.createElement('span'); a.textContent = n.label; const b = document.createElement('span'); b.textContent = n.total;
              row.append(a, b); row.onclick = () => open(n.id); row.onmouseenter = () => hover = n; row.onmouseleave = () => hover = null; list.appendChild(row); }
            const bars = document.getElementById('bars'), maxV = Math.max(1, ...D.volume.map(v => v.c));
            for (const v of D.volume.slice(-60)) { const d = document.createElement('div'); d.style.height = (100 * v.c / maxV) + '%'; d.title = v.m + ': ' + v.c; bars.appendChild(d); }
            if (D.volume.length) document.getElementById('range').textContent = T.range.replace('{0}', D.volume[Math.max(0, D.volume.length - 60)].m).replace('{1}', D.volume[D.volume.length - 1].m).replace('{2}', D.count);
            </script></body></html>
            """;
    }
}
