#!/bin/bash
# 首次使用前编译内核。约 20 秒，之后一直可用。
# 需要 Xcode 或 Command Line Tools（xcode-select -p 能返回路径即可）
cd "$(dirname "$0")" || exit 1
[ -f mac ] && [ mac -nt mac.swift ] && { echo "已是最新: $(pwd)/mac"; exit 0; }
command -v swiftc >/dev/null || { echo "缺 swiftc，先装 Command Line Tools: xcode-select --install"; exit 1; }
swiftc -O mac.swift -o mac || exit 1
echo "编译完成: $(pwd)/mac"
