# per-app 操控档案

> 🔴 **易腐声明。这份文件里的坐标、窗口尺寸、按钮位置、菜单层级、端口号、界面文案全部是易腐信息，
> 产品一改版就废。**
>
> **坐标不是用来点的，是用来事后对照的。**动手一律先 `mac shot` 截现场、按锚点描述在图上找到控件、
> 量出归一化坐标再点（流程见 SKILL.md 第三节的 L2 子流程）。
> 点完了才回来看这份档案：**点中说明档案仍有效；点空说明已改版，立刻改档案。**
>
> **绝不要把这里的坐标当成结论写进别的地方。**
>
> 真正耐用的是「行为」那几行：架构、AX 能不能用、有没有本地端口、回车是不是发送、
> 输入走哪条路。这几条改版后大概率还成立，坐标一定不成立。

## 怎么维护这份档案（三条操作指令）

1. **每个易腐字段行尾带 `# 核对 YYYY-MM-DD`。** 没有日期的行按「未核对」处理。
2. **核对第一步是比版本号**，取法：
   `defaults read "<App路径>/Contents/Info.plist" CFBundleShortVersionString`。
   🔴 **版本变了，本条所有坐标一律作废，行为层（AX / 端口 / 回车语义）全部重测**，不要试着复用。
3. **坐标对不上就地改那一行并更新该行日期，不新增条目**（同一个 app 永远只有一条记录）。
   行为层与档案不符属于重大变更：重跑 `probe.sh` 覆盖整个 yaml 块，并把标题里的日期换掉。

**坐标怎么记**：三元组 = 归一化坐标（4 位小数，1% 的量化误差在 1440 点宽上就是 ±14 点）
+ 锚点描述（截图上靠什么认出这个控件）+ 实测时的窗口尺寸。
核对判据：量出来的归一化坐标与档案差 > 0.02 就改档案。

**怎么增补新 app**：跑 `bash $SKILL_DIR/scripts/probe.sh <app名>`，
把输出照下面的 yaml 骨架誊一遍；probe 探不到的「输入 / 发送 / 借焦点 / 坑」四行，
实测之后自己补。所有条目都要写实测日期。

---

## 豆包工作（DoubaoWork）· 实测 2026-09-01（整条重写，推翻 08-30 版）

🔴 **08-30 那版把架构判成「原生」，是错的，而且这个错让整条路线走歪了一整轮。**
它内嵌完整 Chromium，**CDP 可开，L0 直接可用**——根本不用碰坐标和焦点。

```yaml
bundle: com.work.pc.doubao
版本: 2.27.10              # 核对 2026-09-01（08-30 是 2.26.8）
进程名: DoubaoWork          # System Events 用这个
窗口owner: 豆包工作           # mac windows 用这个（两边不同名！）
架构: 🔴 内嵌 Chromium 147.0.7727.149（不是原生！）
  Frameworks 藏在 Contents/Helpers/DoubaoWork Browser.app/Contents/Frameworks/
  顶层 Contents/Frameworks/ 是空的 —— 08-30 只看顶层，所以判成了原生
  渲染窗口属于子进程「豆包工作浏览器」(DoubaoWork Browser)，
  主进程 DoubaoWork 的同位置同尺寸窗口是个壳

L0: ✅✅ CDP —— 唯一推荐路线
  启动: open -a /Applications/DoubaoWork.app --args --remote-debugging-port=9333
        （zsh 下 --remote-allow-origins=* 要加引号，否则 no matches found）
  验证: curl -s --noproxy "*" http://127.0.0.1:9333/json/version
  target: doubaowork://doubaowork-chat/chat 是主界面（用 `cdp.js 9333 list` 挑）
  URL scheme: doubaowork://（已注册，路由格式仍未挖到）
  AppleScript: 无字典
  ⚠️ 默认启动时也开着两个高位端口（实测 53768 / 49853），但**都不是 CDP**：
     49853 是自定义 HTTP，所有常见路径全 404；53768 无响应。别在这上面耗时间，
     直接用 --remote-debugging-port 重启。
L1 AX: ❌ 控件级不可用（AXManualAccessibility → -25205 明拒）
  ⚠️ 09-01 见到 `可编辑控件=1 AXTextField`，看着有戏，但没必要试——CDP 全面胜出
L2 坐标: ⚠️ 能用但脆弱，只在无法重启 app 时退而求其次
  窗口会被移动（origin 实测从 (0,33) 变成 (916,33)）、窗口 id 会过期、
  多 agent 同机时抢焦点——这三件事 CDP 全都免疫

输入: ✅ CDP `Input.insertText`（等价输入法上屏，tiptap/ProseMirror 认账，发送键会变蓝）
      ❌ 别用 el.value= / textContent=，典型的「字进 UI 但 app 不认」
点击: 🔴 **DOM 的 el.click() 对侧栏项和输入框下方的 chip 无效**（实测三次全部 0 变化），
      必须用 `Input.dispatchMouseEvent` 发真实指针事件 → cdp.js 的 `mouse` 命令
截图: ✅ CDP `Page.captureScreenshot` —— 2400×1600，无窗口红绿灯，比 mac shot 更适合做配图，
      且窗口被完全遮挡 / 在别的 Space 都能截
发送: ⚠️ 回车只换行，必须点发送按钮（这条 08-30 的结论仍然成立）
借焦点: **0 秒**。CDP 全程不碰焦点
```

**DOM 锚点**（易腐，仅供对照；变了就现场重挖）：

| 控件 | 选择器 / 找法 |
|---|---|
| 输入框 | `[contenteditable=true]`，类名 `tiptap ProseMirror` |
| 侧栏对话项 | `a.group\/conversation-item`，按 innerText 匹配标题，`href` 是 `/chat/<id>` |
| 输入框下方 chip（技能/项目/本地电脑…） | `div.overflow-visible-item-*`，**类名带随机后缀，别写死**；按 textContent 找到后打临时 id 再用 `mouse` 点 |
| 执行环境切换 | `button` 里 textContent 为「本地电脑」/「云电脑」，点开后选项是浮层里的 span |

**一条通用手法**：`querySelector` 选不中的元素（类名带 hash、有多个同类），
用 `eval` 遍历 DOM 按 textContent 找到目标 → 给它 `el.id='hs-xxx'` 打临时标记 → 再 `mouse '#hs-xxx'`。
比写复杂选择器稳，也不怕类名变。


**🔴 2026-09-12 补：两个让人绕远路的坑（都真实踩过）**

1. **先确认 9333 上是谁。** 本机的 Google Chrome 也可能监听 9333（比如浏览器自动化工具在用）。
   直接 `curl 127.0.0.1:9333/json/version` 会拿到 Chrome 的响应（Chrome/153），
   看着像通了，但 `/json/list` 是 0 个 target——于是很容易误判成「豆包新版本封了 CDP」。
   **判据：豆包工作的 CDP 报的是 Chromium 147.0.7727.149；报 153 的那个是 Chrome。**
   动手前先 `lsof -iTCP:<port> -sTCP:LISTEN -P -n` 看 COMMAND 是 `DoubaoWor` 还是 `Google`。
   建议直接用 9444 之类不冲突的端口。

2. **`open --args` 对已在运行的 app 是空操作，`mac open --relaunch` 也救不了。**
   返回「运行中=是(pid XXXXX)」且 pid 不变 = 根本没重启，参数没进去。
   2026-09-17复测修正：先确认没有待保存内容，再用`osascript -e 'tell application "DoubaoWork" to quit'`正常退出，随后`open -g -a /Applications/DoubaoWork.app --args --remote-debugging-port=9444`成功带入参数。不能把强杀作为默认重启方法。

3. **发送键 id 会变，两个都要试。** 短文本是 `#chat_input_send_button`；
   **长文本（实测 245 字）insertText 之后变成 `#flow-end-msg-send`**，前者会报「元素未找到」。
   稳妥写法：`[...document.querySelectorAll('[id*=send]')]` 挑 `offsetParent!==null` 的那个。

4. **退到坐标点击是危险的，别退。** 输入框为空时，右下角那个位置是**麦克风**（长按录音，提示「松手发送，按 Esc 取消」）；
   有文字时它才变成发送键。2026-09-12 用 `mac type` 投递带空格的文本，两次触发长按录音，
   在用户房间里放着视频的情况下开了近两分钟麦克风。**空格是触发源。**
   CDP 路线完全免疫这一条——这也是为什么它是唯一推荐路线。

`# 核对 2026-09-12`

**2026-09-17补：原生Office选区与后台录屏**

- 主任务可用`doubaowork://doubaowork-chat/chat/<id>`只读复开。路由返回时正文可能仍空，必须等预期任务文字出现再录，不能把暂时空白当作任务丢失。
- Playwright用`/json/version`返回的WebSocket地址连接；本次HTTP自动探测带尾斜杠的`/json/version/`返回400。主页面中的Office另有iframe target，表格初次加载明显慢于聊天，等工具栏与真实网格出现再判断。
- 内置表格的名称框真实选中H4后，聊天输入区自动附H4选区卡。本次指令不含坐标，产物正确只把H4变粗；逐格核验其余值、公式、样式不变。证据只覆盖表格选区，不能外推网页框选编辑。
- ScreenCaptureKit跨Space首帧报-5822时，内嵌主页面`Page.startScreencast`可在后台采集真实界面，不需激活窗口。输出30fps包含静止帧保持，须记录收到的真实帧及时间，不能用封装帧率做产品性能宣传。

---

## 千问办公（QwenWorkCN）· 实测 2026-08-30

```yaml
bundle: cn.qwenwork.desktop.mac
进程名: QwenWorkCN
窗口owner: 千问办公
架构: Electron
版本: 未记录 —— 下次核对时补，取法见顶部第 2 条

L0:
  URL scheme: qwenwork-cn://
  本地端口: 高位随机（那次实测是 54365 / 54367）⭐
    🔴 Electron 高位端口极可能每次启动就变，**不要当常量**。
    现查: lsof -nP -iTCP -sTCP:LISTEN -a -p <pid>
    那次探到的是 JSON-RPC 2.0 服务：GET 返回 "Method not allowed"、
    POST 返回 -32003 Unauthorized → 有认证，token 未挖。
    **挖通了就是最优路线，值得后续投入**
L1 AX: ❌ 不可用，但失败方式很微妙，值得整段读：
  a) AX 树**只在窗口激活时可见**：窗口在后台时 axset 报「没有可编辑控件」，
     activate 之后同一条命令就能探到 1 个。所以 L1 在这里并不省掉 activate。
  b) axset 返回 err=0、读回是空字符串——**但截图显示字确实写进了输入框**（能写不能读）。
  c) 🔴 决定性的一条：**写进去了，可发送键仍是灰的**，底下还透着 placeholder 残影。
     说明只画进了 UI 层，没触发 input 事件，app 内部状态仍认为是空的，
     真点发送会发出空消息（Slate/React 自维护 state，不认 AX 写入）。
  → 判据必须是「发送键有没有由灰变亮」，不是 err、不是读回、也不是「截图看见字了」。
L2 坐标: ✅ 主力（CGEvent 投递真实事件，发送键会正常变亮）

输入: ✅ mac type <pid> "文本" global（CGEvent 全局 Unicode）有效，不需要剪贴板
      ⚠️ Cmd+V 粘贴实测失败过一次（原因未定，全局投递更稳）
复杂交互: ✅ 已验证可行（判据用「状态变化」，不要用界面文案——文案最易腐）
  模型选择器: 点下拉 → 菜单弹出 → 点选项 → 判据是右下角模型名区域的内容发生变化
  技能调用: 输入 "/" 唤出技能浮层 → 判据是浮层出现
            （当日输入框提示文字是「/输入以筛选」，但文案会变，不要拿它做断言）
借焦点: 约 2.4–3.0 秒（含菜单等待）    # 核对 2026-08-30
```

**易腐坐标**（实测窗口 1320×800，仅供点完之后对照）：

| 控件 | 归一化 (x, y) | 锚点描述（截图上怎么认） |
|---|---|---|
| 输入框 | (0.6053, 0.4163) | 窗口中部的输入区 · 文字标签待下次现场核对时补 |
| 模型下拉 | (0.7689, 0.5013) | 输入区下方一行控件里靠右的那个下拉，点开会弹菜单 |
| 发送按钮 | (0.8765, 0.5013) | 与模型下拉同一行、更靠右 |

`# 核对 2026-08-30`
**易腐观测**（当日界面上看到的三档模型、技能列表等）不写进这里当结论，用时现看现截。

---

## WorkBuddy · 实测 2026-08-30

```yaml
bundle: com.workbuddy.workbuddy
可执行名: Electron          # ⚠️ pgrep -x Electron 会撞上别的 Electron 应用，用 pid 或 bundle id 作主键
架构: Electron
版本: 5.4.7 / Chrome 138.0.7204.251   # 核对 2026-09-02（08-30 是 5.3.14，已漂移）

L0: ⭐⭐ CDP —— ✅ 本轮已端到端实证跑通，是三个 app 里唯一做到「零焦点占用」的
  open -a WorkBuddy --args --remote-debugging-port=9333
  # 9333 只是任选的一个空闲端口，被占就换任意 >1024 的
  # 默认启动没有 CDP，只有另一个端口上的 app 自有 HTTP 服务，所有常见路径都 404
  验证: curl -s --noproxy "*" http://127.0.0.1:9333/json/version
  target 结构: page（app 外壳，file://）+ iframe（真实内容，站点 URL）
  用户在 WorkBuddy 实测项目里已有完整 CDP 工具链（cdp_helper.py），
  路径现找: mdfind -name cdp_helper.py
  🔴 **调它之前必须 export no_proxy="127.0.0.1,localhost" NO_PROXY="127.0.0.1,localhost"**
     Python 的 urllib/requests 会读 http_proxy 环境变量，**连 127.0.0.1 也走代理**，
     报 `HTTPError: 502 Bad Gateway`——看起来像服务坏了，实为本地代理配置。
     curl 侧对应的是 --noproxy "*"。排查顺序：先用 curl --noproxy 确认服务活着，再查调用方代理。
L1 AX: ❌ 可编辑控件 0 个
截图: 🔴 **screencapture 对它无解**（2026-09-02 实测）——窗口在别的 Space + Electron 后台不保留渲染帧，
  mac shot 直接 could not create image，ScreenCaptureKit 对跨 Space 窗口一律 -3811，CGWindowListCreateImage 已废弃。
  ✅ **唯一出路 CDP**：同处境下 cdp.js shot 拿到 736KB 完整界面、53 种颜色、零焦点。默认端口全非 CDP（18488/39099/50727… 都不认 /json/version），
  必须 `mac open WorkBuddy --cdp <端口> --relaunch`（会重启，先跟用户说）。mac shot 失败时会自动探到已开的 CDP 端口并指过去，不重复重启。
  ⚠️ 退出慢：terminate 后要 10 秒以上才真的退，内核已把超时放到 30 秒。
  ⚠️ 装待定更新（2026-10-10 实测，5.7.6→5.7.7）：osascript quit 会被拦下、缩回托盘不退出；改用 `kill -TERM <主进程>` 4 秒内正常退出，再 `open -g -a WorkBuddy` 启动时自动安装 ~/Library/Caches/com.tencent.workbuddy.mac.BundleMigration 里 phase=prepared 的更新。这样重启不带 CDP 端口，要用 CDP 得再 --relaunch。
L2 坐标: 可用但没必要——有 CDP 就走 CDP

对比（同一批任务实测）: L0/CDP 借焦点 0 秒、直接拿结构化文本；
L2 借焦点 1.9–3.0 秒、只能截图判读像素。**走对层和走错层是数量级差异。**
```

**已知坑**（来自用户的 WorkBuddy 操作手册，本轮未复验）：
- composer 是 Slate 编辑器，**DOM 和 state 会各自骗人，唯一可信的是 Slate state**
  （从 contenteditable 的 `__reactFiber$` 往上爬，手册记录 hop 15）。
  实测出现过 DOM 显示 344 字、实际提交 210 字。
- 截图要在 page target 上做，iframe target 会报
  `Command can only be executed on top-level targets`。
- **上传走系统文件框，CDP 注入的文件到不了主进程 → 这条永远够不着**，认出来就交还用户。
- Clash 必须 rule 模式，global 模式下相关域名被推去国外节点，全线 502。

---

---

## 爱奇艺 · 实测 2026-08-30

> 收录理由不是「以后要常操作爱奇艺」，而是它是**非 AI 类、原生架构**的对照样本。
> 前面三个 app 全是 AI 客户端、全是 Electron 或自绘，结论有严重的样本偏差。

```yaml
bundle: com.iqiyi.player
可执行名: qiyimac          # 显示名「爱奇艺」，磁盘名也是「爱奇艺.app」
架构: 原生（Frameworks 里只有 libswift_Concurrency.dylib）
版本: 17.8.0               # 核对 2026-08-30

L0: URL scheme qips（路由格式未挖）；无 AppleScript 字典；无本地端口
L1 AX: ❌ 控件级为空。hit-test 命中的是 AXWindow 本身，说明只实现了窗口级 AX
L2 坐标: ✅ 主力，且**非常干净**——CGEvent 全局投递一次成功，点哪个菜单跳哪个
截图: ✅ 后台零焦点直接截到，完全不打扰用户

输入: mac op 一条命令搞定，焦点占用 0.54-0.57 秒
发送: 输入即触发联想，无需回车
后台写入: ✅ postToPid（mac op --bg）实测可用——微信/Chrome 占前台时把字投给爱奇艺进程，零焦点、不切 Space，字进了搜索框（2026-09-02）。
  ⚠️ 但 postToPid 的**鼠标点击**不稳（✕ 清除键点不掉），只有**键盘投递**可靠。要点控件仍回借焦点的 mac op。
坑: ⚠️ 窗口曾在别的 Space，activate 时 origin 出现瞬时中间值 -1127（真实值 166）
```

**这个 app 贡献的两条通用结论**：
1. **原生 app 后台截图毫无障碍**，和 Chromium 系形成清晰对照。
2. **全窗像素差分在它身上完全失效**——首页轮播图一直在播，全窗变化率恒高。
   逼出了「只看落点邻域」的差分设计。

---

## 剪映专业版 · 实测 2026-08-30

> 同样是对照样本：**CEF 架构**，且是「找不到、不好找」的定位难题标本。

```yaml
bundle: com.lemon.lvpro
磁盘名: VideoFusion-macOS.app      # 🔴 与显示名「剪映专业版」完全无关
架构: CEF（Chromium Embedded Framework）—— 和 Electron 同源
版本: 11.3.13090（外壳）  # 核对 2026-09-02（08-30 是 11.2.13042，已漂移）

L0: ⭐ CDP 可用！open -a "<绝对路径>" --args --remote-debugging-port=9334
    实测通了：Chrome/121.0.6167.86，但只有一个 about:blank target
    → **主界面不在 CEF 那一层**，CDP 探通了也够不着控件。CEF 常只包部分界面
L1 AX: ❌ 控件级为空
L2 坐标: ✅ 可用
截图: ⚠️ **窗口级差异**——主窗后台截得干干净净（2026-09-02 复验 185 色 1.3MB），
      「版本更新」小弹窗怎么都是空图（2026-09-02 复验：后台、前台、全屏三种都读不到——
       全屏截图拍到的是当前可见 Space 上的别的窗口，说明 activate 报成功但 Space 没真的切过去）

🔴 磁盘上有两份同 bundle id（11.2.13042 与 11.2.0），启动必须用绝对路径
边界: 剪映的草稿操作走 huashu-jianji（草稿 JSON 路线），本 skill 不重复造
```

**这个 app 贡献的四条通用结论**（都已写进 SKILL.md 和 probe.sh）：
1. **CEF 要当 Electron 对待**，一样试 `--remote-debugging-port`。
2. **CDP 通了不等于够得着**——先确认目标控件在不在 web 那一层。
3. **后台能否截图是窗口级的，不是 app 级的。**
4. **同 bundle id 多副本**是真实存在的静默坑。

---

## 预览（Preview）· 实测 2026-08-30

> 收录理由：**文档类取证的默认落点**。看合同、看发票、看图，十次里有八次窗口是它。

```yaml
bundle: com.apple.Preview
磁盘路径: /System/Applications/Preview.app    # 🔴 系统 app 不在 /Applications
窗口owner: 预览                                # 中文系统上不叫 Preview
版本: 11.0                                    # 核对 2026-08-30

L0: 无需要——`open -a <绝对路径> <文件>` 就是它的接口
L1 AX: 未测（取证任务不需要写操作）
截图: ✅ 后台零焦点直接截到，原生 app 的典型表现

窗口模型: 一个文档一个窗口（实测 PDF 与 PNG 拿到两个独立 window id）
         ⚠️ 但系统「标签页偏好设置」若设成「总是」，会合并成标签——
            截图前先核对窗口 title 是不是你要的那个文件名，别只认 window id
```

**一次实录**：`open <某.pdf>` 之后 `mac windows` 里**根本没有对应窗口**，
只有一个先前打开的 PNG 窗口。换成 `open -a /System/Applications/Preview.app <某.pdf>` 立刻出窗。
**没出窗不等于 app 挂了，先怀疑是文件被 LaunchServices 派给了别的 app**（见下面的共性结论 7）。

`# 核对 2026-08-30`

---

## WPS Office · 实测 2026-09-08（版本未变，就地补行为层）

> 收录理由：没装 Microsoft Office 的机器上，`.docx / .xlsx / .doc / .xls` 全归它。
> 公司档案库里的合同、报价单、工资表全是这两类，绕不开。
> 🔴 **先读本节最后一行**：本地 docx/xlsx/pptx 的读写改，多数根本不该来操控这个 app。

```yaml
bundle: com.kingsoft.wpsoffice.mac
磁盘名: wpsoffice.app                # 🔴 全小写、无空格，与显示名「WPS Office」不同
窗口owner: WPS Office
架构: 自有框架（Contents/Frameworks/office6），非 Electron
版本: 7.2.2                          # 核对 2026-09-08

L0: 无 AppleScript 字典；URL scheme 有 ksowps/ksoet/ksowpp/ksopdf（未验证参数格式）
    本地端口 61491 与 127.0.0.1:4709 都在，但 HTTP 无响应，不是 CDP  # 核对 2026-09-08
    `open -g -a wpsoffice <文件>` 是唯一稳的入口，且 -g 不抢焦点、新标签落在当前 Space
L1 AX: ⚠️ 分区可用，别信 probe 的一句话结论。窗口在当前 Space 时有 31 个元素，
    菜单栏（开始/插入/页面/引用/审阅/视图/工具）全部可 eN@json 点；
    **正文区完全不在 AX 里**（自绘），probe 报的「可编辑控件=0 → L1 无望」是
    窗口跨 Space 时测的假象（AX 只见当前 Space），窗口回来后重跑就有了。
截图: ⚠️ 2026-09-08 复测**推翻「后台直接截到」**：同一天、同一台机、可编辑的 docx 窗口，
    `mac see` 与 `mac shotfg` 双双判空（同一时刻 App Store 窗口和全屏 screencapture 都正常）。
    → **别在 WPS 截图上纠缠**：直接 `screencapture -x` 全屏 + `sips -Z 1400` 缩放。
      换算：全屏图缩到 1400 宽时，图上 1px = 1512/1400 ≈ 1.08 屏幕点，
      窗口内坐标 = 屏幕点 − 窗口 origin（本例 (0,33)）。# 核对 2026-09-08

🔴 窗口模型: **单窗口多标签**，这是它最容易翻车的一点 ——
   打开第二份文档**不新开窗口，而是复用同一个 window id，只换标签**。
   实测：docx 与 xlsx 先后打开，两次都是 id=17107，只有 title 变了。
   → 想截先打开的那一份，**再 `open -a` 它一次把标签切回来**，window id 不变，
     然后重新截图。别去 `mac windows` 里找第二个 id，没有那个 id。
   → 每次截图前用 `mac windows | grep WPS` 核对 title，**title 才是当前标签的真身**。
文档类型判读: 顶部标签图标 W=文字 / S=表格 / P=演示；截图里能一眼确认打开对了没有

🔴 写操作: **正文区不响应 postToPid，必须走 clickin**  # 实测 2026-09-08
   `mac op` 的后台档（postToPid 投点击）在 WPS 正文区**光标根本不动**，
   随后的文字会落到文档开头 —— 而 op 的落点差分会因为「整段文字被顶下去」
   而报 `effect=confirmed`，**这是假阳性，最贵的一个坑**。
   正确姿势两条命令：`mac clickin <id> <x> <y> @图` 定位（0.2s，不激活不抢焦点）
   → `mac type <pid> "文本"`（走 postToPid，零焦点）。
   菜单栏则用 `mac clickin <id> eN@see.json`，AX 坐标准且免看图。
输入吞吐: 一次 type 182 字 / 0.58 秒，零丢字，撤销栈正常 —— **大段文字一次投完，别分段**
验证锚点: 🔴 **状态栏左下角「字数: N」** —— 这是应用状态指示器级判据，比截图看字可靠得多。
   写前写后各截一次，字数没变就是没进去。
批注（审阅）流程 · 实测 2026-09-08 全程零焦点，约 3.5 秒:
   双击选词（两次 clickin 间隔 0.25s）→ clickin eN@json 点「审阅」
   → clickin 点「插入批注」（审阅工具条左起第 7 个）→ mac type 写正文。
   批注框出现即带账号署名与时间戳；截图右侧批注栏可直接取证。
   ⚠️ 双击选词只适合短词。**长锚点（>10 字）改走查找选中**：
   Cmd+F → 打字 → 查找下一处 → 关对话框 → 点「插入批注」，锚点范围精确到整句，
   状态栏「字数: 43/4158」当场证明选区是 43 字。实测 3 条批注的锚点全部正确。
删除批注: 点进批注框 → 点功能区「删除批注」（「插入批注」右邻）。
   ⚠️ Cmd+Z 撤不掉已生成的批注，别指望它——今天靠这招才清掉误建的空批注。

🔴 查找（Cmd+F）三个坑 · 2026-09-08 全踩，先看这条能省三轮:
   ① **范围跟随光标上下文**：光标停在批注框里按 Cmd+F，查找范围自动变成「批注」，
      于是连正文里肉眼可见的「违约责任」都报「无法找到」。对话框底部
      「在以下范围中查找(I)」下拉切回「主文档(M)」立刻正常。
   ② **不循环**：从光标处往后搜，越过目标就报「无法找到」，不会绕回开头。
      先 `mac key <pid> 115 cmd`（Cmd+Home 到文首）再搜，或点「查找上一处(B)」反向搜。
   ③ **Esc 不保证关掉对话框**，它留在屏上会挡住功能区点击——今天因此误建了一条空批注。
      关它要点「关闭」按钮。
   选区判据两条，比截图可靠：状态栏「字数: 选中/全文」；`mac key <pid> 8 cmd` + `pbpaste`。
```

🔴 **但先想清楚要不要开这个 app。** docx/xlsx/pptx 本身就是 zip+XML，
本地文件的读、写、批注、修订用 `python-docx` / `openpyxl` 直接改，
实测**加 3 条批注 0.003 秒**，比屏幕操控快三个数量级，且 WPS 打开后
锚点高亮、署名、连线全部正确认账（2026-09-08 双向验证）。
**只有这几种情况才值得操控 app**：文件不在本地（云文档 / app 内部状态）、
需要触发 app 自己的计算或渲染或导出、要的就是「界面上确实这样」的取证截图。
详见 `控制面详解.md` 的「文件格式层」。

`# 核对 2026-09-08`

---

## 系统设置（System Settings）· 实测 2026-08-30

> 收录理由：**唯一一个「用户口头一句话就要动」的系统面板**（关蓝牙、开热点、改声音输出），
> 而且它是本档案里第一个**读写分层实证得最干净**的样本。

```yaml
bundle: com.apple.systempreferences
窗口owner: 系统设置                  # 中文系统
架构: 原生（SwiftUI）

L0: ⭐ 直接跳面板，不用点侧栏 ——
    open "x-apple.systempreferences:com.apple.BluetoothSettings"
    ⚠️ 面板 id 在新版 macOS 改过名：蓝牙是 `com.apple.BluetoothSettings`，
       不是老写法 `com.apple.Bluetooth`。跳错会落在上次停留的面板上，
       **务必用 `mac windows` 核对 title 是不是目标面板名**——
       title 会跟着面板走（「蓝牙」→「显示器」），是判断跳没跳对的最快判据
    已验证的面板 id: com.apple.BluetoothSettings（蓝牙）
                     com.apple.Displays-Settings.extension（显示器）
    🔄 **窗口 id 是复用的**（同一次实测里蓝牙→显示器都是 id=18321、pid 不变）。
       ⚠️ 但 app 被重启过就会换一套（那轮 pid 25749 → 48891），
       **所以别缓存，每次重新 `mac windows` 取一次**——理由不是「一定会变」，是「可能变」
截图: ✅ 后台零焦点直接截到
点击: 🔴 **必须借焦点**——见下面那段实录

状态回读: 🔴 不要读 defaults ——
  `defaults read /Library/Preferences/com.apple.Bluetooth ControllerPowerState` 读不到（无输出）
  ✅ 可靠判据: system_profiler SPBluetoothDataType | grep -E "^ +State:"  → On / Off
  这是第 5 级证据（app 自己的状态），比截图看开关颜色更硬
```

**一次教科书式的分层实录（关蓝牙）**——三次点击，同一个坐标，三种结果：

| 第几次 | 做法 | 回显 | 实际 | 说明 |
|---|---|---|---|---|
| 1 | `clickin 18321 0.9274 0.0826` | `clicked (0,33)` | 没反应 | **归一化被静默取整成 0**，点在窗口左上角。回显里的 `(0,33)` 就是唯一线索 |
| 2 | `clickin 18321 670 71` | `clicked (670,104)` | 仍 `State: On` | 坐标全对，但**窗口在后台，收不到合成事件** |
| 3 | activate → 同一条 clickin → 还焦点 | `clicked (670,104)` | `State: Off` ✅ | 只差一个 activate |

**第 2 次是最值得记的**：回显一模一样、坐标一模一样、退出码一样，只有 app 状态不同。
**这就是「不回读就会连报三次成功」的活标本**，而且它同时证明了第六节那条
「读=完全后台，写=必须借焦点」不是洁癖，是硬约束。

**易腐坐标**（实测窗口 723×859，仅供点完之后对照）：

| 控件 | 窗口内点数 (x, y) | 锚点描述 |
|---|---|---|
| 蓝牙总开关 | (670, 71) | 右上角第一个卡片最右侧的胶囊开关，蓝=开、灰=关 |

`# 核对 2026-08-30`

⚠️ 关蓝牙前先看一眼「我的设备」里连着什么：**若键盘/鼠标/触控板走蓝牙，关掉就再也点不回来**
（那次实测只连着 AirPods，安全；但这个检查必须每次做）。
查法：`system_profiler SPBluetoothDataType | grep -A3 "Connected:"`

---

## 跨 app 的共性结论（这几条比上面任何一条坐标都耐用）

1. **至今实测的六个 app，AX 控件级全部不可用**：豆包工作明拒（`-25205`）、千问办公能写不能读、
   WorkBuddy 树断在 `AXWebArea`、爱奇艺与剪映只到 `AXWindow` 一层。
   样本已跨越「AI 客户端 / 视频 app / 剪辑工具」和「原生 / Electron / CEF」两个维度，
   方向一致——**默认假设 AX 不可用，直接备好 L2**，但仍然花 10 秒跑一次 `mac ax`：改版可能修好它。
   ⚠️ **别拿首次调用的结果下结论**：实测同一坐标首查返回 `AXMenuBar`、再查变成 `AXWindow`，
   AX 接口首次访问会给不完整的树。
2. **Electron 客户端优先找本地端口。**三个里有两个开着本地服务。
   这是比坐标高一个数量级的通道，**值得每次都花 30 秒探一下**。
   探端口记得加 `--noproxy "*"`，Clash 之类的本地代理会吞掉本地请求。
3. **输入方式各不相同**：剪贴板 / 全局 Unicode / CDP，各走各的。
   **没有一条通用输入路径**——这就是这份档案必须存在的理由。
   不过 `mac op` 的全局 Unicode 投递目前命中率最高（六个里五个可用），**默认先试它**。
4. **发送键行为不一致**：豆包回车不发送。**默认假设「回车能发」是错的，一律截图确认。**
5. **一个 app 有三个名字**：可执行名（`System Events` 用）、本地化显示名（`mac windows` 的 owner、
   也是用户说的那个）、磁盘文件名。三者可能两两不同——剪映是极端案例，
   磁盘叫 `VideoFusion-macOS.app`、显示叫「剪映专业版」、可执行名又是第三个。
   **匹配统一用 pid 或 bundle id 作主键；按名字找 app 用 Spotlight 的 `kMDItemDisplayName`**
   （`probe.sh` 已内置，且不要求 app 在运行）。

6. 🔴 **「后台能不能截到」不可预测，别按架构也别按 app 推断。**
   实测：千问办公（Electron）能截、WorkBuddy（Electron）不能；
   剪映主窗（CEF）能截、同一个 app 的版本更新弹窗不能。
   同架构两种结果、同 app 两个窗口两种结果。**一律用 `mac shotfg` 现场试一次**，
   它先试后台、判空、必要时才借焦点。
   （这条最初被我写成「Chromium 系一定截不到」，当天就被回归测试证伪——
   记在这里当反面教材：**样本三个就敢下普适结论，是这份档案最容易犯的错。**）

7. 🔴 **`open -a "<中文显示名>"` 在中文 macOS 上必然失败**，报
   `Unable to find application named '预览'`。`open -a` 认的是**磁盘上的 app 名或绝对路径**，
   不认本地化显示名——这是第 5 条「一个 app 三个名字」在 `open` 命令上的具体落点。
   **一律写绝对路径**：`/System/Applications/Preview.app`、`/Applications/wpsoffice.app`。
   （这也和「不要用 `open -b <bundleid>`」不冲突：禁 bundle id 是因为同 id 可能有多副本，
   绝对路径两个问题一起解决。）
   找路径的顺序：`ps -p <pid> -o comm=` 拿正在运行那份的真实路径最快；
   app 没在跑就用 `probe.sh` 内置的 Spotlight `kMDItemDisplayName` 查。

8. 🔴 **取证任务里，`open <文件>`（不带 `-a`）是不可靠的**——它把选择权交给 LaunchServices，
   而那份关联表可能是错的、可能被别的 app 抢注过。实测同一批文件：
   `.xlsx` 被派给了**文本编辑**（打开是一窗乱码，且顺带弹出一个空的「未命名」窗口）、
   `.pdf` **一个窗口都没出**。
   → **要截图取证就显式指定 app**，别赌默认关联。
   → 副作用要交代：错误关联留下的垃圾窗口是**你自己制造的 orphan**，
     按用户的「克制的修改者」规矩，**告诉他，不要自作主张关**——
     未保存的「未命名」窗口一关就弹存储 sheet，那是第七节的停手线。

9. **文档类 app 的窗口模型必须先确认是「一文档一窗口」还是「单窗口多标签」。**
   预览是前者，WPS 是后者。**后者会让「按 window id 收集多份文档」这个直觉整个失效**——
   id 相同、内容已经换了，不核对 title 就会连截两张同样的图还以为成功了。
   这是第八节「不要信工具返回值」在多文档场景下的变体：
   **window id 存在 ≠ 它现在显示的是你要的那份。**

---

## Google Chrome —— 「扩展够不着的那一小步」的例外用法  # 核对 2026-08-31

⚠️ **第七节的停手线仍然成立：浏览器里的事默认全部交给 huashu-chrome。** 这里记的是它的一个
真实缺口——**hover 触发的菜单**：扩展发的合成 `mouseover` 只能把 `aria-expanded` 改成 `true`，
组件库（实测 mantine）的 dropdown 根本不渲染，`role="menuitem"` 查出来是 0 个。
键盘（focus + ArrowDown）、`.click()`、CDP 的 real click 全部无效——real click 会直接
执行按钮的默认动作，而不是展开菜单。

**配方**（LibTV 画布的「逐帧拉片 → 深度动作捕捉」实证，两次成功）：

1. huashu-chrome 里选中目标、`eval` 拿按钮坐标：`screenX + rect.x + rect.width/2`（Y 同理）
2. `osascript -e 'tell application "Google Chrome" to activate'` —— **必须先激活**，
   否则 hover 打在压着它的那个窗口上（实测第一次就是这么打空的）
3. `mac hover <x> <y> 1400`
4. **回 huashu-chrome `eval` 查 `[role="menuitem"]` 的实时坐标**，别用固定偏移
5. `mac click <chromePid> <菜单项x> <菜单项y>` —— 中间没有 mouseMoved，菜单不会收
6. 后续的普通按钮回到 huashu-chrome 点，最后把焦点还给原来的 app

**坑**（都实际踩过）：

- 🔴 **`window.screenY` 只在该标签页是活动标签时才可信。** 用户切走标签后它会返回 0，
  算出来的屏幕坐标偏一整个浏览器 chrome 的高度，hover 打在别人的页面上。
  **每次取坐标前先 `tabs(select, focus:true)`。**
- 🔴 **画布类应用里工具栏浮在节点上方，节点一旦贴着视窗顶部，工具栏就在视窗外**（`rect.y` 为负）。
  扩展的 scroll 工具推不动 react-flow（它要原生 wheel），**用 `mac scroll` 真实滚轮把画布推下来**。
  直接改 `.react-flow__viewport` 的 transform 也不行——React 下一次渲染就重置，点击时已经飘走了。
- 菜单项相对按钮的偏移在同一版本里是稳定的（实测两次都是正下方 157px），但**这是易腐信息**，
  只用于事后对照，不要拿来点。

**更该先试的一条**：这类「UI 上唯一入口」的功能，先用 `network`（或在页面里 hook fetch/XHR）
把请求体抓出来，多半能直连接口批量跑，比每次借焦点点菜单可靠一个数量级。
LibTV 那次抓到 `POST /api/task/generation/create`，此后 9 次提取全部走 curl，一次都没再碰浏览器。

### 第二个例外：`chrome.google.com/webstore/*`（含开发者后台 devconsole）· 实测 2026-09-05

扩展在这些页面一律被拒（`The extensions gallery cannot be scripted`），主 profile 又不开放 CDP。
**但 Chrome 的 AppleScript `execute javascript` 不受这条保护约束**——零焦点、跨 Space、DOM 全权，
读 textarea、set value + dispatch input、点按钮、走两层 mat-dialog 全部可用（当晚从「已拒绝」
改文案到「待审核」全程用它）。前提是「查看 → 开发者 → 允许 Apple 事件中的 JavaScript」已开（实测机器已开）。

```applescript
tell application "Google Chrome"
  repeat with w in windows
    repeat with t in tabs of w
      if URL of t contains "devconsole" then return execute t javascript "document.title"
    end repeat
  end repeat
end tell
```
导航用 `set URL of t to "..."`。JS 太长就写进文件、`on run argv` 里 `read POSIX file ... as «class utf8»` 读进来。
Material 表单要用原型 setter 写 value 再派发 `input`，看字数计数器变了才算模型收到。
比 9-02 那套「另起独立 profile 的 Chrome + Playwright connect_over_cdp」省掉一次重新登录（Google 对新 profile 必要求通行密钥）。

---

## 微信（WeChat）· 实测 2026-09-02

```yaml
bundle: com.tencent.xinWeChat
窗口owner: 微信
架构: 原生（非 Electron/CEF）
版本: 未记录 —— 下次核对时补

窗口模型: 🔴 两个窗口易混——「微信 (窗口)」常在别的 Space（后台截出来是空图）；
  「微信 (聊天)」才是能操作的主窗。一律取 mac windows 里 on=1 的那个。
L1 AX: ❌ 不可用——AX 树极大，`mac ax` / 遍历 AXTextField 直接超时（>30s）。别在微信上走 AX。
截图: 🔴🔴 **窗口级 `mac shot -l` 是过期/缓存渲染，滚动位置和真实前台不一致！**
  实测：-l 截图显示聊天列表顶部是 A/B/C，但真实前台顶部是「文件传输助手 / 好友」。
  按 -l 截图量的坐标去点，点到的是别的行（会话没切、标题没变）。
  → **微信必须用全屏 `screencapture -x -o` 定位坐标和验证结果，不能信窗口级 -l。**
点击: activate 微信 后用全局 `mac click <pid> <全屏逻辑坐标>`（全屏坐标=全屏像素÷2）。
发送: ✅ **回车即发送**（key code 36）。
输入焦点坑: 🔴 点偏了（比如想点搜索框但坐标不准）会把字打进「当前会话的消息输入框」，
  不是搜索框。发送类任务这一步最危险——**打完字必须全屏截图核对①会话标题=目标人 ②输入框内容正确，再回车**。
置顶区: 顶部依次是 文件传输助手、置顶联系人（按 mac shot 看不到真实顺序，看全屏）。
```

**标准发消息流程（实测跑通，2026-09-02 给置顶「好友」发消息）**：
1. `mac windows 微信` 取 on=1 的「微信 (聊天)」窗口 id 和 pid。
2. `osascript -e 'tell application id "com.tencent.xinWeChat" to activate'` 切前台。
3. **全屏截图**定位目标会话行（别用窗口级 -l，它的滚动位置是假的）。
4. `mac click <pid> <全屏逻辑x> <全屏逻辑y>` 点会话行 → **全屏截图确认右侧标题=目标人**。
5. 点输入框 → `mac type <pid> "文本" global` → **全屏截图确认标题+输入框内容都对**。
6. 确认无误 → `mac key <pid> 36` 回车发送 → 全屏截图看气泡出现。
7. 还焦点。截图涉私聊内容，任务完只留发送确认、其余删。

`# 核对 2026-09-02`

## Blender · 实测 2026-09-06（4.4.3）

架构: 原生自绘，无 URL scheme、无 AppleScript 字典，AX 树基本为空。**但它有完整 L0：`/Applications/Blender.app/Contents/MacOS/Blender --background [file.blend] --python 脚本.py -- 参数`**，建模/材质/灯光/渲染全部走 bpy，不要碰坐标点击。
取证: `open -a Blender x.blend` 启动 GUI 后 `mac shot <id>` 后台截窗口正常（非 Chromium，后台渲染没问题）。`mac windows Blender` 列出主窗口 title=`<文件名> - Blender 4.4.3`，另有 5 个系统残留窗口默认隐藏。
GUI 预设: 后台模式里 `bpy.data.screens` 全套布局都在，可以先把每个 VIEW_3D 的 `shading.type="MATERIAL"`、`region_3d.view_perspective="CAMERA"` 设好再存盘，GUI 一打开就是想要的取证画面，省掉切视图的点击。
后台脚本三个坑（都会抛 KeyError，不是环境问题）:
- `read_factory_settings(use_empty=True)` 后新建材质 `use_nodes=True` 的节点树里**没有**默认 Principled BSDF，World 也没有 Background 节点，要自己 `nodes.new`。
- Hair 粒子 render_type=OBJECT 时实例缩放 = hair_length × size，原型物体自身尺寸再乘一次，毫米级原型直接消失。要精确摆放小物件就读 evaluated mesh 自己采样放实例（`evaluated_depsgraph_get()` → `obj.evaluated_get(dg).data.polygons`）。
- Metal GPU 要显式 `preferences.addons["cycles"].preferences.compute_device_type="METAL"` + `get_devices()` + 每个 device `use=True`，否则默认 CPU。M4 Pro 参考：1920×1440 / 512spp / 自适应+OIDN 降噪，约 2 分钟。

**追记 2026-09-06 晚（邮轮建模，另一会话）**：
- **要和别的实例并存时用 `open -n -g -a Blender --args /abs/x.blend`**。`-n` 起独立实例——当时另一个会话的 GUI 里开着未保存的 donut.blend，不带 `-n` 会把文件塞进那个实例并弹「未保存」模态框。`-g` 不抢焦点、窗口落在别的 Space（on=0），**但 `mac shot` 照样截到完整渲染画面（3024×1718，判空=否）**——正文「open -g 起的窗口从未渲染截不到」那条对 Blender 不成立（它是 Metal 自绘，不依赖窗口服务器的曝光事件）。
- 换视口预设不用借焦点：改脚本另存一份（`cruise_gui.blend` / `cruise_gui_wire.blend`），`kill` 自己起的那个 pid 再 `open -n -g` 重开，全程零焦点。**只 kill 自己起的 pid**，`ps -eo pid,etime,command | grep MacOS/Blender` 先看清哪个是别人的。
- bpy 三个新坑：① `ShaderNodeMix`（新版混合节点）的 `inputs["A"]` 按名取到的是 Float 槽不是 Color 槽，链色走 `ShaderNodeMixRGB`（Color1/Color2/Fac）最省事；② Ocean 修改器 `use_foam=True` 后波浪一调大 foam 属性会铺满整个海面，材质里混白色直接把海涂白——远景海面关 foam；③ Nishita 天空 `sun_rotation` 实测约定：0°=太阳在 +Y，90°=−X，180°=−Y，270°=+X（逆时针），别猜，4 张 480×270 的小图 30 秒测出来。
- 另一个会话在同机跑 Cycles 动画时，我的 2560×1440/192spp 单帧仍只要 1-2 分钟，GPU 争用可接受。

---

## Keynote（讲演）· 实测 2026-09-08（14.1）

> 收录理由：办公三件套里唯一有完整 AppleScript 字典的演示工具，
> 「一句话做份 deck」这类任务它是 L0 直通车，**一次鼠标都不用点**。

```yaml
bundle: com.apple.iWork.Keynote
显示名: Keynote 讲演                 # 磁盘名 Keynote.app，System Events 用 Keynote
架构: 原生 + Frameworks
版本: 14.1                           # 核对 2026-09-08

L0 AppleScript: ✅ 28 条命令，最优先。make / set / export / add chart / delete / duplicate 齐全
L1/L2/L3: 用不上。有 L0 就别看图
实测吞吐: 新建文档→换主题→4 页（含 3×3 表格）→导出 PDF = 2.3 秒，全程零焦点
```

**两个必踩的坑（都会直接报错停住）**

1. 🔴 **版式名跟着主题走，不能跨主题复用。**
   `slide layout` 列表是**每个文档自己的**：默认「基本（白色）」有「显著事实 / 仅标题 / 节 / 议程」，
   而「黑色」主题里这些**全都没有**，只有「标题 / 标题与项目符号 / 引文 / 空白 / 照片-3联」等。
   拿错名字报 `-1728 不能获得 slide layout "X"`。
   **建完文档第一件事是读它自己的列表**：`name of every slide layout of d`。
2. 🔴 **`set base slide` 必须在 document 作用域写全路径。**
   写成 `tell slide 1 … set base slide to slide layout "标题"` 报 `-10006`。
   正确：`set base slide of slide 1 to slide layout "标题"`（在 `tell d` 里）。

**可直接抄的骨架**（实测跑通，导出 PDF 落盘且 4 个 /Type/Page 对得上）

```applescript
tell application "Keynote"
  set d to make new document with properties {document theme:theme "黑色"}
  tell d
    set base slide of slide 1 to slide layout "标题"
    set object text of default title item of slide 1 to "标题"
    set s2 to make new slide with properties {base slide:slide layout "标题与项目符号"}
    set object text of default title item of s2 to "小标题"
    set object text of default body item of s2 to "第一条" & linefeed & "第二条"
    set s3 to make new slide with properties {base slide:slide layout "空白"}
    tell s3
      set tb to make new table with properties {row count:3, column count:3}
      tell tb to set value of cell 1 of row 1 to "表头"
    end tell
  end tell
  export d to (POSIX file "/abs/out.pdf") as PDF
end tell
```

验证要穿透到内容：`mdls` 的 `kMDItemPageCount` 对 Keynote 导出的 PDF 返回 null，
数 `/Type/Page` 对象数，或 `qlmanage -t` 转图看一眼。

`# 核对 2026-09-08`

---

## Microsoft Word · 实测 2026-09-08（16.105.3）—— 本机不可用

```yaml
bundle: com.microsoft.Word
版本: 16.105.3                       # 核对 2026-09-08
L0 AppleScript: 字典有 423 条命令，含 Word comment 类（author/comment text/scope）
                与 accept/reject all revisions —— 接口层面批注和修订完全可程序化
```

🔴 **但本机这台 Word 未激活，AppleScript 自动化整条走不通**（实测 2026-09-08）：
启动后停在**模板画廊模态**上（左下角有「激活」按钮，账号已登录），
`open file name` 无效、`make new document` 报 `-2710 不能生成类 document`、
`count of documents` 恒为 0。窗口 title 为空、后台截图判空，要 `shotfg` 才看得到画廊。

→ **本机 .docx 任务一律走 WPS 或直接改 XML**，不要在 Word 上浪费轮次。
→ 若哪天激活了，批注走 `make new Word comment at d with properties {note reference:r, comment text:"…"}`，
   其中 `r` = `text object of paragraph N of text object of d`；`open` 不返回引用，要用 `active document` 接。

`# 核对 2026-09-08`

---

## Microsoft Word · 实测 2026-09-08（首次收录）

> 收录理由：本机 Word 与 WPS 并存；合同类 docx 的**验收取证**用 Word 更接近对方所见。
> 🔴 结论同 WPS 节最后一行：docx 的批注/修订**不要用 GUI 做**，`python-docx` 直接写 OOXML 层。

```yaml
bundle: com.microsoft.Word
磁盘名: Microsoft Word.app
窗口owner: Microsoft Word
入口: 🔴 `mac open` **不收文件路径**（传 .docx 路径报「找不到 app」，退出码 1）——
     改用 `/usr/bin/open -a "Microsoft Word" <文件>`；`mac open` 只解析 app 名/显示名
截图: ✅ 后台零焦点直接截到；窗口 on=0（跨 Space）也判空=否，821x834 全渲染
翻页: `mac frontmost <pid>` → `mac key <pid> 121`（PageDown）；步长约半页到一页，
     别猜，用状态栏「第 N 页，共 M 页 / 字数」核对落点  # 本机状态栏是应用级判据
🔴 本机未订阅: 打开即顶部横幅「需要订阅才可编辑和保存」→ 文档**只读**。
     看 / 截 / 取证不受影响；要改走 WPS 或 OOXML 层
批注渲染 · 实测:
   `python-docx 1.2.0` 的 `Document.add_comment(runs, text=, author=, initials=)`
   写入的批注层，Word 打开后**右栏批注框（署名 + 正文）、被批注文字高亮、
   红色引用线全部正确**，3 条锚点各自落在对的段落（跨 run 的锚点也定位准）。
   ⚠️ 只有 `Document` 有 `add_comment`，`Paragraph` 没有；签名吃 `Run | Sequence[Run]`
```

`# 核对 2026-09-08`

## Google Chrome多进程寻址 · 实测2026-09-09

同时存在主Chrome和headless截图进程时，按应用名发送Apple事件会误连headless进程，`count windows=0`。本轮主浏览器窗口和47个标签完整，无需重启或重载扩展。用JXA的`$.SBApplication.applicationWithProcessIdentifier(pid)`按现场核实的主进程ID连接，再通过`valueForKey`枚举窗口和标签；执行JS使用`performSelectorWithObject('executeJavascript:', $(source))`，直接动态方法调用本轮不可用。该观察仅验证Chrome。商店上传及提审方法记录在huashu-chrome的`learnings`及仓库`docs/经验/chrome.google.com.md`。


## WorkBuddy历史过程截图 · 核对2026-09-17（5.5.6）

已完成任务默认只露最终结果，早先的警告和确认并不一定消失。先展开`.cr-collapse__header`中「已完成…」的那项，再展开标题为「等待你的回答」的历史工具记录，可同时读到原警告、删留范围、当时的问题和选中答案。它是历史回看，保留「已完成」状态，不能伪装成当前待确认。截图前用原生「收起侧边栏」按钮消掉任务悬浮提示；无需编辑DOM内容，也无需重放删除实验。CDP截图在后台跨Space仍有效。端口仅本轮为9412，使用前probe现查。


### iPhone镜像（2026-09-28真机补拍）

- 原生菜单点击可用，但小程序内容区曾反复无响应；本轮有效组合是HID事件源、先mouseMoved到落点并等250ms再click、事件后保持前台700ms。仅切HID源/延迟而不移动仍失败，补移动后标签、输入、按钮和滚动均成功。保留原工具的空闲/遮挡/Space检查。这组参数写在项目内的变体工具里，未修改通用内核。
- 2026-10-27复测（macOS 26.6.2、iPhone镜像1.6、iOS 27.0.1，微信小游戏）：跨Space对游戏画布使用`mac op --bg`时，动态战斗画面的像素变化曾被判成`effect=confirmed`，但暂停状态未改变；换到静态结算页点击「返回」，按钮无变化、结果为`suspected_noop`。只能说明本次后台投递未控制游戏，不能把动画差分或退出码当成实际点击成功。苹果开发模式USB直连方案首次加载DDI超时，尚未验证成功。
- Unicode合成输入无效，带flags的Cmd-V曾变成字母v；System Events实际command-down粘贴可用。粘贴前后应完整保存/恢复NSPasteboard所有类型。
- `screencapture -v -V 秒数 -l 窗口ID`可录镜像窗口；录像含窗口标题栏/阴影，交付节选需按实物核对裁切，不把节选长度当响应耗时。

## WorkBuddy 5.6.2 · 2026-09-28 MCP与任务引导实测

- 「专家·技能·连接器→连接器→自定义连接器→添加MCP」是JSON编辑器。界面展示配置路径 `~/.workbuddy/mcp.json`；仅在磁盘写文件，已打开的列表未立即刷新。通过编辑器保存后，列表出现新服务，首次需点「信任」；信任后实际加载成功（本次官方远程MCP为35/35工具）。不得把「写了配置文件」当连接成功。
- Monaco使用 `.native-edit-context`（role=textbox）；旁边 `.ime-text-area` 是readonly，不能当输入框。此次后台编辑器布局5px，普通snapshot漏掉控件。用CDP聚焦native-edit-context、Meta+A、Backspace，再一次插入紧凑单行JSON并点保存成功。多行插入曾报JSON无效；之后不要读回打印含Authorization的编辑器全文，只检查保存状态/服务名/工具数。任何截图也避开凭据编辑页。
- Playwright连接该端口时受本机代理影响，HTTP入口返回400；带NO_PROXY并先读取 `/json/version` 的webSocketDebuggerUrl，再connectOverCDP该ws地址可用。操作Electron，不另启干净浏览器。
- 任务运行时在输入框发送，默认是排队。队列每条的三个按钮依次是引导、编辑、删除；必须现hover核tooltip，不靠旧ref。第一个tooltip实测为「引导会话，消息将被发出但模型不会中断」，可把修复意见送入正在运行的轮次，省掉停任务重开。普通发送后看见文字排在队列中不代表模型已接收。
- 「技能→添加技能→上传技能」弹窗明确接受含SKILL.md的文件夹或.zip；单.md须含YAML名称与描述。此条只核验了界面格式说明，不能据此声称.skill后缀已验证可导入。


## WorkBuddy 5.6.2历史会话取景 · 2026-09-29 16:19:53+0800

- 对话内搜索显示命中数，不等于视口已经跳到命中消息。本次填关键词后需按Enter，再对已加载的`.cr-self-message`命中项scrollIntoView；长会话虚拟列表的初次估算位置可能落在后续回复。截图前必须核像素，不能只看搜索1/1。
- 历史完成态可展开`.cr-collapse__header`查看读取/生图记录；只证明旧任务过程，不与新补录的等待拼成同一次运行。


### 2026-09-29 20:22:14+0800 · WorkBuddy后台任务等待与素材落位

- 状态不能只读`.cr-message-list.innerText`：安全中心确认区在列表外，任务仍显示处理时长，会误判成模型还在运行。本次sed -i临时文件、mv/覆盖同名图片、测试清理均触发；每轮先用snapshot核是否出现「允许删除/取消删除」，再判断是否等待生成。不要选整会话始终允许。
- 生图后让客户端只回报原始文件路径，协调侧在已授权项目中复制为新稳定文件名并核SHA，保留源图，能避免重命名/覆盖反复触发确认。这里是保留原图的安全落位，不授权删除或绕过账号/系统权限。
- 队列消息不等于已发送给模型；hover核「引导会话」后点击才即时送达。任务换向时撤下自己过时、未执行的队列要求，原文先留项目任务书，避免新图完成又回头做旧图。


### WorkBuddy5.6.2独立文件窗口 · 2026-09-29

- 实测纠正「Input.insertText即可证明输入已进state」：独立文件窗口Slate可显示整段文字且发送按钮可用，但输入store的draft.content.blocks仍为空，点击发送无动作。只读核对React context的getSnapshot后发现此差异；补真实Input.dispatchKeyEvent(Space keyDown/up)，blocks同步、UI发送创建任务成功。不直接写内部store。
- HTML元素编辑需开启编辑→真实鼠标双击目标元素→同一CDP连接Input.insertText；DOM合成dblclick或跨连接选择后输入本轮未生效。用磁盘diff验收，不能只看工具return。
- HTML保存可能早于「退出编辑」：本轮标题手动改字后，退出前磁盘已变化；保存含编辑器附加的data-page-node-id/data-dm-ref和样式。承诺仅限实际内容变更，不能称源代码字节不变。
- 原生另存为弹窗可能在其他Space；CDP截图看不到系统sheet。mac windows/shot能确认，AX未必可访问，不强点或假装取消成功。


## Photos：生成实况照片的导入与往返验证（2026-10-06）

- 本轮实测4组JPG+MOV及2张静态图，一次AppleScript `import fileList into targetAlbum skip check duplicates false`返回6个media item，相簿也为6项；没有拆成10项。文件必须配好相同ContentIdentifier，MOV带still-image-time定时元数据。
- 不必先读照片库：用`PHLivePhoto.request(withResourceFileURLs:...)`可验证任意本地配对资源。3秒成片本轮使用1.5秒静帧，timed metadata也写1.5秒；不要把中间帧封面配到0秒标记。
- 强验证：仅对新建目标相簿执行`export selectedItems to destination with using originals`，回导得到6张JPG+4段MOV；4对再次通过PHLivePhoto识别。仅数相簿项目数不足以单独证明动态保留。
- 交给iPhone时从照片App的独立相簿走AirDrop，或用已开启的iCloud照片同步。原始JPG/MOV文件包作为备份；不能把只传JPG等同于传了Live Photo。操作中不必激活Photos，也不需要读取其它相簿内容。手机目标App的实际发布能力需另测，Apple格式验证不等于第三方App发布验证。


### WorkBuddy 5.7.6 · PPT元素AI编辑与回存（2026-10-07）

来源：PPT 编辑实测，实际客户端＋OOXML差分；适用本机5.7.6，不把端口/类名写成通用接口。
- 本轮CFBundleIdentifier为`com.tencent.workbuddy.mac`，旧档案的`com.workbuddy.workbuddy`不适用于此构建；启动/退出按实际app路径或现读Info.plist，勿硬编码旧bundle。
- 文字进入编辑并选中后、图片单击选中后，浮动工具栏可见「AI编辑」。点击先出现就地textarea「说说你想怎么修改」；确认会把选区与要求带到右侧对话，仍需点发送。不能只看浮动入口就声称已修改，也不要用全选重填composer的方法抹掉自动带入的选区引用。
- PPT原生文字和图片对象可分别定点操作。本轮AI改标题、AI改图片大小/位置、人改页脚，只有一页三个对象变；图片SHA未变，不能据此推广成图片像素编辑或图片内文字生成。
- AI执行保存会重载Office iframe，target id改变且回到第一页。每轮重新list定位iframe、再选目标页核看，不沿用旧target、不在封面上判断修改结果。
- SDK呈现的坐标可能按pt取整。本輪按260→208缩放，XML比值0.800023986；右缘差0.031pt、底缘差0.781pt。报约20%和实际误差，不把UI对齐说成像素级零误差。
- 原生文本人工接改用真实双击/选区/键盘输入，点击画布别处后「未保存」亮起，再点保存；重开同路径并比对文件，不能只凭AI说已保存。测试仅副本，来源文件SHA保留。


### WorkBuddy5.7.6 · 小程序预览与混元请求取证（2026-10-08）

- 生图工具completed回执只有prompt/status/localPath，未必含model。此版实际请求可在对应会话CLI日志的`[ImageService] buildBaseRequestBody`找到model与branch；按会话与时间对应原图。只摘相关行，不整行打印巨大产品配置；水印/参数说明不能单独证明模型。本轮图像请求已逐条核对，适用本版本日志结构。
- 小程序预览是独立CDP iframe target，主窗口Playwright `page.frames`本轮未枚举到它。可用cdp.js目标子串`/preview/`；每次刷新会从p1变p2/p3并更换target id，旧ref必须丢弃。主页面交互元素无差分不代表刷新失败，应核新iframe与画面。
- 本轮绝对/相对assets图片地址在官方预览均404；WorkBuddy在正式源代码中把小JPEG转data URI模块、require给imageSrc后，naturalWidth=512/naturalHeight=768与截图双检通过。没有改DOM假造界面；微信真机尚未验，不推广成真机通用结论。
- CDP采样视频的文件birthtime会晚于第一帧，不能用它直接推事件切点。本项目录制器已补`started_at_unix`，后续与逐帧elapsed联用；既有原片须抽帧校验切点，避免上一轮结果混进新案例。
- 验证范围：桌面预览、云服务开通、分享版本选择；不含账号绑定/正式发布/微信真机。


### Photos · HEIC实况封装与往返验证 · 2026-10-09

来源：本机3组实况实测。HEIC用ImageIO输出UTType.heic，在MakerApple字典键17写UUID；MOV用AVAssetWriter写同UUID content.identifier与1.5秒still-image-time。封面必须截同一时点；本轮首次封面时点选错，已在导入前修正片段并重验。

PHLivePhoto.request三组均识别；Photos一次导入6个配对资源＋5张JPG，得到8项（3实况＋5静态）。仅导出新相簿原件后再次识别，HEIC/MOV SHA256均与输入一致。此证据证明苹果本地格式与图库配对，不代表小红书手机实发验证，也不代表已发送到iPhone。


## 桌面层 AppKit＋WKWebView 自研应用（2026-10-09，本机验证）

来源：本机自研应用的原生窗口截图与测试结果；macOS26.6.2，AppKit＋WKWebView。仅限本应用实测，不推广到所有WKWebView程序。

- 桌面层窗口（desktopIconWindow+1）会被当前mac工具的excludeDesktopElements窗口列表过滤；CGWindowListCopyWindowInfo(.optionAll)能读到其真实窗口ID。直接screencapture截这一层失败，本轮通过本应用菜单「临时浮在应用窗口上方」后由mac shot成功取证，结束必须恢复。
- WKWebView被遮挡时，CSS过渡/rAF可能停在起始帧。只设inactiveSchedulingPolicy=.none未解决本例；程序已改为遮挡时不运行动画、同步更新transform与命中区域。不要用后台动画中的旧getBoundingClientRect作为最终穿透区域。
- WKWebView.takeSnapshot对本例3D背面表现与真实窗口截图不一致；真实窗口截图确认木质背面正确，而WebKit快照可能显示镜像正面。3D视觉验收以mac shot真实窗口为准。
- AX可从本应用的AXMenuBar找到菜单项，在唯一匹配并dry预演后使用AXPress。窗口截图与CGWindowIsOnscreen不代表下层Finder也在当前Space；Finder激活可能留在别的Space，需先核对，不能直接截预想的区域。


## ChatGPT桌面App · 2026-10-10 13:29 CST（26.924.22138，限定本机取材）

来源：本轮probe.sh、PlistBuddy、ps、mac windows/shot及实际窗口图。状态：下述读取已实测，写控制未验证。

- 本机/Applications/ChatGPT.app（26.924.22138）与/Applications/Codex.app（26.825.51511）共享bundle id com.openai.codex。使用绝对路径启动，并用ps确认真实可执行路径，不能仅按bundle或显示名判断版本。
- open -g绝对路径能后台启动；当前Space外on=0的主窗口，mac shot仍得到3024×1718非空图。AX windows=0、probe未发现CDP端口，只证明本轮控制面未找到，不推出所有版本均不能后台控制。
- sdef包含Chrome Suite的23条命令，但实际AppleScript枚举windows属性只返回空字段。字典存在不能当作整个原生App可脚本控制的证据。
- 本次启动恢复了旧项目文件夹信任提示，未经本任务授权不替用户信任它们。

### WorkBuddy5.7.7｜整卡复用发布补充｜2026-10-13
- 已验证：ImageGen改图要实际传image1及input_fidelity；仅prompt写「以参考图为底」而漏传image1会纯文生图。不同任务须以不同key-theme开头，工具按prompt前缀+秒级时间戳命名，同前缀并发曾10张只落盘5张。
- 已验证：原整卡含示例姓名/订单数，不能直接冒充问卷结果；图片内字句必须逐张实看，首次局部改字曾只改右格、漏掉中间格同一句，精确指明格位和短替换词后复核通过。通用构图的复用不等于编造用户历史。
- 适用本版本：wb.invoke miniprogram:publish 的formal任务服务回读auditing表示代码上传并提审成功，不等于published；沿已绑定applicationId/localDir，不新建/解绑账号。网页sites_deploy会残留旧静态文件，当前index不引用旧coffeedata即可避免加载，不能据目录本地删除声称线上文件已删除。


## macOS输入源 · 2026-10-22实测

- 环境：macOS 26.6.2；豆包、搜狗和系统拼音。验证：系统设置UI＋独立Swift/Carbon进程回读。
- TISDisableInputSource返回0后，独立进程及系统设置仍可能显示原输入源；不能只凭返回码宣告停用。此例系统设置中移除搜狗后才验证成功。原因未证实，不推断为所有系统的API缺陷。
- 输入源列表行的AX click可能不改变选择；先让列表获得焦点，再用上下键选择，按新AX状态核验后点击移除。
- 此例只剩豆包和ABC时，ABC的移除按钮禁用。保留系统备用布局，在已获授权的范围内关闭自动切换到文稿输入法、中英键切ABC、地球键切换；不要承诺删除所有系统备用输入源。
- 本经验不授权改其他用户的快捷键或后台服务。

## Xcode 26.2 Organizer · 2026-10-22实测

- `open -g -a Xcode`后，System Events点「Window→Organizer」菜单能成功（后台进程的菜单栏可读可点）。但Organizer窗口落在别的Space（`mac windows` 显示 on=0），之后 `mac ax` 返回 windows=0，System Events按名取窗口报-1728，坐标写操作也走不了。后台截图正常。
- 结论：只读看一眼可以全后台完成；要点侧栏的「Crashes」或切换产品，得请用户把窗口挪到当前桌面，或者由用户自己点。下载的崩溃数据落在 `~/Library/Developer/Xcode/Products/<bundleID>/<版本>/` 下，之后可以直接读文件，不用再操作界面。
- 仅此一例，未在其他Xcode版本验证。

## 豆包输入法设置 · 2026-10-27实测

- 本机豆包输入法1.0.2.7的设置是独立辅助应用，可直接执行 `open '/Library/Input Methods/DoubaoIme.app/Contents/Applications/DoubaoImeSettings.app'`，无需先操作菜单栏。
- 本轮启动后，窗口在当前Space，AX可读到「语音输入」「键盘输入」「外观」等设置项；这是本机该版本的观察，路径和界面可能随版本变化。

## FanBox2.17.0 · 后台新建终端可能转移焦点｜2026-10-30源码核对

- 来源：FanBox 安装包里的 app.asar 与源码仓库的 app.js、electron/main.js 哈希一致；渲染层`fanboxAgentCtl.onCreate`调用`term.newTab()`，后者会`activate(id)`并异步`xterm.focus()`。因此后台agent新建终端时存在切换标签和输入焦点的风险。
- 输入法合成：该版本xterm在`compositionupdate`展示预编辑文本，结束后才转交终端输入；FanBox的.cast旁路记录PTY输入输出，不保证记录尚未提交的合成内容。
- 这是源码分析，未通过语音输入或弹窗复现；不能据此认定任何某次丢字的原因。跨版本先核源码或运行包，经验不授权操作其他会话、读取语音或改用户设置。

## TRAE CN（2026-11-01，TRAE宝桌宠截图实测）
- 带 `--remote-debugging-port=9333` 启动时可走 CDP：页面 `Agent`（主窗口）、`Floating`（桌宠悬浮对话 floatingChat.html）、`floatingTaskStatus`、`floatingQuickActions`。窗口最小化或在别的桌面空间时 visibilityState=hidden，`cdp shot` 会挂住、innerText 为空。
- 桌宠开关在用户设置 `~/Library/Application Support/Trae CN/User/settings.json`：`trae.floatingChat.enabled`（true 才显示）、`trae.floatingChat.buttonIcon":"traebao"`；还受服务端动态配置 `iCubeApp.desktopPet.enable` 控制。改 settings.json 后约3秒桌宠出现在右下角。
- 悬浮框输入：`cdp.js 9333 insert Floating ".chat-input-v2-input-box-editable" "文本"` 后 `press Floating Enter` 可发出（零焦点）。悬浮框和主窗口是同一条全局TRAE宝对话。
- computer-use 在这台机上点 TRAE 窗口常被判「落在程序坞」拒绝；最小化用 osascript 激活后 cmd+M。验证状态：本次已验证。

## 夸克（Quark，com.quark.desktop）· 实测 2026-11-03（7.4.5.1026 trial3）

- 架构：Chromium 内核浏览器（Chrome/144），带 AppleScript 字典；AX 可编辑控件 0，直接走 CDP。`mac open "/Applications/Quark.app" --cdp 9333 --relaunch` 可通，重启后会话标签页自动恢复（进程号会变，回显「运行中=是」不代表没重启）
- 任务模式（Kbot）是网页：新标签页点「任务」后跳到 `https://www.qianwen.com/quarkchat`，target 用 title/url 子串 `quarkchat` 寻址。target id 的短前缀不认，要全 id 或子串
- 输入：`insert '[contenteditable=true]'` 上屏可用；`press Enter` 即发送。模型菜单 `.ms-trigger` 打开，选项 `.ms-option-label`（`text:` 选择器 mouse 不认，先 eval 打 data 属性再点）
- 焦点：CDP mouse/insert/shot 全程不抢焦点；`curl -X PUT /json/new` 新开标签会变成该窗口的活动标签，用户在用浏览器时别用，复用已有标签
- 语音浮窗（`Qwen Voice Agent` surface=orb/conversation）是隐藏窗口，后台 eval 会挂住超时，别读
- 额度：trial 会员一个 4—5 分钟的任务就触发「当前使用额度已达到上限」，全账号共用不分模型，按约 5 小时滚动刷新；长任务实测前先确认额度
