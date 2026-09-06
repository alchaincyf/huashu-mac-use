// huashu-mac-use 的操控内核 v2。用 build.sh 编译成 mac 后使用。
//
// 设计原则（v2 相对 v1 的变化）：
//   1. 教训进代码不进文档：坐标语义统一、截图失败自诊断、AX 查两次、跨 Space 拒绝写，都在这里做。
//   2. 退出码三态：0 成功 / 1 失败 / 2 拒绝或未知（refused / unknown）。上层不许把 2 当成功。
//   3. 所有坐标为「逻辑点」，屏幕尺寸运行时读取，不写死。
import Foundation
import CoreGraphics
import ApplicationServices
import AppKit

let args = CommandLine.arguments
func die(_ s: String, _ code: Int32 = 1) -> Never { print(s); exit(code) }
let USAGE = """
用法（坐标三种写法通用：≤1 归一化 / >1 窗口内点数 / x y @截图.png 按图上像素；加 --dry 只解析不执行）:
  mac windows [owner关键词] [--all]       列窗口：id / pid / owner / on(当前Space可见) / origin / 尺寸 / 标题
  mac shot <windowid> <路径>              截单窗口（后台/被遮挡也能截）。失败自诊断，壳窗口自动改截兄弟窗口
  mac shotfg <windowid> <路径>            先试后台，确认空图才借焦点并立刻还
  mac see <windowid|owner关键词> [--out 路径]  一次拿到：降采样截图 + 收据 + AX 元素表（可用时）→ 之后用 eN@<json> 点
  mac clickin <windowid> <x> <y> [@图] [eN@see.json] [--dry]   按窗口内坐标点击（不激活；不在当前 Space 拒绝）
  mac hoverin <windowid> <x> <y> [@图] [holdms]                真实悬停（组件库菜单要它）
  mac click <pid> <x> <y> [@全屏图] [bg]  全局坐标点击
  mac hover <x> <y> [holdms]
  mac scroll <x> <y> <dy> [dx] [steps]    真实滚轮（画布类应用）
  mac type <pid> <文本> [global]          投递 Unicode（中文可用）
  mac key <pid> <keycode> [cmd]           36=回车 53=Esc 51=删除 9=v（走全局流）
  mac ax <pid>                            AX 探测（内部查两次取最大）
  mac axset <pid> <文本>                  给第一个可编辑控件设值并读回
  mac op <windowid> <x> <y> <文本> [@图] [send <sx> <sy>] [shot <路径>] [--bg|--fast] [--force] [--dry]
                                          写操作默认入口。默认后台优先阶梯：
                                            ① postToPid 零焦点写入 → 截图差分验证 → 生效就结束（用户毫无感觉）
                                            ② 判不出生效才升级借焦点，且升级前必过三道闸：终端send / 借焦点锁 / 用户在场
                                          --bg 只走①不升级   --fast 跳过①直接借焦点   --force 拆掉三道闸
  mac hud <毫秒> [文案] [corner|glow|plain] 屏幕四角脉冲取景框，提示用户 agent 正在接管（鼠标穿透/不抢焦点/跨 Space）
                                          借焦点时自动闪。MAC_HUD=0 关闭；默认对屏幕捕获隐身，取证截图不会带上它，
                                          要录屏演示它本身用 MAC_HUD_CAPTURABLE=1
  mac idle                                用户此刻在不在场：键鼠空闲秒数 + 前台 app + 借焦点锁归谁。动手前先问这一句
  mac open <显示名|路径> [--cdp 端口] [--relaunch] [--dry]   按显示名解析绝对路径启动；--cdp 带调试端口并等通
  mac frontmost                           当前前台 app 名
"""
guard args.count > 1 else { die(USAGE) }

// MARK: - 屏幕与窗口

let screen = CGDisplayBounds(CGMainDisplayID())

struct Win {
    let id: Int, pid: pid_t, owner: String, x: Double, y: Double, w: Double, h: Double
    let title: String, layer: Int, onscreen: Bool?
    var bounds: CGRect { CGRect(x: x, y: y, width: w, height: h) }
}
// 必须用 .optionAll：.optionOnScreenOnly 会让被遮挡的窗口整个消失
func allWindows() -> [Win] {
    let raw = (CGWindowListCopyWindowInfo([.optionAll, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
    return raw.map { w in
        let b = w[kCGWindowBounds as String] as? [String: Any] ?? [:]
        return Win(id: w[kCGWindowNumber as String] as? Int ?? 0,
                   pid: pid_t(w[kCGWindowOwnerPID as String] as? Int ?? 0),
                   owner: w[kCGWindowOwnerName as String] as? String ?? "?",
                   x: b["X"] as? Double ?? 0, y: b["Y"] as? Double ?? 0,
                   w: b["Width"] as? Double ?? 0, h: b["Height"] as? Double ?? 0,
                   title: w[kCGWindowName as String] as? String ?? "",
                   layer: w[kCGWindowLayer as String] as? Int ?? 0,
                   onscreen: w[kCGWindowIsOnscreen as String] as? Bool)
    }
}
func winInfo(_ id: Int) -> Win? { allWindows().first { $0.id == id } }
func origin(of id: Int) -> CGPoint? { winInfo(id).map { CGPoint(x: $0.x, y: $0.y) } }
// onscreen 只有明确为 true 才算在当前 Space 可见；缺席（其它 Space）和 false 都当不可见
func isOnscreen(_ id: Int) -> Bool { winInfo(id)?.onscreen == true }

// 系统残留窗口：自动填充、loginwindow、墙纸这类，以及 500x500 无标题的辅助窗口。--all 才显示
let junkOwners: Set<String> = ["自动填充", "AutoFill", "loginwindow", "coreautha", "sink", "墙纸", "Wallpaper",
                               "Window Server", "Dock", "通知中心", "NotificationCenter", "控制中心", "ControlCenter",
                               "Spotlight", "聚焦"]
func isJunk(_ w: Win) -> Bool {
    if w.layer != 0 || w.h < 120 { return true }
    if junkOwners.contains(w.owner) { return true }
    if w.title.isEmpty && w.w == 500 && w.h == 500 { return true }
    return false
}
func fmt(_ w: Win) -> String {
    "id=\(w.id) pid=\(w.pid) owner=\(w.owner) on=\(w.onscreen == true ? 1 : 0) origin=(\(Int(w.x)),\(Int(w.y))) \(Int(w.w))x\(Int(w.h)) title=\(w.title)"
}
func receipt(_ w: Win) -> String { "receipt=\(w.id):\(w.pid):\(Int(w.x)),\(Int(w.y)),\(Int(w.w))x\(Int(w.h))" }

// activate 之后窗口常在动画中，此刻读到的 origin 是中间值（实测读到过 x=-1359，真实 276）。
// 屏幕外读数不参与稳定判定，连续两次一致才认。
func stableOrigin(of id: Int) -> CGPoint? {
    var o = CGPoint(x: -999_999, y: -999_999), hits = 0
    for _ in 0..<90 {
        guard let n = origin(of: id) else { usleep(10_000); continue }
        let on = n.x > -screen.width && n.y > -screen.height && n.x < screen.width && n.y < screen.height
        if on && abs(n.x - o.x) < 1 && abs(n.y - o.y) < 1 { hits += 1; if hits >= 2 { return n } } else { hits = 0 }
        o = n; usleep(10_000)
    }
    return o.x < -100_000 ? nil : o
}

func screenLocked() -> Bool {
    guard let d = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
    return (d["CGSSessionScreenIsLocked"] as? Bool) ?? false
}

// MARK: - 图片

func loadPixels(_ path: String, _ w: Int) -> ([UInt8], Int, Int)? {
    guard let img = NSImage(contentsOfFile: path),
          let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil), cg.width > 0 else { return nil }
    let h = max(1, Int(Double(w) * Double(cg.height) / Double(cg.width)))
    var buf = [UInt8](repeating: 0, count: w * h * 4)
    guard let ctx = CGContext(data: &buf, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                              space: CGColorSpaceCreateDeviceRGB(),
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
    return (buf, w, h)
}
func imageSize(_ path: String) -> (Int, Int)? {
    guard let img = NSImage(contentsOfFile: path),
          let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return nil }
    return (cg.width, cg.height)
}
// 判空用「颜色种类数」：空壳只有背景+边框两三种，真界面轻松几十种。
// 裁掉四周 8% 和顶部 18%，排除窗口装饰。（v1 用极差被红绿灯按钮骗过）
func looksBlank(_ path: String) -> Bool {
    guard let (px, w, h) = loadPixels(path, 64) else { return true }
    let x0 = w * 8 / 100, x1 = w - x0, y0 = h * 18 / 100, y1 = h - h * 8 / 100
    guard x1 > x0, y1 > y0 else { return true }
    var seen = Set<Int>()
    for y in y0..<y1 { for x in x0..<x1 {
        let i = (y * w + x) * 4
        seen.insert((Int(px[i]) >> 4) << 8 | (Int(px[i+1]) >> 4) << 4 | (Int(px[i+2]) >> 4))
    } }
    return seen.count < 6
}
func downsample(_ src: String, to dst: String, width: Int) -> (Int, Int)? {
    guard let img = NSImage(contentsOfFile: src),
          let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil), cg.width > 0 else { return nil }
    let w = min(width, cg.width), h = max(1, cg.height * w / cg.width)
    guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                              space: CGColorSpaceCreateDeviceRGB(),
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    ctx.interpolationQuality = .high
    ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
    guard let out = ctx.makeImage(),
          let png = NSBitmapImageRep(cgImage: out).representation(using: .png, properties: [:]) else { return nil }
    try? png.write(to: URL(fileURLWithPath: dst))
    return (w, h)
}

// 像素差分：只看「你点的那一块」，全窗变化率在有动画的 app 里没有诊断力
func diffReport(_ before: String, _ after: String, hitX: Double, hitY: Double) -> String {
    let W = 160
    guard let (pa, w, h) = loadPixels(before, W), let (pb, _, _) = loadPixels(after, W), pa.count == pb.count
    else { return "  ⚠️ 差分不可用（前后有一张没截成）" }
    let r = 0.12
    let lx0 = Int(max(0, hitX - r) * Double(w)), lx1 = Int(min(1, hitX + r) * Double(w))
    let ly0 = Int(max(0, hitY - r) * Double(h)), ly1 = Int(min(1, hitY + r) * Double(h))
    var changed = 0, lc = 0, lt = 0, minX = w, maxX = -1, minY = h, maxY = -1
    for y in 0..<h { for x in 0..<w {
        let i = (y * w + x) * 4
        let d = abs(Int(pa[i]) - Int(pb[i])) + abs(Int(pa[i+1]) - Int(pb[i+1])) + abs(Int(pa[i+2]) - Int(pb[i+2]))
        let inL = x >= lx0 && x < lx1 && y >= ly0 && y < ly1
        if inL { lt += 1 }
        if d > 24 { changed += 1; if inL { lc += 1 }; minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y) }
    } }
    let pct = Double(changed) * 100 / Double(w * h), lpct = lt > 0 ? Double(lc) * 100 / Double(lt) : 0
    if changed == 0 {
        return "  effect=suspected_noop 全窗 0% 变化。按可能性：①坐标没落在控件上 ②窗口没真正激活 ③控件不响应合成事件 ④截图早于刷新"
    }
    var out = String(format: "  📍 落点邻域(±12%%) 变化 %.1f%%  |  全窗 %.1f%%", lpct, pct)
    if lpct < 1.0 {
        out += String(format: "\n  effect=suspected_noop 落点几乎没变，全窗变化集中在 (%.2f,%.2f)，多半是 app 自己的动画",
                      Double(minX + maxX) / 2 / Double(w), Double(minY + maxY) / 2 / Double(h))
    } else if lpct < 8.0 {
        out += "\n  effect=partial 落点变化很小，可能只是焦点高亮/光标。看截图坐实"
    } else {
        out += "\n  effect=confirmed 落点确实变了。仍需确认变的是「文字进去」不是「弹出了别的东西」"
    }
    return out
}

// MARK: - 截图（自诊断）

@discardableResult
func capture(_ id: Int, _ path: String) -> Bool {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    p.arguments = ["-x", "-o", "-l\(id)", path]   // -l 取窗口自己的合成缓冲区，不受遮挡
    let pipe = Pipe(); p.standardError = pipe; p.standardOutput = pipe
    try? p.run(); p.waitUntilExit()
    return p.terminationStatus == 0
}
// 同一 app 家族里同位置同尺寸的另一个窗口：多进程 app（内嵌 Chromium 的壳 + 渲染子进程）常有一对
func siblings(of w: Win) -> [Win] {
    allWindows().filter { s in
        s.id != w.id && abs(s.x - w.x) < 3 && abs(s.y - w.y) < 3 && abs(s.w - w.w) < 3 && abs(s.h - w.h) < 3
            && (s.pid == w.pid || s.owner.hasPrefix(w.owner) || w.owner.hasPrefix(s.owner))
    }
}
// 进程族里有没有 Chromium 子进程（Helper / crashpad_handler / Browser Framework）。
// screencapture 对「后台 + 跨 Space 的 Electron/CEF 窗口」拿到的是空图或直接失败，
// 因为 Chromium 后台不保留渲染缓冲——这类窗口的唯一出路是 CDP，实测 CDP 截图不受 Space / 遮挡影响。
func chromiumFamily(pid: pid_t) -> Bool {
    let p = Process(); p.executableURL = URL(fileURLWithPath: "/bin/ps")
    p.arguments = ["-eo", "ppid=,comm="]
    let pipe = Pipe(); p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
    try? p.run(); p.waitUntilExit()
    let out = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    for line in out.split(separator: "\n") {
        let parts = line.split(separator: " ", maxSplits: 1)
        guard parts.count == 2, Int(parts[0].trimmingCharacters(in: .whitespaces)) == Int(pid) else { continue }
        let comm = parts[1]
        if comm.contains("Helper") || comm.contains("crashpad_handler") || comm.contains("Browser Framework") { return true }
    }
    return false
}
// 这个 app 家族有没有已经开着的 CDP 端口？有就不必 --relaunch（重启会丢未保存内容）。
// 扫进程族的 LISTEN 端口，逐个问 /json/version，认得的就是 CDP。--noproxy 防本地代理吞掉。
func liveCdpPort(pid: pid_t) -> String? {
    func sh(_ cmd: [String]) -> String {
        let p = Process(); p.executableURL = URL(fileURLWithPath: cmd[0]); p.arguments = Array(cmd.dropFirst())
        let pipe = Pipe(); p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
        try? p.run(); p.waitUntilExit()
        return String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    }
    let pids = sh(["/usr/bin/pgrep", "-P", "\(pid)"]).split(separator: "\n").map(String.init) + ["\(pid)"]
    let allPids = (pids + sh(["/usr/bin/pgrep", "-P", pids.joined(separator: ",")]).split(separator: "\n").map(String.init))
    let plist = sh(["/usr/sbin/lsof", "-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", allPids.joined(separator: ",")])
    var ports = Set<String>()
    for l in plist.split(separator: "\n") {
        // NAME 列形如 127.0.0.1:9335 (LISTEN)——取含冒号的那个字段，不是最后一个（最后一个是 (LISTEN)）
        for field in l.split(separator: " ") where field.contains(":") && field.contains(".") {
            if let port = field.split(separator: ":").last, Int(port) != nil { ports.insert(String(port)) }
        }
    }
    for port in ports where Int(port) != nil {
        if sh(["/usr/bin/curl", "-s", "--noproxy", "*", "-m", "2", "http://127.0.0.1:\(port)/json/version"]).contains("webSocketDebuggerUrl") { return port }
    }
    return nil
}
// 返回实际截到的窗口与说明；失败直接 die 并说明真因
func shotSmart(_ id: Int, _ path: String) -> (Win, String) {
    guard let w = winInfo(id) else { die("窗口 \(id) 不存在或 id 已过期 → mac windows 重取") }
    if screenLocked() { die("屏幕已锁定：screencapture 必失败（CDP / AX 不受影响）。解锁后重试或改走 CDP shot") }
    if capture(id, path) { return (w, "") }
    for s in siblings(of: w) where capture(s.id, path) {
        return (s, "\n  ℹ️ 壳窗口截不到，已改截同位置兄弟窗口 id=\(s.id)（\(s.owner)）。以后直接用这个 id")
    }
    // 是不是 Chromium 系？是的话给带 app 名的确切 CDP 配方，而不是泛泛一句「改走 cdp.js」
    if chromiumFamily(pid: w.pid) {
        let cdpjs = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent().path + "/cdp.js"
        let head = "窗口 \(id)（\(w.owner)）截不到：它是 Chromium 系（Electron/CEF），"
            + (isOnscreen(id) ? "" : "且在别的 Space，")
            + "后台不保留渲染帧 → screencapture 这条路对它无解。改走 CDP（实测不受 Space/遮挡影响）：\n"
        // 已经开着 CDP 就直接用它，别建议 --relaunch（重启会丢未保存内容）
        if let port = liveCdpPort(pid: w.pid) {
            die(head + "  它已经开着 CDP 端口 \(port)，无需重启：\n  node \(cdpjs) \(port) shot auto <路径>", 2)
        }
        die(head + "  mac open \"\(w.owner)\" --cdp 9333 --relaunch   # ⚠️ 会重启它，未保存内容会丢，先跟用户说\n"
            + "  node \(cdpjs) 9333 shot auto <路径>", 2)
    }
    die("窗口 \(id)（\(w.owner)）截不到，且没有可用的兄弟窗口。可能：从未渲染过（open -g 启动或最小化中）→ 让它显示一次；或该窗口最小化", 1)
}
func describeShot(_ path: String) -> String {
    let sz = imageSize(path).map { "\($0.0)x\($0.1)px" } ?? "?"
    return "\(sz) 判空=\(looksBlank(path) ? "是（可能后台不渲染，试 shotfg 或 CDP）" : "否")"
}

// MARK: - 坐标解析（全部命令共用）
// 三种写法：≤1 归一化 / >1 窗口内点数 / 带 @图 时按图上像素换算。eN@see.json 按 see 的元素表取中心点。
struct Coord { let pt: CGPoint; let note: String }
func extractRef(_ list: inout [String]) -> String? {
    if let i = list.firstIndex(where: { $0.hasPrefix("@") }) { let r = String(list.remove(at: i).dropFirst()); return r }
    return nil
}
func resolve(_ xs: String, _ ys: String, ww: Double, wh: Double, ref: String?) -> Coord {
    // eN@see.json
    if xs.hasPrefix("e"), let at = xs.firstIndex(of: "@") {
        let ref = String(xs[..<at]), json = String(xs[xs.index(after: at)...])
        guard let d = try? Data(contentsOf: URL(fileURLWithPath: json)),
              let obj = try? JSONSerialization.jsonObject(with: d) as? [String: Any],
              let els = obj["elements"] as? [[String: Any]],
              let e = els.first(where: { ($0["ref"] as? String) == ref }),
              let cx = e["cx"] as? Double, let cy = e["cy"] as? Double
        else { die("在 \(json) 里找不到元素 \(ref)（先 mac see 生成）") }
        return Coord(pt: CGPoint(x: cx, y: cy), note: "元素\(ref)「\((e["title"] as? String) ?? "")」→窗口内(\(Int(cx)),\(Int(cy)))")
    }
    guard let x = Double(xs), let y = Double(ys) else { die("坐标不是数字: \(xs) \(ys)") }
    if let img = ref {
        guard let (iw, ih) = imageSize(img) else { die("读不到截图 \(img)") }
        let px = x * ww / Double(iw), py = y * wh / Double(ih)
        return Coord(pt: CGPoint(x: px, y: py),
                     note: String(format: "图上(%.0f,%.0f)@%dx%d→窗口内(%.0f,%.0f)", x, y, iw, ih, px, py))
    }
    if x <= 1.0 && y <= 1.0 && ww > 0 && wh > 0 {
        return Coord(pt: CGPoint(x: x * ww, y: y * wh), note: String(format: "归一化(%.4f,%.4f)→窗口内(%.0f,%.0f)", x, y, x * ww, y * wh))
    }
    return Coord(pt: CGPoint(x: x, y: y), note: String(format: "窗口内绝对(%.0f,%.0f)", x, y))
}
func guardOnScreen(_ p: CGPoint, _ ctx: String) {
    guard p.x >= 0, p.y >= 0, p.x <= screen.width, p.y <= screen.height else {
        die("拒绝点击屏幕外坐标 (\(Int(p.x)),\(Int(p.y)))，屏幕 \(Int(screen.width))x\(Int(screen.height))。\(ctx)", 2)
    }
}

// MARK: - 屏幕接管提示（HUD）
//
// agent 借焦点的那一两秒，用户看到的是窗口自己跳到前面、光标自己动——不解释就是「见鬼了」。
// 官方只发一条系统通知；这里做得更直观：屏幕四周脉冲一圈边框 + 顶部一行说明。
// 三条硬约束，破一条这个提示自己就成了新的干扰源：
//   鼠标穿透（ignoresMouseEvents）· 永不抢焦点（.accessory + nonactivatingPanel + orderFrontRegardless）
//   跨 Space 常驻（canJoinAllSpaces + stationary），否则切一下 Space 它就消失了
final class HUDView: NSView {
    var pulse: CGFloat = 1.0
    var text: String = ""
    var style: String = "corner"      // corner(默认) | glow | plain
    private var accent: NSColor { NSColor(calibratedRed: 0.85, green: 0.35, blue: 0.18, alpha: pulse) }
    override func draw(_ dirty: NSRect) {
        switch style {
        case "plain":
            let t: CGFloat = 7
            accent.setStroke()
            let b = NSBezierPath(roundedRect: bounds.insetBy(dx: t / 2, dy: t / 2), xRadius: 14, yRadius: 14)
            b.lineWidth = t; b.stroke()
        case "corner":
            // 取景框式：四角 L 形粗标记，中间留空不压内容
            let arm: CGFloat = 140, t: CGFloat = 11, inset: CGFloat = 3
            accent.setStroke()
            let p = NSBezierPath(); p.lineWidth = t; p.lineCapStyle = .round
            let x0 = inset + t / 2, y0 = inset + t / 2
            let x1 = bounds.width - inset - t / 2, y1 = bounds.height - inset - t / 2
            for (cx, cy, dx, dy) in [(x0, y0, 1.0, 1.0), (x1, y0, -1.0, 1.0), (x0, y1, 1.0, -1.0), (x1, y1, -1.0, -1.0)] {
                p.move(to: NSPoint(x: cx + arm * CGFloat(dx), y: cy))
                p.line(to: NSPoint(x: cx, y: cy))
                p.line(to: NSPoint(x: cx, y: cy + arm * CGFloat(dy)))
            }
            p.stroke()
            NSColor(calibratedRed: 0.85, green: 0.35, blue: 0.18, alpha: pulse * 0.30).setStroke()
            let thin = NSBezierPath(roundedRect: bounds.insetBy(dx: 1.5, dy: 1.5), xRadius: 10, yRadius: 10)
            thin.lineWidth = 3; thin.stroke()
        default:
            // glow：粗边框 + 内侧渐变辉光，最醒目
            let t: CGFloat = 12
            if let ctx = NSGraphicsContext.current?.cgContext {
                ctx.saveGState()
                let glow: CGFloat = 46
                ctx.clip(to: [bounds])
                let cols = [NSColor(calibratedRed: 0.85, green: 0.35, blue: 0.18, alpha: pulse * 0.42).cgColor,
                            NSColor(calibratedRed: 0.85, green: 0.35, blue: 0.18, alpha: 0).cgColor] as CFArray
                if let g = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: cols, locations: [0, 1]) {
                    ctx.saveGState(); ctx.clip(to: [NSRect(x: 0, y: 0, width: bounds.width, height: glow)])
                    ctx.drawLinearGradient(g, start: CGPoint(x: 0, y: 0), end: CGPoint(x: 0, y: glow), options: [])
                    ctx.restoreGState()
                    ctx.saveGState(); ctx.clip(to: [NSRect(x: 0, y: bounds.height - glow, width: bounds.width, height: glow)])
                    ctx.drawLinearGradient(g, start: CGPoint(x: 0, y: bounds.height), end: CGPoint(x: 0, y: bounds.height - glow), options: [])
                    ctx.restoreGState()
                    ctx.saveGState(); ctx.clip(to: [NSRect(x: 0, y: 0, width: glow, height: bounds.height)])
                    ctx.drawLinearGradient(g, start: CGPoint(x: 0, y: 0), end: CGPoint(x: glow, y: 0), options: [])
                    ctx.restoreGState()
                    ctx.saveGState(); ctx.clip(to: [NSRect(x: bounds.width - glow, y: 0, width: glow, height: bounds.height)])
                    ctx.drawLinearGradient(g, start: CGPoint(x: bounds.width, y: 0), end: CGPoint(x: bounds.width - glow, y: 0), options: [])
                    ctx.restoreGState()
                }
                ctx.restoreGState()
            }
            accent.setStroke()
            let b = NSBezierPath(roundedRect: bounds.insetBy(dx: t / 2, dy: t / 2), xRadius: 16, yRadius: 16)
            b.lineWidth = t; b.stroke()
        }
        guard !text.isEmpty else { return }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 15, weight: .semibold),
            .foregroundColor: NSColor.white.withAlphaComponent(min(1, pulse + 0.25)),
        ]
        let size = (text as NSString).size(withAttributes: attrs)
        let pad: CGFloat = 14, bh = size.height + 12
        let box = NSRect(x: (bounds.width - size.width) / 2 - pad, y: bounds.height - bh - 44,
                         width: size.width + pad * 2, height: bh)
        NSColor(calibratedRed: 0.72, green: 0.27, blue: 0.13, alpha: min(0.94, pulse)).setFill()
        NSBezierPath(roundedRect: box, xRadius: bh / 2, yRadius: bh / 2).fill()
        (text as NSString).draw(at: NSPoint(x: box.minX + pad, y: box.minY + 6), withAttributes: attrs)
    }
}
func showHUD(ms: Double, text: String, style: String = "corner") {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)          // 不进 Dock、不抢激活
    var panels: [NSPanel] = []
    for scr in NSScreen.screens {                // 多屏全都罩上
        let win = NSPanel(contentRect: scr.frame, styleMask: [.borderless, .nonactivatingPanel],
                          backing: .buffered, defer: false)
        win.setFrame(scr.frame, display: false)
        win.level = .screenSaver
        win.backgroundColor = .clear
        win.isOpaque = false
        win.hasShadow = false
        win.ignoresMouseEvents = true            // 鼠标穿透：不挡用户任何操作
        // 🔴 对人眼可见、对屏幕捕获不可见——取证截图绝不能带上这圈提示。
        // 录屏演示这个功能本身时要它出镜：MAC_HUD_CAPTURABLE=1
        win.sharingType = ProcessInfo.processInfo.environment["MAC_HUD_CAPTURABLE"] == "1" ? .readOnly : .none
        win.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        let v = HUDView(frame: NSRect(origin: .zero, size: scr.frame.size))
        v.text = scr == NSScreen.screens.first ? text : ""
        v.style = style
        win.contentView = v
        win.orderFrontRegardless()               // 不是 makeKeyAndOrderFront：不夺 key
        panels.append(win)
    }
    let t0 = Date()
    let timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { _ in
        let el = Date().timeIntervalSince(t0)
        if el * 1000 >= ms { exit(0) }
        let p = CGFloat(0.30 + 0.70 * abs(sin(el * 5.0)))   // 脉冲闪烁
        for w in panels { (w.contentView as? HUDView)?.pulse = p; w.contentView?.needsDisplay = true }
    }
    RunLoop.current.add(timer, forMode: .common)
    DispatchQueue.global().asyncAfter(deadline: .now() + ms / 1000 + 3) { exit(0) }   // 保险：绝不留窗
    app.run()
}
/// 借焦点/接管屏幕前闪一下。子进程异步跑，不拖慢主流程；MAC_HUD=0 关掉
func flashHUD(_ text: String, ms: Int = 1600) {
    guard ProcessInfo.processInfo.environment["MAC_HUD"] != "0" else { return }
    let exe = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().path
    let p = Process()
    p.executableURL = URL(fileURLWithPath: exe)
    p.arguments = ["hud", "\(ms)", text]
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    try? p.run()
}

// MARK: - 用户在场闸（借焦点前的第零道闸）
//
// 借焦点 = 从用户手里抢东西。抢之前先看他在不在动手，这是 Anthropic 官方 computer use 的
// 同一条判据（"generally waits if you're in the middle of typing"）。
// 坑：我们自己合成的事件同样会刷新系统空闲计时器（实测 post 一个 mouseMoved，读数从
// 165.7 秒掉回 1.0），于是连续两条 mac 命令会把上一条的尾迹误判成「用户在动」而自锁。
// 解法是把自己最后一次合成的时刻写进尾迹文件，读数与它吻合就当自己的痕迹排除掉。
let TRAIL = NSTemporaryDirectory() + "huashu-mac-synthetic.trail"
func markSynthetic() {
    try? "\(Date().timeIntervalSince1970)".write(toFile: TRAIL, atomically: true, encoding: .utf8)
}
func syntheticAgo() -> Double? {
    guard let s = try? String(contentsOfFile: TRAIL, encoding: .utf8),
          let t = Double(s.trimmingCharacters(in: .whitespacesAndNewlines)) else { return nil }
    let d = Date().timeIntervalSince1970 - t
    return d >= 0 ? d : nil
}
/// 用户真实动手距今几秒。两路信号都判定为自身尾迹时返回 3600（无用户活动证据 → 放行）
func userIdle() -> Double {
    let k = CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: .keyDown)
    let m = CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: .mouseMoved)
    let ago = syntheticAgo()
    // 尾迹只在 10 秒内有效：时间一长，尾迹时刻会和用户真实空闲时长自然趋同，
    // 两路都被误排除就会退到兜底值当成「完全空闲」而放行。
    func clean(_ v: Double) -> Double? {
        if let a = ago, a < 10, abs(a - v) < 0.8 { return nil }   // 这一路是我们自己刚留下的
        return v
    }
    return [clean(k), clean(m)].compactMap { $0 }.min() ?? 3600
}
let IDLE_NEED = 2.0     // 用户 2 秒内动过键鼠 = 他在场
let IDLE_WAIT = 15.0    // 最多静静等他停手 15 秒
/// 等用户停手 → (是否等到, 等了几秒, 最后读数)
func waitUserIdle(need: Double = IDLE_NEED, maxWait: Double = IDLE_WAIT) -> (Bool, Double, Double) {
    let t0 = Date()
    var last = userIdle()
    while last < need {
        if Date().timeIntervalSince(t0) >= maxWait { return (false, Date().timeIntervalSince(t0), last) }
        usleep(250_000)
        last = userIdle()
    }
    return (true, Date().timeIntervalSince(t0), last)
}

// 借焦点是全机唯一资源：两个 agent 同时 activate 会互相踢，且双方都以为自己成功了。
// 官方用机器级单会话锁；我们只锁「借焦点」这一小段，读操作和后台写完全不受影响。
let FOCUS_LOCK = NSTemporaryDirectory() + "huashu-mac-focus.lock"
func lockHolder() -> (pid: pid_t, age: Double)? {
    guard let s = try? String(contentsOfFile: FOCUS_LOCK, encoding: .utf8) else { return nil }
    let p = s.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ":")
    guard p.count == 2, let pid = pid_t(p[0]), let t = Double(p[1]) else { return nil }
    let age = Date().timeIntervalSince1970 - t
    if age > 30 || kill(pid, 0) != 0 { return nil }   // 陈旧锁 / 持有者已退出
    return (pid, age)
}
func acquireFocusLock(maxWait: Double = 5.0) -> pid_t? {
    let t0 = Date()
    while let h = lockHolder(), h.pid != getpid() {
        if Date().timeIntervalSince(t0) >= maxWait { return h.pid }
        usleep(200_000)
    }
    try? "\(getpid()):\(Date().timeIntervalSince1970)".write(toFile: FOCUS_LOCK, atomically: true, encoding: .utf8)
    return nil
}
func releaseFocusLock() {
    if let h = lockHolder(), h.pid == getpid() { try? FileManager.default.removeItem(atPath: FOCUS_LOCK) }
}

// 合成事件打给「落点最上层那个窗口」，不是打给目标 app。目标被遮挡时这一下会戳在用户
// 正在用的窗口上，而且静默无痕。CGWindowListCopyWindowInfo 的返回顺序即 z 序（前→后）。
func topWindowAt(_ p: CGPoint) -> Win? {
    allWindows().first { $0.onscreen == true && $0.layer == 0 && $0.w >= 1 && $0.h >= 1 && $0.bounds.contains(p) }
}
// 官方按 app 类别分级，终端与 IDE 标注「等同 shell 访问」。我们只机制化最危险的那一格：
// 往终端类窗口里按回车/发送 = 执行命令，必须显式 --force。填字不拦。
let shellOwners: Set<String> = ["Terminal", "终端", "iTerm2", "iTerm", "Warp", "Alacritty", "kitty", "Ghostty",
                                "Code", "Visual Studio Code", "Cursor", "Xcode", "FanBox", "Claude", "Codex"]
func isShellish(_ w: Win) -> Bool { shellOwners.contains(w.owner) }

// MARK: - 事件

let DELIVERY_WINDOW: UInt32 = 300_000   // postToPid 是异步的，post 完立刻退出会丢包
let src = CGEventSource(stateID: .privateState)
func clickAt(_ p: CGPoint, bg: Bool = false, pid: pid_t = 0) {
    for t in [CGEventType.leftMouseDown, .leftMouseUp] {
        guard let e = CGEvent(mouseEventSource: src, mouseType: t, mouseCursorPosition: p, mouseButton: .left) else { continue }
        e.setIntegerValueField(.mouseEventClickState, value: 1)
        if bg { e.postToPid(pid) } else { e.post(tap: .cghidEventTap); markSynthetic() }
        usleep(50_000)
    }
    if bg { usleep(DELIVERY_WINDOW) }
}
func moveMouse(_ p: CGPoint) {
    CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
    markSynthetic()
}
func typeUnicode(_ text: String, pid: pid_t, global: Bool) {
    let a = Array(text)
    for i in stride(from: 0, to: a.count, by: 12) {
        var u = Array(String(a[i..<min(i + 12, a.count)]).utf16)
        for down in [true, false] {
            guard let e = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: down) else { continue }
            e.keyboardSetUnicodeString(stringLength: u.count, unicodeString: &u)
            if global { e.post(tap: .cghidEventTap); markSynthetic() } else { e.postToPid(pid) }
        }
        usleep(10_000)
    }
    if !global { usleep(DELIVERY_WINDOW) }
}
// NSRunningApplication.activate() 对命令行工具无效（返回 true 但不激活），只有 AppleEvent 路径有效
func activateScript(_ a: NSRunningApplication?) -> NSAppleScript? {
    guard let a = a else { return nil }
    if let b = a.bundleIdentifier { return NSAppleScript(source: "tell application id \"\(b)\" to activate") }
    guard let n = a.localizedName else { return nil }
    return NSAppleScript(source: "tell application \"\(n)\" to activate")
}

// MARK: - AX

func axAttr(_ el: AXUIElement, _ n: String) -> Any? {
    var v: CFTypeRef?; return AXUIElementCopyAttributeValue(el, n as CFString, &v) == .success ? v : nil
}
func axWake(_ app: AXUIElement) -> AXError {
    var probe: CFTypeRef?
    _ = AXUIElementCopyAttributeValue(app, kAXRoleAttribute as CFString, &probe)   // 只读 AXRole 即可唤醒 a11y
    return AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue) // Electron 专属；永不设 AXEnhancedUserInterface
}
func isEditable(_ el: AXUIElement) -> Bool {
    let role = axAttr(el, kAXRoleAttribute as String) as? String ?? ""
    return role == "AXTextArea" || role == "AXTextField" || axAttr(el, "AXPlaceholderValue") != nil
}
func axWalk(_ root: AXUIElement, _ visit: (AXUIElement, Int) -> Bool) {
    var seen = 0
    func rec(_ el: AXUIElement, _ d: Int) {
        if d > 25 || seen > 6000 { return }
        seen += 1
        if !visit(el, d) { return }
        for c in (axAttr(el, kAXChildrenAttribute as String) as? [AXUIElement]) ?? [] { rec(c, d + 1) }
    }
    rec(root, 0)
}
func axCountEditables(_ app: AXUIElement, print p: Bool) -> Int {
    var n = 0
    axWalk(app) { el, _ in
        if isEditable(el) {
            n += 1
            if p && n <= 5 {
                let ph = axAttr(el, "AXPlaceholderValue") as? String ?? ""
                print("  可编辑: \(axAttr(el, kAXRoleAttribute as String) as? String ?? "") \(ph.isEmpty ? "" : "placeholder=\(ph)")")
            }
        }
        return true
    }
    return n
}

// MARK: - 命令

var rest = Array(args.dropFirst(2))
let dry = rest.contains("--dry"); rest.removeAll { $0 == "--dry" }
let force = rest.contains("--force"); rest.removeAll { $0 == "--force" }   // 跳过在场闸/遮挡闸/终端闸
let fastMode = rest.contains("--fast"); rest.removeAll { $0 == "--fast" }  // op：直接借焦点，不走后台阶梯
let bgOnly = rest.contains("--bg"); rest.removeAll { $0 == "--bg" }        // op：只走后台，不升级

/// --dry 的闸预检：不执行、不等待，只报「现在跑会被哪道闸拦」。
/// 有它才能安全预演，否则只能拿真命令去试拒绝分支——那正是踩坑的来源。
func presencePreview() -> String {
    let v = userIdle()
    return v >= IDLE_NEED ? String(format: "在场=pass(空闲%.1fs)", v)
                          : String(format: "在场=WAIT(用户%.1fs前动过，最多等%.0fs)", v, IDLE_WAIT)
}
func lockPreview() -> String {
    if let h = lockHolder() { return "借焦点锁=HELD(pid \(h.pid))" }
    return "借焦点锁=free"
}

/// 全局事件流与借焦点都会打断用户。等他停手，等不到就拒绝而不是硬抢。--force 拆闸
func gateUserPresence(_ what: String, alt: String) {
    guard !force else { return }
    let (passed, waited, last) = waitUserIdle()
    if !passed {
        die(String(format: "refused: 用户正在用电脑（%.1f 秒前还在动键鼠），等了 %.0f 秒仍未停手，没有打断他。\n%@%@；确实要现在做加 --force", last, waited, what, alt), 2)
    }
    if waited > 0.5 { print(String(format: "⏳ 用户刚在动键鼠，等他停手 %.1f 秒后才动手", waited)) }
}

switch args[1] {

case "windows":
    let all = rest.contains("--all"); rest.removeAll { $0 == "--all" }
    let filter = rest.first
    var hidden = 0
    let list = allWindows().sorted { ($0.owner, -$0.id) < ($1.owner, -$1.id) }
    for w in list {
        if let f = filter, !w.owner.localizedCaseInsensitiveContains(f) { continue }
        if !all && isJunk(w) { hidden += 1; continue }
        print(fmt(w))
    }
    if hidden > 0 { print("（已隐藏 \(hidden) 个系统残留/浮层窗口，--all 显示）") }

case "shot":
    guard rest.count >= 2, let id = Int(rest[0]) else { die("用法: mac shot <windowid> <路径>") }
    let (w, note) = shotSmart(id, rest[1])
    print("shot id=\(w.id) -> \(rest[1])  \(describeShot(rest[1]))  \(receipt(w))\(note)")

case "see":
    // 一次调用拿到看图即点的全部材料：降采样截图（坐标可直接 x y @图 点）、收据、AX 元素表（eN@json 点）
    guard rest.count >= 1 else { die("用法: mac see <windowid|owner关键词> [--out 路径]") }
    var out: String?
    if let i = rest.firstIndex(of: "--out"), i + 1 < rest.count { out = rest[i + 1]; rest.removeSubrange(i...i + 1) }
    let target: Win
    if let id = Int(rest[0]) { guard let w = winInfo(id) else { die("窗口 \(id) 不存在") }; target = w }
    else {
        guard let w = allWindows().sorted(by: { $0.h * $0.w > $1.h * $1.w })
                .first(where: { !isJunk($0) && $0.owner.localizedCaseInsensitiveContains(rest[0]) })
        else { die("没有 owner 含「\(rest[0])」的窗口 → mac windows 看看") }
        target = w
    }
    let outPath = out ?? NSTemporaryDirectory() + "see-\(target.id).png"
    let raw = NSTemporaryDirectory() + "see-raw-\(target.id).png"
    let (w, note) = shotSmart(target.id, raw)
    guard let (iw, ih) = downsample(raw, to: outPath, width: 1400) else { die("降采样失败") }
    print("截图: \(outPath) \(iw)x\(ih)px（点它：mac clickin \(w.id) <图上x> <图上y> @\(outPath)）")
    print(receipt(w) + (isOnscreen(w.id) ? "" : "  ⚠️ 不在当前 Space：读可以，写只能走 CDP 或 mac op") + note)
    if looksBlank(outPath) { print("⚠️ 图判空：后台不渲染，改 mac shotfg 或 cdp.js shot") }
    // AX 元素表
    let app = AXUIElementCreateApplication(w.pid)
    _ = axWake(app)
    usleep(300_000)
    var elements: [[String: Any]] = []
    let actionable: Set<String> = ["AXButton", "AXTextField", "AXTextArea", "AXCheckBox", "AXRadioButton", "AXPopUpButton",
                                   "AXMenuButton", "AXLink", "AXComboBox", "AXSlider", "AXTab", "AXIncrementor", "AXDisclosureTriangle"]
    if let wins = axAttr(app, kAXWindowsAttribute as String) as? [AXUIElement], !wins.isEmpty {
        for win in wins {
            axWalk(win) { el, _ in
                let role = axAttr(el, kAXRoleAttribute as String) as? String ?? ""
                guard actionable.contains(role), elements.count < 150 else { return elements.count < 150 }
                var pos = CGPoint.zero, size = CGSize.zero
                if let pv = axAttr(el, kAXPositionAttribute as String) { AXValueGetValue(pv as! AXValue, .cgPoint, &pos) }
                if let sv = axAttr(el, kAXSizeAttribute as String) { AXValueGetValue(sv as! AXValue, .cgSize, &size) }
                guard size.width > 0, size.height > 0 else { return true }
                let cx = pos.x + size.width / 2 - w.x, cy = pos.y + size.height / 2 - w.y
                guard cx >= 0, cy >= 0, cx <= w.w, cy <= w.h else { return true }
                let title = (axAttr(el, kAXTitleAttribute as String) as? String)
                    ?? (axAttr(el, kAXDescriptionAttribute as String) as? String)
                    ?? (axAttr(el, "AXPlaceholderValue") as? String)
                    ?? (axAttr(el, kAXValueAttribute as String) as? String) ?? ""
                let enabled = (axAttr(el, kAXEnabledAttribute as String) as? Bool) ?? true
                elements.append(["ref": "e\(elements.count + 1)", "role": role, "title": String(title.prefix(40)),
                                 "cx": cx, "cy": cy, "enabled": enabled])
                return true
            }
        }
    }
    if elements.isEmpty {
        print("AX 元素表: 无（跨 Space / app 不暴露 / 树断在 AXWebArea）。用图上像素坐标点，Chromium 系走 cdp.js snapshot")
    } else {
        let json = outPath + ".json"
        let obj: [String: Any] = ["receipt": receipt(w), "window": ["id": w.id, "pid": Int(w.pid), "w": w.w, "h": w.h], "elements": elements]
        if let d = try? JSONSerialization.data(withJSONObject: obj, options: [.prettyPrinted]) { try? d.write(to: URL(fileURLWithPath: json)) }
        print("AX 元素表 \(elements.count) 个（点它：mac clickin \(w.id) e3@\(json)）:")
        for e in elements {
            let dis = (e["enabled"] as? Bool) == false ? " [disabled]" : ""
            print("  \(e["ref"]!) \(e["role"]!) \"\(e["title"]!)\" (\(Int(e["cx"] as! Double)),\(Int(e["cy"] as! Double)))\(dis)")
        }
    }

case "clickin", "hoverin":
    guard rest.count >= 3, let id = Int(rest[0]) else { die("用法: mac \(args[1]) <windowid> <x> <y> [@图] [--dry]") }
    let ref = extractRef(&rest)
    guard let w = winInfo(id) else { die("窗口 \(id) 不存在（可能已关闭或 id 过期）") }
    let c = resolve(rest[1], rest[2], ww: w.w, wh: w.h, ref: ref)
    var edge = ""
    if c.pt.x < 8 && c.pt.y < 8 { edge = "  ⚠️ 落点贴着窗口左上角，大概率坐标算错" }
    if dry {
        print("dry: \(args[1]) 窗口\(id) \(c.note) on=\(isOnscreen(id) ? 1 : 0)\(edge)")
        var g = "  闸预检: 跨Space=" + (isOnscreen(id) ? "pass" : "BLOCK(不在当前 Space)")
        let gp = CGPoint(x: w.x + c.pt.x, y: w.y + c.pt.y)
        if let top = topWindowAt(gp), top.id != id { g += "  遮挡=BLOCK(上层是「\(top.owner)」窗口 \(top.id))" }
        else { g += "  遮挡=pass" }
        g += "  " + presencePreview()
        print(g)
        if force { print("  ⚠️ --force 会拆掉以上全部闸") }
        exit(0)
    }
    // 合成事件打给「当前 Space 上那个位置的窗口」，不是给目标 app；目标不在当前 Space 时会戳到用户正在用的窗口
    guard isOnscreen(id) else {
        die("refused: cross-space 窗口 \(id)（\(w.owner)）不在当前 Space，合成事件会打到别的窗口上。用 mac op（会激活）、CDP，或请用户把它挪到当前 Space", 2)
    }
    guard let o = stableOrigin(of: id) else { die("读不到窗口位置") }
    let p = CGPoint(x: o.x + c.pt.x, y: o.y + c.pt.y)
    guardOnScreen(p, "多半是 activate 后窗口仍在动画。等 1 秒重跑")
    // 遮挡闸：这一下打给「落点最上层那个窗口」。目标被压住时会静默戳在用户正在用的窗口上
    if !force, let top = topWindowAt(p), top.id != id {
        die("refused: occluded 落点 (\(Int(p.x)),\(Int(p.y))) 上层是「\(top.owner)」的窗口 \(top.id)，点下去会戳到它而不是目标 \(w.owner)。\n用 mac op（会激活目标）、CDP，或请用户把目标窗口移到最前；确认要点加 --force", 2)
    }
    // 在场闸：clickin 不激活，但走全局事件流，跟用户共用同一个鼠标指针
    gateUserPresence("clickin 走全局事件流，跟他共用同一个鼠标指针。", alt: "改走 CDP 或 mac op --bg")
    if args[1] == "clickin" {
        let saved = CGEvent(source: nil)?.location
        clickAt(p)
        if let s = saved { moveMouse(s) }
        print("clicked \(c.note) → 全局(\(Int(p.x)),\(Int(p.y))) pid \(w.pid)\(edge)\n→ 必须回读：mac shot \(id) <路径>，看应用状态指示器不是看有没有字")
    } else {
        var hold: UInt32 = 700_000
        if rest.count > 3, let ms = Double(rest[3]) { hold = UInt32(ms * 1000) }
        let start = CGEvent(source: nil)?.location ?? p
        for i in 1...8 { let f = Double(i) / 8; moveMouse(CGPoint(x: start.x + (p.x - start.x) * f, y: start.y + (p.y - start.y) * f)); usleep(20_000) }
        usleep(hold)
        print("hovered \(c.note) held \(hold / 1000)ms — 光标留在原地，后续 click 可直接点弹出项")
    }

case "click", "hover":
    let isClick = args[1] == "click"
    let need = isClick ? 3 : 2
    guard rest.count >= need else { die("用法: mac \(args[1]) \(isClick ? "<pid> " : "")<x> <y> [@全屏图] [bg|holdms] [--dry]") }
    let ref = extractRef(&rest)
    let pid: pid_t = isClick ? (pid_t(rest[0]) ?? 0) : 0
    let xi = isClick ? 1 : 0
    let c = resolve(rest[xi], rest[xi + 1], ww: screen.width, wh: screen.height, ref: ref)
    guardOnScreen(c.pt, "")
    let bg = isClick && rest.count > 3 && rest[3] == "bg"
    if dry {
        print("dry: \(args[1]) 全局 \(c.note)\(bg ? " postToPid" : "")")
        if isClick, !bg {
            let t = topWindowAt(c.pt)
            print("  闸预检: 落点上层=" + (t.map { $0.pid == pid ? "pass(就是目标 pid \(pid))" : "BLOCK(「\($0.owner)」pid \($0.pid))" } ?? "无窗口")
                  + "  " + presencePreview())
        }
        exit(0)
    }
    if isClick, !bg, !force, let top = topWindowAt(c.pt), top.pid != pid {
        die("refused: occluded 落点 (\(Int(c.pt.x)),\(Int(c.pt.y))) 上层是「\(top.owner)」(pid \(top.pid))，不是目标 pid \(pid)。点下去会戳到它。\n改 mac clickin（按窗口定位）或 CDP；确认要点加 --force", 2)
    }
    if !bg { gateUserPresence("全局坐标点击/悬停跟用户共用同一个鼠标指针。", alt: "改 mac clickin（按窗口定位）或 CDP") }
    if isClick {
        let saved = CGEvent(source: nil)?.location
        clickAt(c.pt, bg: bg, pid: pid)
        if !bg, let s = saved { moveMouse(s) }
        print("clicked \(c.note) \(bg ? "postToPid" : "global") -> pid \(pid)")
    } else {
        var hold: UInt32 = 700_000
        if rest.count > 2, let ms = Double(rest[2]) { hold = UInt32(ms * 1000) }
        let start = CGEvent(source: nil)?.location ?? c.pt
        for i in 1...8 { let f = Double(i) / 8; moveMouse(CGPoint(x: start.x + (c.pt.x - start.x) * f, y: start.y + (c.pt.y - start.y) * f)); usleep(20_000) }
        usleep(hold)
        print("hovered \(c.note) held \(hold / 1000)ms")
    }

case "scroll":
    guard rest.count >= 3, let sx = Double(rest[0]), let sy = Double(rest[1]), let dy = Int32(rest[2]) else { die("用法: mac scroll <x> <y> <dy> [dx] [steps]") }
    let dx = rest.count > 3 ? (Int32(rest[3]) ?? 0) : 0, steps = rest.count > 4 ? (Int(rest[4]) ?? 6) : 6
    gateUserPresence("滚轮走全局流，会滚到他正在看的窗口上。", alt: "Chromium 系改 cdp.js")
    moveMouse(CGPoint(x: sx, y: sy)); usleep(120_000)
    for _ in 0..<steps {
        CGEvent(scrollWheelEvent2Source: src, units: .pixel, wheelCount: 2, wheel1: dy, wheel2: dx, wheel3: 0)?.post(tap: .cghidEventTap)
        markSynthetic()
        usleep(40_000)
    }
    print("scrolled at (\(Int(sx)),\(Int(sy))) dy=\(dy) dx=\(dx) ×\(steps)")

case "type":
    guard rest.count >= 2, let pid = pid_t(rest[0]) else { die("用法: mac type <pid> <文本> [global]") }
    let global = rest.count > 2 && rest[2] == "global"
    // global 走全局流 → 打进「当前前台窗口」，那可能正是用户在用的那个
    if dry {
        let fp = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? -1
        print("dry: type \(rest[1].count) 字 -> pid \(pid) \(global ? "global(打给前台)" : "postToPid(直投进程)")")
        print("  闸预检: " + (global ? ("frontmost=" + (fp == pid ? "pass" : "BLOCK(前台 pid \(fp) 不是目标)") + "  " + presencePreview())
                                     : "postToPid 不碰前台，无闸"))
        exit(0)
    }
    if global, !force, let fp = NSWorkspace.shared.frontmostApplication?.processIdentifier, fp != pid {
        die("refused: frontmost 前台 pid \(fp) 不是目标 pid \(pid)，global 会把字打进前台那个窗口。\n去掉 global 走 postToPid（直投目标进程），或先 mac op 激活目标", 2)
    }
    if global { gateUserPresence("global 会把字打进他的前台窗口。", alt: "去掉 global 走 postToPid") }
    typeUnicode(rest[1], pid: pid, global: global)
    print("typed \(rest[1].count) chars -> pid \(pid) \(global ? "global" : "postToPid")（返回值不代表生效，必须回读）")

case "key":
    guard rest.count >= 2, let pid = pid_t(rest[0]), let code = UInt16(rest[1]) else { die("用法: mac key <pid> <keycode> [cmd]") }
    let flags: CGEventFlags = rest.count > 2 && rest[2] == "cmd" ? .maskCommand : []
    // 这一下走全局流，落在「当前前台 app」身上（pid 参数只用于回显），所以闸看的是前台是谁
    let frontApp = NSWorkspace.shared.frontmostApplication
    let frontName = frontApp?.localizedName ?? "?"
    // frontmost 闸（对齐官方 computer use：动作发生时前台必须就是目标，否则什么都不做）。
    // 前台随时会被用户切走，不查这一下就会按到他正在用的窗口上——实测踩过。
    if dry {
        let fp = frontApp?.processIdentifier ?? -1
        print("dry: key \(code)\(flags.contains(.maskCommand) ? "+cmd" : "") 会落在前台「\(frontName)」(pid \(fp))")
        print("  闸预检: frontmost=" + (fp == pid ? "pass(前台就是目标)" : "BLOCK(目标 pid \(pid) 不在前台)")
              + "  回车=" + (code == 36 && shellOwners.contains(frontName) ? "BLOCK(\(frontName) 是终端/IDE 类)" : "pass")
              + "  " + presencePreview())
        exit(0)
    }
    if !force, let fp = frontApp?.processIdentifier, fp != pid {
        die("refused: frontmost 前台是「\(frontName)」(pid \(fp))，不是目标 pid \(pid)。按键落在前台窗口上，会打错地方。\n先 mac op 激活目标再按，或加 --force", 2)
    }
    if !force {
        if code == 36, shellOwners.contains(frontName) {
            die("refused: 前台是 \(frontName)（终端/IDE 类），这一下回车等于执行命令。确认要执行加 --force", 2)
        }
    }
    gateUserPresence("按键会落在他的前台窗口「\(frontName)」上。", alt: "等他停手")
    for down in [true, false] {
        guard let e = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: down) else { continue }
        e.flags = flags; e.post(tap: .cghidEventTap); markSynthetic(); usleep(30_000)
    }
    print("key \(code)\(flags.contains(.maskCommand) ? "+cmd" : "") -> 前台 app（pid \(pid) 仅回显）")

case "ax":
    guard rest.count >= 1, let pid = pid_t(rest[0]) else { die("用法: mac ax <pid>") }
    let app = AXUIElementCreateApplication(pid)
    let manual = axWake(app)
    Thread.sleep(forTimeInterval: 0.8)
    var wins: CFTypeRef?
    let we = AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &wins)
    let winCount = (wins as? [AXUIElement])?.count ?? 0
    // 首次访问某 app 的 AX 接口可能返回不完整的树，查两次取最大
    let n1 = axCountEditables(app, print: false)
    Thread.sleep(forTimeInterval: 0.4)
    let n2 = axCountEditables(app, print: true)
    let n = max(n1, n2)
    print("AXIsProcessTrusted=\(AXIsProcessTrusted()) windows=\(winCount)(err=\(we.rawValue)) setManual=\(manual.rawValue) 可编辑控件=\(n)\(n1 != n2 ? "（两次分别 \(n1)/\(n2)）" : "")")
    if winCount == 0 { print("  windows=0 常见于目标在别的 Space（AX 只见当前 Space），不等于没窗口。用 mac windows 复核") }
    print(n >= 1 ? "→ L1 有希望，但必须 mac axset 实写并截图看状态指示器才算数（err=0 也可能是暗拒）"
                 : "→ L1 无望：Chromium 系走 CDP，其它走 L2 坐标")

case "axset":
    guard rest.count >= 2, let pid = pid_t(rest[0]) else { die("用法: mac axset <pid> <文本>") }
    let app = AXUIElementCreateApplication(pid)
    _ = axWake(app); Thread.sleep(forTimeInterval: 0.8)
    var target: AXUIElement?
    axWalk(app) { el, _ in if target == nil && isEditable(el) { target = el }; return target == nil }
    guard let t = target else { die("没有可编辑控件 → Chromium 系走 CDP insert，其它 mac op") }
    _ = AXUIElementSetAttributeValue(t, kAXFocusedAttribute as CFString, kCFBooleanTrue)
    let r = AXUIElementSetAttributeValue(t, kAXValueAttribute as CFString, rest[1] as CFTypeRef)
    let rb = axAttr(t, kAXValueAttribute as String) as? String ?? ""
    print("setValue err=\(r.rawValue) 读回=\"\(rb.prefix(40))\"\n→ err 和读回都不是判据。截图看发送键有没有由灰变亮；没变就是 app 不认这次输入，改 mac op 或 CDP")

case "op":
    guard rest.count >= 4, let wid = Int(rest[0]) else { die("用法: mac op <windowid> <x> <y> <文本> [@图] [send <sx> <sy>] [shot <路径>] [--bg|--fast] [--force] [--dry]") }
    let ref = extractRef(&rest)
    let text = rest[3]
    var sendXY: (String, String)?, shotPath: String?
    var i = 4
    while i < rest.count {
        if rest[i] == "send", i + 2 < rest.count { sendXY = (rest[i + 1], rest[i + 2]); i += 3 }
        else if rest[i] == "shot", i + 1 < rest.count { shotPath = rest[i + 1]; i += 2 }
        else { i += 1 }
    }
    guard let w = winInfo(wid) else { die("窗口 \(wid) 不存在。先 mac windows 重取 id") }
    let c = resolve(rest[1], rest[2], ww: w.w, wh: w.h, ref: ref)
    let sc = sendXY.map { resolve($0.0, $0.1, ww: w.w, wh: w.h, ref: ref) }
    let plan = fastMode ? "--fast 直接借焦点" : (bgOnly ? "--bg 只走后台档，不升级" : "默认阶梯：先后台档，未生效才升级借焦点")
    if dry {
        print("dry: op 窗口\(wid)（\(w.owner)）点击 \(c.note)\(sc.map { "，发送 \($0.note)" } ?? "，不发送") 输入\(text.count)字 on=\(isOnscreen(wid) ? 1 : 0)")
        print("  策略: \(plan)   用户此刻空闲 \(String(format: "%.1f", userIdle())) 秒（<\(Int(IDLE_NEED)) 秒视为他在场，借焦点档会先等）")
        var g = "  闸预检: 终端send=" + (isShellish(w) && sc != nil ? "BLOCK(\(w.owner) 是终端/IDE 类，按发送等于执行命令，需 --force)" : "pass")
        g += "  " + lockPreview() + "  " + presencePreview()
        print(g)
        if force { print("  ⚠️ --force 会拆掉以上全部闸") }
        exit(0)
    }
    // 闸一：终端/IDE 类窗口按「发送」等于执行命令。官方把这类标注为「等同 shell 访问」，我们只挡这一格。
    if isShellish(w), sc != nil, !force {
        die("refused: \(w.owner) 是终端/IDE 类窗口，点发送等于执行命令。只填不发就去掉 send；确认要执行加 --force", 2)
    }
    // ---- 第一档：postToPid 后台投递。把事件投给进程而不是屏幕坐标——不抢焦点、不切 Space、不受遮挡。
    // 只有部分 app 的 run loop 接受非活动事件（实测爱奇艺吃、豆包不吃），所以必须回读验证，
    // 判不出生效才升级到借焦点。默认走这条阶梯；--fast 跳过它，--bg 只走它。
    // 返回 ok / noop / unverifiable。三者必须分开——把 unverifiable 当成 noop 去升级，
    // 等于在「可能已经写进去了」的情况下再写一遍。实测 TextEdit 上就这么重复输入了一次。
    func backgroundWrite() -> (status: String, log: String) {
        let after = shotPath ?? (NSTemporaryDirectory() + "opbg-after-\(wid).png")
        let bp = NSTemporaryDirectory() + "opbg-before-\(wid).png"
        let haveBefore = capture(wid, bp)
        clickAt(CGPoint(x: w.x + c.pt.x, y: w.y + c.pt.y), bg: true, pid: w.pid)
        usleep(120_000)
        if !text.isEmpty { typeUnicode(text, pid: w.pid, global: false) }
        if let s = sc { usleep(120_000); clickAt(CGPoint(x: w.x + s.pt.x, y: w.y + s.pt.y), bg: true, pid: w.pid) }
        var log = "① 后台档 postToPid（零焦点/跨 Space/不受遮挡）→ pid \(w.pid) 点击 \(c.note)\(sc == nil ? "，未发送" : "，已发送")"
        Thread.sleep(forTimeInterval: 2.0)
        guard capture(wid, after) else { return ("unverifiable", log + "\n  effect=unverifiable 截图失败，判不出后台这一下有没有生效") }
        log += "\n截图: \(after) \(describeShot(after))"
        if looksBlank(after) { return ("unverifiable", log + "\n  effect=unverifiable 截回来是空图（跨 Space / 后台不渲染 / 本来就是白底），差分不可信") }
        guard haveBefore, !looksBlank(bp) else { return ("unverifiable", log + "\n  effect=unverifiable 基线图缺失或为空，差分不可信") }
        let d = diffReport(bp, after, hitX: c.pt.x / max(w.w, 1), hitY: c.pt.y / max(w.h, 1))
        let ok = d.contains("effect=confirmed") || d.contains("effect=partial")
        return (ok ? "ok" : "noop", log + "\n" + d)
    }
    if !fastMode {
        let r = backgroundWrite()
        print(r.log)
        if r.status == "ok" { print("→ 后台档已生效，全程零焦点，用户完全不受打扰。仍需确认变的是你要的（看状态指示器/副作用）"); exit(0) }
        if bgOnly { die("→ --bg 只走后台档，不升级。status=\(r.status)", 2) }
        if r.status == "unverifiable" {
            die("""
            → 🔴 停在这里，不自动升级借焦点。
            后台档的事件已经投出去了，但验证不可信（截图空/失败），**它很可能已经生效**——
            再走一遍借焦点就是重复写入（实测 TextEdit 上重复输入过一次）。
            先换硬判据确认：CDP 读 DOM / AppleScript 读文档 / app 自己的状态指示器 / 落盘文件。
            确认「确实没生效」再跑 mac op --fast（跳过后台档直接借焦点）。
            """, 2)
        }
        print("\n⬆️ 后台档判定未生效（截图可信、落点零变化），升级到借焦点档")
    }

    // ---- 第二档：借焦点。抢用户的东西之前先过两道闸 ----
    // 闸二：借焦点是全机唯一资源，两个 agent 同时 activate 会互相踢且都以为自己成功了
    if let holder = acquireFocusLock() {
        die("refused: 另一个 mac 进程（pid \(holder)）正持有借焦点锁，等 5 秒未释放。等它结束，或改走 CDP / --bg", 2)
    }
    // 闸三：用户在不在动手。官方 computer use 的同一条判据——正在打字就等他停手
    gateUserPresence("借焦点会抢走他正在用的前台窗口。", alt: "改走 CDP 或 mac op --bg，或等他手停下来重试")
    flashHUD("huashu-mac-use 正在操作「\(w.owner)」")   // 提前起，HUD 进程约 0.3 秒才画出来
    guard let target = NSRunningApplication(processIdentifier: w.pid) else { die("取不到 app 实例") }
    let prev = NSWorkspace.shared.frontmostApplication
    let actScript = activateScript(target), backScript = activateScript(prev)   // 预编译，还原要在计时窗口内
    func restoreFocus() { var e: NSDictionary?; backScript?.executeAndReturnError(&e) }
    let savedMouse = CGEvent(source: nil)?.location
    // watchdog：v1 卡死过一次要人工 kill。超时一律还焦点再退出
    DispatchQueue.global().asyncAfter(deadline: .now() + 12) {
        restoreFocus(); print("refused: op 超过 12 秒未完成，已还焦点。多半是模态对话框阻塞或 app 无响应"); exit(2)
    }
    var beforePath: String?
    if shotPath != nil {
        let bp = NSTemporaryDirectory() + "macop-before-\(wid).png"
        if capture(wid, bp) { beforePath = bp } else if let s = siblings(of: w).first(where: { capture($0.id, bp) }) { _ = s; beforePath = bp }
    }
    let t0 = Date()
    let alreadyFront = target.isActive
    var aerr: NSDictionary?
    if !alreadyFront { actScript?.executeAndReturnError(&aerr) }
    var ready = false
    for _ in 0..<120 { if target.isActive, let o = origin(of: wid), o.x > -100_000 { ready = true; break }; usleep(10_000) }
    guard ready else { die("激活超时（1.2 秒）。err=\(aerr?["NSAppleScriptErrorMessage"] ?? "无")。常见：app 在别的 Space 且终端全屏、模态对话框、缺自动化权限", 2) }
    guard let o = stableOrigin(of: wid) else { restoreFocus(); die("读不到窗口 \(wid) 的位置，可能已关闭") }
    // frontmost 回目标名不代表 Space 切过去了（终端全屏时 Space 不切），点击会打到终端上。以 onscreen 为准
    guard isOnscreen(wid) else {
        restoreFocus()
        die("refused: cross-space 激活后窗口 \(wid) 仍不在当前 Space（终端全屏时不会切 Space），已还焦点。改走 CDP，或请用户把窗口挪到当前 Space", 2)
    }
    let pt = CGPoint(x: o.x + c.pt.x, y: o.y + c.pt.y)
    guard pt.x >= 0, pt.y >= 0, pt.x <= screen.width, pt.y <= screen.height else {
        restoreFocus(); die("拒绝点击屏幕外坐标 (\(Int(pt.x)),\(Int(pt.y)))。焦点已还原。窗口 origin=(\(Int(o.x)),\(Int(o.y)))", 2)
    }
    let edge = (c.pt.x < 8 && c.pt.y < 8) ? "\n⚠️ 落点贴着窗口左上角，大概率坐标算错" : ""
    clickAt(pt)
    if !text.isEmpty { typeUnicode(text, pid: w.pid, global: true) }
    var sendNote = "，未发送"
    if let s = sc { usleep(120_000); clickAt(CGPoint(x: o.x + s.pt.x, y: o.y + s.pt.y)); sendNote = "，已点发送 \(s.note)" }
    if !alreadyFront { restoreFocus() }   // 立刻还焦点，之后的等待和截图不占用户前台
    let held = Date().timeIntervalSince(t0)
    if let s = savedMouse { moveMouse(s) }
    var shotNote = ""
    if let sp = shotPath {
        Thread.sleep(forTimeInterval: 3.0)   // 合成缓冲区刷新滞后，等够再截；这 3 秒在还焦点之后
        if capture(wid, sp) || siblings(of: w).contains(where: { capture($0.id, sp) }) {
            shotNote = "\n截图: \(sp) \(describeShot(sp))"
            if let bp = beforePath { shotNote += "\n" + diffReport(bp, sp, hitX: c.pt.x / max(w.w, 1), hitY: c.pt.y / max(w.h, 1)) }
            else { shotNote += "\n  effect=unverifiable 无基线图（操作前窗口截不到），差分跳过" }
        } else { shotNote = "\n  effect=unverifiable 截图失败（窗口可能已最小化/未渲染）。操作未必失败，换 CDP 或全屏截图交叉验证" }
    }
    let focusNote = alreadyFront ? String(format: "目标本就在前台，未动焦点（耗时 %.2f 秒）", held)
                                 : String(format: "焦点占用 %.2f 秒，已还给 %@", held, prev?.localizedName ?? "?")
    print(focusNote + "\n点击: \(c.note)" + sendNote + edge + shotNote)
    print("→ effect 只说「有没有发生事情」；确认「发生的是你要的」看应用状态指示器（发送键由灰变亮）或副作用（任务进列表）")
    exit(0)

case "shotfg":
    guard rest.count >= 2, let wid = Int(rest[0]) else { die("用法: mac shotfg <windowid> <路径>") }
    let outPath = rest[1]
    let (w, note) = shotSmart(wid, outPath)
    if !looksBlank(outPath) { print("截图: \(outPath) \(describeShot(outPath))\n后台直接截到，未动焦点 ✅ \(receipt(w))\(note)"); break }
    // 后台截不到才走到这里，下面要借焦点：同样过锁和在场闸
    if let holder = acquireFocusLock() {
        die("refused: 另一个 mac 进程（pid \(holder)）正持有借焦点锁。等它结束，或对 Chromium 系改用 cdp.js shot", 2)
    }
    gateUserPresence("为一张截图不值得抢他的焦点。", alt: "Chromium 系改 cdp.js shot（不受遮挡/Space 影响）")
    flashHUD("huashu-mac-use 正在截「\(w.owner)」的窗口", ms: 1200)
    guard let tapp = NSRunningApplication(processIdentifier: w.pid) else { die("取不到窗口所属进程") }
    let pv = NSWorkspace.shared.frontmostApplication
    let goScript = activateScript(tapp), backHome = activateScript(pv)
    let wasFront = tapp.isActive
    let tStart = Date()
    if !wasFront { var e: NSDictionary?; goScript?.executeAndReturnError(&e) }
    for _ in 0..<120 { if tapp.isActive { break }; usleep(10_000) }
    let tmpA = NSTemporaryDirectory() + "shotfg-\(w.id)-a.png"
    var settled = false
    for _ in 0..<12 {   // 渲染追赶：连续两张不再空才算画完
        usleep(60_000)
        guard capture(w.id, tmpA), !looksBlank(tmpA) else { continue }
        usleep(60_000)
        if capture(w.id, outPath), !looksBlank(outPath) { settled = true; break }
    }
    if !settled { _ = capture(w.id, outPath) }
    if !wasFront { var e: NSDictionary?; backHome?.executeAndReturnError(&e) }
    let heldFg = Date().timeIntervalSince(tStart)
    try? FileManager.default.removeItem(atPath: tmpA)
    print(wasFront ? "截图: \(outPath)\n目标本就在前台，未动焦点"
                   : String(format: "截图: %@\n后台是空图，借焦点 %.2f 秒后已还给 %@", outPath, heldFg, pv?.localizedName ?? "?"))
    if looksBlank(outPath) { print("⚠️ 借了焦点仍是空图（最小化 / 别的 Space / 禁止捕获）。Chromium 系改 cdp.js shot") }

case "open":
    // 显示名解析：中文 macOS 上 open -a "预览" 必失败，磁盘名可能与显示名无关（剪映 → VideoFusion-macOS.app）
    guard rest.count >= 1 else { die("用法: mac open <显示名|路径> [--cdp 端口] [--relaunch] [--dry]") }
    let relaunch = rest.contains("--relaunch"); rest.removeAll { $0 == "--relaunch" }
    var cdpPort: String?
    if let i = rest.firstIndex(of: "--cdp"), i + 1 < rest.count { cdpPort = rest[i + 1]; rest.removeSubrange(i...i + 1) }
    let name = rest[0]
    func sh(_ cmd: [String]) -> String {
        let p = Process(); p.executableURL = URL(fileURLWithPath: cmd[0]); p.arguments = Array(cmd.dropFirst())
        let pipe = Pipe(); p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
        try? p.run(); p.waitUntilExit()
        return String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }
    var path = ""
    let fm = FileManager.default
    if name.hasSuffix(".app"), fm.fileExists(atPath: name) { path = name }
    else if fm.fileExists(atPath: "/Applications/\(name).app") { path = "/Applications/\(name).app" }
    else if fm.fileExists(atPath: "/System/Applications/\(name).app") { path = "/System/Applications/\(name).app" }
    else {
        // 排除自动更新暂存目录（Application Support/*/installed_versions），它们是假的「多副本」
        let q = "kMDItemContentType == 'com.apple.application-bundle' && (kMDItemDisplayName == '\(name)*'c || kMDItemFSName == '\(name)*'c)"
        let hits = sh(["/usr/bin/mdfind", q]).split(separator: "\n").map(String.init)
            .filter { !$0.contains("/Application Support/") && !$0.contains("/installed_versions/") }
        path = hits.first(where: { $0.hasPrefix("/Applications/") || $0.hasPrefix("/System/Applications/") }) ?? hits.first ?? ""
    }
    guard !path.isEmpty else { die("找不到 app「\(name)」。试磁盘英文名或给 .app 绝对路径") }
    let plist = "\(path)/Contents/Info.plist"
    let bid = sh(["/usr/bin/defaults", "read", plist, "CFBundleIdentifier"])
    let ver = sh(["/usr/bin/defaults", "read", plist, "CFBundleShortVersionString"])
    let running = NSWorkspace.shared.runningApplications.first { $0.bundleURL?.path == path || ($0.bundleIdentifier == bid && !bid.isEmpty) }
    print("app: \(path)  bundle=\(bid) v\(ver)  运行中=\(running != nil ? "是(pid \(running!.processIdentifier))" : "否")")
    func cdpAlive(_ port: String) -> Bool {
        sh(["/usr/bin/curl", "-s", "--noproxy", "*", "-m", "2", "http://127.0.0.1:\(port)/json/version"]).contains("webSocketDebuggerUrl")
    }
    if let port = cdpPort {
        if cdpAlive(port) { print("CDP: 端口 \(port) 已通 ✅  下一步: node cdp.js \(port) list"); exit(0) }
        if let r = running {
            guard relaunch else {
                die("CDP 未开而 app 正在运行。带端口必须重启它（会关掉当前窗口）：确认后加 --relaunch 重跑，或让用户自己退出后再跑本命令", 2)
            }
            if dry { print("dry: 将退出 pid \(r.processIdentifier) 并以 --remote-debugging-port=\(port) 重启"); exit(0) }
            r.terminate()
            // 实测 WorkBuddy 退出要 10 秒以上，10 秒的旧超时会误判成「没退」然后放弃，
            // 而 app 随后其实退干净了——留 30 秒，且用进程存活复核（isTerminated 有时滞后）
            var gone = false
            for _ in 0..<300 {
                if r.isTerminated || NSRunningApplication(processIdentifier: r.processIdentifier) == nil { gone = true; break }
                usleep(100_000)
            }
            if !gone { die("app 30 秒内没有退出（可能弹了「是否保存」对话框需要你处理）。处理后重跑，或直接 mac open \"\(name)\" --cdp \(cdpPort!) 手动带端口启动", 2) }
            usleep(500_000)   // 给 macOS 释放 bundle 锁，紧接着 open 才不会撞上「正在退出」
        }
        if dry { print("dry: open -a \(path) --args --remote-debugging-port=\(port)"); exit(0) }
        _ = sh(["/usr/bin/open", "-a", path, "--args", "--remote-debugging-port=\(port)"])
        for _ in 0..<40 { if cdpAlive(port) { print("CDP: 端口 \(port) 已通 ✅（启动后等了约 \(0)s）  下一步: node cdp.js \(port) list"); exit(0) }; usleep(500_000) }
        die("启动了但 20 秒内 \(port)/json/version 没应答。可能不是 Chromium 系，或端口被占（换一个 >1024 的）", 2)
    }
    if dry { print("dry: open -a \(path)"); exit(0) }
    _ = sh(["/usr/bin/open", "-a", path])
    print("已 open -a（app 会到前台；只读探测请用 probe.sh，不需要启动）")

case "hud":
    let hms = rest.count > 0 ? (Double(rest[0]) ?? 1600) : 1600
    showHUD(ms: hms, text: rest.count > 1 ? rest[1] : "huashu-mac-use 正在接管屏幕",
            style: rest.count > 2 ? rest[2] : (ProcessInfo.processInfo.environment["MAC_HUD_STYLE"] ?? "corner"))

case "idle":
    let s = userIdle()
    let front = NSWorkspace.shared.frontmostApplication?.localizedName ?? "?"
    let verdict = s < IDLE_NEED ? "🔴 用户在场（借焦点档会先等他停手，最多 \(Int(IDLE_WAIT)) 秒）"
                                : "🟢 用户空闲（借焦点档可直接走）"
    print(String(format: "键鼠空闲 %.1f 秒（阈值 %.0f）  前台 app: %@\n%@", s, IDLE_NEED, front, verdict))
    if let h = lockHolder() { print(String(format: "🔒 借焦点锁被 pid %d 持有 %.1f 秒前取得", h.pid, h.age)) }
    else { print("🔓 借焦点锁空闲") }
    print("注：读操作（windows/shot/see/ax/CDP）不看这个闸，任何时候都能跑")

case "frontmost":
    print(NSWorkspace.shared.frontmostApplication?.localizedName ?? "?")

default:
    die("未知命令: \(args[1])\n" + USAGE)
}
