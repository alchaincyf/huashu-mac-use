# 与 agent-desktop 的分工

[agent-desktop](https://github.com/vercel-labs/agent-desktop) 是 Vercel Labs 开源的 computer use CLI（Rust，Apache-2.0）。它读 macOS / Windows / Linux 的辅助功能树，给每个可操作控件一个 ref，agent 按 ref 做无头语义操作。它不含模型，循环在 agent 里。

v1.1 起本 skill 不再自己维护通用的 AX 操作层：同桌面的原生控件交给它，本 skill 负责它结构上做不到的部分。

## 谁做什么

| 能力 | 谁来做 | 为什么 |
|---|---|---|
| 同桌面原生控件的读、点、输入、选择 | agent-desktop | ref 快照完整，无头语义操作，不借焦点，台前调度收起的窗口也能点 |
| 别的桌面上的 Chromium 系 app | `cdp.js` | AX 看不见其他桌面的窗口，CDP 不受桌面限制 |
| 窗口取证截图 | `mac shot` | 跨桌面能截，无阴影，带收据（id:pid:位置尺寸），失败自诊断 |
| 物理输入（拖拽、真实悬停、自绘界面坐标点击） | `mac op` / `clickin` / `hoverin` | 有在场闸、遮挡闸、前台闸、借焦点锁和 HUD；agent-desktop 的 `--headed` 没有 |
| 用户在不在场 | `mac idle` | agent-desktop 没有对应能力 |
| 新 app 能力探测 | `probe.sh` 判架构与端口，agent-desktop `snapshot` 判 AX 能读多少 | 两者互补 |
| 中文 app 的经验 | `references/app档案.md` | 上游不会有 |

agent-desktop 也能用 `launch --cdp` 带调试端口启动 Electron app，但它自己不驱动 CDP，要转交 agent-browser。本机已有 `cdp.js` 时直接用 `cdp.js`，少装一个工具。

## 安装与权限

```bash
npm install -g agent-desktop     # 或 brew install agent-desktop
agent-desktop status             # 看 permissions.accessibility / screen_recording 是否 granted
agent-desktop skills get platform
```

辅助功能权限授给的是**启动它的终端 app**，和本 skill 内核要的是同一份授权。已经在用本 skill 的机器装完即可用，实测不弹任何授权框。

## 对照实测 · 2026-11-07

环境：macOS 26（Darwin 25.6），agent-desktop 1.2.0，内核为本仓库 v1.0.0。终端全屏占一个桌面，其他 app 在另一个桌面，台前调度开启。

| 测试 | agent-desktop | 本 skill 内核 |
|---|---|---|
| 读同桌面 Electron app（终端本身）的控件 | 0.4 秒，136 个带 ref 的控件，含 DOM id | `mac ax` 只探到 1 个可编辑控件 |
| 读别的桌面上的飞书、微信、WPS、WorkBuddy | 全部 `ACTION_NOT_SUPPORTED`（not exposed through accessibility） | AX 同样 0 个窗口，内核会提示改 CDP 或坐标 |
| 截别的桌面上的计算器 | 能截（带窗口阴影） | 能截 |
| 截别的桌面上的 WPS、微信 | 未测 | 判空（后台未渲染） |
| 计算器被台前调度收进侧边缩略图时点 AC 7 + 8 = | 每下约 0.1 秒，显示 15，前台不变 | `mac windows` 把它当系统残留滤掉，坐标也点不到缩略图 |
| 计算器在前台时点 AC 9 + 1 = | 未测（上一行已证明不需要前台） | 显示 10，正确；每下约 6.4 秒（后台档无效后升级）；五次全报 `suspected_noop`，误报 |
| 往后台 TextEdit 输入中英混排 | `type` 0.09 秒，读回正确，前台不变 | `mac type <pid>` 0.55 秒，读回正确，前台不变 |
| `--headed` 点计算器（TextEdit 在前台） | 报 `ACTION_FAILED`（被遮挡），但前台已被切到计算器 | 不适用 |

结论：同桌面上 agent-desktop 全面领先；别的桌面上它看不见窗口，CDP 和后台截图仍是本 skill 的地盘。

## 内核已知问题（本轮实测发现，未修）

1. `mac windows` 把台前调度收起的窗口（约 39×98）当系统残留隐藏，要 `--all` 才看得到。
2. `mac op` 的 effect 判定只看落点 ±12% 邻域，结果显示在窗口别处时误报 `suspected_noop`。
3. `mac shot` 对部分 on=0 的窗口挂死不返回，没有超时自诊断。已复现两次：WorkBuddy 某窗口（超过 120 秒）、系统设置屏外的「关于本机」小窗（122×145，30 秒和 60 秒超时都被杀）。同一时刻截当前桌面窗口 0.4 秒正常。SKILL.md 已要求调用时套超时。

修之前先问：这条路径还会不会走？第 1、2 条所在的同桌面场景已改由 agent-desktop 承担，第 3 条在取证主路径上，优先级最高。

## 回流规则

- agent-desktop 版本变了，行为可能变。结论注明版本号和日期，和本文冲突时以新实测为准，改这里不改正文。
- 它补上了某项本 skill 独有的能力（比如跨桌面、在场判断），就把对应行从「谁做什么」里删掉，内核里那段代码移进 `_archive/`。
- 发现它的 bug，先确认能稳定复现，再考虑给上游提 issue。
