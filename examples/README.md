# 配置示例

适用版本：1.2.1。这里没有真实凭据或信任记录。程序安装后会在本机私有目录创建实际文件，不要把填好的文件提交到仓库。

| 示例 | 实际文件 | 用途 |
| --- | --- | --- |
| credentials.example.json | %LOCALAPPDATA%\CodexPushover\credentials.json | 两个凭据字段；在程序设置中填写自己的值 |
| notification-options.example.json | %LOCALAPPDATA%\CodexPushover\notification-options.json | 当前内容格式、长度及交互提醒开关 |
| installation.example.json | %LOCALAPPDATA%\CodexPushover\installation.json | 实际 Codex 配置目录和可选元数据查询；只作字段对照，不覆盖完整安装记录 |
| hooks.example.json | 实际 Codex 配置目录中的 hooks.json | 三项事件的结构；保留已有其他 hooks |

两个交互提醒开关默认 false。`--suggestions on/off` 和 `--input-reminders on/off` 会保存选择，关闭时取消相应待发记录；设置窗口提供相同操作。总开关由 `--enable` / `--disable` 控制，不在该 JSON 示例中。

hooks 示例中的 Windows 命令使用系统的 `%LOCALAPPDATA%`，WSL 路径里的 `REPLACE_WITH_WINDOWS_USER` 必须替换。推荐运行 `--install-hooks`，让程序使用核对过的安装路径生成定义，再由用户在 Codex 中审核；开关打开不等于 hook 已被信任。

installation 示例的元数据字段留空表示不启用 WSL 查询。已有合适环境时才填写本机绝对路径、发行版和数据库位置，不要照抄其他电脑的值。新电脑仍须分别确认真实桌面触发、接口接受和手机收到。
