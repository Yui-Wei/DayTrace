# 拾时 · DayTrace

Windows 11 桌面活动记录工具，当前版本 **V1.1.3**。

自动记录前台软件和窗口标题、统计使用时长，支持系统托盘、开机自启动、手动分类，以及 OpenAI 兼容接口的 AI 分类和日报。时间轴支持全天、一小时、两分钟展开。

## 使用

双击目录中的 `DayTrace.exe`。具体操作参见 [使用说明](使用说明.md)。

## 发布与更新

运行 `source/package.ps1` 可生成带校验信息的更新包。推送 `vX.Y.Z` 标签会由 GitHub Actions 自动编译并发布 `DayTrace.exe`、`update.json` 与压缩包，供软件内更新使用。

自动检查设置在关于页，默认关闭，开启后每天首次启动或打开时检查一次。发现正式新版后可在软件内下载、校验并更新。

## 编译

在项目根目录用 Windows PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

使用 Windows 自带的 .NET Framework 4.x 编译器及 WPF，无需下载第三方依赖。生成程序位于根目录 `DayTrace.exe`。

## 目录

- `source/`：当前版本源码、主题、图标和构建脚本。
- `使用说明.md`：功能和升级说明。
- `验证结果.txt`：V1.1.3 的验证结果。
- `分类颜色预览.png`：界面预览。
- `关于页预览.png`：关于与更新界面预览。

## 数据与版本管理

用户数据保存在 `%LOCALAPPDATA%\DayTrace`，API Key 保存在 Windows 凭据管理器；迁移源码目录不会迁移或删除这些数据。

Git 跟踪源码、构建脚本及说明。生成的程序、压缩包、临时文件及本地用户数据不纳入版本管理。正式版本标记为 `v1.1.3`。后续按 [版本规则](VERSIONING.md) 发布。
