# 局域网网页终端管理器

原生 SwiftUI macOS app，用来管理本机目录中的网页项目，并把它们快速发布到局域网内访问。

## 功能

- 管理多个网页根目录
- 启动和停止 `python3 -m http.server`
- 自动展示本机地址与局域网 IP 地址
- 配置端口、监听范围、入口路径和启动后自动打开
- 打开网页、打开目录、复制访问地址
- 在选中目录中执行维护命令
- 查看每个服务的日志尾部
- 首次启动会自动识别 `DestinyApp` 和旧的“战斗设计”目录
- 提供 macOS 原生版和本地 Web 版

## macOS 版

Release 下载：

- `LANWebTerminalManager-v版本号-macOS.zip`：macOS app 平台包

构建：

```bash
./build_app.sh
```

构建完成后打开：

```bash
open "build/局域网网页终端管理器.app"
```

## 安装提示

Release 包是本地 ad-hoc 签名，未做 Apple notarization。如果 Safari 下载后 macOS 提示应用“已损坏”或无法验证，先解压并放到 `/Applications`，然后执行：

```bash
xattr -dr com.apple.quarantine "/Applications/局域网网页终端管理器.app"
```

再重新打开 app。

也可以直接开发运行：

```bash
swift run
```

## Web 版

Release 下载：

- `LANWebTerminalManager-v版本号-web.zip`：Web 平台包，支持 macOS / Windows / Linux

Web 版使用 Node.js 本地服务提供管理 UI 和 API，默认只监听本机 `127.0.0.1:4177`：

下载并解压 Web 包后，优先打开：

```text
index.html
```

启动页会检测本地服务是否已经运行。服务完整时会自动进入管理器；服务未运行时会按平台提示安装 Node.js / Python，并提供启动脚本。

也可以手动启动：

```bash
./web_start.sh
```

Windows:

```bat
web_start.bat
```

然后打开：

```text
http://127.0.0.1:4177
```

也可以指定端口：

```bash
LWM_PORT=4188 ./web_start.sh
```

Web 版支持添加/删除网页终端、启动/停止服务、选择主页、复制/打开局域网地址、执行维护命令和查看日志。因为它能执行本机命令，默认不要绑定到公网地址。

## 配置位置

服务列表保存在：

```text
macOS:   ~/Library/Application Support/LANWebTerminalManager/endpoints.json
Windows: %APPDATA%\LANWebTerminalManager\endpoints.json
Linux:   ~/.config/LANWebTerminalManager/endpoints.json
```
