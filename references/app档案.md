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

`# 核对 2026-09-01`

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

## WPS Office · 实测 2026-08-30

> 收录理由：没装 Microsoft Office 的机器上，`.docx / .xlsx / .doc / .xls` 全归它。
> 公司档案库里的合同、报价单、工资表全是这两类，绕不开。

```yaml
bundle: com.kingsoft.wpsoffice.mac
磁盘名: wpsoffice.app                # 🔴 全小写、无空格，与显示名「WPS Office」不同
窗口owner: WPS Office
架构: 自有框架（Contents/Frameworks/office6），非 Electron
版本: 7.2.2                          # 核对 2026-08-30

L0: 无 CLI；`open -a /Applications/wpsoffice.app <文件>` 是唯一稳的入口
L1 AX: 未测
截图: ✅ 后台零焦点直接截到（内容完整，字号版式都在）

🔴 窗口模型: **单窗口多标签**，这是它最容易翻车的一点 ——
   打开第二份文档**不新开窗口，而是复用同一个 window id，只换标签**。
   实测：docx 与 xlsx 先后打开，两次都是 id=17107，只有 title 变了。
   → 想截先打开的那一份，**再 `open -a` 它一次把标签切回来**，window id 不变，
     然后重新截图。别去 `mac windows` 里找第二个 id，没有那个 id。
   → 每次截图前用 `mac windows | grep WPS` 核对 title，**title 才是当前标签的真身**。
文档类型判读: 顶部标签图标 W=文字 / S=表格 / P=演示；截图里能一眼确认打开对了没有
```

`# 核对 2026-08-30`

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
