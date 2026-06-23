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

## 构建

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

## 配置位置

服务列表保存在：

```text
~/Library/Application Support/LANWebTerminalManager/endpoints.json
```
