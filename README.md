# Cursor 账号切换器

给 Cursor 每个账号开一个独立实例，同时在线、互不干扰。界面用 WebView2 渲染，启动默认直达 **Agent 面板**。

![界面](assets/icon-preview.png)

## 它解决什么

Cursor 默认只记一个登录态。想同时用「公司号」和「个人号」，切来切去既丢上下文又得反复登录。

这个工具给每个账号分配独立的 `--user-data-dir`，各自保存登录状态、聊天记录、设置；主账号仍然用 Cursor 自带的默认目录，不受影响。

## 功能

| 功能 | 说明 |
|---|---|
| 多实例并行 | 每个账号一个独立数据目录，可同时运行 |
| 状态识别 | 从 `state.vscdb` / `auth.json` 读邮箱与订阅类型，实时显示运行状态 |
| 打开 / 重启 / 关闭 | 账号在跑时卡片上直接给出三个按钮：打开窗口、重启实例、关闭进程 |
| 后台态识别 | 关窗口只是隐藏到托盘时显示「已在后台」，不再误报成运行中 |
| 通知归属 | 系统通知不区分实例，切换器自己判断每条通知属于哪个账号，卡片上直接跳到正确窗口 |
| Agent 面板启动 | 默认 `--glass`，启动即进入对话界面；可切换成编辑器窗口 |
| 设置同步 | 把主账号的 `settings.json` / 快捷键 / 片段复制到其他账号，哈希保护防止覆盖你自己的改动 |
| 会话同步 | 白名单同步最近项目、终端历史等 UI 状态，**绝不触碰 `cursorAuth/*` 凭证** |
| 版本选择 | 多份 Cursor 安装时自动选版本最高的那份启动 |
| 打开项目 | 指定用某个账号的实例打开某个文件夹 |

## 安装

需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 和 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win11 已内置）。

```powershell
git clone <this-repo>
cd CursorAccountSwitcher
dotnet build
dotnet run
```

打包成可直接分发的版本：

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o dist
```

`dist\CursorAccountSwitcher.exe` 即可运行。

## 使用

1. 打开后会自动列出主账号（Cursor 默认数据目录）
2. 点「添加账号」→ 起个名字 → 立即启动
3. 在新窗口里点右上角头像 → Sign In 登录第二个账号
4. 之后两个账号可以同时开着，卡片上的「打开」把窗口提到前台，「重启」关掉再重新启动，「关闭」结束这个账号的进程

> 关闭窗口不等于退出：Electron 应用（Cursor / Antigravity / Codex）点窗口右上角的关闭通常只是隐藏到托盘，进程会继续活着。所以卡片会显示「已在后台 · 窗口已关闭」，这时用「打开」可以把窗口重新显示出来，用「关闭」才会真正结束进程。

数据目录在 `%APPDATA%\CursorAccountSwitcher\profiles\<id>`，每个账号独立。移除账号时可以选择是否连数据一起删。

## 关于 Agent 面板

Cursor 3.x 有两种窗口形态：

- `--glass` → **Agent 面板**（对话优先，启动直接能和 Agent 说话）
- `--classic` → 传统编辑器窗口（VS Code 布局）

切换器默认用 Agent 面板。底部「编辑器模式」开关可以改回传统布局，改完下次启动生效。

## 数据安全

- 凭证只读不写：`StateDbReader` 用 `SqliteOpenMode.ReadOnly` 打开数据库
- 同步白名单：`SessionSync` 只同步 7 个纯 UI 状态键，另有防御性检查拦截含 `auth0` / `oauth2` / `cursorAuth` 的键
- 设置同步有哈希记录：目标账号里被你自己改过的文件会被跳过，不会被主账号版本覆盖
- 启动参数只有 `--user-data-dir` 和窗口模式标志，没有 `--reset` 之类的破坏性开关

## 已知情况

**机器上装了多份 Cursor 时**，早期版本会按「第一个找到的」选 exe，可能选中迁移到其它盘的旧安装。旧版客户端连 `agentn.api5.cursor.sh` 会持续 `Premature close` / TLS 中断，表现为发消息一直失败。

现在 `CursorLocator` 会收集所有安装、按 `ProductVersion` 取最高，副标题里也会提示「另检测到 X，已忽略」。确认选对了再启动。

## 结构

```
UiBridge.cs                 WebView2 与本地服务之间的 JSON 桥
assets/ui/                  index.html / app.css / app.js —— 全部界面
Services/
  CursorLocator.cs          定位 Cursor 安装，多版本时选最新
  AntigravityLocator.cs     定位 Antigravity 2.x（Antigravity.exe）
  CodexLocator.cs           定位 Codex 桌面版（商店版 ChatGPT.exe）
  CursorProcesses.cs        枚举进程、解析 --user-data-dir、判断归属
  NativeWindows.cs          枚举顶层窗口（隐藏到托盘的也能找到）、显示窗口、WM_CLOSE
  RuntimeSnapshot.cs        一次查完进程表与窗口表，供所有账号复用
  InstanceService.cs        启动实例
  WindowActivator.cs        Win32 窗口前置
  StateDbReader.cs          只读 state.vscdb 取 Cursor 登录信息、按文本反查会话
  AntigravityLoginReader.cs 读 app_storage.json 取 Antigravity 登录名
  CodexLoginReader.cs       解析 auth.json 的 id_token 取 Codex 邮箱与套餐
  NotificationFeed.cs       读 Windows 通知库，取最近的应用通知
  NotificationAttribution.cs 判断通知属于哪个账号（会话标题 / 活跃时间）
  SettingsSync.cs           设置文件同步（哈希保护）
  SessionSync.cs            UI 状态同步（白名单）
  ProfileStore.cs           账号档案 JSON 持久化
  AppPrefs.cs               应用偏好
  AppCatalog.cs             三个应用的路径、进程名与隔离参数
Models/AccountProfile.cs    账号档案模型
Models/AppKind.cs           应用枚举与 JSON 转换
```

## Antigravity 多账号

副账号启动时同时做三件事：窗口数据用独立的 `--user-data-dir`、用户目录指向该账号的沙箱
（对话和登录落在 `<沙箱>\.gemini`）、进程环境里带 `SSH_CONNECTION`，让语言服务把 token
写成该目录下的文件，而不是机器上共享的那条 `gemini:antigravity` 凭据。主账号不改环境，
继续用现有登录。旧版 VS Code 的「Antigravity IDE」与 2.x 独立版路径不同，本工具只支持后者。

## Codex 多账号

Codex 桌面版由 Microsoft Store 分发。副账号启动时同时设置 `CODEX_HOME`（登录、会话、配置）
和 `--user-data-dir`（窗口数据），两者都指向该账号自己的目录。主账号继续用 `~\.codex`。

> 早期版本以为商店版忽略 `--user-data-dir`、命令行里不带数据目录，于是靠「启动时记下新出现的
> PID」来猜归属。实测 26.1111+ 的主进程命令行明确带 `--user-data-dir=<CODEX_HOME>`，
> 所以现在和 Cursor / Antigravity 用同一套命令行归属判断，异常退出也不会留下导致卡片卡住的假记录。

**Codex 不响应 WM_CLOSE**。实测隐藏态、显示态、`taskkill` 不带 `/F`、向所有顶层窗口发 WM_CLOSE
都没能让它退出——它把关闭一律当成「隐藏到托盘」。所以「关闭」对 Codex 是结束进程树
（它是对话应用，没有未保存文件，代价可接受），对话框里也会明说。Cursor / Antigravity 是
VS Code，窗口可见时 WM_CLOSE 会正常退出（实测 2 秒内），有未保存内容会弹提示；窗口隐藏时
会先叫出来再关，避免看不到保存提示。

卡片状态按窗口可见性区分：进程在但窗口不可见时显示「已在后台 · 窗口已关闭」，圆点变黄，
这时「打开」能把窗口重新叫出来。

## 通知与多实例

**系统通知点不进正确的账号**，这是应用本身的限制，不是切换器的 bug：

```
Windows 通知库里的处理器
  handler 174  AUMID = Anysphere.Cursor      ← 所有 Cursor 实例共用同一个
HKCU\Software\Classes\CLSID\{E058B543-…}
  = "Electron Notification Activator"
  LocalServer32 = C:\Program Files\Cursor\Cursor.exe   ← 不带任何参数
```

点击 toast 时 Windows 按 AUMID 找到这个 COM 激活器，用**不带参数**的 exe 去激活，
于是必然打开默认数据目录那个实例（也就是主账号）。AUMID 由应用内部硬编码，外部改不了；
改注册表会劫持 `cursor://` 协议、影响整台机器上所有通知，所以不做。

切换器的做法是自己记归属：读 Windows 通知库里最近 24 小时的通知，判断每条属于哪个账号，
在对应卡片上显示「最近通知」一行。点这一行会跳到正确的窗口，不需要经过系统 toast。

归属判断有两条依据：

| 依据 | 适用 | 说明 |
|---|---|---|
| 会话标题反查 | Cursor | 通知正文第一行是会话名，在哪个账号的 `state.vscdb` 里找得到就属于谁 |
| 活跃时间 | Antigravity / Codex / 标题未落库 | 比较各账号数据目录的最近写入时间与通知到达时间 |

判断不出来时**不显示通知行**，而不是猜一个账号——指错比不指更糟。Antigravity 的会话名
不落盘（正文也只有固定文案），所以它主要靠活跃时间；超过 30 分钟的通知不再按时间判断，
因为那时的活跃状态说明不了现在。

## License

MIT
