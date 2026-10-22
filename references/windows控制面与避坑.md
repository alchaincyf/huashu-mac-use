# Windows 控制面与避坑指南

本文档作为 `huashu-mac-use` 扩展至 Windows 平台（`win.exe` 内核）的核心技术参考，阐述 Windows 环境下的四层控制面实现原理、避坑先例与工程细节。

---

## 一、双端控制面对齐映射

| 层级 | macOS 实现 (`mac.swift`) | Windows 实现 (`win.exe`) | 原理与降级路径 |
|---|---|---|---|
| **L0 结构层** | `cdp.js` (Node.js WebSocket) | `cdp.js` (Node.js WebSocket) | 双端 100% 复用。扫进程 LISTEN 端口问 `/json/version`，连通后零焦点操控 DOM |
| **L1 语义层** | AXUIElement 两次探测与 ValueAttribute | Windows UI Automation (UIA 3.0) 树遍历与 ValuePattern | 优先寻访 `Button`/`Edit`/`Document` 控件，纯自绘应用（如 Flutter、自绘 Win32）提取不到时优雅退到 L2 |
| **L2 坐标层** | 逻辑点 / 图上像素 / `eN@see.json` 换算 | 物理点 / 图上像素 / `eN@see.json` 换算 | 启动时显式声明 `Per-Monitor V2 DPI Aware`，杜绝 DWM 虚拟化拉伸误差 |
| **L3 像素层** | `screencapture -l` + 12位色彩判空 | `PrintWindow(PW_RENDERFULLCONTENT)` + 12位色彩判空 | 截取目标窗口自身呈现缓冲区，不受遮挡影响；判空确认黑屏/未渲染时降级 `shotfg` |

---

## 二、Windows 平台特有暗坑与攻克方案

### 1. Agent 沙箱桌面隔离 (Desktop Isolation)
- **现象**：在 Antigravity、Codex 或其他 Windows 沙箱运行环境中，Agent 子进程常被操作系统调度在独立的桌面对象（例如 `WinSta0\exebox-xxxx`）中。此时直接调用 Win32 原生 `EnumWindows` 会返回 0，导致「明明开着窗口却列不出来」。
- **解法**：内核在初始化阶段通过 P/Invoke 调用 `OpenDesktop("default", 0, false, DESKTOP_ALL)` 获取用户交互式桌面句柄，并使用 `EnumDesktopWindows` 进行枚举，同时通过 `AttachCurrentThreadToDefaultDesktop()` 将当前线程关联至默认桌面，彻底穿透隔离。

### 2. 高分屏 DPI 缩放虚拟化陷阱
- **现象**：在 125%、150% 或 200% 缩放的 Windows 高分屏设备上，如果进程未声明 DPI 感知，Win32 API（如 `GetWindowRect`, `SetCursorPos`）返回的尺寸会被 DWM 虚拟化拉伸，导致截图与鼠标落点出现几十到上百像素的系统性偏移。
- **解法**：在 `Main` 入口第一行显式调用 `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`，确保截图分辨率、UIA 坐标与输入坐标 1:1 锚定物理像素。

### 3. 取景框 HUD 对屏幕捕获隐身
- **需求**：当 Agent 升级到第二档借焦点操作时，屏幕四角需要向用户脉冲闪烁橙色取景框进行安全提示；但**这一提示绝不能出现在 Agent 自己拍摄的取证截图与录屏中**。
- **解法**：HUD 窗体采用 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST` 扩展样式，并在窗口句柄创建时调用 Windows 10 2004+ 引入的 API：
  ```csharp
  SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); // 0x00000011
  ```
  该 API 指示 DWM 在进行窗口捕捉与截图时直接将该层剔除，完美复刻 macOS 上 `NSPanel.sharingType = .none` 的效果。

### 4. 后台写 unverifiable 阻断防重写机制
- **教训**：在 `op` 第一档后台写入时，若截图无法判定生效状态（如未渲染、白底导致差分率不可信），内核**严禁自动升级到借焦点**。
- **原因**：后台事件可能已经投递并被目标软件消化，此时盲目借焦点重写会导致文本重复录入。因此遇到 `effect=unverifiable` 时，内核主动退出并返回状态码 2，提示换用硬判据或带 `--fast` 显式重试。
