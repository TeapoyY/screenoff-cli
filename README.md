# ScreenOff CLI

Windows 10/11 命令行息屏工具。通过 DDC/CI 关闭显示器电源，让电脑和后台任务继续运行。按下并松开空格，或单击鼠标左键、右键，可恢复亮屏。

[下载独立 EXE](https://github.com/TeapoyY/screenoff-cli/releases/latest) · [MIT License](LICENSE)

## 使用

将 `screenoff.exe` 放到常用目录，或加入用户 `PATH`。从当前已解锁的 Windows 桌面运行。

```powershell
screenoff probe                          # 只读检查各显示器的 DDC/CI 支持
screenoff off                            # 倒计时 3 秒，等待唤醒后退出
screenoff off --delay 5 --for 60          # 5 秒后关屏，关屏指令获接收后计时 60 秒
screenoff status                         # 查询正在运行的 CLI 会话
screenoff wake                           # 从另一个终端取消倒计时或恢复显示器
screenoff off --delay 1 --dry-run         # 验证命令与倒计时，无硬件读写
```

`--delay` 接受 1–60 秒。`--for` 接受 1–86,400 秒。`--off` 和 `--wake` 也可用作命令别名。倒计时期间可在前台终端按 Ctrl+C 取消。桌面快捷方式可把目标设为 `"C:\path\to\screenoff.exe" off`。

| 退出码 | 含义 |
| --- | --- |
| `0` | 命令完成。`wake` 在会话已退出或原本空闲时返回 `0`。 |
| `2` | 命令或参数无效。 |
| `3` | 桌面已有会话、控制进程未响应，或等待恢复超时。 |
| `4` | 当前会话不可用、硬件操作失败，或 `probe` 发现不支持的显示器。 |

`status` 输出单个状态值 `idle`、`busy`、`starting`、`countdown`、`poweringoff`、`off`、`restoring` 或 `stopping`。`off` 表示显示器已接收关屏命令。亮屏时 CLI 会用重新取得的显示器句柄读取电源状态。若部分显示器的关屏指令失败，CLI 会尝试恢复已受影响的显示器。`wake` 最多等待 45 秒观察控制进程退出。

可在启动 `off` 前设置 `SCREENOFF_STATUS_PATH`，将带时间戳的状态同时写入文件。该路径的父目录需已存在。

## 显示器兼容性

在显示器菜单中启用 DDC/CI。真实息屏要求所有检测到的显示器都明确支持 DDC/CI 电源控制。扩展坞、转换器、虚拟显示器和部分笔记本内屏可能无法通过此接口控制。原桌面版在运行时会独占硬件控制，请先正常退出旧版再启动真实关屏。

系统关机、强制终止进程或显示器驱动卡住可能中断恢复。此时可使用显示器实体电源键。

## 构建与测试

从 Windows PowerShell 或 PowerShell 运行。构建使用 Windows 随 .NET Framework 4.x 提供的 C# 编译器。发布文件为 `dist\screenoff.exe`。

```powershell
.\build.ps1
.\test.ps1
```

测试包含模拟硬件状态机和同一 Windows 桌面的跨进程 dry-run。真实显示器上的通断电结果取决于设备和连接方式。

---

ScreenOff CLI powers down DDC/CI monitors while keeping Windows running. It requires an interactive Windows 10/11 desktop. Use `probe` to inspect monitor support, `off` to start a session, `status` to query it, and `wake` from another terminal to cancel or restore. Space or a left/right click also wakes the monitors. `off --for 60` schedules a wake, and `off --dry-run` exercises the countdown without accessing display hardware.
