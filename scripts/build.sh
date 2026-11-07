#!/bin/bash
# 首次使用前编译内核。约 20 秒，之后一直可用。
# 需要 Xcode 或 Command Line Tools（xcode-select -p 能返回路径即可）
cd "$(dirname "$0")" || exit 1
# agent-desktop 是同桌面控件操作的执行引擎（可选，没装就退回内核自己的 AX/坐标路径）
ad_hint() {
  if command -v agent-desktop >/dev/null; then
    echo "agent-desktop: $(agent-desktop version 2>/dev/null | grep -o '"version":"[0-9.]*"' | tail -1 | cut -d'"' -f4)"
  else
    echo "建议安装 agent-desktop（可选，同桌面控件操作快几十倍、不借焦点）: npm install -g agent-desktop"
  fi
}
[ -f mac ] && [ mac -nt mac.swift ] && { echo "已是最新: $(pwd)/mac"; ad_hint; exit 0; }
command -v swiftc >/dev/null || { echo "缺 swiftc，先装 Command Line Tools: xcode-select --install"; exit 1; }
swiftc -O mac.swift -o mac || exit 1
echo "编译完成: $(pwd)/mac"
ad_hint
