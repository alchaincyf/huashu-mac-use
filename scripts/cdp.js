#!/usr/bin/env node
// cdp.js — 通过 Chrome DevTools Protocol 操控内嵌 Chromium 的桌面 app
//
// 为什么存在：全局 CGEvent 点击要求目标窗口在前台且不被遮挡，多 agent 同时跑就会互相抢焦点，
// 跨 Space 时更会打错对象。CDP 完全不碰焦点、不碰屏幕坐标，用 DOM 选择器定位，
// 窗口被遮住、在别的 Space、甚至最小化都能操作。
//
// 前提：目标 app 用 --remote-debugging-port=<port> 启动（CEF / Electron 都吃这个参数）。
//   open -a /Applications/Xxx.app --args --remote-debugging-port=9333
//
// 用法：
//   node cdp.js <port> list
//   node cdp.js <port> snapshot <target> [--all]        # 列出可交互元素并打 ref（默认只列视口内可见）
//   node cdp.js <port> find  <target> '<文本>' [--role button] [--all]   # 按文本/aria-label/placeholder 模糊找元素
//   node cdp.js <port> wait  <target> <条件> [超时秒=10]  # 条件: css选择器 | text:<文本> | gone:<选择器>
//   node cdp.js <port> eval  <target> '<js表达式>'      # 返回值 JSON 化后打印
//   node cdp.js <port> click <target> '<选择器>'         # 真实 DOM click()
//   node cdp.js <port> text  <target> '<选择器>' '<文本>'   # 给输入框写值并派发 input/change
//   node cdp.js <port> mouse <target> '<选择器>'         # 渲染器级真实鼠标点击，之后打印 DOM 差分
//   node cdp.js <port> insert <target> '<选择器或空>' '<文本>'  # Input.insertText（输入法上屏），之后打印 DOM 差分
//   node cdp.js <port> press <target> <Enter|Escape|Backspace|Slash|At> [选择器]
//   node cdp.js <port> shot  <target> <输出路径> [选择器]  # 整页或单元素截图
//   node cdp.js <port> html  <target> [选择器]           # 打印 outerHTML（默认 body，截断 20000 字）
//   node cdp.js <port> act   <target> <脚本文件|内联脚本|->  # 一次会话顺序执行多步（见下）
//
// <target> 可以是 target id，也可以是 title/url 的子串（取第一个 type=page 的匹配）。
// 特殊值 auto = 第一个 type=page 且 url 不含 background 的 target。
//
// 选择器：所有接选择器的地方都接受 ref=eN 或 eN（snapshot/find 打出来的 ref），
// 内部转成 [data-hs-ref="eN"]。ref 是打在元素上的属性，页面刷新后失效，重新 snapshot 即可。
//
// wait 的退出码是三态：0 satisfied / 1 unsatisfied（条件本身无法评估，如选择器语法错）/ 2 unknown（超时）。
// 上层不能把 2 当成功。
//
// act 脚本：每行一步，# 开头是注释，参数含空格用双引号包住。
//   find "文本" [--role button]      # 结果第一条的 ref 记为 $last，后续步骤可用
//   mouse <ref或选择器>
//   insert <ref或选择器或-> "文本"    # - 表示不切焦点直接上屏
//   press Enter [ref或选择器]
//   wait <条件> [秒]
//   shot <路径> [选择器]
//   eval <js>
//   sleep <秒>
//   snapshot [--all]
// 任一步 wait 返回 unknown、find 零命中、元素找不到，就停下并打印已完成到第几步，退出码 2。
// 脚本参数可以是文件路径、内联多行字符串，或 - 表示从 stdin 读。

const [, , portArg, cmd, ...rest] = process.argv;
const PORT = portArg || '9333';
const BASE = `http://127.0.0.1:${PORT}`;
// 常见 http_proxy 指向本地代理，127.0.0.1 不能走它
process.env.NO_PROXY = [process.env.NO_PROXY, '127.0.0.1', 'localhost'].filter(Boolean).join(',');

function usage() {
  const src = require('node:fs').readFileSync(__filename, 'utf8');
  const lines = src.split('\n').slice(1);
  const end = lines.findIndex(l => !l.startsWith('//'));
  console.log(lines.slice(0, end).map(l => l.replace(/^\/\/ ?/, '')).join('\n'));
}

async function listTargets() {
  const r = await fetch(`${BASE}/json/list`);
  return r.json();
}

// auto：不能取「第一个 page」——Electron/内嵌浏览器常带隐藏页（picker / launcher / background），
// 实测某 app 的第一个 page 是隐藏的技能选择页，snapshot 出来是空的。
// 改成对每个候选页打分：可见视口面积 + 可交互元素数，取最高，并回显选中了谁，方便下次直接指定。
async function pickTargetAuto(targets) {
  const cands = targets.filter(t => t.type === 'page' && !/background|devtools/i.test(t.url));
  if (cands.length <= 1) return cands[0] || targets[0];
  let best = null, bestScore = -1;
  for (const t of cands) {
    let score = 0;
    try {
      const s = await connect(t.webSocketDebuggerUrl);
      try {
        score = await evaluate(s, `(document.visibilityState==='visible' ? innerWidth*innerHeight : 0)
          + document.querySelectorAll('button,a[href],input,textarea,[contenteditable],[role=button]').length * 1000`);
      } finally { s.close(); }
    } catch { score = 0; }
    if (score > bestScore) { bestScore = score; best = t; }
  }
  console.error(`auto → ${(best.title || '').slice(0, 30)} (${best.url.slice(0, 60)})  # 下次可直接指定这个 title/url 子串`);
  return best;
}
function pickTarget(targets, sel) {
  return (
    targets.find(t => t.id === sel) ||
    targets.find(t => t.type === 'page' && (t.title.includes(sel) || t.url.includes(sel))) ||
    targets.find(t => t.title.includes(sel) || t.url.includes(sel))
  );
}

function connect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    let id = 0;
    const pending = new Map();
    ws.addEventListener('message', ev => {
      const msg = JSON.parse(ev.data);
      if (msg.id && pending.has(msg.id)) {
        const { resolve: res, reject: rej } = pending.get(msg.id);
        pending.delete(msg.id);
        msg.error ? rej(new Error(JSON.stringify(msg.error))) : res(msg.result);
      }
    });
    ws.addEventListener('error', e => reject(new Error('WebSocket 连接失败: ' + (e.message || e.type))));
    ws.addEventListener('open', () =>
      resolve({
        send(method, params = {}) {
          return new Promise((res, rej) => {
            const mid = ++id;
            pending.set(mid, { resolve: res, reject: rej });
            ws.send(JSON.stringify({ id: mid, method, params }));
          });
        },
        close: () => ws.close(),
      })
    );
  });
}

async function evaluate(sess, expr) {
  const r = await sess.send('Runtime.evaluate', {
    expression: expr,
    returnByValue: true,
    awaitPromise: true,
    userGesture: true, // 有些控件只认用户手势触发的事件
  });
  if (r.exceptionDetails) throw new Error('JS 异常: ' + JSON.stringify(r.exceptionDetails.exception?.description || r.exceptionDetails));
  return r.result?.value;
}

const jsStr = s => JSON.stringify(String(s));
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ref=e3 / e3 → [data-hs-ref="e3"]；其它原样当 CSS 选择器
function resolveSel(s) {
  const m = /^(?:ref=)?(e\d+)$/.exec(String(s || '').trim());
  return m ? `[data-hs-ref="${m[1]}"]` : s;
}

// ---------- 可交互元素采集（在页面里执行） ----------
// 返回 [{ref, kind, text, match, x, y, w, h, disabled, visible}]
// ref 打在 data-hs-ref 属性上，已有的不改，保证同一元素多次采集 ref 稳定。
const COLLECT_JS = `(function(opts){
  const SEL = 'button,a[href],input,textarea,select,[contenteditable="true"],[contenteditable=""],[contenteditable="plaintext-only"],'
    + '[role="button"],[role="menuitem"],[role="tab"],[role="option"],[role="checkbox"],[role="switch"],[role="link"],[onclick]';
  const norm = s => (s == null ? '' : String(s)).replace(/\\s+/g, ' ').trim();
  const vw = window.innerWidth, vh = window.innerHeight;
  let counter = window.__hsRefCounter || 0;
  const out = [];
  for (const el of document.querySelectorAll(SEL)) {
    if (el.type === 'hidden') continue;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    const visible = r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'
      && r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw && !el.closest('[aria-hidden="true"]');
    if (!visible && !opts.all) continue;
    let ref = el.getAttribute('data-hs-ref');
    if (!ref) { ref = 'e' + (++counter); el.setAttribute('data-hs-ref', ref); }
    const tag = el.tagName.toLowerCase();
    const role = el.getAttribute('role');
    let kind = tag;
    if (role) kind = tag + '/' + role;
    else if (el.isContentEditable) kind = tag + '/editable';
    else if (tag === 'input' && el.type && el.type !== 'text') kind = 'input/' + el.type;
    // 文本回退链：可见文本 > aria-label > placeholder（含 tiptap 类编辑器子节点的 data-placeholder）> title > value
    //   > 图片 alt > svg title > 测试 id（图标按钮往往只有这个，显示成 #xxx）
    const ownId = el.id && !/^radix-/.test(el.id) ? el.id : '';
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-test-id') || el.getAttribute('name') || ownId;
    const parts = [norm(el.innerText), norm(el.getAttribute('aria-label')),
      norm(el.getAttribute('placeholder') || el.getAttribute('data-placeholder') || el.querySelector('[data-placeholder]')?.getAttribute('data-placeholder')),
      norm(el.getAttribute('title')), (tag === 'input' || tag === 'textarea') ? norm(el.value) : '',
      norm(el.querySelector('img[alt]')?.alt), norm(el.querySelector('svg title')?.textContent),
      testId ? '#' + norm(testId) : ''];
    const text = parts.find(Boolean) || '';
    const disabled = !!(el.disabled || el.getAttribute('aria-disabled') === 'true');
    out.push({ ref, kind, text: text.slice(0, 40), match: parts.join(' ').toLowerCase().replace(/\\s+/g, '').slice(0, 300),
      x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), disabled, visible });
  }
  window.__hsRefCounter = counter;
  return out;
})`;

async function collect(sess, opts = {}) {
  return (await evaluate(sess, `(${COLLECT_JS})(${JSON.stringify(opts)})`)) || [];
}

const fmtEl = e =>
  `ref=${e.ref} ${e.kind} "${e.text}" [${e.x},${e.y} ${e.w}×${e.h}]` + (e.disabled ? ' [disabled]' : '') + (e.visible ? '' : ' [hidden]');

function diffSnap(before, after) {
  const b = new Map(before.map(e => [e.ref, e]));
  const a = new Map(after.map(e => [e.ref, e]));
  const add = [], rem = [], chg = [];
  for (const [ref, e] of a) if (!b.has(ref)) add.push('+ ' + fmtEl(e));
  for (const [ref, e] of b) if (!a.has(ref)) rem.push('- ' + fmtEl(e));
  for (const [ref, e] of a) {
    const o = b.get(ref);
    if (!o) continue;
    const bits = [];
    if (o.disabled !== e.disabled) bits.push(`disabled ${o.disabled}→${e.disabled}`);
    if (o.text !== e.text) bits.push(`text "${o.text}"→"${e.text}"`);
    if (bits.length) chg.push(`~ ref=${ref} ${e.kind} ${bits.join(', ')}`);
  }
  const lines = [];
  for (const arr of [add, rem, chg]) {
    lines.push(...arr.slice(0, 8));
    if (arr.length > 8) lines.push(`  (${arr[0][0]} 还有 ${arr.length - 8} 条)`);
  }
  return lines.length ? lines : ['suspected_noop: 动作前后可交互元素无变化'];
}

// 动作前后各采一次可见交互元素，打印差分。settle 给 UI 一点反应时间。
async function withDiff(sess, action, settleMs = 300) {
  const before = await collect(sess);
  await action();
  await sleep(settleMs);
  const after = await collect(sess);
  for (const l of diffSnap(before, after)) console.log(l);
}

// ---------- 各命令 ----------
async function doEval(sess, expr) {
  const out = await evaluate(sess, expr);
  console.log(typeof out === 'string' ? out : JSON.stringify(out, null, 2));
}

async function doClick(sess, sel) {
  const out = await evaluate(
    sess,
    `(() => { const el = document.querySelector(${jsStr(resolveSel(sel))});
      if (!el) return 'NOT_FOUND';
      el.scrollIntoView({block:'center'});
      el.click();
      return 'clicked: ' + (el.innerText||el.getAttribute('aria-label')||el.tagName).slice(0,60); })()`
  );
  console.log(out);
  if (out === 'NOT_FOUND') throw new Error('元素未找到: ' + sel);
}

async function doText(sess, sel, value) {
  const out = await evaluate(
    sess,
    `(() => { const el = document.querySelector(${jsStr(resolveSel(sel))});
      if (!el) return 'NOT_FOUND';
      el.focus();
      const v = ${jsStr(value)};
      if (el.isContentEditable) { el.textContent = v; }
      else {
        const setter = Object.getOwnPropertyDescriptor(el.constructor.prototype,'value')?.set;
        setter ? setter.call(el, v) : (el.value = v);
      }
      el.dispatchEvent(new InputEvent('input',{bubbles:true,data:v,inputType:'insertText'}));
      el.dispatchEvent(new Event('change',{bubbles:true}));
      return 'typed into ' + el.tagName + ' len=' + v.length; })()`
  );
  console.log(out);
  if (out === 'NOT_FOUND') throw new Error('元素未找到: ' + sel);
}

async function doMouse(sess, sel) {
  // Input.dispatchMouseEvent：渲染器层面的真实鼠标事件，坐标是页面内 CSS 像素。
  // 比 el.click() 强一层——很多组件库（mantine/radix/tiptap 菜单）只认真实指针事件。
  // 仍然不需要 OS 焦点、不受窗口遮挡与 Space 影响。
  await withDiff(sess, async () => {
    const box = await evaluate(
      sess,
      `(() => { const el=document.querySelector(${jsStr(resolveSel(sel))}); if(!el) return null;
        el.scrollIntoView({block:'center'});
        const r=el.getBoundingClientRect();
        return {x:r.x+r.width/2, y:r.y+r.height/2, label:(el.innerText||el.getAttribute('aria-label')||el.tagName).slice(0,40)}; })()`
    );
    if (!box) throw new Error('元素未找到: ' + sel);
    for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) {
      await sess.send('Input.dispatchMouseEvent', {
        type, x: box.x, y: box.y, button: 'left', clickCount: type === 'mouseMoved' ? 0 : 1,
      });
    }
    console.log(`mouse click @(${box.x.toFixed(0)},${box.y.toFixed(0)}) → ${box.label}`);
  });
}

async function doInsert(sess, sel, text) {
  // 走 Input.insertText：等价于输入法上屏，React/Slate/Vue 的 state 会更新。
  // 比 DOM 的 el.value= 可靠得多——后者常见「字画进 UI 但发送键仍是灰的」。
  await withDiff(sess, async () => {
    if (sel && sel !== '-') {
      const r = await evaluate(sess, `(() => { const el=document.querySelector(${jsStr(resolveSel(sel))}); if(!el) return 'NOT_FOUND'; el.focus(); return 'focused'; })()`);
      if (r === 'NOT_FOUND') throw new Error('焦点元素未找到: ' + sel);
    }
    await sess.send('Input.insertText', { text: String(text ?? '') });
    console.log(`insertText: ${String(text ?? '').length} 字`);
  });
}

async function doPress(sess, key, sel) {
  // 真实键盘事件，用于快捷键与「/ 唤起菜单」这类只认 keydown 的交互
  const map = {
    Enter: { windowsVirtualKeyCode: 13, key: 'Enter', code: 'Enter', text: '\r' },
    Escape: { windowsVirtualKeyCode: 27, key: 'Escape', code: 'Escape' },
    Backspace: { windowsVirtualKeyCode: 8, key: 'Backspace', code: 'Backspace' },
    Slash: { windowsVirtualKeyCode: 191, key: '/', code: 'Slash', text: '/' },
    At: { windowsVirtualKeyCode: 50, key: '@', code: 'Digit2', text: '@', modifiers: 8 },
  };
  const k = map[key];
  if (!k) throw new Error('未知按键: ' + key + '（可用: ' + Object.keys(map).join('/') + '）');
  if (sel) {
    const r = await evaluate(sess, `(() => { const el=document.querySelector(${jsStr(resolveSel(sel))}); if(!el) return 'NOT_FOUND'; el.focus(); return 'focused'; })()`);
    if (r === 'NOT_FOUND') throw new Error('焦点元素未找到: ' + sel);
  }
  await sess.send('Input.dispatchKeyEvent', { type: 'keyDown', ...k });
  if (k.text) await sess.send('Input.dispatchKeyEvent', { type: 'char', ...k });
  await sess.send('Input.dispatchKeyEvent', { type: 'keyUp', ...k });
  console.log(`press: ${key}`);
}

async function doHtml(sess, sel) {
  const out = await evaluate(
    sess,
    `(document.querySelector(${jsStr(resolveSel(sel || 'body'))})?.outerHTML || 'NOT_FOUND').slice(0,20000)`
  );
  console.log(out);
}

async function doShot(sess, path, sel) {
  if (!path) throw new Error('shot 需要输出路径');
  let clip;
  if (sel) {
    clip = await evaluate(
      sess,
      `(() => { const el = document.querySelector(${jsStr(resolveSel(sel))}); if (!el) return null;
        el.scrollIntoView({block:'center'});
        const r = el.getBoundingClientRect();
        return {x:r.x, y:r.y, width:r.width, height:r.height, scale:1}; })()`
    );
    if (!clip) throw new Error('元素未找到: ' + sel);
  }
  const r = await sess.send('Page.captureScreenshot', {
    format: 'png',
    captureBeyondViewport: !!clip,
    ...(clip ? { clip } : {}),
  });
  const fs = require('node:fs');
  fs.writeFileSync(path, Buffer.from(r.data, 'base64'));
  console.log(`截图: ${path} (${(fs.statSync(path).size / 1024).toFixed(0)}KB) — 未借焦点，窗口可被遮挡`);
}

async function doSnapshot(sess, all) {
  const els = await collect(sess, { all });
  if (!els.length) { console.log(all ? '无可交互元素' : '视口内无可见交互元素（试试 --all）'); return; }
  for (const e of els.slice(0, 200)) console.log(fmtEl(e));
  if (els.length > 200) console.log(`... 共 ${els.length} 个，只列前 200；用 find "<文本>" 定位目标`);
}

const normQ = s => String(s || '').toLowerCase().replace(/\s+/g, '');

// 返回命中数组；零命中时打印候选。
async function doFind(sess, query, role, all) {
  if (!query) throw new Error('find 需要文本');
  const q = normQ(query);
  const els = await collect(sess, { all });
  const pool = role ? els.filter(e => e.kind === role || e.kind.endsWith('/' + role)) : els;
  const hits = pool.filter(e => e.match.includes(q));
  if (hits.length) {
    for (const e of hits.slice(0, 10)) console.log(fmtEl(e));
    if (hits.length > 10) console.log(`... 共 ${hits.length} 条命中，只列前 10`);
    return hits;
  }
  console.log(`not_found: "${query}"` + (role ? ` (role=${role})` : '') + (all ? '' : '（默认只搜视口内可见元素，可加 --all）'));
  const qset = new Set(q);
  const scored = pool.filter(e => e.match)
    .map(e => ({ e, score: [...new Set(e.match)].filter(c => qset.has(c)).length }))
    .filter(s => s.score > 0)
    .sort((x, y) => y.score - x.score || x.e.match.length - y.e.match.length)
    .slice(0, 5);
  if (scored.length) { console.log('最相近的候选:'); for (const s of scored) console.log('  ' + fmtEl(s.e)); }
  process.exitCode = 1;
  return [];
}

// 三态：返回 'satisfied' | 'unsatisfied' | 'unknown'
async function doWait(sess, cond, secs) {
  if (!cond) throw new Error('wait 需要条件');
  const timeout = (Number(secs) > 0 ? Number(secs) : 10) * 1000;
  let expr;
  if (cond.startsWith('text:')) expr = `(document.body?.innerText || '').includes(${jsStr(cond.slice(5))})`;
  else if (cond.startsWith('gone:')) expr = `!document.querySelector(${jsStr(resolveSel(cond.slice(5)))})`;
  else expr = `!!document.querySelector(${jsStr(resolveSel(cond))})`;
  const t0 = Date.now();
  while (Date.now() - t0 < timeout) {
    let ok;
    try { ok = await evaluate(sess, expr); }
    catch (e) {
      // 选择器语法错是确定的否定；页面导航中 evaluate 临时失败则继续轮询
      if (/SyntaxError|not a valid selector/.test(e.message)) { console.log(`unsatisfied: 条件无法评估 ${e.message.slice(0, 120)}`); return 'unsatisfied'; }
      await sleep(200); continue;
    }
    if (ok) { console.log(`satisfied (${((Date.now() - t0) / 1000).toFixed(1)}s)`); return 'satisfied'; }
    await sleep(200);
  }
  console.log(`unknown: timeout ${timeout / 1000}s`);
  return 'unknown';
}

// ---------- act：多步脚本 ----------
function tokenize(line) {
  const toks = [];
  const re = /"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)'|(\S+)/g;
  const unesc = s => s.replace(/\\(.)/g, (_, c) => (c === 'n' ? '\n' : c === 't' ? '\t' : c));
  let m;
  while ((m = re.exec(line))) toks.push(m[1] !== undefined ? unesc(m[1]) : m[2] !== undefined ? unesc(m[2]) : m[3]);
  return toks;
}

async function readScript(arg) {
  const fs = require('node:fs');
  if (!arg) throw new Error('act 需要脚本文件、内联脚本或 -');
  if (arg === '-') return fs.readFileSync(0, 'utf8');
  try { if (fs.statSync(arg).isFile()) return fs.readFileSync(arg, 'utf8'); } catch {}
  return arg;
}

class StopAct extends Error {}

async function doAct(sess, scriptArg) {
  const lines = (await readScript(scriptArg)).split('\n');
  let last = null;
  let step = 0;
  const sub = s => (s === '$last' ? (last || (() => { throw new StopAct('$last 为空：前面没有成功的 find'); })()) : s);
  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    step++;
    console.log(`[${step}] ${line}`);
    const [op, ...args] = tokenize(line);
    try {
      if (op === 'find') {
        const ri = args.indexOf('--role');
        const role = ri >= 0 ? args[ri + 1] : undefined;
        const all = args.includes('--all');
        const hits = await doFind(sess, args[0], role, all);
        if (!hits.length) throw new StopAct('find 零命中');
        last = 'ref=' + hits[0].ref;
      } else if (op === 'mouse') await doMouse(sess, sub(args[0]));
      else if (op === 'insert') await doInsert(sess, sub(args[0]), args[1]);
      else if (op === 'click') await doClick(sess, sub(args[0]));
      else if (op === 'text') await doText(sess, sub(args[0]), args[1]);
      else if (op === 'press') await doPress(sess, args[0], args[1] && sub(args[1]));
      else if (op === 'wait') {
        const r = await doWait(sess, sub(args[0]), args[1]);
        if (r !== 'satisfied') throw new StopAct('wait ' + r);
      } else if (op === 'shot') await doShot(sess, args[0], args[1] && sub(args[1]));
      else if (op === 'eval') await doEval(sess, line.replace(/^eval\s+/, ''));
      else if (op === 'sleep') await sleep((Number(args[0]) || 1) * 1000);
      else if (op === 'snapshot') await doSnapshot(sess, args.includes('--all'));
      else throw new StopAct('未知步骤: ' + op);
    } catch (e) {
      if (e instanceof StopAct || /未找到|NOT_FOUND/.test(e.message)) {
        console.log(`stopped: 完成 ${step - 1} 步，第 ${step} 步失败 — ${e.message}`);
        process.exitCode = 2;
        return;
      }
      throw e;
    }
  }
  console.log(`done: ${step} 步全部完成`);
}

// ---------- 入口 ----------
async function main() {
  if (!portArg) { usage(); return; }
  const targets = await listTargets();

  if (cmd === 'list' || !cmd) {
    for (const t of targets) console.log(`${t.type}\t${t.id}\t${(t.title || '').slice(0, 40)}\t${t.url.slice(0, 80)}`);
    return;
  }

  const t = (!rest[0] || rest[0] === 'auto') ? await pickTargetAuto(targets) : pickTarget(targets, rest[0]);
  if (!t) throw new Error(`找不到 target: ${rest[0]}\n可用的:\n` + targets.map(x => `  ${x.type} ${x.title} ${x.url}`).join('\n'));
  const sess = await connect(t.webSocketDebuggerUrl);

  try {
    if (cmd === 'eval') await doEval(sess, rest[1]);
    else if (cmd === 'click') await doClick(sess, rest[1]);
    else if (cmd === 'text') await doText(sess, rest[1], rest[2]);
    else if (cmd === 'mouse') await doMouse(sess, rest[1]);
    else if (cmd === 'insert') await doInsert(sess, rest[1], rest[2]);
    else if (cmd === 'press') await doPress(sess, rest[1], rest[2]);
    else if (cmd === 'html') await doHtml(sess, rest[1]);
    else if (cmd === 'shot') await doShot(sess, rest[1], rest[2]);
    else if (cmd === 'snapshot') await doSnapshot(sess, rest.includes('--all'));
    else if (cmd === 'find') {
      const ri = rest.indexOf('--role');
      await doFind(sess, rest[1], ri >= 0 ? rest[ri + 1] : undefined, rest.includes('--all'));
    } else if (cmd === 'wait') {
      const r = await doWait(sess, rest[1], rest[2]);
      process.exitCode = r === 'satisfied' ? 0 : r === 'unsatisfied' ? 1 : 2;
    } else if (cmd === 'act') await doAct(sess, rest[1]);
    else throw new Error('未知命令: ' + cmd);
  } finally {
    sess.close();
  }
}

main().catch(e => {
  console.error('错误: ' + e.message);
  process.exit(1);
});
