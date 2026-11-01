# Codex 手机通知

Windows 托盘工具，在 Codex 桌面版本轮结束后，通过 Pushover 向手机发送项目名称、用户输入和本轮回复。无需 Codex Remote 连接，也不需要额外服务器。

当前版本：**1.2.1**。支持可选的输入建议和提问提醒；设置窗口已增高以容纳全部选项。

## 下载

- [单文件 CodexPushover.exe](https://github.com/LeiGuo0812/pushover_link/raw/refs/heads/main/dist/CodexPushover.exe)
- [EXE 与复用指南](https://github.com/LeiGuo0812/pushover_link/raw/refs/heads/main/dist/CodexPushover-1.2.1.zip)
- [源码包](https://github.com/LeiGuo0812/pushover_link/raw/refs/heads/main/dist/CodexPushover-source-1.2.1.zip)

主分支只保留当前版本分发文件，旧版本通过 Git 标签与提交查看。

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

4. 在 Codex 的 Hooks 设置中查看并信任 `UserPromptSubmit`、`Stop` 和 `PreToolUse`。`PreToolUse` 只匹配提问工具；修改定义后需要重新审核。
5. 在设置中打开所需的交互提醒开关。先发送测试通知，再完成真实本地聊天和一次提问，分别核对本机事件、接口结果与手机显示。

## 1.2.1 更新

默认窗口内容区从 920 × 740 增至 920 × 860（100% 缩放），完整显示新增开关、字符上限和底部按钮；屏幕空间不足时允许滚动。

双击蓝色托盘图标，在“通知设置”中独立切换：

- **输入框建议提醒**：恢复 1.1.2 过滤的、可按 Tab 采纳的下一步输入建议，标题为“Codex 有新的输入建议”。只发送建议正文，不发送用于生成建议的后台上下文。
- **需要回答时提醒**：Codex 调用 `request_user_input` 或 `request_user_input_async` 时，发送问题和选项，标题为“Codex 需要你回答”。新增的 `PreToolUse` 必须先由用户信任。

开关自动保存，重启后保留。配置中缺少对应选项时默认关闭，需要自行开启。关闭会取消该类尚未发送的记录，重新开启不会补发这些记录；已经发出的网络请求无法撤回。正常回复仍由总开关控制。

提问、自动建议和正式回复各自去重，同一轮的多次提问不会相互覆盖。此功能针对本机 Codex 桌面，不覆盖 ChatGPT 网页、云端聊天或不经过本机工具 hook 的界面提示，也不会自动回答或批准操作。详见 [交互提醒说明](docs/交互提醒.md)。

39 项核心测试和 8 项布局检查通过。两类交互提醒的合成发送测试均获 Pushover 接受；用户报告收到真实提问通知，但本机尚未关联到对应 hook 事件，因此未将这项记为完整链路验证通过。详细结果见[使用说明的验证记录](docs/使用说明.md#验证记录)。

## 运行方式

- 单个 EXE 提供中文设置窗口、蓝色托盘图标和登录启动功能。
- 正常运行有两个同名进程：托盘／发送进程，以及 `--guard` 守护进程。
- hook 只进行本地处理并返回 `{}`，网络发送由常驻后台完成。
- 持久队列保留失败消息；网络错误和 5xx 有限重试，长期断网后继续补发；4xx 暂停等待处理。
- 每轮正式回复一条；开启交互提醒后，可另收到提问或建议通知。每条正文最多 1024 个 Unicode 字符，超长截断并标注。

只有 HTTP 200 且响应 `status=1` 才记录“Pushover已接受并入队”。这不代表手机已经收到；超时重试也不能保证严格只发送一次。

云端编排可能不支持本机用户 command hook，需要单独验证。不能用 CLI 测试通过替代真实桌面回合验证。

## 文档与构建

- [日常使用、暂停、恢复和卸载](docs/使用说明.md)
- [配置原理、换电脑复用与完整 Codex 提示词](docs/配置与原理.md)
- [从源码构建](docs/构建说明.md)
- [配置示例](examples)：空凭据、通知开关、三项 hooks 和可选元数据配置，使用前核对本机路径。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\src\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-CoreTests.ps1
```

仓库中的程序和源码不包含真实凭据、通知历史、用户对话或本机信任配置。
