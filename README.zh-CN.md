# DiskLens 磁盘透镜

[English](README.md) · **简体中文** · [日本語](README.ja.md)

Windows 磁盘空间分析器：顶部四格统计卡，左边按用途着色的资源地图（单击目录进入），右边可勾选、可「复制脚本」的清理建议。右键方块可以在资源管理器中显示、在网上搜索、移到回收站或永久删除。

## 实测（2026-10-17，本机）

| 场景 | 规模 | 耗时 | 对账 |
|---|---|---|---|
| D:\ 常规扫描（标准权限） | 42 万文件 | 3.6 秒 | 未统计 0.50%（MFT 自身和无权访问的目录） |
| C:\ 常规扫描（标准权限） | 48 万文件 | 7.7 秒 | 未统计 28%（112 个无权访问的目录、卷影副本等） |
| C:\ MFT 深度扫描（管理员） | 72 万记录 | 1.6 秒 | 与 TotalClusters−FreeClusters 差 0 字节 |
| D:\ MFT 深度扫描（管理员） | 46 万记录 | 0.8 秒 | 与 TotalClusters−FreeClusters 差 0 字节 |
| 主文件夹常规扫描 | 24 万文件 | 1.3 秒 | — |

硬链接只计一次（C 盘去重 55,017 个副本、14.8 GB）；搜索 200 万条目单个子串约 2.5 ms。

## 下载

在 Releases 页下载 `DiskLens-<版本>-win-x64.zip`，**先解压**到任意文件夹，再运行里面的 `DiskLens.exe`。别在压缩包窗口里直接双击 exe：解压软件只会单独抽出 exe，旁边的文件都不在，程序起不来。

## 打开

双击 `Start-DiskLens.cmd`（或在 PowerShell 里运行 `.\Start-DiskLens.ps1`）：第一次会先构建，要几分钟，之后直接启动。已经构建过的话，也可以直接打开：

```text
src\DiskLens\bin\Release\net10.0-windows\DiskLens.exe
```

启动后在磁盘卡片上点「扫描」，扫描方式自动选择（启动页不再提供扫描文件夹的入口，见 DESIGN §9.1）。常规扫描无需管理员；本地 NTFS 卷的 MFT 深度扫描需要以管理员身份重启。未授权目录、系统保留空间等差额会单独标为「未统计」。

需要 Windows 10/11（x64）、.NET 10 Desktop Runtime 和 Microsoft Edge WebView2 Runtime。本项目不要求登录账号。

界面有简体中文、English、日本語三种：在启动页左上角的语言按钮里切换，马上生效，选择会记住，下次启动就是它；没选过时跟系统界面语言（中文简繁都算中文、日文用日文，其余英文）。

## 开发和验证

需要 .NET 10 SDK、Node.js 和 pnpm。在 PowerShell 中执行：

```powershell
.\build.ps1          # 检查前端、构建界面和 Release 桌面程序
.\build.ps1 -Check   # 另外运行后端沙箱测试和真实 WebView2 集成检查
```

单独运行检查：

```powershell
pnpm --dir web check
pnpm --dir web test
dotnet run --project tools/DiskLens.Check -- all
dotnet run --project tools/DiskLens.UiCheck
```

界面文字在 `web/src/lib/locales`（中文原文就是 msgid），后台文字写在 `L.T(中文, 英文, 日文)` 里，做法见 DESIGN §8.8。

测试文件只生成在 `.sandbox`；删除测试只处理该目录内的夹具。界面集成检查会临时打开窗口，扫描自己生成的文件夹，验证前后端桥接，并把截图保存到 `.sandbox/ui/desktop.png`。

浏览器预览：

```powershell
pnpm --dir web dev
```

浏览器中会明确显示「演示模式 · 模拟数据」，不访问真实文件。运行桌面程序才会读取真实磁盘。桌面开发可设置 `DISKLENS_DEV_URL=http://localhost:5173`，只接受本机 HTTP/HTTPS 地址。

## 操作

- 单击选中，双击目录放大；面包屑、后退和上一级切换位置。
- 搜索支持名称、通配符、`ext:mp4,mkv`、`size:>1gb`、`type:file`、排除词 `-cache`；多条件以空格连接，`a | b` 表示满足任一组。默认按相关度排（名字整个对上的在前），可切回按占用；`wd` 这样的拼音首字母找得到「文档」；一条都对不上时给出近似结果（`dwnld` 找到 download）。
- 扫描之后别的程序删掉的文件，不用重扫就从地图、列表和搜索里消失（顶栏写明同步了几项）；新建的要重扫才算进来。
- 「占用空间」按实际分配统计；「文件大小」按逻辑长度统计。硬链接只向父目录计入一次。
- 侧栏提供详情、类别构成、大文件和清理建议。右键可显示于资源管理器、按名称网上搜索、复制路径或打开属性。
- 删除会先列出条目和保护原因。默认移到回收站，空间要等清空回收站后才释放。永久删除及清理命令会再次确认。
- 「复制脚本」给出的 PowerShell 脚本只删扫描时就在、之后没动过的东西：每个目标运行时先核对文件身份，扫描开始之后新建、改过或挪进来的条目一律保留，`.ssh` 之类的密钥目录不删。要删这些，重新扫描后再复制脚本。
- `Ctrl+F` 搜索，`Alt+←/→` 后退/前进，`Alt+↑` 上一级，`F5` 重扫，`Delete` 移到回收站，`Shift+Delete` 永久删除，`Esc` 关闭浮层。

WebView2 配置保存在程序目录下的 `data\webview2-user` 或 `data\webview2-admin`（程序目录不可写时退回 `%LOCALAPPDATA%\DiskLens`），管理员与普通权限使用不同目录。扫描结果（文件树）仅保存在当前进程内存中；每个范围最近一次扫描的摘要（含根下面的大项和认出的应用，用来对照前后两次扫描）存在同一 `data` 目录的 `scan-summaries-*.json`；界面语言存在同一目录的 `settings.json`（管理员和普通权限共用，提权后语言不变）。网上搜索会发送所选条目的名称，界面会调用本机默认浏览器。

详细契约和设计见 [docs/DESIGN.md](docs/DESIGN.md)。

## 许可

[MIT](LICENSE)
