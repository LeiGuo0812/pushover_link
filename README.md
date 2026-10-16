# Codex 手机通知

Windows 托盘工具，在 Codex 桌面版本轮结束后，通过 Pushover 向手机发送项目名称、用户输入和本轮回复。无需 Codex Remote 连接，也不需要额外服务器。

当前版本：**1.1.2**。

## 下载

- [单文件 CodexPushover.exe](https://github.com/LeiGuo0812/pushover_link/raw/refs/heads/main/dist/CodexPushover.exe)
- [EXE 与复用指南](https://github.com/LeiGuo0812/pushover_link/raw/refs/heads/main/dist/CodexPushover-1.1.2.zip)
- [源码包](https://github.com/LeiGuo0812/pushover_link/raw/refs/heads/main/dist/CodexPushover-source-1.1.2.zip)

## 安装

需要 Windows、.NET Framework 4.x、支持用户级 hooks 的 Codex 桌面版，以及已安装 Pushover 的手机。换电脑时请核对实际后端：现有元数据查询支持已配置的 WSL Python 3，其他后端需按复用指南验证。

1. 下载 EXE，在其所在目录打开 PowerShell，运行：

   ```powershell
   .\CodexPushover.exe --install
   $exe = Join-Path $env:LOCALAPPDATA 'CodexPushover\CodexPushover.exe'
   & $exe --settings
   ```

2. 在设置窗口填写自己的 Pushover Application Token 和 User Key。凭据仅保存在当前用户私有目录；不要发到聊天或提交 Git。
3. 按[复用指南](docs/配置与原理.md)核对这台电脑的 Codex 配置目录、后端与元数据查询配置，再执行：

   ```powershell
   & $exe --install-hooks
   ```

4. 在 Codex 的 Hooks 设置中查看并信任 `UserPromptSubmit` 和 `Stop` 两项。修改 hook 定义后需要重新审核。
5. 先发送测试通知，再完成一轮真实本地聊天，确认手机收到且输入、回复匹配。

## 1.1.2 更新

新版 Codex 会为输入框自动建议运行独立后台回合。此版本识别明确的应用生成指令，按 `session_id + turn_id` 保存忽略标记，阻止建议回合入队。正常回复、下一轮回复，以及用户主动请求的建议或 JSON 回复仍会通知。

升级不改变 hook 命令，无需因此重新信任。31 项隔离核心测试已通过；使用实际记录回放，确认自动建议被过滤、正式回复被保留。详见 [CHANGELOG](CHANGELOG.md)。

## 运行方式

- 单个 EXE 提供中文设置窗口、蓝色托盘图标和登录启动功能。
- 正常运行有两个同名进程：托盘／发送进程，以及 `--guard` 守护进程。
- hook 只进行本地处理并返回 `{}`，网络发送由常驻后台完成。
- 持久队列保留失败消息；网络错误和 5xx 有限重试，长期断网后继续补发；4xx 暂停等待处理。
- 默认每轮一条，正文最多 1024 个 Unicode 字符，超长截断并标注。

只有 HTTP 200 且响应 `status=1` 才记录“Pushover已接受并入队”。这不代表手机已经收到；超时重试也不能保证严格只发送一次。

云端编排可能不支持本机用户 command hook，需要单独验证。不能用 CLI 测试通过替代真实桌面回合验证。

## 文档与构建

- [日常使用、暂停、恢复和卸载](docs/使用说明.md)
- [配置原理、换电脑复用与完整 Codex 提示词](docs/配置与原理.md)
- [从源码构建](docs/构建说明.md)

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-CoreTests.ps1
```

仓库中的程序和源码不包含真实凭据、通知历史、用户对话或本机信任配置。
