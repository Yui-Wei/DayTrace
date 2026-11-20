# 拾时下载页（GitHub Pages）

这是无需构建、无需依赖的静态网站。所有资源路径都是相对路径，适用于 `https://Yui-Wei.github.io/DayTrace/` 这样的项目 Pages 地址。

## 本地预览

在项目根目录运行：

```powershell
python -m http.server 8317 --bind 127.0.0.1 --directory docs
```

然后打开 `http://127.0.0.1:8317/`。

## 发布到本项目 GitHub Pages

页面确认后，将 `docs/` 提交到 `main` 分支。到仓库 **Settings → Pages → Build and deployment**：

- Source：`Deploy from a branch`
- Branch：`main`
- Folder：`/docs`
- 点击 Save。

网站地址：https://yui-wei.github.io/DayTrace/ 。发布源为 main 分支的 /docs；后续经用户确认的页面更新推送到该目录后会自动部署。

## 下载与版本信息

主下载入口始终是 `https://github.com/Yui-Wei/DayTrace/releases/latest/download/DayTrace.exe`，无需跟随每次发布手动修改。

页面尝试读取 GitHub 最新 Release 显示版本和大小。接口限流或不可用时，仍可通过主按钮下载最新版，并通过版本页获取压缩包。

截图是使用隔离演示记录生成的实际软件界面，不包含用户的真实记录或密钥。
