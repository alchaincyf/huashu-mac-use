#!/usr/bin/env python3
"""gen-flow.py — huashu-mac-use 工作流程图（工程图纸风，原语抄自微众课件 diagrams/gen-diagrams.py）
产出 流程图-图纸.html（独立页，内联 CSS，1920×1080），用 headless Chrome 截成 流程图-图纸.png
版面：viewBox 1728×740，上排 y=110，下排 y=410，回程线 y=366，右下标题栏禁区 x≥930,y≥676
"""
import os
HERE = os.path.dirname(os.path.abspath(__file__))
W, H = 1728, 740
NH = 176
DATE = "2026-09-06"
NAVY = "var(--navy)"; RED = "var(--red)"; INK2 = "var(--ink2)"; INK3 = "var(--ink3)"
BODY = "var(--body)"; TITLE = "var(--title)"; MONO = "var(--mono)"

def esc(s): return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
def _style(size, fam, fill, weight, ls):
    return f"font:{weight} {size}px/1 {fam};fill:{fill}" + (f";letter-spacing:{ls}em" if ls else "")
def text(x, y, s, size=20, fam=BODY, fill=NAVY, anchor="start", weight=400, ls=0):
    return f'<text x="{x}" y="{y}" text-anchor="{anchor}" style="{_style(size, fam, fill, weight, ls)}">{esc(s)}</text>'
def lines(x, y, ls_, size=20, lh=27, fam=BODY, fill=INK2, anchor="start", weight=400):
    sp = "".join(f'<tspan x="{x}" dy="{0 if i == 0 else lh}">{esc(s)}</tspan>' for i, s in enumerate(ls_))
    return f'<text x="{x}" y="{y}" text-anchor="{anchor}" style="{_style(size, fam, fill, weight, 0)}">{sp}</text>'
def node(x, y, w, num, title, desc, tag=None, double=False, dashed=False, h=NH):
    out = [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" class="nd{" dsh" if dashed else ""}"/>']
    p = 18
    if double:
        out.append(f'<rect x="{x+5}" y="{y+5}" width="{w-10}" height="{h-10}" class="nd"/>'); p = 24
    out.append(text(x + p, y + 32, num, 20, MONO, INK3, ls=.08))
    out.append(text(x + p, y + 68, title, 28, TITLE, NAVY, weight=700))
    out.append(lines(x + p, y + 100, desc))
    if tag:
        out.append(f'<line x1="{x+p}" y1="{y+h-34}" x2="{x+w-p}" y2="{y+h-34}" class="hl"/>')
        out.append(text(x + p, y + h - 11, tag, 19, MONO, INK3))
    return "".join(out)
GS = 44
def gate(cx, cy, label, s=GS, with_label=True):
    d1 = f"M{cx},{cy-s} L{cx+s},{cy} L{cx},{cy+s} L{cx-s},{cy} Z"; s2 = s - 6
    d2 = f"M{cx},{cy-s2} L{cx+s2},{cy} L{cx},{cy+s2} L{cx-s2},{cy} Z"
    o = f'<path d="{d1}" class="nd"/><path d="{d2}" class="nd"/>'
    if with_label: o += text(cx, cy + 7, label, 19, MONO, NAVY, anchor="middle")
    return o
def gnote(cx, cy, ls_):
    n = len(ls_); return lines(cx, cy - GS - 14 - (n - 1) * 27, ls_, anchor="middle")
HS = 88
def human(cx, cy, ls_, s=HS):
    d = f"M{cx},{cy-s} L{cx+s},{cy} L{cx},{cy+s} L{cx-s},{cy} Z"; n = len(ls_); lh = 30
    return f'<path d="{d}" class="hm"/>' + lines(cx, cy - (n - 1) * lh / 2 + 8, ls_, 22, lh, BODY, RED, "middle", 500)
def wire(pts, dashed=False, arrow=True):
    d = "M" + " L".join(f"{x},{y}" for x, y in pts); m = ' marker-end="url(#arr)"' if arrow else ""
    return f'<path d="{d}" class="wr{" dsh" if dashed else ""}"{m}/>'
def above(x, y, s, anchor="middle", fill=INK2): return text(x, y - 13, s, 20, BODY, fill, anchor=anchor)
def below(x, y, s, anchor="middle", fill=INK2): return text(x, y + 27, s, 20, BODY, fill, anchor=anchor)
def dot(x, y): return f'<circle cx="{x}" cy="{y}" r="3.5" class="jn"/>'
def stop(x, y): return f'<line x1="{x-12}" y1="{y}" x2="{x+12}" y2="{y}" class="wr"/>'
def rg(x, y, w, h): return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" class="rg"/>'
def titleblock(skill, nodes, gates, loops, sheet="1 / 1", rev="2"):
    cells = [("SKILL", skill, 220), ("REV", rev, 70), ("DATE", DATE, 170), ("NODES", str(nodes), 80), ("GATES", str(gates), 80), ("LOOPS", str(loops), 80), ("SHEET", sheet, 90)]
    bw, bh = sum(w for _, _, w in cells), 56; x, y = W - bw, H - bh
    out = ['<g class="tb">', f'<rect x="{x}" y="{y}" width="{bw}" height="{bh}" class="nd"/>']; cx = x
    for i, (k, v, w) in enumerate(cells):
        if i: out.append(f'<line x1="{cx}" y1="{y}" x2="{cx}" y2="{y+bh}" class="hl"/>')
        out.append(text(cx + 10, y + 17, k, 12, MONO, INK3, ls=.14)); out.append(text(cx + 10, y + 44, v, 19, MONO, NAVY)); cx += w
    out.append('</g>'); return "".join(out)
def legend(x, y):
    out = [wire([(x, y), (x + 54, y)]), text(x + 66, y + 7, "主流程", 19, BODY, INK2),
           wire([(x + 150, y), (x + 204, y)], dashed=True), text(x + 216, y + 7, "回路：回滚 / 重跑", 19, BODY, INK2),
           gate(x + 410, y, "", s=17, with_label=False), text(x + 440, y + 7, "闸门：不过不许往下走", 19, BODY, INK2)]
    hx = x + 692
    out.append(f'<path d="M{hx},{y-17} L{hx+17},{y} L{hx},{y+17} L{hx-17},{y} Z" class="hm"/>')
    out.append(text(hx + 30, y + 7, "人在场的点", 19, BODY, INK2)); return "".join(out)
DEFS = '<defs><marker id="arr" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 Z" style="fill:var(--navy)"/></marker></defs>'

def macuse():
    o = []
    ya, yb = 110, 410
    ca, cb = ya + NH / 2, yb + NH / 2      # 198 / 498
    top_y = 56; ret_y = 366; loop_a = 326
    # ---------- 上排
    x01, w01 = 40, 220
    g1x = 340
    x02, w02 = 420, 290
    x06, w06 = 790, 250
    g3x = 1110
    x07, w07 = 1190, 210
    rx, ry, rw, rh = 1420, ya - 30, 290, 246
    x08, w08 = 1440, 250
    o.append(node(x01, ya, w01, "01", "探测", ["架构、字典、端口、AX", "30 秒；硬试要 20 分钟"], "probe.sh"))
    o.append(gate(g1x, ca, "G1")); o.append(gnote(g1x, ca, ["有接口？", "字典·CLI·端口"]))
    o.append(node(x02, ya, w02, "02", "走接口", ["Chromium 系起端口走 CDP", "Blender 走 bpy，设置走深链"], "mac open --cdp · cdp.js"))
    o.append(node(x06, ya, w06, "06", "回读", ["发送键亮了？进列表了", "工具返回 success 不算"], "effect=confirmed…"))
    o.append(gate(g3x, ca, "G3")); o.append(gnote(g3x, ca, ["状态变了？", "副作用有了？"]))
    o.append(node(x07, ya, w07, "07", "取证", ["截图落项目目录", "原图与加工件分离"], "只 mv 不 rm"))
    o.append(rg(rx, ry, rw, rh))
    o.append(text(rx + 14, ry + rh - 12, "回流区 · 收工硬步骤", 19, MONO, INK3, ls=.04))
    o.append(node(x08, ya, w08, "08", "回流", ["能变成工具行为吗？", "能→改代码；不能→进档案"], None, double=True))
    o.append(wire([(x01 + w01, ca), (g1x - GS, ca)]))
    o.append(wire([(g1x + GS, ca), (x02, ca)])); o.append(above((g1x + GS + x02) / 2, ca, "有", fill=INK3))
    o.append(wire([(x02 + w02, ca), (x06, ca)])); o.append(above((x02 + w02 + x06) / 2, ca, "零焦点", fill=INK3))
    o.append(wire([(x06 + w06, ca), (g3x - GS, ca)]))
    o.append(wire([(g3x + GS, ca), (x07, ca)]))
    o.append(wire([(x07 + w07, ca), (x08, ca)]))
    c03 = x01 + w01 / 2
    o.append(wire([(g1x, ca + GS), (g1x, ret_y), (c03, ret_y), (c03, yb)]))
    o.append(above((g1x + c03) / 2, ret_y, "没有：去看图点击"))
    e04 = 350 + 60
    o.append(wire([(g3x, ca + GS), (g3x, loop_a), (e04, loop_a), (e04, yb)], dashed=True))
    o.append(above((g3x + e04) / 2, loop_a, "suspected_noop：回去重看，不算失败"))
    o.append(wire([(x08 + w08 / 2, ya), (x08 + w08 / 2, top_y), (c03, top_y), (c03, ya)], dashed=True))
    o.append(above((x08 + w08 / 2 + c03) / 2, top_y, "app 档案：下次开工先读；坐标只用来对照，不用来点"))
    # ---------- 下排
    x03, w03 = 40, 250
    x04, w04 = 350, 270
    g2x = 700
    hx = 900
    x05, w05 = 1050, 270
    o.append(node(x03, yb, w03, "03", "看图", ["截图＋收据＋AX 元素表", "图上像素直接当坐标"], "mac see"))
    o.append(node(x04, yb, w04, "04", "写入", ["先 postToPid 直投进程", "截图差分判生效"], "mac op --dry 预演"))
    o.append(gate(g2x, cb, "G2")); o.append(gnote(g2x, cb, ["投递生效了？"]))
    o.append(human(hx, cb, ["用户在动？", "等他停手", "最多 15 秒"]))
    o.append(node(x05, yb, w05, "05", "借焦点", ["前台 / 遮挡 / 锁三道闸再借", "四角取景框，半秒还回"], "报出实测借了几秒"))
    o.append(wire([(x03 + w03, cb), (x04, cb)]))
    o.append(wire([(x04 + w04, cb), (g2x - GS, cb)]))
    o.append(wire([(g2x + GS, cb), (hx - HS, cb)])); o.append(above((g2x + GS + hx - HS) / 2, cb, "否", fill=INK3))
    o.append(wire([(hx + HS, cb), (x05, cb)])); o.append(above((hx + HS + x05) / 2, cb, "空闲", fill=INK3))
    o.append(wire([(hx, cb + HS), (hx, cb + HS + 26)], arrow=False)); o.append(stop(hx, cb + HS + 26))
    o.append(text(hx, cb + HS + 56, "等不到：拒绝，不抢", 20, BODY, INK2, anchor="middle"))
    jx = x06 + w06 / 2
    o.append(wire([(g2x, cb - GS), (g2x, ret_y), (jx, ret_y)], arrow=False))
    o.append(above((g2x + jx) / 2, ret_y, "生效"))
    o.append(wire([(x05 + w05, cb), (1360, cb), (1360, ret_y), (jx, ret_y)], arrow=False))
    o.append(above((1360 + jx) / 2, ret_y, "借完立刻还"))
    o.append(dot(jx, ret_y))
    o.append(wire([(jx, ret_y), (jx, ya + NH)]))
    o.append(legend(40, 700))
    o.append(titleblock("huashu-mac-use", 8, 3, 3))
    return "".join(o)

CSS = '''
:root{--ivory:#F6F3EC;--navy:#0B2545;--red:#8B2E2E;--ink2:#3E4A5E;--ink3:#7A8494;--hair:rgba(11,37,69,.3);--hair2:rgba(11,37,69,.16);--paper:#FFFFFF;
--title:"Baskerville","Songti SC","STSong","SimSun","宋体",serif;--body:"PingFang SC","Hiragino Sans GB","Microsoft YaHei",sans-serif;--mono:"Menlo","Monaco","Consolas","Courier New",monospace}
*{box-sizing:border-box;margin:0;padding:0}
html,body{width:1920px;height:1080px;overflow:hidden;background:var(--ivory);color:var(--navy);font-synthesis:none;-webkit-font-smoothing:antialiased;font-family:var(--body)}
section.page{position:relative;width:1920px;height:1080px;padding:64px 96px 56px;display:flex;flex-direction:column}
.top{display:flex;justify-content:space-between;align-items:flex-end;padding-bottom:16px;border-bottom:1px solid var(--hair)}
.tracker{font:20px/1 var(--body);letter-spacing:.2em;color:var(--navy)}
.mark{position:relative;width:192px;height:1px;margin-bottom:6px}.mark i{position:absolute;top:0;left:128px;width:64px;height:1px;background:var(--navy)}
.foot{border-top:1px solid var(--hair);padding-top:16px;min-height:44px;display:flex;justify-content:space-between;gap:48px;font:20px/1.5 var(--body);color:var(--ink2);margin-top:auto}
.foot .mono{font-family:var(--mono);font-size:19px;color:var(--ink3)}
.m-diagram{display:flex;flex-direction:column;padding:22px 0 0 0;min-height:0}
.m-diagram .head{display:flex;justify-content:space-between;align-items:flex-end;gap:64px;padding-bottom:16px}
.m-diagram .kicker{font:20px/1 var(--mono);letter-spacing:.2em;color:var(--red)}
.m-diagram h1{margin-top:14px;font:700 40px/1.25 var(--title);color:var(--navy);white-space:nowrap}
.m-diagram .sub{max-width:760px;font:22px/1.55 var(--body);color:var(--ink2);text-align:right;padding-bottom:4px}
.m-diagram svg.sheet{display:block;width:100%;height:auto;overflow:visible}
.m-diagram .wr{fill:none;stroke:var(--navy);stroke-width:1;stroke-linejoin:miter;stroke-linecap:butt}
.m-diagram .wr.dsh{stroke-dasharray:7 5}
.m-diagram .nd{fill:#FFF;stroke:var(--navy);stroke-width:1}
.m-diagram .nd.dsh{stroke-dasharray:6 4}
.m-diagram .hm{fill:#FFF;stroke:var(--red);stroke-width:1.5}
.m-diagram .jn{fill:var(--navy)}
.m-diagram .hl{stroke:var(--hair);stroke-width:1}
.m-diagram .rg{fill:none;stroke:var(--hair);stroke-width:1;stroke-dasharray:3 4}
'''
PAGE = '''<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>huashu-mac-use · 工作流程图</title><style>{css}</style></head>
<body><section class="page">
  <div class="top"><div class="tracker">怎么做到的</div><div class="mark"><i></i></div></div>
  <div class="m-diagram">
    <div class="head"><div><div class="kicker">huashu-mac-use · 工作流程图</div><h1>读随便读，写要过闸，每一步回读</h1></div>
    <p class="sub">先探测再选层：有结构接口就零焦点走接口；没有才看图点击，写操作先后台投递，判不出生效才借焦点，借前过闸、半秒还回。收工把教训写进工具</p></div>
    <svg viewBox="0 0 {w} {h}" width="100%" xmlns="http://www.w3.org/2000/svg" class="sheet">{defs}{body}</svg>
  </div>
  <div class="foot"><span>一条主路零焦点，一条备用路过闸借焦点；两条路都要回读，都要回流</span><span class="mono">github.com/alchaincyf/huashu-mac-use</span></div>
</section></body></html>'''
html = PAGE.format(css=CSS, w=W, h=H, defs=DEFS, body=macuse())
open(os.path.join(HERE, "流程图-图纸.html"), "w").write(html)
print("✓ 流程图-图纸.html")
