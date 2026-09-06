#!/bin/bash
# 能力探测——接手任何 app 的第 0 步，30 秒问清该走哪条路线。
# 用法: probe.sh <app名>        例: probe.sh 千问办公 / probe.sh WorkBuddy
# 只覆盖「静态 + L0 + L1」几行；「输入 / 发送 / 借焦点 / 坑」四行探不到，实测后自己补。
# 照 references/app档案.md 的 yaml 骨架誊，不要直接粘散行文本。
#
# 🔴 必须设 UTF-8：不设的话中文参数（本脚本第一个用例就是 probe.sh 千问办公）
# 会在变量传递中被拆成乱码字节。有些机器 env 里 LANG/LC_* 默认全空。
export LANG="${LANG:-zh_CN.UTF-8}"
export LC_ALL="${LC_ALL:-zh_CN.UTF-8}"

# 🔴 PATH 补全：agent 跑在某些终端 app（实测 FanBox）里时 PATH 缺 /usr/sbin，
# lsof 直接 command not found，而本脚本对 lsof 的调用都带 2>/dev/null——
# 结果是**静默报「本地端口: 无」**，把最值钱的 CDP 通道整条藏起来。
# 2026-09-01 实测踩到：豆包工作明明开着 CDP，probe 却说没有端口。
export PATH="/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin:$PATH"

DIR="$(cd "$(dirname "$0")" && pwd)"
MAC="$DIR/mac"
[ -x "$MAC" ] || { echo "内核未编译，先跑: bash $DIR/build.sh"; exit 1; }
APP="$1"
[ -z "$APP" ] && { echo "用法: probe.sh <app名>"; exit 1; }

# ── 定位 .app：四条路依次试，按「不需要 app 在运行」优先排序 ──────────────
# 用户给的名字几乎总是本地化显示名（「剪映」「千问办公」），
# 而磁盘名可能完全无关（剪映 → VideoFusion-macOS.app）。靠文件名匹配注定失效。
AP="/Applications/$APP.app"

# ① 按磁盘文件名。⚠️ 不要用 kMDItemKind=='Application'：kMDItemKind 是本地化字符串，
#    中文 macOS 上恒返回 0 条。用 UTI 判据，能命中 /System/Applications 和 ~/Applications。
[ -d "$AP" ] || AP=$(mdfind "kMDItemContentType == 'com.apple.application-bundle'" 2>/dev/null \
                     | grep -iE "/${APP}[^/]*\.app$" | head -1)

# ② ⭐ 按 Spotlight 的本地化显示名反查——**这是显示名场景的主力路径**。
#    kMDItemDisplayName 就是 Finder 里显示的那个名字（剪映的中文名躺在
#    Contents/Resources/zh-Hans.lproj/InfoPlist.strings 里，Spotlight 已经替你读好了）。
#    关键优势：**app 不需要在运行**。旧版脚本只有「运行中进程反查」这一条显示名路线，
#    所以对「没开的中文名 app」必然失败——剪映就是这么漏掉的。
#    末尾 'c' = case-insensitive；用前缀通配是因为显示名常带后缀（「剪映专业版」）。
if [ ! -d "$AP" ]; then
  AP=$(mdfind "kMDItemContentType == 'com.apple.application-bundle' && kMDItemDisplayName == '${APP}*'c" 2>/dev/null | head -1)
fi

# ③ 运行中的进程反查（②漏掉时的保底：Spotlight 索引损坏、或 app 装在被排除的目录）
if [ ! -d "$AP" ]; then
  RP=$(osascript -e "tell application \"System Events\" to get POSIX path of application file of (first process whose name is \"$APP\" or displayed name is \"$APP\")" 2>/dev/null)
  [ -d "$RP" ] && AP="$RP"
fi
# ④ 从窗口 owner 名反查 pid 再反查路径（任意位置的 .app 都能命中，不只 /Applications）
if [ ! -d "$AP" ]; then
  PIDX=$("$MAC" windows "$APP" 2>/dev/null | head -1 | grep -o 'pid=[0-9]*' | cut -d= -f2)
  [ -n "$PIDX" ] && AP=$(ps -o comm= -p "$PIDX" 2>/dev/null | sed 's|\(.*\.app\)/Contents/MacOS/.*|\1|')
fi
# ⚠️ 变量后面紧跟全角字符必须写 ${VAR}：写成 "$APP（" 时 bash 3.2 会把「（」的首字节
# 并进变量名，app 名整个消失（实测输出「找不到 app: ��试试…」）。
[ -d "$AP" ] || { echo "找不到 app: ${APP}"; echo "  试试磁盘上的英文名（如 QwenWorkCN），或直接给 .app 的完整路径"; exit 1; }

PL="$AP/Contents/Info.plist"
BID=$(defaults read "$PL" CFBundleIdentifier 2>/dev/null)
EXE=$(defaults read "$PL" CFBundleExecutable 2>/dev/null)
VER=$(defaults read "$PL" CFBundleShortVersionString 2>/dev/null)

DISP=$(mdls -name kMDItemDisplayName -raw "$AP" 2>/dev/null)

echo "════ 静态 ════"
echo "路径: $AP"
echo "显示名: ${DISP:-未知}       # 用户说的名字 / CGWindow 的 owner 多半是它"
echo "bundle: $BID"
echo "可执行名: $EXE       # System Events 用这个；三个名字可能互不相同，别混用"
echo "版本: ${VER:-未知}       # 🔴 核对档案第一步。版本变了，档案里的坐标一律作废、行为层重测"

# 🔴 同一个 bundle id 装了多份：静默的坑，症状是「探的是 A、跑的是 B、坐标全对不上」且不报错。
# 实测剪映就有两份（11.2.13042 与 11.2.0，bundle id 都是 com.lemon.lvpro），
# 多半是升级时 Finder 保留了旧版并改名成「XXX 2.app」。
# lsappinfo / open -b 按 bundle id 取，无法分辨到底是哪一份。
# 排除自动更新的暂存目录（Application Support/*/installed_versions、Caches），那不是「装了两份」
DUPS=$(mdfind "kMDItemContentType == 'com.apple.application-bundle' && kMDItemCFBundleIdentifier == '$BID'" 2>/dev/null \
       | grep -v "/Application Support/\|/installed_versions/\|/Caches/\|/\.Trash/")
DUPN=$(echo "$DUPS" | grep -c . )
if [ "${DUPN:-0}" -gt 1 ]; then
  # ⚠️ 又踩了本文件开头警告过的那个坑：写 "$BID）" 会让 bash 3.2 把全角「）」的首字节
  # 并进变量名，bundle id 整个消失。变量后紧跟全角字符，一律 ${VAR}。
  echo "🔴 警告: 磁盘上有 $DUPN 份同 bundle id（${BID}）的 app —— 系统按哪份启动是不确定的"
  echo "$DUPS" | while read -r D; do
    [ -n "$D" ] && echo "     $D  (v$(defaults read "$D/Contents/Info.plist" CFBundleShortVersionString 2>/dev/null))"
  done
  echo "     → 操作时用**绝对路径** open \"<完整路径>\"，不要用 open -b <bundleid>"
  echo "     → 已在运行时，用 ps -o comm= -p <pid> 确认跑的到底是哪一份，再谈坐标"
fi

# 🔴 先查「藏起来的 Chromium」：有些 app 顶层 Contents/Frameworks 是空的，
# 却把整个 Chromium 埋在 Contents/Helpers/<Xxx> Browser.app/Contents/Frameworks/ 里。
# 2026-08-30 就因为只看顶层，把豆包工作判成了「原生」，白走一整轮 L2 坐标。
NESTED_CHROMIUM=$(find "$AP/Contents" -maxdepth 6 -type d -name "*Browser Framework.framework" 2>/dev/null | head -1)
if [ -z "$NESTED_CHROMIUM" ]; then
  NESTED_CHROMIUM=$(find "$AP/Contents" -maxdepth 6 -type d \( -name "Electron Framework.framework" -o -name "Chromium Embedded Framework.framework" \) 2>/dev/null | grep -v "^$AP/Contents/Frameworks/" | head -1)
fi
if [ -n "$NESTED_CHROMIUM" ]; then
  CHROME_VER=$(ls "$NESTED_CHROMIUM/Versions" 2>/dev/null | grep -E '^[0-9]+\.' | head -1)
  echo "架构: 🔴 内嵌 Chromium（嵌套在 Helpers 里，顶层 Frameworks 看不到）${CHROME_VER:+ 版本 $CHROME_VER}"
  echo "      路径: ${NESTED_CHROMIUM#$AP/}"
  echo "      ⭐ 直接走 CDP，不要试坐标："
  echo "      $MAC open \"$APP\" --cdp 9333 --relaunch    # 一条命令：重启带端口并等到通"
  echo "      node $DIR/cdp.js 9333 snapshot auto"
fi

if [ -d "$AP/Contents/Frameworks" ] && [ -n "$(ls -A "$AP/Contents/Frameworks" 2>/dev/null)" ]; then
  FW=$(ls "$AP/Contents/Frameworks" 2>/dev/null)
  if echo "$FW" | grep -qi electron; then
    echo "架构: Electron → L1 多半在 AXWebArea 断掉；**优先找本地端口**（CDP/RPC）"
  elif echo "$FW" | grep -qi "Chromium Embedded"; then
    # ⭐ CEF 和 Electron 同源（都是 Chromium），**同样吃 --remote-debugging-port**。
    # 别因为「不是 Electron」就跳过 CDP 路线——这是最容易漏掉的一条高价值通道。
    # 判断 app 是不是壳：看 Resources 下有没有打包的前端资源。
    echo "架构: CEF（Chromium Embedded Framework）→ ⭐ 和 Electron 同源，**一样试 CDP**"
    echo "      open -a \"$APP\" --args --remote-debugging-port=9333   然后 curl 那个端口"
    echo "      ⚠️ 但 CEF 常只包一部分界面：主干是原生、内嵌页面才是 web。"
    echo "         CDP 探到了也先确认要操作的控件在不在网页那一层，不在就还得回 L2。"
  elif echo "$FW" | grep -qiE "flutter|Qt[0-9A-Za-z]*\.framework"; then
    echo "架构: $(echo "$FW" | grep -oiE 'flutter|Qt[0-9A-Za-z]*' | head -1) 自绘 → 🔴 L1 必然为空（整个界面画在一张画布上），直接备 L2"
  else
    echo "架构: 原生 + Frameworks（$(echo "$FW" | head -3 | tr '\n' ' ')）"
  fi
elif [ -z "$NESTED_CHROMIUM" ]; then
  echo "架构: 原生/自绘 → L1 大概率整棵树为空，直接备好 L2 坐标"
  echo "      ⚠️ 下结论前用进程表复核一次（app 要在运行）："
  echo "      ps -eo pid,comm | grep -i \"$EXE\"   # 出现 Helper / crashpad_handler 就还是 Chromium 系"
fi

SC=$(plutil -convert json -o - "$PL" 2>/dev/null | python3 -c "
import json,sys
try:
    d=json.load(sys.stdin)
    print(' '.join(x for t in d.get('CFBundleURLTypes',[]) for x in t.get('CFBundleURLSchemes',[])))
except Exception: pass" 2>/dev/null)
echo "L0 URL scheme: ${SC:-无}"
# ⚠️ 不能只看 plist 里有没有 NSAppleScriptEnabled：不少 app 声明了却拿不到字典（sdef 报 -43）。
# 真调一次 sdef，且要求字典里有 Standard Suite 之外的命令，才算「有字典」。
SDEF=$(sdef "$AP" 2>/dev/null)
if [ -n "$SDEF" ] && echo "$SDEF" | grep -q '<suite ' && echo "$SDEF" | grep -v 'Standard Suite' | grep -q '<command '; then
  echo "L0 AppleScript: 有字典 ✅ 最优先（$(echo "$SDEF" | grep -c '<command ') 条命令，看: sdef \"$AP\"）"
else
  echo "L0 AppleScript: 无可用字典（仍可用 activate/quit）"
fi

echo
echo "════ 动态 ════"
# ⚠️ 不要用 pgrep -x "$EXE"：Electron app 的可执行名就叫 Electron，实测一个 app 就返回 5 个
# pid（主进程 + 渲染进程），两个 Electron app 同开必错，取到渲染进程会假报「无可见窗口」。
# 按 bundle id 取主进程 pid 才是唯一正确的主键。
PID=$(lsappinfo info -only pid "$BID" 2>/dev/null | grep -o '[0-9]*' | head -1)
# fallback 用绝对路径匹配，不要用 osascript tell application——那会把 app 拉起来，
# 一个只读探测脚本不该有启动 app 的副作用。
[ -z "$PID" ] && PID=$(pgrep -f "$AP/Contents/MacOS/" 2>/dev/null | head -1)
if [ -z "$PID" ]; then
  echo "⚠️ 未运行。先 $MAC open \"$APP\" 让它显示一次（open -g 后台启动的窗口截不到图），再跑本脚本。"
  exit 0
fi
echo "pid: $PID"

echo "--- 本地端口（Electron 类最值钱的通道）---"
# ⚠️ 只查主 pid 会漏掉端口：Chromium 系把服务开在渲染/浏览器子进程上。
# 实测豆包工作主进程 0 个端口，子进程「DoubaoWork Browser」两个。
ALLPIDS=$(pgrep -f "$AP/Contents" 2>/dev/null | tr '\n' ',' | sed 's/,$//')
PORTS=$(lsof -nP -iTCP -sTCP:LISTEN -a -p "${ALLPIDS:-$PID}" 2>/dev/null | awk 'NR>1{print $9}')
if [ -z "$PORTS" ]; then echo "无"; else
  for P in $PORTS; do
    N=${P##*:}
    # ⚠️ 必须 --noproxy：跑着 Clash 之类本地代理时不加会被代理吞掉，误判成「没有服务」
    R=$(curl -s --noproxy "*" -m 3 "http://127.0.0.1:$N/json/version" 2>/dev/null | head -c 120)
    if echo "$R" | grep -q "webSocketDebuggerUrl\|Browser"; then
      echo "  $P → ⭐⭐ CDP！直接走 CDP，不用碰坐标"
    else
      R2=$(curl -s --noproxy "*" -m 3 -X POST "http://127.0.0.1:$N" -H "Content-Type: application/json" \
           -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' 2>/dev/null | head -c 120)
      [ -n "$R2" ] && echo "  $P → JSON-RPC: $R2" || echo "  $P → HTTP: ${R:-无响应}"
    fi
  done
  echo "  提示: 没有 CDP 时 → $MAC open \"$APP\" --cdp 9333 --relaunch（会重启 app；9333 任选，被占就换）"
fi

echo "--- 窗口（记 id 和 origin，操作用 clickin 传窗口内相对坐标）---"
"$MAC" windows 2>/dev/null | grep "pid=$PID " || echo "  无可见窗口：可能未渲染（open -g 启动过）、已最小化，或在别的 Space"

echo "--- AX（判据是可编辑控件数，不是窗口数）---"
"$MAC" ax "$PID" 2>/dev/null | tail -3
