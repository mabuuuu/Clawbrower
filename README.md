# Clawbrower

OpenClaw 桌面悬浮窗客户端 —— 一个轻量级 WPF 应用，以始终置顶的悬浮窗形式接入 OpenClaw 对话网关，支持流式 Markdown 渲染、语音输入（PTT）、MCP 远程控制。

## 功能概览

### 对话
- **悬浮窗 UI**：始终置顶的紧凑窗口，不抢占焦点，适合边工作边对话
- **流式输出**：WebSocket 长连接，逐字接收回复并实时渲染
- **Markdown 渲染**：自研解析器 + 渲染器，支持标题、列表、表格、代码块、行内格式（粗体/斜体/行内代码）
- **会话管理**：多会话切换、历史消息滚动加载、新建/停止对话
- **全局热键**：可配置快捷键唤起/隐藏窗口

### 语音输入（PTT / 唤醒词）
- **按住说话**：默认 F12 键，按住录音、松开发送，全局键盘钩子（窗口最小化也可用）
- **唤醒词激活**：说"二七二七"自动开始对话（本地 ONNX 检测，无需按键），说完停顿约 2 秒自动结束发送；阈值可在设置中调节
- **实时反馈**：录音时显示浮动状态层（"说话中..."脉冲动画），收码转写后显示用户消息，收到回复后自动播放 TTS 音频
- **伪流式 TTS**：分段接收 mp3 数据，边收边播，减少等待时间
- **语音服务器**：WebSocket 连接 `:9529/speech`，PCM 16kHz/16bit/mono 采集，200ms 分块发送

### MCP 远程控制
- **windows-mcp 隧道**：启动 `windows-mcp.exe` 并通过 `frpc` 建立远程隧道，实现从外部远程控制本机
- **设备名称标识**：frpc 代理名称使用设置中配置的设备名，便于在 frp 服务端区分不同设备
- **自动重启**：修改 MCP 配置后自动重启隧道以应用新配置
- **Defender 排除**：自动将 `frpc.exe` 添加到 Windows Defender 排除项

### 外观定制
- **窗口透明度**：调节背景透明度
- **文字透明度与颜色**：独立控制文字透明度和颜色，实时预览
- **深色主题**：全局深色配色，包括 ComboBox 等 WPF 控件

## 技术栈

| 组件 | 技术 |
|------|------|
| 运行时 | .NET 8.0 (net8.0-windows) |
| UI 框架 | WPF + Windows Forms（托盘图标） |
| 音频 | NAudio 2.2.1（录音采集 + mp3 播放） |
| 加密 | BouncyCastle.Cryptography 2.6.2（ED25519 签名认证） |
| 通信 | WebSocket（ClientWebSocket） |
| 配置 | JSON 文件（`%LOCALAPPDATA%/Clawbrower/settings.json`） |

## 项目结构

```
Clawbrower/
├── App.xaml / App.xaml.cs        # 应用入口、托盘菜单、生命周期管理
├── MainWindow.xaml / .cs         # 悬浮窗主界面
├── SettingsWindow.xaml / .cs      # 统一设置窗口（外观/连接/语音 三标签页）
├── RecordingOverlay.xaml / .cs    # 语音录音状态浮层
├── Controls/
│   └── MarkdownBlock.xaml.cs      # Markdown 渲染控件（FlowDocument）
├── Dialogs/
│   ├── ConnectionDialog.xaml.cs   # 连接设置对话框
│   ├── McpConfigDialog.xaml.cs    # MCP 远程控制设置
│   ├── SpeechSettingsDialog.xaml.cs # 语音设置（已整合到统一设置）
│   └── InputDialog.xaml.cs        # 通用输入对话框
├── Models/
│   ├── GatewayProtocol.cs        # 网关协议数据模型
│   └── MarkdownBlocks.cs          # Markdown 解析结果模型
├── Services/
│   ├── GatewayClient.cs           # WebSocket 网关客户端（握手/认证/流式消息）
│   ├── ConfigService.cs           # 配置管理（网关/设备/MCP/语音）
│   ├── MarkdownParser.cs          # Markdown 解析器
│   ├── SpeechService.cs           # 语音协调状态机
│   ├── SpeechClient.cs            # 语音 WebSocket 客户端
│   ├── AudioCaptureService.cs     # NAudio 录音采集
│   ├── AudioPlayer.cs             # NAudio mp3 播放
│   ├── KeyboardHookService.cs     # 全局键盘钩子（PTT）
│   ├── McpService.cs              # MCP + frpc 进程管理
│   └── Logger.cs                  # 日志
├── ViewModels/
│   └── MainViewModel.cs           # 主视图模型（消息/会话/连接状态）
├── mcp/                           # windows-mcp.exe + frpc.exe 二进制
├── Clawbrower.bat                 # 启动脚本
└── launch.ps1                     # PowerShell 启动脚本
```

## 构建与运行

### 前置要求

- .NET 8.0 SDK
- Windows 10/11

### 构建

```bash
dotnet build -c Debug
```

### 运行

```powershell
# 方式一：PowerShell 脚本（自动关闭旧实例再启动）
.\launch.ps1

# 方式二：直接运行
.\bin\Debug\net8.0-windows\Clawbrower.exe

# 方式三：批处理
.\Clawbrower.bat
```

### 发布

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## 配置说明

首次启动会弹出连接设置对话框。所有配置存储在 `%LOCALAPPDATA%/Clawbrower/settings.json`。

### 连接配置
| 字段 | 说明 | 默认值 |
|------|------|--------|
| GatewayUrl | OpenClaw 网关 WebSocket 地址 | `ws://127.0.0.1:18789` |
| 认证方式 | 密码认证 / Token 认证 | — |

### 设备认证
- 首次启动自动生成 ED25519 密钥对
- 设备 ID 基于公钥派生
- 连接时使用密钥对签名完成握手认证
- 首次连接需服务端批准配对（`openclaw devices approve <requestId>`）

### 语音配置
| 字段 | 说明 | 默认值 |
|------|------|--------|
| 语音服务器地址 | WebSocket 地址 | `ws://127.0.0.1:9529/speech` |
| PTT 按键 | 推住说话的虚拟键码 | F12 |
| 模式 | PTT / 唤醒词 | PTT |

> 语音服务器地址默认从网关地址的 host 部分派生（`:9529/speech`）。

### MCP 远程控制配置
| 字段 | 说明 |
|------|------|
| 设备名称 | frpc 隧道代理名称（区分不同设备） |
| 本地端口 | windows-mcp 监听端口 |
| 远程端口 | frpc 隧道映射的远程端口 |
| Frp 服务器地址 | frp 服务端地址 |
| Frp 服务器端口 | frp 服务端端口 |
| Frp 认证令牌 | frp auth.token |

## 架构概览

```
┌──────────────────────────────────────────────┐
│                  Clawbrower                   │
│                                               │
│  ┌─────────────┐    ┌──────────────────────┐  │
│  │  MainWindow  │◄──►│   MainViewModel      │  │
│  │  (悬浮窗UI)  │    │ (消息/会话/状态管理)  │  │
│  └──────┬──────┘    └──────────┬───────────┘  │
│         │                      │              │
│         │   ┌──────────────────┼──────────┐   │
│         │   ▼                  ▼          ▼   │
│  ┌──────┴───────┐  ┌────────────┐  ┌────────┐│
│  │GatewayClient  │  │SpeechService│  │McpSvc  ││
│  │  (WebSocket)  │  │ (PTT状态机) │  │(进程管)││
│  └───────┬───────┘  └─────┬──────┘  └───┬────┘│
│          │                │             │     │
│  ┌───────▼───────┐  ┌─────▼──────┐  ┌───▼──┐  │
│  │  OpenClaw     │  │ 语音服务器  │  │frpc + │ │
│  │  Gateway      │  │  :9529     │  │win-mcp│  │
│  │  :18789       │  │  /speech   │  │ 隧道   │  │
│  └───────────────┘  └────────────┘  └───────┘  │
└──────────────────────────────────────────────┘
```

## 日志

日志输出到 `%LOCALAPPDATA%/Clawbrower/logs/`，按日期轮转。

## License

私有项目。
