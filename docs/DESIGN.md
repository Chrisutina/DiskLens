# DiskLens 磁盘透镜 · 设计文档

> 把磁盘画成一张能点、能删、而且算得准的地图。别家工具把 WinSxS 的硬链接算两遍我能念叨一年，这个不许。

- 版本：v0.1 设计稿，2026-10-15
- 契约以代码为准：`src/DiskLens.Core/Contracts.cs` ↔ `web/src/lib/types.ts`，两边必须同步改。
- 本文是所有并行开发者的唯一事实来源。文档和代码冲突时，先问集成负责人，别自己猜。

---

## 0. 需求 → 验收点

| # | 用户原话 | 落地方式 | 验收 |
|---|---|---|---|
| R1 | 根据不同目录的不同大小显示不同的尺寸标签页 | 嵌套 squarified treemap：目录有标题条，文件按类型着色 | 面积与所选度量成正比；12k 个节点的布局加绘制 < 16 ms |
| R2 | 右击可以在资源管理器打开 | 右键菜单：目录直接打开，文件在父目录里选中 | 路径带逗号、空格、中文或超长都能正确打开 |
| R3 | 也可以浏览器搜索 | 右键「在网上搜索」，引擎可选必应/百度/谷歌，只发送名字，不发送完整路径 | 管理员进程里打开的浏览器是普通权限 |
| R4 | 也可以直接删除 | 默认移到回收站，Shift 或菜单可永久删除；必须二次确认；系统关键路径直接拦截 | 删除后树和统计原地更新，不需要重扫 |
| R5 | 软件打开可以自己选择扫描磁盘 | 启动页为每个卷显示独立磁盘卡；已保存范围可重新扫描 | 显示文件系统、容量、可用空间、SSD/HDD |
| R6 | 管理员权限下可以深度扫描 | 管理员 + 本地 NTFS 卷时直接读 `$MFT`；其他情况开启 SeBackupPrivilege 再遍历 | 普通权限下可一键以管理员身份重启，并自动开始深度扫描 |
| R7 | UI 要精美 | 按 §8、§9 的视觉规范 | 浏览器预览与真机截图逐项对照 |
| R12 | 最终形态应该和图片一样 | 浅色仪表盘：四格统计卡 + 按用途着色的资源地图 + 可勾选、可生成脚本的清理建议（§4.7、§6.2、§8、§9） | 同尺寸截图与参考图并排对比，布局、配色、字体、交互一致 |
| R8 | 搜索逻辑要快 | 内存名称索引 + 并行向量化子串匹配 | 200 万条目：建索引 < 500 ms，普通查询 < 50 ms |
| R9 | 统计要准确 | §4.6 准确性规则，并与卷的已用空间对账；扫描报告分开给出拒绝访问、跳过的链接/挂载点、读取失败和未解释容量（§4.6） | 差额显式展示，有依据的部分单列、其余写「原因未确认」；MFT 模式差额 < 0.5% |
| R10 | 可以建议可删除内容 | §6 规则表，分「可放心清理 / 谨慎 / 别删」三组 | 每条都有依据、大小和动作 |
| R11 | 侧边栏可以显示其他详情 | §9.3：详情 / 构成 / 大文件 / 清理 四个标签页 | 选中节点后立即更新 |

---

## 1. 技术栈与目录

- 后端：.NET 10 / C#，目标框架 `net10.0-windows`。除 `Microsoft.Web.WebView2` 外不引入任何 NuGet 包。P/Invoke 用 `LibraryImport` 或 `DllImport` 都行，结构体保持 blittable。
- 宿主：WinForms 窗体里放一个 WebView2（本机运行时 154.x）。
- 前端：Svelte 5（runes 模式，禁止 `on:click`、`export let` 这类旧语法）+ TypeScript + Vite，包管理用 pnpm；图标用 `@lucide/svelte`。字体 Geist / Geist Mono 用 `@fontsource-variable/*` 随包打包。
- 机器：Windows 11，24 核，C/D/E 盘都是 NTFS。开发进程**没有**管理员权限。

```
disk-lens/
├─ DiskLens.slnx
├─ docs/DESIGN.md                    本文件
├─ src/DiskLens.Core/                类库：树、扫描、搜索、清理、删除、外壳操作（不引用 WinForms）
│  ├─ Contracts.cs                   DTO、请求记录、JSON 选项（契约）
│  ├─ FileTree.cs                    树模型（SoA）
│  ├─ FileCategories.cs              扩展名 → 类别
│  ├─ Purposes.cs, Roles.cs          主用途 U；数据角色、证据等级、所属对象种类（与前端 purposes.ts / roles.ts 有契约测试）
│  ├─ Classify/                      用途分类：PurposeRules（规则表）、PurposeClassifier（用途、角色、所属对象、证据）、PurposeEnv
│  ├─ DtoFactory.cs                  公用 DTO 构造、时间换算
│  ├─ Volumes.cs                     卷列表、提权检测、特权、SSD 检测
│  ├─ Scan/ScanCounters.cs           进度计数器、扫描报告的四类计数和异常清单
│  ├─ Scan/WalkScanner.cs            常规遍历扫描
│  ├─ Scan/MftScanner.cs             $MFT 深度扫描
│  ├─ Scan/ScanService.cs            扫描编排、模式选择、进度与取消事件、汇总、异常清单、用途分类（搜索索引和清理建议由 BridgeHost 在首次请求时懒建）
│  ├─ Query/TreeQueries.cs           视图裁剪、详情、扩展名统计、最大文件
│  ├─ Search/SearchIndex.cs          搜索
│  ├─ Cleanup/CleanupAdvisor.cs      清理建议规则与执行
│  └─ Shell/ShellOps.cs, DeleteService.cs, Protection.cs
├─ src/DiskLens/                     WinExe 宿主：Program、MainForm、BridgeHost、app.manifest、app.ico
│  └─ wwwroot/                       前端构建产物（git 忽略，`pnpm build` 生成）
├─ tools/DiskLens.Check/             自检控制台：dotnet run --project tools/DiskLens.Check -- <area|all>
└─ web/                              Svelte 前端；pnpm build 输出到 ../src/DiskLens/wwwroot
```

---

## 2. 运行时模型

### 2.1 启动

- `DiskLens.exe [--scan <path>] [--mode auto|mft|walk]`：以管理员身份重启时带上这两个参数，前端启动后读 `app.info.autoScanPath`，自动开始扫描。
- 窗口：标题「DiskLens 磁盘透镜」，默认 1440×900、最小 1100×700（都是逻辑像素，按 DPI 换算），居中，屏幕放不下就最大化。标题栏跟浅色页面一致：`DWMWA_USE_IMMERSIVE_DARK_MODE=20` 设 0、`DWMWA_CAPTION_COLOR=35` = `#F4F4F5`、`DWMWA_TEXT_COLOR=36` = `#18181B`；窗体和 WebView2 的底色也是 `#F4F4F5`，启动时不闪。
- WebView2：
  - userDataFolder = `<exe 目录>\data\webview2-admin` 或 `webview2-user`（exe 目录不可写时退回 `%LOCALAPPDATA%\DiskLens`）。提权和不提权的进程不能共用一个目录。
  - `SetVirtualHostNameToFolderMapping("disklens.local", <exe 目录>\wwwroot, Deny)`，然后导航到 `https://disklens.local/index.html`。
  - 设置了环境变量 `DISKLENS_DEV_URL` 时（例如 `http://localhost:5173`）改为导航到这个地址，方便热更新调试。
  - `AreDefaultContextMenusEnabled=false`（右键菜单我们自己画）、`IsZoomControlEnabled=false`、`IsPinchZoomEnabled=false`、`AreBrowserAcceleratorKeysEnabled=false`（F5/Ctrl+R 交给前端）、`IsStatusBarEnabled=false`；`AreDevToolsEnabled` 只在 Debug 构建或设置了 `DISKLENS_DEVTOOLS=1` 时打开。`DefaultBackgroundColor = #11151F`。
- 界面语言（§8.8）：启动时按 `data\settings.json` 里保存的选择定，没存过就按系统界面语言（中文简繁都算中文、日文用日文，其余英文）；窗口标题、启动失败的提示框跟着用这个语言，导航地址带 `?lang=zh-CN|en|ja`，前端第一帧就是这个语言。`settings.json` 和摘要文件在同一个 `data` 下（跟着一起退回 `%LOCALAPPDATA%\DiskLens`），但**不按权限分开**：以管理员身份重启之后语言不能变回去；它只有一项、写法同摘要文件（临时文件写完刷盘再改名覆盖），坏了、版本不对、语言不认识都当没有。
- 扫描摘要文件（§4.8）：和 WebView2 目录在同一个 `data` 下，`scan-summaries-admin.json` 或 `scan-summaries-user.json`，跟着 WebView2 目录一起退回 `%LOCALAPPDATA%\DiskLens`（宿主 `MainForm.DataPaths` 一次定出两者）。按权限分开，和 WebView2 目录同一做法：① 提权和不提权的窗口可以同时开着，各写各的文件，不会互相挡住替换、抢同一个文件；② 两种身份的扫描口径不同（MFT、普通身份进不去的目录），普通窗口里的「上次扫描结果」应该是它自己重扫能复现的数字；③ 装在只有管理员能写的目录（Program Files）时，提权进程写 exe 旁的 `data`、普通进程退回 `%LOCALAPPDATA%`，本来就分在两处，按身份分文件名让两种布局行为一致。

### 2.2 桥接协议

- JS → C#：`window.chrome.webview.postMessage({ id, method, params })`，`id` 是自增整数。
- C# → JS（应答）：`PostWebMessageAsJson({ id, ok: true, result })`，或 `{ id, ok: false, error: { message } }`。
- C# → JS（事件）：`{ event: "scan.progress", data }`，没有 `id` 字段。
- 序列化统一用 `DiskLens.Core.Json.Options`：Web 默认值（camelCase），枚举序列化为 camelCase 字符串（如 `"alloc"`、`"mft"`）。
- 请求参数反序列化成 `Contracts.cs` 里的请求记录；参数缺失时使用记录的默认值。
- 方法表见 §10。

### 2.3 线程与锁

- `WebMessageReceived` 在 UI 线程触发。BridgeHost 解析后用 `Task.Run` 把处理函数丢到线程池，结果通过 `Control.BeginInvoke` 回到 UI 线程再 `PostWebMessageAsJson`。
- 必须在 UI 线程执行的只有 `dialog.pickFolder` 和 `node.properties`，用 `Invoke` 切过去。
- 当前树挂在 `ScanService.Tree` 上。**读树和改树都必须持有 `tree.Sync` 锁。** 删除操作的磁盘 IO 放在锁外，只在改树时加锁（见 §7）。
- 新扫描在后台线程构建一棵全新的树，完成后整体替换引用，旧的节点 ID 全部作废。前端在 `scan.start` 时清空所有缓存的 ID。
- 节点 ID 越界或指向已删除节点时，抛出 `ArgumentException("节点不存在或已删除")`。
- 扫描完成后 `TreeWatcher` 盯着扫描范围（§4.9），别的程序删掉的东西从计时器线程落进树；它和删除、清理共用 `DeleteService.GateOf(tree)` 这扇写门，门被占着就先攒着。

---

## 3. 树模型 FileTree（集成负责人实现，其他人只调用）

结构数组（SoA）存储，下标就是节点 ID，`0` 是根节点。**不变量：子节点下标 > 父节点下标**（`AddNode` 保证），所以聚合只需要倒序扫一遍。

构建期（只追加，不加锁，调用方自己保证串行）：

```csharp
var tree = new FileTree(@"C:\");                        // 根节点 0，名字就是根路径
int id = tree.AddNode(parent, name, flags, size, alloc, mtimeFileTime);
tree.SetSizes(id, size, alloc);                          // 修正（如硬链接的过期目录项）
tree.AddFlags(id, NodeFlags.Inaccessible);
tree.ResolveHardLinkGroup(ReadOnlySpan<int> nodes);      // 同一文件的多个链接：完整路径按 OrdinalIgnoreCase 最小的那个计入，其余标 HardLinkDup
tree.Seal();                                             // 聚合 + 生成按 alloc 降序排好的子节点数组；之后才能查询
```

查询期（持有 `tree.Sync`）：`Count`、`Parent(i)`、`Children(i)`（`ReadOnlySpan<int>`，按 alloc 降序，已删除节点排在最后）、`NameSpan(i)`/`GetName(i)`、`GetPath(i)`、`FindByPath(path)`、`Flags(i)`、`IsDir(i)`、`IsDeleted(i)`、`Size(i)`、`Alloc(i)`、`Value(i, metric)`、`MTime(i)`（FILETIME，0 表示未知）、`Files(i)`、`Dirs(i)`、`Category(i)`、`Ext(i)`、`ExtId(i)`、`Depth(i)`、`Version`、`HardLinkGroup(i)`、`CategoryTotals(metric)`；分类之后（`SetClassification`，§4.7）还有 `Purpose(i)`、`Role(i)`、`PurposeEvidence(i)`、`Owners`、`OwnerOf(i, out root)`、`OwnerEvidenceAt(root)`、`PurposeTotals(metric)`。

- `size` 是逻辑大小（主数据流长度），`alloc` 是实际占用（簇）。目录的这两个值是**聚合值**：自身值（MFT 模式下的 `$INDEX_ALLOCATION` 等）加上所有计入的子孙之和。
- `HardLinkDup` 节点保留自己的大小用于显示，但对父节点贡献 0。
- `Files(i)` 计的是文件条目数，硬链接的每个链接都算一条；`Dirs(i)` 是子孙目录数。虚拟节点两者都不计。
- `MarkDeleted(i)` 返回**净释放字节数**（alloc）：从所有祖先减去 i 的计入值，给 i 的整棵子树打上 `Deleted`；如果子树里有硬链接组的计入链接，而组里还有活着的链接，就把计入身份转给活着的链接中路径最小的那个，并把它的大小加回它的祖先（所以删一个还有别的链接的文件，净释放为 0）。最后对所有祖先的子节点重新排序，`Version++`。
- `NodeFlags`（ushort，与前端 `F` 位同值）：Directory=1，HardLinkDup=2，Reparse=4，Inaccessible=8，Hidden=16，System=32，Compressed=64，Sparse=128，Cloud=256，Metafile=512，Deleted=1024，Virtual=2048，Encrypted=4096。视图专用位（不存进树）：Aggregate=8192，Unexpanded=16384。
- 类别 `K`（byte）：0 其他、1 文件夹、2 视频、3 图片、4 音频、5 文档、6 压缩包、7 代码与配置、8 程序与库、9 系统、10 磁盘镜像、11 数据与模型、12 未统计（虚拟）。规则：Virtual 且非目录 → 12；目录 → 1；Metafile 或 pagefile.sys/hiberfil.sys/swapfile.sys → 9；其他按扩展名查 `FileCategories`。
- 扩展名：取最后一个 `.` 之后的部分（不能是最后一个字符），转小写；长度超过 16 或含空格时视为无扩展名。

---

## 4. 扫描

### 4.1 模式选择（ScanService）

`mode=auto` 时，满足以下全部条件走 MFT，否则走 Walk：进程已提权、路径是卷根（如 `C:\`）、卷是本地固定盘或可移动盘、文件系统是 NTFS。
`mode=mft` 但条件不满足时，直接报错，不偷偷降级。
MFT 扫描中途抛异常（除取消外）时，降级为 Walk 重扫，并在 summary.warnings 里写一句「MFT 读取失败，已改用常规扫描：<原因>」（换一套新计数器，MFT 那次的异常清单作废，并立即发一条 mode=walk 的进度）。
扫描文件夹（不是卷根）一律走 Walk。

取消（dise §4.2）：`Cancel()` 返回是否接受（没有扫描在跑、刚好扫完都是 false）；接受时先置 `Cancelling`，再立即发一条 `cancelling=true` 的进度，之后每次采样都带着它，阶段停在被打断的那一步——卡在系统 IO 里停不下来就一直报取消中，不提前说已停止。终态 `scan.error`（cancelled=true）在扫描线程全部退出之后才发（Walk 等齐工人线程、MFT 等齐解析任务才往外抛）。每次扫描一把 `Gate` 锁：所有进度都在锁里发，终态在锁里落定（`Running=false`），所以进度不会乱序，终态之后也不会再冒出进度；锁序先 Gate 后服务锁。

### 4.2 进度计数器 ScanCounters

扫描器只写计数器，ScanService 每 120 ms 读一次并发出 `scan.progress` 事件；开始时、阶段变化时、取消被接受时另外立即发一条（见下）。

- `AddBatch(files, dirs, bytes, ReadOnlySpan<long> categoryBytes)`：**按目录或按数据块批量累加**，禁止每个文件做一次 Interlocked（24 线程抢同一条缓存行会很慢）。
- `Phase`：`prepare` → `mft` 或 `walk` → `tree`（仅 MFT）→ `links` → `finalize`。前端把每个阶段当成不同的事件来演出（§8.5）。值一变就同步回调 `PhaseChanged`，ScanService 借它立即发一条进度——比采样间隔还快的扫描也能收到每个阶段，不靠 120 ms 采样碰运气（dise §4.2）。
- `Percent`：0..1，未知为 -1。MFT = 已读字节 / MFT 有效长度；Walk 扫卷根时 = 已统计 alloc / 卷已用空间（上限 0.99）；扫文件夹时未知。
- `CurrentPath`：最近处理的目录（volatile，偶尔写一下就行）。
- 扫描报告的四类计数（dise §4.3）：`InaccessibleDirs`（拒绝访问入口）、`LinksSkipped`（跳过的链接）、`MountPointsSkipped`（跳过的卷挂载点）、`Errors`（读取失败及其他错误）。**只经 `Record(kind, path, reason)` / `RecordLink(path, tag, volume)` 加**：计数先加一，前 `MaxIssues = 10000` 条连路径和原因记进这一类的清单，超出的只计数——总数始终准确，几十万个拒绝访问也不会把内存吃光；并发记录时记下的条数恰好封顶。只在出事时调用（每个目录至多一次），不在逐文件的热路径上。扫描结束、发布结果前 `SortIssues()` 按路径排好，`Page(kind, offset, limit)` 翻页。
- 另有：`HardLinkDups`、`HardLinkSavedBytes`、`Warn(msg)`（最多保留 50 条）。

### 4.3 WalkScanner（常规扫描，任何权限、任何文件系统）

```csharp
public static FileTree WalkScanner.Scan(string rootPath, ScanCounters counters, CancellationToken ct)
```

- 开始前：如果进程已提权，调用 `Volumes.EnableBackupPrivilege()`。
- 并行遍历：目录工作队列 + N 个工作线程。N：SSD 为 clamp(ProcessorCount, 4, 24)，HDD 为 2，网络盘为 8，未知为 8（用 `Volumes.IsSsd`）。用「未完成计数」判定结束。每个线程有自己的 128 KB 枚举缓冲区。
- 打开目录：`CreateFileW("\\?\" + path, FILE_LIST_DIRECTORY|SYNCHRONIZE, 全共享, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS)`。长路径一律加 `\\?\` 前缀（UNC 路径用 `\\?\UNC\`）。
- 枚举：`GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` 循环读到 `ERROR_NO_MORE_FILES`。不支持时（FAT/exFAT/部分网络盘）依次降级为 `FileIdBothDirectoryInfo`、`FileFullDirectoryInfo`。跳过 `.` 和 `..`。
- 每个条目：size = EndOfFile，alloc = AllocationSize，mtime = LastWriteTime，属性映射为 flags：HIDDEN→Hidden，SYSTEM→System，COMPRESSED→Compressed，SPARSE_FILE→Sparse，ENCRYPTED→Encrypted，REPARSE_POINT→Reparse，OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS → Cloud。
- 重解析标签：`FILE_ID_EXTD_DIR_INFO.ReparsePointTag`；降级结构里的标签放在 `EaSize` 字段。
- **是否进入子目录**：是目录，并且（不是重解析点，或者标签不是名称代理：`(tag & 0x20000000) == 0`）。所以 junction、符号链接、挂载点只记一个 Reparse 节点，不进入；OneDrive 这类云标签的目录照常进入。没进去的记 `RecordLink`：联接点和卷挂载点是同一个标签 `0xA0000003`，只能看重解析数据里的替代名——对这个标签多开一次（`OPEN_REPARSE_POINT`，访问权限 0，不跟随）问 `FSCTL_GET_REPARSE_POINT`，替代名以 `\??\Volume{` 开头的算 `MountPointsSkipped`，其余（联接点、目录符号链接、标签读不出的重解析目录、读不到目标的挂载点标签）算 `LinksSkipped`。一个卷上就几十上百个，不在热路径上。
- 实际占用修正：带 Compressed 或 Sparse 属性、或标签是 WOF（`0x80000017`）的文件，用 `GetCompressedFileSizeW` 取真实占用作为 alloc。
- 硬链接去重（只在 NTFS/ReFS 本地卷上做；FileId 为 0 时跳过）：分片字典 `FileId(UInt128) → 首个节点`（256 片，各自加锁）。再次遇到同一 FileId 时，把 (首个节点, 当前节点) 记成一对。遍历结束后按组处理：对每个组的一个链接打开文件（`FILE_READ_ATTRIBUTES`，全共享，`BACKUP_SEMANTICS|OPEN_REPARSE_POINT`），用 `GetFileInformationByHandleEx(FileStandardInfo)` 取真实大小（目录项里的大小对多链接文件可能是过期的），必要时再用 `GetCompressedFileSizeW`，把全组都 `SetSizes`，然后 `ResolveHardLinkGroup`。`HardLinkDups += 组大小 - 1`，`HardLinkSavedBytes += (组大小 - 1) * alloc`。
- 错误：`ERROR_ACCESS_DENIED`（及网络拒绝 65）→ 目录标 Inaccessible，记一条拒绝访问（路径 + 「拒绝访问（错误 5）：里面有什么、有多大都不知道」），不算错误也不写 Warn，结束时汇总成一句警告；其他错误（共享冲突、路径过长、设备错误、扫描期间被删……）记一条错误，原因带系统的错误说明和错误码，前 10 条写进 Warn（2/3「找不到」是常态，只进清单不弹警告）；压缩/稀疏文件的占用核实失败（拒绝访问、32、1920 除外）和硬链接组整组打不开也记错误。单个目录出错不影响整体扫描。
- 文件流（ADS）在 Walk 模式下不统计（代价太大）；MFT 模式会统计。
- 树的修改用一把锁保护：每个目录读完后，加一次锁把整批子节点 `AddNode` 进去，再把子目录推进队列。
- 支持取消：检查 `ct`，抛 `OperationCanceledException`。

### 4.4 MftScanner（深度扫描：管理员 + 本地 NTFS 卷根）

```csharp
public static bool MftScanner.CanScan(string rootPath, out string reason)
public static FileTree MftScanner.Scan(string volumeRoot, ScanCounters counters, CancellationToken ct)
```

1. **打开卷**：`CreateFileW(@"\\.\C:", GENERIC_READ, FILE_SHARE_READ|WRITE|DELETE, OPEN_EXISTING, 0)`。用 `DeviceIoControl(FSCTL_GET_NTFS_VOLUME_DATA)` 取 `BytesPerSector`、`BytesPerCluster`、`BytesPerFileRecordSegment`、`MftValidDataLength`、`MftStartLcn`、`TotalClusters`、`FreeClusters`。卷句柄的读取偏移和长度都必须按扇区对齐（簇对齐自然满足）。用 `RandomAccess.Read(SafeFileHandle, Span<byte>, long)` 读取。
2. **定位 MFT**：读 0 号记录（位于 `MftStartLcn * BytesPerCluster`），做 fixup，解析它的 `$DATA` 运行列表，得到 VCN→LCN 的区段表。如果 0 号记录有 `$ATTRIBUTE_LIST`（MFT 严重碎片化），就按列表读取扩展记录，把后续 VCN 段的运行列表也拼上（列表本身可能是常驻或非常驻）。实现一个 `MftStream.Read(long mftOffset, Span<byte>)`，读取可以跨越区段边界。记录总数 = `MftValidDataLength / BytesPerFileRecordSegment`。
3. **流水线读取与解析**：一个读线程按块读取（每块 4 MB，按记录大小对齐），放进有界队列（缓冲区池大小 8）；N 个解析任务并行消费。每条记录：
   - 签名必须是 `FILE`；做 **fixup**：USA 偏移在 0x04，个数在 0x06；第 i 个 512 字节步长的最后两个字节必须等于 USA[0]，然后替换成 USA[i]。校验失败的记录跳过，并计数。
   - 头部：0x10 序列号，0x12 硬链接数，0x14 首个属性偏移，0x16 标志（0x01 在用，0x02 目录），0x20 基记录引用（非 0 表示扩展记录）。不在用的记录跳过。
   - 属性遍历：0x00 类型、0x04 长度、0x08 非常驻标志、0x09 名字长度、0x0A 名字偏移、0x0C 标志（0x0001 压缩、0x4000 加密、0x8000 稀疏）。常驻：0x10 值长度、0x14 值偏移。非常驻：0x10 起始 VCN、0x20 运行列表偏移、0x22 压缩单元、0x28 分配大小、0x30 实际大小、0x40 总分配（仅当运行列表偏移 ≥ 0x48 且带压缩或稀疏标志时有效）。遇到 0xFFFFFFFF 结束。所有长度和偏移都要做边界检查，越界就放弃这条记录（损坏的记录不能让扫描崩溃）。
   - `$STANDARD_INFORMATION`(0x10)：修改时间在值偏移 0x08，文件属性在 0x20。
   - `$FILE_NAME`(0x30)：父引用在 0x00（低 48 位是记录号，高 16 位是序列号），名字长度在 0x40（按字符计），命名空间在 0x41（0 POSIX、1 Win32、2 DOS、3 Win32&DOS），名字从 0x42 开始。
   - `$REPARSE_POINT`(0xC0)：值的前 4 字节是重解析标签。常驻的挂载点标签顺手看替代名（布局同 `FSCTL_GET_REPARSE_POINT` 的输出），`\??\Volume{` 开头的记成卷挂载点（`MftTable.Mount` 位，扩展记录里的也跟着并）。
   - **大小规则**：logical size = 未命名 `$DATA` 的大小（常驻取值长度；非常驻取起始 VCN 为 0 的那一段的实际大小）。alloc = 所有起始 VCN 为 0 的**非常驻**属性的占用之和，带压缩或稀疏标志时用「总分配」，否则用「分配大小」。这样就包含了命名流（ADS、WOF 压缩数据）、`$INDEX_ALLOCATION`、非常驻的 `$BITMAP`、`$ATTRIBUTE_LIST`、`$EA` 等。常驻数据的 alloc 为 0（它就占在 MFT 记录里，已经算进 `$MFT` 自己了）。
   - **扩展记录**：基引用非 0 时，这条记录的属性归属基记录。它的贡献（大小、名字）放进单独的并发列表，解析完后单线程合并。因为每条扩展记录都指回自己的基记录，所以除 0 号记录外不需要解析 `$ATTRIBUTE_LIST`。
   - 并行写入：按记录号索引的数组（状态、序列号、size、alloc、mtime、属性、重解析标签），每个记录只由一个解析任务写，不会冲突；名字和链接写进每块自己的缓冲区。
   - 进度：`Phase="mft"`，Percent = 已读字节 / MFT 大小；Files 和 Bytes 按块批量累加；类别按扩展名统计。
4. **名字去重**：一条记录如果有命名空间不是 2 的名字，就丢掉它所有命名空间为 2（纯 DOS 短名）的名字。剩下的每个名字就是一个链接；超过一个链接就是硬链接。
5. **建树**（`Phase="tree"`）：按父记录号把链接做计数排序分桶，然后从 5 号记录（根目录）开始 BFS，逐层 `AddNode`。链接的父序列号必须等于父记录当前的序列号，否则视为孤立。根目录指向自己的那个名字跳过。
   - 元文件：0–15 号记录（除了 5）以及 `$Extend` 的整棵子树，标 Metafile，统一挂到根下的虚拟目录 `$NTFS 元数据`（flags = Directory|Virtual|Metafile）。
   - 孤立记录（在用的基记录，但从根出发到不了）挂到根下的虚拟目录 `$孤立记录`，大小照常计入。每个孤立子树的根记一条错误，路径写成 `<卷根>$孤立记录\<名字>`（它真正的位置正是不知道的那部分）；损坏、被跳过的记录也各记一条错误，路径是 `<卷根>$MFT`、原因里写记录号。
   - 名称代理目录（联接点、目录符号链接、卷挂载点）MFT 建树本来就不会穿过，按常规扫描的口径记成跳过的链接或挂载点；MFT 扫描不看目录权限，没有拒绝访问。
   - 目录检测用头部标志 0x02；目录自身的 alloc（如 `$INDEX_ALLOCATION`）作为自身值写进节点。
   - 用 BFS 而不是递归，并且对父链做环检测（损坏的卷可能有环）。
6. **硬链接**（`Phase="links"`）：对每个多链接记录调用 `tree.ResolveHardLinkGroup(该记录的所有节点)`，规则和 Walk 模式完全一致，所以两种模式下每个目录的数字可以直接比对。
7. 对账：返回前把 `TotalClusters - FreeClusters` 换算出的已用字节和全树 alloc 之和比较，差值超过 0.5% 就 Warn 一条，写清两个数。
8. 性能目标：100 万文件的系统盘 < 5 秒；峰值内存 < 600 MB。

### 4.5 硬链接规则（两种模式一致）

同一文件的所有链接里，完整路径按 OrdinalIgnoreCase 排序最小的那个「计入」，其余标 `HardLinkDup`：显示它们自己的大小，但不计入父目录。这样整棵树的总和等于真实占用，而且结果是确定的（`C:\Windows\System32\...` 排在 `C:\Windows\WinSxS\...` 前面，所以系统文件计在 System32，WinSxS 显示的是它真正独占的部分）。详情面板会写明「硬链接 ×N，计入位置：<路径>」。

### 4.6 准确性规则与对账（ScanService）

- 扫描卷根时：`未统计 = 卷已用 - 树的 alloc 总和`，其中卷已用 = `GetDiskFreeSpaceExW` 的 total − totalFree（用 TotalNumberOfFreeBytes，不用受配额影响的可用值）。卷根扫描**总是**在根下加一个虚拟节点「未统计（系统保留或无权访问）」（flags=Virtual，K=12，U=13 未解释空间），大小即为未统计值（可以是 0），然后才 `Seal`——删到回收站之后 `RefreshSummary` 要让它长大，节点得先在。扫文件夹时不加，`UnaccountedBytes` 恒为 0，进度里的 `UsedBytes` 也报 0。
- MFT 模式下元数据已经全部算进树里，所以未统计应该接近 0；Walk 模式下未统计包括 `$MFT`、卷影副本，以及无权访问的目录。
- summary 里给出对账所需的全部数字：卷容量、已用、可用、已统计（alloc 和 size）、未统计、硬链接去重节省，以及下面的扫描报告。
- **扫描报告**（dise §4.3）分开给四项，口径写在 `Contracts.cs` 的注释里：
  1. `InaccessibleDirs`：发现的拒绝访问入口，读不了的目录各算一个——里面还有多少东西不知道，所以它**不是**「没读到的目录总数」。MFT 扫描恒为 0。
  2. `LinksSkipped` / `MountPointsSkipped`：按规则没进入的链接（联接点、目录符号链接、类型读不出的重解析目录）和卷挂载点，分开计。
  3. `Errors`：读取失败及其他错误，拒绝访问不算在内——共享冲突、路径过长、设备错误、扫描期间被删、占用或硬链接无法核实、MFT 损坏记录和孤立子树。
  4. `UnaccountedBytes` 拆成 `GapParts`（各份之和等于它）：常规扫描读不到 NTFS 元数据，卷根扫描时先扣**文件系统报告的 `$MFT` 大小**（`GetFileAttributesExW("X:\$MFT")`，不用管理员也问得到；只是文件大小，算下限；`FSCTL_GET_NTFS_VOLUME_DATA` 非管理员拿不到，`$LogFile`、`$Bitmap` 也问不到），封顶在差额；余数是 `unconfirmed`「原因未确认」——无权访问的目录、卷影副本、扫描期间的写入都可能，扫描结果给不出依据就不归因于其中任何一项，更不全算到权限头上。MFT 扫描的元数据已经在「$NTFS 元数据」里统计了，差额全是原因未确认。网络盘、非 NTFS 不问 `$MFT`。
  前三项是**扫描时的数字**：`RefreshSummary` 不随删除重算，和 `scan.issues` 的 `Total` 一一相等（清单里可能有之后被删掉的路径）；未解释容量和它的拆分跟着卷空间和删除实时刷新。
- **异常清单** `scan.issues {kind, offset, limit, scanId}`：只翻当前结果那次扫描的清单（计数器和 `_tree`、`_summary` 一起换）；带了 `scanId` 却对不上就报错——正在扫的那次给「扫描尚未完成」，更早的给「已经失效」——不拿别的扫描充数；还没有结果也报错。应答分开给 `total`（准确总数）、`recorded`（扫描时实际记下的条数，≤ 10000）、本页 `items`（路径 + 原因，按路径排序）和 `truncated`（`offset + items.length < total`：本页之后还有没展示的，不管是能往后翻还是根本没记下——界面不能把它当完整清单）。`limit` 夹在 1..1000，`offset` 越界给空页。
- 默认度量是「占用空间」(alloc)，可切换为「文件大小」(size)。切换只改变显示和排序，不需要重扫。
- 单位：1024 进制，显示为 KB/MB/GB/TB，和资源管理器一致。

### 4.7 用途分类 PurposeClassifier（参考图的「类别」着色依据，P 负责）

```csharp
public static Classification PurposeClassifier.Classify(FileTree tree, CancellationToken ct)   // 调用方随后 tree.SetClassification(c)
// Classification：Purposes（每节点用途）、每节点一个字节的角色 + 用途证据、所属对象表 Owners、所属对象的根（只记根，不按节点开数组）
```

- 时机：ScanService 在 `Seal` 之后、发 `scan.done` 之前调用（Phase 仍是 `finalize`）。200 万节点 < 300 ms（M1b 实测中位数 65–72 ms，加角色和所属对象前 55 ms）。
- 每个节点一个主用途 U、一个数据角色、一个用途证据等级，外加最近的所属对象（dise §7.1）；四样一趟算完，见下面「数据角色」「所属对象」「证据等级」。
- 用途编号、key、名称见 `Purposes.cs`：0 未识别、1 应用程序、2 游戏、3 应用数据、4 开发项目、5 SDK 与运行环境、6 缓存、7 日志与诊断、8 AI 与模型、9 文档与媒体、10 数据库与数据集、11 归档与镜像、12 系统与恢复、13 未解释空间（虚拟状态：卷已用减去已统计的差额，不冒充系统文件）。前端 `purposes.ts` 同号同名；`web/tests/purposes.test.ts` 直接读 `Purposes.cs` 逐项比对，只改一边就失败。
- **判定顺序**（dise §7.3）：有证据的具体数据角色 → 所属容器的规则或继承 → 扩展名兜底；无法判断就是「未识别」，详情写明依据不足。实现是「规则号即优先级」（`PurposeRules`，号越小越优先）：节点在「自己的证据」和「从父目录继承的规则」里取号小的；子孙的证据比继承来的更优先才覆盖。
  1. **元数据** → 系统与恢复：元文件、`$NTFS 元数据`、`$孤立记录`；虚拟叶子 → 未解释空间。
  2. **恢复数据** → 系统与恢复：卷根下的 `$Recycle.Bin`、`System Volume Information`、`Recovery`、`$WinREAgent`、`Windows.old`、`Config.Msi`、`$Windows.~BT`、`$Windows.~WS`、`$GetCurrent`、`$SysReset`。整块都算恢复数据，里面的日志也不例外。
  3. **日志与诊断**：任意位置名为 `Logs`、`LogFiles`、`PerfLogs`、`CrashDumps`、`Crashpad`、`CrashReports`、`Crash Reports`、`Minidump`、`LiveKernelReports`、`WER` 的目录（不区分大小写；单数 `log` 这类谁都可能叫的名字不收，node_modules 里就有个包叫 log）；`%ProgramData%\Microsoft\Diagnosis`；扩展名 `.log`、`.etl`、`.evtx`、`.evt`、`.blg`、`.dmp`、`.mdmp`、`.hdmp`、`.wer` 的文件。文件自己就说明了角色，比所在容器优先：已识别游戏里的 `.log` 主用途是日志与诊断（所属对象仍是该游戏，M1b 记录）。
  4. **缓存（明确位置）**：包管理器缓存——`npm_config_cache` 或 `%LOCALAPPDATA%\npm-cache`、`%APPDATA%\npm-cache`；`%LOCALAPPDATA%\pnpm\store`、`%LOCALAPPDATA%\pnpm-cache`；`%LOCALAPPDATA%\Yarn\Cache`；`%LOCALAPPDATA%\pip\Cache`；`UV_CACHE_DIR` 或 `%LOCALAPPDATA%\uv\cache`；`NUGET_PACKAGES` 或 `%USERPROFILE%\.nuget\packages`；`NUGET_HTTP_CACHE_PATH` 或 `%LOCALAPPDATA%\NuGet\v3-cache`；`%USERPROFILE%\.gradle\caches`；`%USERPROFILE%\.m2\repository`；`CARGO_HOME` 或 `%USERPROFILE%\.cargo` 下的 `registry`；`%LOCALAPPDATA%\go-build`、`%USERPROFILE%\go\pkg\mod`；`PUPPETEER_CACHE_DIR` 或 `%USERPROFILE%\.cache\puppeteer`；`%LOCALAPPDATA%\ms-playwright`；`ELECTRON_CACHE` 或 `%LOCALAPPDATA%\electron\Cache`；`%LOCALAPPDATA%\node-gyp\Cache`；`%USERPROFILE%\.bun\install\cache`；`%LOCALAPPDATA%\Composer`；`%LOCALAPPDATA%\Pub\Cache`；conda 安装目录下的 `pkgs`；Hugging Face 缓存 hub（`HF_HUB_CACHE`、`HUGGINGFACE_HUB_CACHE`、`HF_HOME\hub`、`%USERPROFILE%\.cache\huggingface\hub`、`XDG_CACHE_HOME\huggingface\hub`）下名字以 `models--`、`datasets--` 开头的仓库（按需下载、能重新拉取的模型和数据集缓存：主用途都是缓存，角色分别是模型数据、数据集，dise §7.3；`datasets--` 是 2026-10-20 用户定的同样处理；里面再大的 safetensors 也不被扩展名拉回 AI；`spaces--` 和 hub 外的杂项不认）；`TEMP`/`TMP`（8.3 短名由 `Path.GetFullPath` 展开成树里的长名）和 `%LOCALAPPDATA%\Temp`；`%SystemRoot%\Temp`、`%SystemRoot%\SoftwareDistribution\Download`、传递优化缓存（`%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache`）；`%ProgramData%\Package Cache`；`%LOCALAPPDATA%\Microsoft\Windows\Explorer`（缩略图缓存）。明确位置比「系统」优先：Windows 目录里的更新缓存、临时目录是缓存。环境变量指向的位置优先（本机就把这些缓存都搬到了 D 盘）。
  5. **系统** → 系统与恢复：`%SystemRoot%` 和卷根的 `Windows`，卷根的 `pagefile.sys`、`hiberfil.sys`、`swapfile.sys`、`DumpStack.log*`、`bootmgr`、`BOOTNXT`。文件自己就是位置规则的目标时扩展名不参与：卷根的 `DumpStack.log` 是系统文件，不因为扩展名是 `.log` 改成日志。
  6. **AI 与模型**：`OLLAMA_MODELS` 或 `%USERPROFILE%\.ollama\models`；`%USERPROFILE%\.lmstudio\models`、`%USERPROFILE%\.cache\lm-studio\models`（及 XDG 下同名位置）；任意位置名为 `models` 且直接父目录名以 `ComfyUI`、`stable-diffusion`、`sd-webui`、`Fooocus`、`InvokeAI` 开头（后面要么结束、要么跟 `-` 或 `_`）的目录；扩展名是 safetensors/gguf/onnx 且文件大小 ≥ 50 MB 的文件。`.ckpt`/`.pt`/`.pth`、`.bin` 也是普通检查点、数据和存档的扩展名，不凭扩展名认成模型（dise §7.3：不得把数据文件统一映射成 AI 模型）。
  7. **数据库与数据集（明确的数据集目录）**：`HF_DATASETS_CACHE` 或 `HF_HOME\datasets`、`%USERPROFILE%\.cache\huggingface\datasets`（及 XDG 下同名位置）；`TFDS_DATA_DIR` 或 `%USERPROFILE%\tensorflow_datasets`；`%USERPROFILE%\.keras\datasets`；`SCIKIT_LEARN_DATA` 或 `%USERPROFILE%\scikit_learn_data`；kagglehub 的 `datasets`。Hugging Face hub 里的 `datasets--` 仓库是能重新拉取的下载缓存，归第 4 条（主用途缓存、角色数据集）。
  8. **缓存（按名字认的应用缓存）**：任意位置名为 `Cache`、`Caches`、`cache2`、`Code Cache`、`GPUCache`、`ShaderCache`、`GrShaderCache`、`DawnCache`、`DawnGraphiteCache`、`DawnWebGPUCache`、`CacheStorage`、`ScriptCache`、`INetCache`、`D3DSCache`、`DXCache`、`GLCache`、`__pycache__` 的目录（浏览器和 Electron 应用的缓存、显卡驱动的着色器缓存、Steam 的 shadercache）。排在系统、AI、数据集之后：Windows 里只是叫 Cache 的目录仍是系统，登记过的 Hugging Face hub 放在叫 caches 的目录里，仓库仍按第 4 条认（模型数据、数据集的角色和所属对象都在）。点开头的 `.cache` 不按名字认（hub、lm-studio 都住在里面）。
  9. **SDK 与运行环境**：`%LOCALAPPDATA%\Android\Sdk`、`%USERPROFILE%\.android`、`%USERPROFILE%\.rustup`、`%USERPROFILE%\.dotnet`、`%ProgramFiles%\dotnet`、`%ProgramFiles(x86)%\Windows Kits`、`%ProgramFiles%\Microsoft Visual Studio`、`%ProgramFiles(x86)%\Microsoft Visual Studio`、`%USERPROFILE%\.jdks`、`%ProgramFiles%\Java`、`%ProgramFiles%\Eclipse Adoptium`、`%ProgramFiles%\Unity\Hub\Editor`、`%USERPROFILE%\flutter`、`%ProgramFiles%\nodejs`、`%LOCALAPPDATA%\Programs\Python`、`%USERPROFILE%\miniconda3`、`%USERPROFILE%\anaconda3`。 conda 的安装目录（`CONDA_ROOT`、`MAMBA_ROOT_PREFIX`、`CONDA_PREFIX` 推出的位置，以及家目录、`%LOCALAPPDATA%`、`%ProgramData%` 下的 miniconda3/anaconda3/miniforge3/mambaforge）也算，它的 `pkgs` 另算缓存。
  10. **游戏**：任意位置名为 `steamapps`、`SteamLibrary`、`Epic Games`、`XboxGames`、`ModifiableWindowsApps`、`GOG Games`、`Riot Games`、`EA Games`、`Origin Games`、`Rockstar Games`、`Amazon Games` 的目录；`GOG Galaxy` 或 `Ubisoft Game Launcher` 下的 `Games`；直接子节点里有 `.build.info` 的目录（Battle.net 的游戏没有统一的库目录，各自装在 Program Files 里，根下都有一份 CASC 的 `.build.info`）；存档目录 `%USERPROFILE%\Saved Games` 和「文档」（含重定向、OneDrive 下的）里的 `My Games`。比程序目录优先：Steam 客户端本体、Battle.net 启动器是应用程序，它们的游戏库是游戏；游戏里的资源、视频、压缩包、package.json 都不翻盘。
  11. **应用程序**：`%ProgramFiles%`、`%ProgramFiles(x86)%`、`ProgramW6432`、`%LOCALAPPDATA%\Programs`、任意位置名为 `WindowsApps` 的目录；各 Program Files 下的 `Common Files`（共用组件）；VS Code 系编辑器的扩展目录 `%USERPROFILE%\.vscode\extensions`、`.vscode-insiders\extensions`、`.cursor\extensions`（dise §7.2「应用程序 → 扩展与插件」，里面每个扩展都带 package.json，不翻成开发项目）。（被 SDK、游戏规则命中的子目录各归各的。）
  12. **应用数据**：`%USERPROFILE%\AppData`（Local、LocalLow、Roaming）、`%LOCALAPPDATA%`、`%APPDATA%`、`%ProgramData%`、`%LOCALAPPDATA%\Packages`；各家浏览器的用户资料目录（Chrome、Edge、Brave、Vivaldi、Chromium 的 `User Data`，Firefox 在 Roaming 和 Local 下的 `Mozilla\Firefox`，Opera 的 `Opera Stable`）。里面的缓存、日志、安装的程序、SDK 各有更优先的规则，剩下的配置、用户数据、浏览器资料落到这里；比项目标记优先，应用自带的 package.json 不会把它们翻成开发项目。
  13. **开发项目**：目录的直接子节点里有下列任一标记，它就是项目根，整棵子树都算开发项目（包括 node_modules、target、obj、.venv）：`.git`（目录或文件）、`package.json`、`Cargo.toml`、`go.mod`、`pyproject.toml`、`requirements.txt`、`setup.py`、`*.sln`、`*.slnx`、`*.csproj`、`pom.xml`、`build.gradle`、`build.gradle.kts`、`CMakeLists.txt`、`composer.json`、`Gemfile`、`deno.json`、`pubspec.yaml`。卷根、用户配置文件根、`Windows`、`Program Files` 系列、`AppData` 及其直接子目录、用户 shell 文件夹（Desktop、Documents、Downloads、Pictures、Videos、Music）**本身**不能当项目根（家目录里偶尔有 package.json，别把整个家染成紫色），它们的子目录可以。项目标记比扩展名兜底和用户文件夹优先：桌面上的项目文件夹整个是开发项目，桌面上的散文件按扩展名或跟着用户文件夹走；比应用数据、程序目录、游戏、SDK、系统弱，它们里面的 package.json 不翻盘。
  14. **扩展名兜底**（只在没有任何容器规则时生效）：`.db`、`.sqlite`、`.sqlite3`、`.db3`、`.mdb`、`.accdb`、`.mdf`、`.ndf`、`.ldf`、`.ibd`、`.parquet`、`.arrow`、`.feather`、`.npy`、`.npz`、`.h5`、`.hdf5`、`.tfrecord` → 数据库与数据集；扩展名类别 K 是视频/图片/音频/文档 → 文档与媒体；K 是压缩包、磁盘镜像 → 归档与镜像。`.bin`、`.dat`、`.tmp`、`.pak` 这类通用数据不猜。
  15. **用户文件夹** → 文档与媒体：用户 shell 文件夹 Desktop、Documents（含 OneDrive 下的同名文件夹）、Pictures、Videos、Music、Downloads（含注册表里搬走的位置）。比扩展名兜底还弱，只给没有类型证据的文件定用途：下载目录里的 ISO 是归档与镜像、数据库是数据库与数据集、setup.exe 跟着文件夹算文档与媒体。
  其余 → 0 未识别。
- **主导用途**：没有命中任何规则的目录（比如 `D:\Media`、`C:\Users\Alex`）取子树里占 ≥ 60% 计入大小的用途，否则未识别。用途只是继承来的目录（自己没有证据），子树里另一种用途 ≥ 60% 时按它展示（AppData 里九成是 DXCache 的 NVIDIA 目录显示成缓存），没人过线就沿用继承的；自己有证据的目录（`AppData\Local`、`Program Files`、`Logs`、项目根……）不换。只改目录自己，子项分类和汇总口径都不动（dise §7.3「目录展示色可以取主要用途」）。自底向上累计，累计数组只给目录分配。
- **数据角色**（`Roles.cs`，35 种，编号与前端 `roles.ts` 一致、契约测试锁住）：0 未知；1 程序本体、2 组件、3 扩展与插件；4 游戏本体、5 游戏资源、6 模组、7 存档；8 配置、9 用户数据、10 浏览器资料；11 源代码、12 项目依赖、13 构建产物；14 运行环境；15 应用缓存、16 包管理器缓存、17 更新缓存、18 临时文件；19 日志、20 崩溃转储、21 错误报告；22 模型权重、23 模型数据；24 文档、25 图片、26 视频、27 音频；28 数据库、29 数据集；30 压缩包、31 安装镜像、32 虚拟磁盘；33 系统组件、34 恢复文件。dise §7.2 的「结构化数据」并进数据集（列式、数组格式），没单列。
  - **规则给出**：每条规则本来就知道角色——包管理器缓存位置 → 包管理器缓存，TEMP → 临时文件，`SoftwareDistribution\Download`、传递优化、`Package Cache` → 更新缓存，按名字认的缓存目录和缩略图缓存 → 应用缓存；`Logs`/`LogFiles`/`PerfLogs`、诊断目录 → 日志，`CrashDumps`/`Crashpad`/`Minidump`/`LiveKernelReports` → 崩溃转储，`WER`/`CrashReports` → 错误报告；恢复数据 → 恢复文件，系统、元数据 → 系统组件；模型目录 → 模型权重，Hugging Face 模型仓库 → 模型数据；数据集目录、Hugging Face 数据集仓库 → 数据集；SDK → 运行环境；游戏库、`.build.info` → 游戏本体，存档目录 → 存档；程序目录 → 程序本体，`Common Files` → 组件，VS Code 扩展目录 → 扩展与插件；AppData → 用户数据，浏览器资料目录 → 浏览器资料；项目标记 → 源代码；用户文件夹只说明在哪、不给角色。节点取用途的同一条规则的角色；**同号规则更深的那条更具体，也算赢**（AppData 里的浏览器资料目录拿到自己的角色和所属对象），项目标记例外（node_modules 里每个包都带 package.json，不能翻回源代码）。
  - **容器里按目录名细分**（不改用途和规则号）：源代码里的 `node_modules`、`bower_components`、`jspm_packages`、`vendor`、`.venv`、`venv`、`Pods` → 项目依赖，`target`、`obj`、`bin`、`dist`、`build`、`out`、`.next`、`.nuxt`、`.svelte-kit`、`.turbo`、`.parcel-cache`、`.gradle`、`.angular`、`cmake-build-*` → 构建产物；游戏本体里的 `mods`、`workshop` → 模组，`SaveGames`、`Saves`、`SaveData` → 存档，`plugins` 一类 → 扩展与插件；程序本体、用户数据、浏览器资料里的 `plugins`、`extensions`、`addons`、`add-ons`、`AddIns` → 扩展与插件。只在对应容器里认，别处叫 bin、build、extensions 的目录什么都说明不了。
  - **文件按扩展名细分**：游戏里程序与库（K=8）是游戏本体、配置扩展名（ini、cfg、conf、config、cnf、prefs、properties）是配置、其余是游戏资源；程序本体、用户数据里的配置扩展名是配置（.json、.xml、.yaml 什么都装，不算）。
  - **扩展名兜底**：用途由扩展名给出时角色也由它给出——日志类扩展名 → 日志（.dmp/.mdmp/.hdmp 崩溃转储、.wer 错误报告），≥ 50 MB 的模型格式 → 模型权重，数据库格式 → 数据库、列式/数组格式 → 数据集，视频/图片/音频/文档 → 同名角色，压缩包 → 压缩包，磁盘镜像 → 安装镜像（vhd/vhdx/avhdx/vmdk/vdi/qcow2/ova/gho 是虚拟磁盘）。再没有就是未知。
- **所属对象**（dise §8.1）：所属对象表每项是（种类、名字、证据），种类 `OwnerKind`：应用、游戏、插件、开发项目、包管理器、浏览器、系统组件、模型、数据集、运行环境。树上只记「所属对象的根 → 对象和来源」，节点的所属对象是从它往上第一个根的那个（同一节点只指向一个，最近的）。认法：
  - **已确认**：位置规则自带（npm、pnpm、Yarn、pip、uv、NuGet、Gradle、Maven、Cargo、Go、Puppeteer、Playwright、Electron、node-gyp、Bun、Composer、Pub、conda 的缓存 → 包管理器；SDK 各条 → 运行环境 .NET/Android SDK/Rust/Java/Visual Studio/Windows SDK/Unity/Flutter/Node.js/Python/Conda；Windows 目录、卷根系统文件、诊断目录、更新缓存、缩略图缓存、恢复数据 → 系统组件 Windows，回收站 → 系统组件「回收站」；浏览器资料 → 浏览器；Ollama、LM Studio 的模型目录 → 应用）；项目标记所在目录 → 开发项目（名字取目录名；node_modules 这类依赖、构建产物里的不另立项目，monorepo 的子包另立）；`.build.info` 所在目录 → 游戏；游戏库里的游戏文件夹 → 游戏（启动器 `Launcher`、`Riot Client`、`Steamworks Shared` 和名字带 `Redist` 的运行库不算）；Hugging Face 的 `models--组织--名字` → 模型「组织/名字」、`datasets--` → 数据集；`WindowsApps`、`AppData\Local\Packages` 下的目录 → 应用，名字是第一个下划线前的包名；VS Code 系扩展目录下的目录 → 插件，名字去掉「-版本」。
  - **推测**（按目录名猜的名字）：`Program Files` 系和 `%LOCALAPPDATA%\Programs` 下的目录 → 应用；它自己不带 .exe、某个子目录直接带 .exe 时是厂商目录（`Adobe\Adobe Photoshop 2026`），带 .exe 的子目录各是一个应用，厂商目录本身也是一个对象（装剩下的）；AppData 的 Local/Roaming/LocalLow、ProgramData 下的目录 → 应用（可能是厂商名）；`Saved Games`、`My Games` 下的目录 → 游戏。这几种只在目录用的是继承来的规则时才认：自己命中别的规则的（`CrashDumps`、`Temp`、`npm-cache`、`Packages`、`Common Files`、`WindowsApps`）不当对象。
  - 临时目录、`Package Cache`、`CrashDumps`、只有编号的 Steam 着色器缓存这类公共位置不硬塞给谁：要么没有所属对象，要么落到最近的上一级对象（`Windows\Temp` 归 Windows、Steam 客户端里的 shadercache 归 Steam）。
  - **合并**：同种同名（不区分大小写）的是同一个对象——应用包和它的 Packages 数据、Firefox 的 Roaming 和 Local、程序目录和 AppData 里的 Steam、游戏库和「保存的游戏」里的同一个游戏。对象本身的证据取几处来源里最强的；每个根各记自己的来源，**节点归属的证据看它所在的那个根**——按目录名并进来的那一处只是推测，不连累游戏库那一处，也不冒充确认过。
  - 扫描根落在容器里面时（直接扫某个游戏、`Program Files\某应用`、`AppData\Roaming\某应用`、npm 缓存的子目录、模型仓库里面）顺着根路径把上面几层走一遍认出根的所属对象；根自己的项目标记、`.build.info` 比上面推出来的优先。根上方只看位置和名字、不比较规则优先级（ponytail，回收站里的 Program Files 副本会被认成应用）。
- **证据等级**（dise §7.3，只分三档，不出百分比）：按依据的**种类**定，不按「是不是规则命中的那一层」——
  - 用途：位置规则、名称规则、安装信息、项目标记、元数据标记命中的整棵子树是**已确认**（规则说的就是整棵子树：npm 缓存里每个文件都是 npm 缓存）；用途由扩展名给出的（游戏里的 .log、下载目录里的 ISO）、用户文件夹里没有类型证据的文件（位置只说明在哪）、按子树主要用途展示的目录是**推测**；未识别和未解释空间是**未识别**。扫描根落在用户文件夹里面也是推测，就是用户文件夹本身是已确认。
  - 所属对象：见上，位置规则、包信息、项目标记、安装信息、游戏库结构是已确认，按目录名猜的是推测；没有可靠依据就没有所属对象（详情写「尚未识别」）。
  - 存放：角色占每节点一个字节的低 6 位、用途证据占高 2 位（`FileTree.Role(i)`、`PurposeEvidence(i)`）；所属对象表和根字典另存（`FileTree.Owners`、`OwnerOf(i, out root)`、`OwnerEvidenceAt(root)`）。
- **分类依据**（详情的 `PurposeBasis`）：写出命中的依据——「位置规则」「继承上级目录的位置规则 / 名称规则」「目录名称规则」「项目标记」「游戏安装信息」「Hugging Face 模型 / 数据集缓存仓库」「日志与诊断类扩展名」「模型格式扩展名」「按扩展名兜底归为…」或「子树中该用途占至少 60%」；换了展示色的继承目录两层来历都写；未识别写明依据不足；虚拟叶子写明是未解释空间。再各补一句角色和所属对象的来历：角色「按扩展名 .x 推断」「来自目录名「node_modules」」或「和用途出自同一条依据」（模型目录里的非模型格式文件说明是按目录推断、可能是清单配置）；所属对象写名字、种类、这一处归属的证据、凭什么认的（位置规则、项目标记、游戏库里的游戏文件夹、按程序目录下的文件夹名推测……）和根路径，没有就写「尚未识别」。都注明用途分类不代表可安全清理。
- **实现补充**（P 实现时确认）：环境变量指向的位置和默认位置取并集（搬家前留在老地方的也算）；另读 `~\.npmrc` 的 cache/store-dir/cache-dir、`%LOCALAPPDATA%\pnpm\config\config.yaml` 的 storeDir/cacheDir、注册表里搬走的 Downloads；额外认 PNPM_HOME、npm_config_store_dir、YARN_CACHE_FOLDER、PIP_CACHE_DIR、GRADLE_USER_HOME、GOCACHE、GOMODCACHE、GOPATH、PLAYWRIGHT_BROWSERS_PATH、BUN_INSTALL、COMPOSER_CACHE_DIR、PUB_CACHE、XDG_CACHE_HOME、CONDA_PKGS_DIRS、CONDA_PREFIX、CONDA_ROOT、MAMBA_ROOT_PREFIX、ANDROID_SDK_ROOT、ANDROID_HOME、ANDROID_USER_HOME、RUSTUP_HOME、DOTNET_ROOT、OneDrive*；`C:\Users` 和每个用户的家都不当项目根；扫描根落在规则目录内部、或扫描根上方的目录名命中名字规则时，根按命中处理；写歪的环境变量（相对路径、盘符根）忽略；主导用途按叶子最终用途汇总、权重是计入的 alloc。
- **实现要求**：路径规则先解析成「规范化完整路径 → 用途」字典，用 `tree.FindByPath` 找到节点后向下批量赋值；不要给 200 万个节点逐个拼路径做字符串匹配。项目根检测只看直接子节点的名字（`LiveChildren` + `NameSpan`），不分配字符串。
- **K→U 默认映射**（扫描进度条用，那时还没有树，只是按文件类型估算，完成后的用途以树为准）：K 视频/图片/音频/文档 → 9 文档与媒体；K 压缩包、磁盘镜像 → 11 归档与镜像；K 代码与配置 → 4 开发项目；K 程序与库 → 1 应用程序；K 系统 → 12 系统与恢复；K 未统计 → 13 未解释空间；K 数据文件、其他、文件夹 → 0 未识别（数据文件里混着日志、缓存、数据库、游戏资源和模型，不猜，更不整类算成 AI）。前端放在 `purposes.ts` 的 `purposeOfCategory`。

### 4.8 扫描摘要持久化（SummaryStore，dise §3.2，M1e）

每个扫描范围只存最近一次**成功完成**的扫描摘要，重启后首页（M2）标「上次扫描结果 · 时间」。它只是摘要，不是可操作的树。第 2 版起顺手记下根下面的大项和认出的应用，并在覆盖前和同一范围的上一次对照（启动页的缓存对照，§9.1）。

- **位置**：§2.1 的 `data` 下 `scan-summaries-{admin|user}.json`。
- **什么时候存**：`ScanService` 把结果换上（`_tree`、`_summary` 在同一把锁里换，`ScanSummaryDto.CompletedAtMs` 也在这时记）之后、发 `scan.done` 之前调 `SummaryStore.Save`，界面收到完成事件时已经读得到这一次。取消、失败、被新扫描顶掉的扫描走不到这里，不碰已有摘要。之后的删除不改已保存的摘要——它记的是扫描完成那一刻。
- **内容**（`SavedSummaryDto`）：根路径、模式、文件系统、是否卷根、完成时刻、耗时、文件数/目录数、已统计占用与大小、卷总量/可用、各用途字节（长度 `Purposes.Count`）、扫描报告的计数（无权限入口、跳过的链接、挂载点、错误）、未解释容量及其拆分。**不存**扫描号、节点 ID、树版本——它们只在那次运行里有效，拿去导航或删除会指错对象；警告文字和 K 类别字节也不存。
- **格式**：`{"version":2,"summaries":[…]}`（第 1 版的文件照读，对照字段为 null；下一次保存写成第 2 版），用 `Json.Options`（camelCase、枚举字符串），不缩进。范围按规整后的根路径认、不区分大小写；同一范围只留完成时刻最晚的一次（先完成的扫描晚落盘也盖不掉新的）。
- **上限**：最多 32 个范围、文件不超过 1 MB；每条记录最多 12 个大项、400 个应用、50 个消失的应用。超了按完成时刻丢最旧的，直到放得下。
- **缓存对照（第 2 版）**：`Save(summary, tree)` 在树锁里取根下面占用最大的 12 项（`TopEntries`）和 App/Game/Browser 类所属对象（`Apps`：显示优先已确认的安装位置，占用是各处根的子树之和，同一对象嵌套的根只算外层）；覆盖同一范围的旧记录之前 `Compare`：大项按名字（不分大小写）带上一次的占用，记 `PreviousAtMs`/`PreviousAlloc`；应用只和**同模式、同权限**（`IsAdmin`）且也记了应用的上一次比——MFT 和常规扫描、管理员和标准权限认得出的不一样，硬比会冒出假的「新应用」。对上的沿用第一次出现的时刻 `FirstSeenMs`，新出现的标 `IsNew` 并记下对照时它还没有的那次 `AbsentAtMs`，上一次有这次没有的进 `RemovedApps`，比过的记 `AppsComparedAtMs`。`FirstSeenMs` 是扫描发现的时刻，不是安装时刻。`IsNew` / `AbsentAtMs` 是「对照出来的新应用」这个结论，之后的扫描对上它时一并沿用（2026-10-27）：启动页按 `FirstSeenMs` 只列最近一周的新应用，结论只留在发现那一条的话，同一范围再扫一次它就从「最近一周」里消失了。旧版本存的记录只在发现那条上标新、没有 `AbsentAtMs`，沿用时拿那条记录的 `AppsComparedAtMs` 补上。
- **写**：同目录的 `.tmp` 写完 `Flush(true)`，再 `File.Move(…, overwrite: true)` 换上正式文件（原子替换），谁读都只会看到完整的旧文件或完整的新文件；残留的 `.tmp`（写到一半崩了、替换没成）不读，下次保存直接覆盖。进程内一把全局锁串行化读写。跨进程只靠原子替换保证文件完好：另一个进程正开着正式文件时 Windows 不让替换（实测对方带共享删除也不行），这次保存失败、只记警告；两个进程同时保存时后写的赢。
- **读**：没有文件 → 空。不是 JSON（含空文件）、版本号不符、超过 1 MB 或 32 个范围、字段缺失或取值无效、范围重复、用途字节长度不对 → 当没有，把它改名成 `.bad` 留底（覆盖上一个，只留最近一个），下次保存写新文件。这会儿读不了（被占用、没权限）→ 读的一方当空，保存的一方放弃这次，不拿空列表把别的范围盖掉。
- **出错处理**：`List` / `Save` 都不抛，失败只记 `Trace.TraceWarning`；存不下不影响扫描结果（`scan.done` 照发），文件坏了不影响启动和扫描。
- **接口**：`scan.savedSummaries` → `SavedSummaryDto[]`（按完成时刻新到旧），只读、不排队（§10）。没放进 `app.info`：那是启动时取一次的程序信息、还排桥接许可；摘要每扫完一次就会变，界面要能单独重取。
- **界面约束**：摘要只是「上次扫描结果」。未重新扫描、拿到新的扫描结果之前，不能用它导航、查详情或删除——要操作先扫描。完整的历史快照另行设计。唯一的外壳动作是 `shell.openFolder`：按路径打开记录里出现过的位置（记录的范围、认出的应用），别的路径一律拒绝。

### 4.9 实时状态（TreeWatcher）

`ScanService` 在**扫描开始之前**就给扫描范围开一个 `TreeWatcher`（`FileSystemWatcher`，含子目录，只要文件名和目录名变化，64 KB 缓冲），树还没建好时只攒着；扫描成功、结果换上之后 `Attach` 这棵树，攒下的删除从这时开始落树（2026-10-27）。扫完才开始盯的话，扫描读过的目录里随后被删的、扫完到开始跟踪之间被删的，树里会一直留着、界面也不提示重扫；扫描时就没读到的，按路径找不到节点，自然跳过。取消、失败、被新扫描顶掉的那次跟踪随之作废；新结果换上时旧的换掉，`Dispose` 时停掉，重扫被取消时旧树照样跟踪。扫描期间的新建同样记进 `OtherChanges`（有的读到了、有的没读到，「统计可能过期」照实说）。

- **落进树的只有删除和改名掉的旧名字**：这两种能按路径精确对上节点（`FindByPath`，跳过元文件和虚拟节点）。落之前在树锁外 `NativeFs.Probe` 确认磁盘上真没了——编辑器保存常是先删再建同名文件，那不算删除。确认后走 `DeleteService.ApplyDeleted`（和删除同一条标删除的路），累计 `RemovedEntries`。
- **新建、改名出来的新名字**不重扫加不进树，只计 `OtherChanges`，界面提示「统计可能过期」；文件内容变大变小不跟踪。
- **和 DiskLens 自己的删除**：每 300 ms 处理一批（最多 5000 条，每 200 条放一次树锁）；写门被占着（自己在删、在清理、在对账）就先攒着，删完再处理——那时自己删的已经标掉了，按路径找不到，自然跳过，释放量也不会被这里抢走。攒到 20 万条就不攒了。
- **跟丢**：缓冲溢出、通知中断、积压太多记 `Overflow`，提示重扫；写门被占着时溢出不报（丢的多半是自己删的，那段时间别的程序删的要等下次扫描）。系统不给通知的位置 `Watching=false`。
- **事件**：`tree.changed`（`TreeChangeDto`：扫描号、在不在跟踪、累计删掉的条目、新建或改名数、是否跟丢、树版本）。扫描完成时先发一条初始状态，之后有变化才发。前端按扫描号认（换过扫描的迟到事件丢掉）；树版本比界面上的汇总新才刷新，攒 1.5 秒安静刷新看得见的部分（不清空、不转圈、不弹错，前进后退记录留着；视图根被删退到最近的活祖先，选中的被删改选当前目录）。自己的删除进行中按 `operation.progress` 每 0.4 秒同样安静刷新，地图跟着一块块消失。

---

## 5. 搜索（SearchIndex）

```csharp
public static SearchIndex SearchIndex.Build(FileTree tree, CancellationToken ct)   // 扫描完成后在后台立即构建
public SearchResultDto Query(SearchRequest req, CancellationToken ct)              // 调用方持有 tree.Sync
```

- 索引：把所有名字转小写（`ToLowerInvariant` 逐字符），拼成一个 `char[]`，名字之间用 `'\0'` 分隔，同时保存每个节点名字的起始偏移（单调递增，可以二分）。200 万节点内存约 60 MB，构建 < 500 ms。
- 查询语法（空格分隔的词之间是 AND；双引号可以把带空格的词括起来）：
  - `foo` → 名字包含 foo（不区分大小写）
  - `-foo` → 名字不包含 foo
  - `*.log`、`img_??.png` → 通配符，对整个名字匹配
  - `a\b` → 含反斜杠的词匹配完整路径（目录路径按需缓存）
  - `ext:mp4,mkv` 或 `ext:mp4;mkv` → 扩展名过滤
  - `size:>1gb`、`size:<10mb`、`size:>=500k`、`size:100mb-2gb` → 按当前度量过滤，单位 b/k/kb/m/mb/g/gb/t/tb，不写单位就是字节
  - `type:file`、`type:dir`（`file:` 和 `dir:` 是简写）
  - 只有过滤词、没有名字词也可以（比如 `ext:iso size:>1gb`）
  - `foo | bar` → 引号外的 `|` 把条件分成几组，满足任一组即可，组内照旧 AND（Windows 文件名里不会有 `|`，不和名字撞；引号里的 `|` 是字面字符）
- 执行：先选最有区分度的正向子串词，在小写名字池上分段并行做 `IndexOf`（.NET 会向量化），命中位置二分映射回节点，跳到下一个名字边界继续找；然后对候选节点检查其余条件。没有子串词时并行全表扫描。每段用固定大小的最小堆取 Top-K，最后合并。
- 排序 `Sort`：`size`（默认，按当前度量降序）或 `relevance`（名字整个就是这个词、或去掉扩展名就是 1000 > 开头 700 > 词首 500（前面是分隔符或从汉字切到字母）> 中间 300，组里各名字词相加，同档再按占用）。顶栏搜索默认按相关度，能切回占用。
- 智能匹配 `Smart`（顶栏开着，默认关）：名字里的汉字也按拼音首字母对（GB2312 一级汉字的 3755 个常用字，第一次用到才建、只收带汉字的名字；拼音里对上的打八折）；一条精确结果都没有、只有一组、有 3 个字符以上的名字词时，把正向名字词放宽成「字符按顺序出现、跨度不超过词长两倍」再找一遍，结果标 `Fuzzy`、一律按相似度排（最多 300 分，低于任何精确匹配）。漏字母的 `dwnld` 对得上，拼错一个字母的 `dowmload` 对不上。
- 结果：匹配总数 `Total`；按 `Sort` 排的前 `Limit` 条（默认 300）；`TookMs`。跳过 Deleted 和 Virtual 节点；目录和文件都参与匹配。`ScopeId` 不为空时只要它的子孙（沿父链检查）。
- 查询语法错误时返回 `Error`（中文，指出哪个词有问题），不抛异常。
- 细则（C 实现时确认，前端语法速查照此写）：词首没加引号的 `-` 才是排除，`"-b"` 是找 `-b`，单独的 `-` 是找连字符；`c:` 这种盘符按路径词处理；`dir:`/`file:` 后面可以带名字（`dir:photos`、`file:*.pdf`），带名字时不能取反；`ext` 接受 `mp4`、`.mp4`、`*.mp4`，逗号后可有空格；引号里的 `size` 值忽略空格；`size:1gb` 不带比较符表示恰好等于；纯 `*` 不算条件，`-*` 排除一切；空查询返回空结果、不报错；最多 100 个条件、4096 个字符；`ScopeId` 只搜严格子孙；`Limit ≤ 0` 只数个数。
- 性能目标：200 万条目，单个子串词 < 50 ms，全表过滤 < 150 ms。实测（24 核，2026-10-26）：建拼音首字母连首次查询 24 ms，相关度子串 2.6 ms，拼音种子 2.5 ms，近似匹配全表 21 ms。

---

## 6. 清理建议（CleanupAdvisor）

```csharp
public static List<SuggestionDto> CleanupAdvisor.Analyze(FileTree tree, CleanupContext ctx, CancellationToken ct)
public static OpResultDto CleanupAdvisor.Run(FileTree tree, SuggestionDto s, CleanupRunRequest req, CleanupContext ctx, CancellationToken ct)
```

- 只看当前树里**确实存在**的节点（用 `tree.FindByPath` 定位；路径用环境变量和 `Environment.GetFolderPath` 解析，不能写死用户名）。大小为 0 的建议不输出。
- 分组：`safe`（可放心清理）、`caution`（先确认，需要你判断）、`protected`（别删，只给正确的处理方法）。
- 动作：`deleteContents`（永久删除文件夹里的内容，保留文件夹本身，跳过被占用的文件）、`deleteNodes`（删除选中的条目，默认移到回收站）、`emptyRecycleBin`、`command`（显示命令，可一键在新的控制台窗口运行，需要时请求提权）、`openSettings`（打开设置页或系统工具）、`info`（只说明）。

| Id | 组 | 目标（存在才输出） | 动作 | 说明要点 |
|---|---|---|---|---|
| user-temp | safe | `%TEMP%` 的内容 | deleteContents | 程序运行时的临时文件；正在使用的会被跳过 |
| win-temp | safe | `%WINDIR%\Temp` 的内容 | deleteContents（需管理员） | 系统临时文件 |
| recycle-bin | safe | `<卷>\$Recycle.Bin` | emptyRecycleBin | 清空这个卷的回收站 |
| wu-download | safe | `%WINDIR%\SoftwareDistribution\Download` 的内容 | deleteContents（需管理员） | 已安装更新的下载缓存 |
| delivery-opt | safe | `%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache` | command：`Delete-DeliveryOptimizationCache -Force`（PowerShell，需管理员） | 传递优化缓存 |
| thumb-cache | safe | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` 下的 `thumbcache_*.db`、`iconcache_*.db` | deleteNodes（永久） | 缩略图缓存，会自动重建；资源管理器占用的会被跳过 |
| browser-cache | safe | Chrome/Edge/Brave 各个 Profile 下的 `Cache`、`Code Cache`、`GPUCache`、`DawnGraphiteCache`、`DawnWebGPUCache`；Firefox `Profiles\*\cache2` | deleteContents | 先关闭浏览器；不会删除登录状态、书签和历史 |
| crash-dumps | safe | `%LOCALAPPDATA%\CrashDumps`、`%WINDIR%\Minidump`、`%WINDIR%\MEMORY.DMP`、`%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive`、`ReportQueue`、`%LOCALAPPDATA%\Microsoft\Windows\WER` | deleteContents / deleteNodes | 崩溃转储和错误报告 |
| dev-caches-&lt;种类&gt; | safe | npm（`npm_config_cache` 或 `%LOCALAPPDATA%\npm-cache`）、pip（`%LOCALAPPDATA%\pip\Cache`）、NuGet http-cache（`NUGET_HTTP_CACHE_PATH` 或 `%LOCALAPPDATA%\NuGet\v3-cache`）、yarn（`%LOCALAPPDATA%\Yarn\Cache`）、Gradle（`%USERPROFILE%\.gradle\caches`）、Cargo（`CARGO_HOME` 或 `%USERPROFILE%\.cargo` 下的 `registry\cache`）、Go（`%LOCALAPPDATA%\go-build`）、uv（`UV_CACHE_DIR` 或 `%LOCALAPPDATA%\uv\cache`）；每种一条 | command（`npm cache clean --force`、`pip cache purge`、`dotnet nuget locals http-cache --clear`、`yarn cache clean`、`uv cache clean`、`go clean -cache`），没有官方命令的用 deleteContents | 删了下次安装会重新下载 |
| pnpm-store | safe | `pnpm store path` 的常见位置 `%LOCALAPPDATA%\pnpm\store` | command：`pnpm store prune` | 不要直接删 store |
| node-modules | caution | 树中所有名为 `node_modules` 的目录（不进入嵌套的 node_modules），按大小取前 50；标题写「闲置项目的 node_modules」，按项目最近修改时间从旧到新排 | deleteNodes（回收站），可勾选 | 项目依赖，删了需要重新安装 |
| build-outputs | safe | 名为 `target` 且同级有 `Cargo.toml` 的目录；名为 `obj` 且同级有 `*.csproj` 的目录；名为 `.next`、`.turbo` 且同级有 `package.json` 的目录 | deleteNodes（永久），可勾选 | 编译产物，可以重新生成 |
| ai-models | caution | `OLLAMA_MODELS` 或 `%USERPROFILE%\.ollama\models`、`HF_HOME` 或 `%USERPROFILE%\.cache\huggingface`、`%USERPROFILE%\.lmstudio\models`，以及 §4.7 规则 4 认出的 `models` 目录 | deleteNodes（回收站），可勾选 | 本地模型，删了要重新下载，体积很大 |
| android-sdk | caution | `%USERPROFILE%\.android\avd`（模拟器镜像）、`%LOCALAPPDATA%\Android\Sdk\system-images` | deleteNodes（回收站），可勾选 | 不用的模拟器和系统镜像可以删，用 Android Studio 的 SDK Manager 也行 |
| old-downloads | caution | `%USERPROFILE%\Downloads` 里修改时间早于 90 天的条目 | deleteNodes（回收站），可勾选 | 按大小排序，你自己挑 |
| large-files | caution | 整棵树中 ≥ 1 GB 的文件，排除系统文件、元文件、虚拟节点和 pagefile/hiberfil/swapfile，取前 50 | deleteNodes（回收站），可勾选 | 视频、镜像、虚拟机磁盘等 |
| windows-old | caution | `<卷>\Windows.old`、`$Windows.~BT`、`$Windows.~WS`、`$WINDOWS.~Q`、`$GetCurrent`、`ESD` | openSettings：`ms-settings:storagesense` | 旧系统和升级残留，10 天内可以回退；需要 TrustedInstaller 权限，请用系统的存储设置清理 |
| hiberfil | protected | `<卷>\hiberfil.sys` | command：`powercfg /h off`（需管理员） | 会关闭休眠和快速启动；不要直接删文件 |
| pagefile | protected | `pagefile.sys`、`swapfile.sys` | openSettings：`SystemPropertiesPerformance.exe` | 虚拟内存，在系统设置里调整 |
| winsxs | protected | `%WINDIR%\WinSxS` | command：`Dism.exe /Online /Cleanup-Image /StartComponentCleanup`（需管理员） | 组件存储，手动删会损坏系统；大小已扣除硬链接 |
| installer | protected | `%WINDIR%\Installer` | info | MSI 安装缓存，删了以后程序无法卸载或修复 |
| restore-points | protected | `<卷>\System Volume Information` | openSettings：`SystemPropertiesProtection.exe` | 还原点和卷影副本，在系统保护里调整占用上限 |

- Selectable 的建议，`Items` 必须覆盖全部 `NodeIds`（上限 100）——前端要靠它算「只勾了一部分」时的大小；不可勾选的建议 `Items` 取前 20。
- 每条建议都要填 `DisplayPath`（§6.1）。
- `Run` 只执行 `Analyze` 输出过的建议，按 `SuggestionId` 查找；`command` 只能运行规则表里写死的命令，**不能**运行前端传来的任意字符串。
- `deleteContents`、`deleteNodes`、`emptyRecycleBin` 都通过 `DeleteService` 执行，并且同样受 §7 保护规则约束。`emptyRecycleBin` 执行后对 `$Recycle.Bin` 下的条目调用 `DeleteService.Reconcile`。
- `command` 和 `openSettings` 不修改树；结果 Message 提示「完成后可按 F5 重新扫描」。

### 6.1 显示路径

`SuggestionDto.DisplayPath` 给参考图右栏那行灰色小字用：用户配置文件前缀写成 `~`（`~\AppData\Local\npm-cache`），多个位置用「, 」连接，太多时写前两个再加「等 N 处」。不在用户目录下的写完整路径。

### 6.2 清理脚本（`cleanup.script`，D1 负责）

入参是勾选的建议（可以带子集 NodeIds），返回一份 PowerShell 脚本，前端写进剪贴板。脚本只描述「要删什么」，不自动提权；`NeedsAdmin` 让前端提示用户。脚本要能在 Windows PowerShell 5.1 和 PowerShell 7 里都正确运行。

```powershell
# DiskLens 清理脚本 · 2026-10-15 21:30 · 5 项 · 约 52.5 GB
# 删除不进回收站，无法恢复。运行前请关闭浏览器、IDE 和相关程序。
# 用法：在 PowerShell 中粘贴运行；标了「需要管理员」的段落请在管理员 PowerShell 中运行。
$ErrorActionPreference = 'Continue'
# 扫描开始的时刻（UTC FILETIME）：这之后新建、改过、挪进来的东西一律不删，要删请重新扫描后再复制脚本
$DiskLensScanStart = 134062818000000000
# Add-Type 内嵌 DiskLensScriptV2 删除逻辑，见下
function Remove-DiskLensItem([string]$Path, [string]$Identity) { ... }
function Clear-DiskLensFolder([string]$Path, [string]$Identity) { ... }

# ── npm 缓存 · 13.4 GB · ~\AppData\Local\npm-cache
npm cache clean --force

# ── 临时文件 · 4.3 GB · ~\AppData\Local\Temp
Clear-DiskLensFolder 'C:\Users\Alex\AppData\Local\Temp' '8C3A1F20-000600000001A2B3-01DC4512AB34CD00-10'
```

**只删确认过的东西（2026-10-27 审计后重写）**：脚本可能隔几天才跑，目录里的东西早变了。旧写法运行时重新列目录、见什么删什么，生成脚本之后新放进去的 `.ssh\id_rsa`、新文件都会被永久删除。确认清单（整棵子树的路径和身份）太大，粘贴不进 PowerShell，所以改成按「扫描开始的时刻」判断，和 DeleteService 只删树里有的东西是同一个底线：

- **目标核对身份**：生成时读每个目标的文件身份（卷序列号、文件 ID、创建时间、目录/重解析两位，`IdentityToken`）写在命令后面；运行时打开目标、在删除用的同一个句柄上比对，对不上（被换成同名的别的东西）整条跳过，写明「生成脚本之后这个位置换成了别的东西」。生成时已经不在、读不到身份的目标只写注释，不算进 `Items` / `Bytes`。
- **目标里面的条目看三个时间**：创建、修改、变更（`ChangeTime`：改名、移动、改属性、加硬链接都会更新）有一个晚于 `$DiskLensScanStart` 就留着——扫描之后新建、改过、挪进来的都不是用户确认过的。目录也一样：挪进来的旧目录自己的 `ChangeTime` 是新的，整个留下、不往里钻（里面的旧文件时间没变，不看目录自己就会被删）；往目录里加删条目也会更新它的时间，所以扫描之后有过变动的子目录同样整个留着，关掉相关程序、重新扫描再复制脚本才删得到。留下的条目每个目标汇总成一句「已保留 N 项…（例如 …）」。被选中的单个文件、链接扫描之后改过的也不删。
- **扫描开始的时刻** = 完成时刻 − 用时，再往前让 2 秒（文件时间按时钟滴答取整），从 `CleanupContext.ScanStartedAtMs` 传进来；不知道就把删除类条目全写成注释。
- **只在记 `ChangeTime` 的卷上清目录**：`CleanupContext.FileSystem` 是 NTFS、ReFS 才写目录类的删除；FAT、exFAT 不记它，认不出挪进来的旧东西，目录条目只写注释、请用「立即清理」，单个文件照写。
- **运行时再拦一道名字**：`.ssh`、`.gnupg`（密钥目录）和以点、空格结尾的名字一律不删，单独点名。
- 辅助逻辑写在脚本开头：
  - `DiskLensScriptV2`：用 `Add-Type` 内嵌的一段 C#（C# 5 写法，兼容 Windows PowerShell 5.1 和 PowerShell 7，不依赖应用 DLL），和 `NativeFs.Delete`、`NativeFs.OpenGuard` 同一套句柄写法。从卷根到目标的上一级逐层取不共享删除权限的目录句柄（`LIST_DIRECTORY`，`OPEN_REPARSE_POINT`，按名字代理位拒绝 junction、符号链接和挂载点；云占位不拒绝）；目标和目标里面的目录也一路握着同样共享方式的句柄，处理完才放，期间谁也改不了名、换不成链接。枚举用 `FindFirstFileW`，路径一律 `\\?\`（深于 260 字符也行）。文件、链接在带 `DELETE` 的句柄上标记删除（POSIX 语义、忽略只读；不认就退回普通删除并在同一个句柄上去掉只读），链接只删链接本身；目录在子项都没了之后才删，冒出新东西就留着。没有权限取得上一级的句柄就整条跳过，不退化为无锁检查；受限语言模式禁用 `Add-Type` 时脚本不能执行这些删除。自检用的 `BeforeList` 钩子平时是 null。
  - `Remove-DiskLensItem` / `Clear-DiskLensFolder`：脚本正文只调这两个，把路径、身份和 `$DiskLensScanStart` 交给 `DiskLensScriptV2`，它返回的每一句都 `Write-Warning` 出来（每个目标最多 20 句错误，多的汇总），出错不中断其他条目。`$DiskLensScanStart` 缺了当 0：什么都算「扫描之后的」，一律不删。
- 每种动作：
  - `command`：规则表里写死的命令原样输出；需要管理员的段落标题加「（需要管理员）」。
  - `deleteContents`：每个目录一行 `Clear-DiskLensFolder <路径> <身份>`，保留目录本身。
  - `deleteNodes`：每个条目一行 `Remove-DiskLensItem <路径> <身份>`。
  - `emptyRecycleBin`：`Clear-RecycleBin -DriveLetter C -Force`。
  - `openSettings`：一行注释说明 + `Start-Process '<target>'`。
  - `info`：只有注释。
- 路径：一律写扫描时的绝对路径（单引号字面量，单引号写成两个单引号）。不用 `$env:USERPROFILE`：换个账号（比如另一个管理员账号）运行时它指向那个账号的同名目录，而界面上看到的路径、大小和保护判定都属于扫描时的账号。路径里有换行或控制字符的条目跳过，并在脚本里注释原因。
- 段落标题行（`# ── 标题 · 大小 · 显示路径`）里的文字同样要去掉换行，防止「注释里换行变成命令」。
- 补充（D1 实现时确认）：只读文件照删，被占用的条目只警告；脚本里连注释都不出现递归删除参数的名字；`Items` 是实际写进脚本的建议条数；`Clear-RecycleBin` 的盘符取自 `CleanupContext.VolumeRoot`；含换行或控制字符、或被 §7 保护规则拦下的条目跳过并写注释；`Analyze` 会先把保护规则必拦的删除条目筛掉，不摆给用户看。dev-caches 每种缓存一条，Id 是 `dev-caches-npm`、`dev-caches-pip` 这样。

---

## 7. 删除与保护

```csharp
public static ProtectionResult Protection.Evaluate(FileTree tree, int node)       // Blocked / Warning / Reason
public static DeleteCheckDto DeleteService.Check(FileTree tree, IReadOnlyList<int> ids)
public static OpResultDto DeleteService.Delete(FileTree tree, IReadOnlyList<int> ids, bool permanent, CancellationToken ct)
public static OpResultDto DeleteService.DeleteContents(FileTree tree, int dirNode, CancellationToken ct)
public static long DeleteService.Reconcile(FileTree tree, int node)                // 子节点在磁盘上已不存在的标记删除，返回释放字节
```

**直接拦截（Blocked）**：虚拟节点、元文件、扫描根节点、卷根；`%WINDIR%` 本身，以及 `%WINDIR%` 下的 `System32`、`SysWOW64`、`WinSxS`、`Installer`、`servicing`、`Boot`、`SystemApps`、`SystemResources`、`Fonts`、`assembly`、`Microsoft.NET` 及其所有子孙；`Program Files`、`Program Files (x86)`、`ProgramData`、`Users`（这几个根目录本身）；用户配置文件根目录本身；`System Volume Information`、`$Recycle.Bin`（用清空回收站代替）、`Recovery`、`Boot`、`EFI`、`bootmgr`、`BOOTNXT`、`pagefile.sys`、`hiberfil.sys`、`swapfile.sys`、`DumpStack.log.tmp`；DiskLens 自己所在的目录。

**警告（Warning，需要额外确认）**：`%WINDIR%`、`Program Files*`、`ProgramData` 下的其他内容；`%APPDATA%`（漫游配置）；带系统属性的条目；超过 10 GB 或超过 10 万个文件的单次删除。

**执行**：
- 先在锁内拿到路径和标志，然后**释放锁**做磁盘 IO，最后再加锁更新树。
- 回收站：用 `IFileOperation`（COM，`FOFX_RECYCLEONDELETE|FOF_ALLOWUNDO|FOF_NOCONFIRMATION|FOF_SILENT|FOF_NOERRORUI|FOFX_EARLYFAILURE`），一次提交多个路径。完成后逐个检查路径是否还存在：不存在就 `MarkDeleted`；还存在就记为失败。
- 永久删除：按树的节点做后序遍历，先删文件后删目录，路径用 `\\?\`。每个叶子（文件、链接、清空了的目录）用 `OPEN_REPARSE_POINT` 打开一个带 `DELETE` 的句柄（共享方式和 `DeleteFileW` 一样），在这个句柄上标记删除（`FileDispositionInfoEx`：POSIX 语义、忽略只读；老系统、FAT 不认就退回普通删除，只读属性在同一个句柄上去掉）——**不跟随重解析点**：junction 只删链接本身，不碰目标。有确认快照时，身份也在这同一个句柄上核对（2026-10-27）：先按路径核对、再按路径删的话，中间被换成同名新文件就删错了；现在中间被改了名，删掉的也只是核对过的那一个。每成功删掉一个节点就记下来，最后在锁内对这些节点 `MarkDeleted`（子树已整体删除的只标子树根）。被占用或无权限的文件跳过，记进 Errors（最多 20 条）。回收站走外壳 `IFileOperation`，只能按路径交，没法绑句柄：核对到提交之间仍有极短的窗口，好在进回收站的还能还原。
- 回收站里的空间只有清空后才释放，结果 Message 要写明这一点。`FreedBytes` 用 `MarkDeleted` 返回的净释放值。
- **边删边落树**：每做完一个确认目标就把它在树里标掉（最多 10 次/秒），地图和列表跟着删除进度消失；释放量照旧只在收尾报一次。

**和别的程序同时删（2026-10-26）**：NTFS 上同一个文件谁先删谁成，没有优先级可言，后到的拿到「找不到文件/路径」或「删除待定」（`ERROR_DELETE_PENDING`）。DiskLens 把这三种都当「已经不在」：树里照样标掉，但不算这次删除的条目和释放量，单独计 `OpResultDto.AlreadyGone`，Message 写「另有 N 项动手前已经不在（可能被别的程序先删了），没算进释放量」。确认快照（`DeletionSnapshot`）执行前逐项复核身份：别人删了又建了一个同名的新文件，拒绝删除，不误删新文件。

---

## 8. 视觉规范

### 8.1 定位

- 最终形态：浅色仪表盘，顶部四格统计卡，左边按**用途**着色的 treemap「资源地图」，右边「清理建议」可以勾选并生成清理脚本。
- 参考图是 macOS 主文件夹的样子；我们做 Windows 版：路径、用途类别、命令换成 Windows 的，**布局、配色、字体、间距、圆角、交互照抄**。
- 原需求里参考图没有的部分（选盘启动页、扫描中、搜索、右键菜单、删除确认、详情、大文件）用同一套视觉语言补齐，要像同一个设计师做的。
- 实现时打开参考图对照：本节写了的以本节为准，本节没写到的以参考图为准。

### 8.2 色彩（只用这些；CSS 变量在 theme.css，用途色的唯一出处是 web/src/lib/purposes.ts）

| 名称 | 值 | 用途 |
|---|---|---|
| page | `#F4F4F5` | 页面底色 |
| card | `#FFFFFF` | 卡片 |
| line | `#E4E4E7` | 卡片描边、分隔线 |
| line-soft | `#F0F0F2` | 列表行分隔 |
| ink | `#18181B` | 主文字、主按钮底色、复选框勾选底色 |
| ink-2 | `#3F3F46` | 次级正文 |
| muted | `#71717A` | 说明文字、路径 |
| faint | `#A1A1AA` | 第三级文字、占位符 |
| hover | `#F4F4F5` | 行和按钮的悬停底色 |
| safe | 文字 `#166534`，装饰 `#16A34A`，底 `#E7F6EC`，描边 `#4CAF6E`，斜纹 `#86C99A` | 可放心清理、增量；文字使用 safe-text |
| review | 文字 `#A84B08`，装饰 `#EA7A1A`，底 `#FDF0E3`，描边 `#F2B577` | 先确认；文字使用 review-text |
| danger | 文字 `#DC2626`，底 `#FDECEC` | 删除 |
| focus | 2px `#18181B` 外环，与元素之间留 2px 白边 | 键盘焦点 |

用途色 U（treemap 块：fill 填充、line 描边；图例方块、圆点和详情标签用 dot。每个主用途一个色系：fill 最浅、line 居中、dot 最深，三者同一色相；绿色留给「可放心清理」和勾选斜纹，用途色里不用）：

| U | 名称 | dot | fill | line |
|---|---|---|---|---|
| 1 | 应用程序 | `#D9A21B` | `#FBF2D7` | `#EED38E` |
| 2 | 游戏 | `#CB4DC1` | `#F9E1F7` | `#EDB6E8` |
| 3 | 应用数据 | `#75A630` | `#ECF6DF` | `#C7E0A3` |
| 4 | 开发项目 | `#7468F0` | `#E7E5FC` | `#C2BCF6` |
| 5 | SDK 与运行环境 | `#22A9C2` | `#DDF2F6` | `#A9DCE6` |
| 6 | 缓存 | `#4F75F2` | `#E1E8FD` | `#B6C7F8` |
| 7 | 日志与诊断 | `#149A86` | `#DAF4EE` | `#9ED8CC` |
| 8 | AI 与模型 | `#E0558A` | `#FCE4EC` | `#F2B6CB` |
| 9 | 文档与媒体 | `#F07B3F` | `#FDEDE3` | `#F5C9AE` |
| 10 | 数据库与数据集 | `#7B3FB5` | `#EFE7F9` | `#D0BAE8` |
| 11 | 归档与镜像 | `#946842` | `#EEE7DF` | `#D3C2B1` |
| 12 | 系统与恢复 | `#64748B` | `#E8EBF0` | `#C6CDD8` |
| 0 | 未识别 | `#A1A1AA` | `#F2F2F3` | `#D6D6DA` |
| 13 | 未解释空间 | `#52525B` | `#F4F4F5` + `#D4D4D8` 斜纹 | `#D4D4D8` |

（图例顺序就是上表的顺序：按编号，未识别和未解释空间垫底；名字和地图提示、详情一致，都来自 `purposes.ts`。未识别和未解释空间都是中性灰：未识别是实心浅灰，未解释空间另加灰斜纹、圆点更深。`web/tests/purposes.test.ts` 校验编号/key/名称与 C# 一致、三档明暗与色相、两种灰分得开、用途之间不撞色、不借清理色。）

- **勾选待清理**：被勾选的建议覆盖到的块，在原填充上叠 45° 斜纹（`#86C99A`，线宽 1.5px，间距 7px），描边换成 1.5px `#4CAF6E`。「类别」和「清理」两种着色模式都画。
- **「清理」着色模式**：可放心清理的块填 safe 底 + safe 描边；先确认的填 review 底 + review 描边；别删的填 `#F2F2F3`；不涉及清理的填 `#FAFAFA` + line 描边。

### 8.3 字体

- 正文：**Geist**（`@fontsource-variable/geist`，随包打包，离线可用），中文回落 `"Microsoft YaHei UI"`。
- 数字、大小、路径、命令：**Geist Mono**（`@fontsource-variable/geist-mono`），中文回落雅黑。所有字节数都用 mono + `tabular-nums`。
- 字号：卡片标题 15/600；卡片副标题 13/400 muted；统计卡标签 13 muted；统计卡大数字 28/600 mono（单位同字号）；统计卡附注 13 mono（增量 safe 色，计数 muted）；treemap 目录标题 13/600 ink、目录大小 12 mono muted；文件块名字 12/500 ink-2，大小 mono 600、字号按块大小在 13–22px 之间取；建议行标题 14/500，路径 12 mono muted，大小 14/600 mono；按钮 13/500。

### 8.4 版式（澄净工作区，2026-10-25）

    顶栏：返回启动页 / 当前范围 / 搜索 / 显示选项 / 扫描详情 / 重新扫描 / 设置
    局部切换：资源地图 | 最近文件
    紧凑概览：已统计占用 | 磁盘可用 | 清理建议 | 未解释空间或扫描范围外
    ┌────────────────────────────────┬───────────────────┐
    │ 资源地图       详情 / 大文件 / 清理 │ 可收起右栏         │
    │ 面包屑与当前目录容量             │ 详情、大文件或清理   │
    │       真实嵌套 treemap           │                   │
    │ 用途 / 清理图例                 │                   │
    └────────────────────────────────┴───────────────────┘

- 不再挂载左侧全局导航；扫描完成直接进入资源地图。概览四格数值 18–20px，地图占剩余高度。
- 局部令牌在 web/src/styles/workspace.css：浅蓝灰底、白色容器、蓝色主动作、14px 卡片圆角。用途/风险色复用原契约，Canvas 从宿主节点读取主题。
- 外边距 12px / 18px / 16px，间距 12px；右栏 360px（窗口小于 1280 时 340px），关闭后地图铺满。800px 以下覆盖式右栏不超过窗口宽度，关闭恢复触发焦点。
- 顶栏可换行；600px 以下图例限制高度并滚动。扫描详情与显示选项适配窄窗口，长路径折行或省略并可查看全路径。
- 不把建议合计加到磁盘可用量上，不把未解释容量统称系统或权限问题。

### 8.5 Treemap（参考图的核心，F 负责）

- 视图根本身不画框，只画它的子节点，外层留 8px。
- 每个块：圆角 6px（短边 < 24px 的块用 3px）；fill 填充 + 1px line 描边；块之间间隙 4px（第二层以下 3px）。「类别」模式下容器里的叶子块比容器深一档（颜色向 line 靠 22%，参考图实测的层次感）。
- 目录块：左上 10px 处写名字（13/600），右上写大小（12 mono muted）；标题行高 28px；子块区域在标题下方，四周内边距 6px。目录太小（宽 < 90 或高 < 64）不画标题、不展开子块，按叶子画。
- 单子目录链：目录块的子块区只铺得出一块、它占兄弟总和的 99% 以上（剩下的零头小到画不出来）、而且还能展开时，把它并进同一个标题行（「Users › Alex」），它的子块直接铺在这一层的子块区里，省掉一层标题和内边距；能连着并（「Program Files (x86) › Steam › steamapps」）。合并块代表链尾：点击进入链尾，悬停、右键对着链尾；右上的大小写链头的（面积是链头的）。看得见的兄弟或成片的零头一律不并，免得藏东西或放大链尾。
- 叶子块（文件、未展开的目录、聚合块）：左上写名字（12/500 ink-2，单行省略），左下写大小（mono 600，字号随块大小 13–22px）；块小到放不下名字时只写大小，再小就留白。
- 聚合块「其余 N 项」：用父目录用途色的 fill（60% 不透明度），名字写「其余 N 项」。
- 未展开的目录（还能点进去）：右下角画 12px 的「↘」角标。
- 悬停：描边换成 dot 色 1.5px + `0 2px 8px rgba(0,0,0,.08)` 阴影；提示卡（白底浮层卡）显示名字、完整路径（mono）、两种度量的大小、占当前视图的百分比、子项数。
- 选中（单击文件、右键、搜索定位）：2px ink 外环。
- 单击目录块 = 进入（放大）；单击文件块 = 选中并打开详情面板；右键 = 菜单；Backspace、鼠标侧键 = 上一级。
- 键盘：地图仅一个 Tab 焦点；上/左选上一项，下/右选下一项，Home/End 到首尾，Enter/空格进入目录或查看文件，Backspace 返回。当前项提供名称、类型、大小与序位，不把上万个方块加入 Tab 顺序。
- 加载：保留旧地图并显示“正在加载地图…”，禁止操作旧方块；失败时清除旧地图并提供重试。详情读取时显示占位，旧详情和操作不再展示。
- 进入/返回过渡：旧画面快照从被点的块放大到全画布（240ms，`cubic-bezier(.2,.8,.2,1)`），新布局同时淡入；返回反向。`prefers-reduced-motion` 时只淡入。
- 图例：卡片底部一行，只列当前视图里出现的用途：8px 圆角方块（dot 色）+ 名字（12 muted）。

### 8.6 文案

- 界面有简体中文、英文、日文三种（§8.8）；中文是原文，下面的例子都按中文写，英文、日文照同样的要求译：平实、主动语态、同一个动作全程同一个词。
- 界面文案用中文，平实、主动语态、说明具体后果。按钮写动作：「扫描」「深度扫描」「移到回收站」「永久删除」「在资源管理器中显示」「在网上搜索」「复制路径」「属性」「复制脚本」「立即清理…」「选中安全项」。
- 同一个动作全程用同一个名字：按钮写「移到回收站」，完成提示就写「已移到回收站，释放 1.2 GB（清空回收站后生效）」。
- 错误说清楚发生了什么、怎么办，不道歉：「无法读取 MFT：需要管理员权限。点「以管理员身份重启」后再试。」
- 空状态给下一步：「没有找到匹配项。试试 ext:mp4 或 size:>1gb。」
- 固定文案：统计卡「清理后可用」「可放心清理」「先确认」「未扫描」；未扫描的附注扫卷根时写「系统保留与无权访问」，扫文件夹时写「扫描范围之外」。资源地图副标题「按占用空间排列，点击方块进入。」；清理建议副标题「勾选要清理的项，生成清理脚本。」；分组「可放心清理」「先确认」「别删 · 用正确的方法」；页脚「52.5 GB 已选 · 5 项」。

### 8.7 动效

- 统计卡数字变化时从旧值滚到新值（300ms）。
- 勾选或取消建议时，treemap 上对应块的斜纹淡入淡出（200ms）；减少动效开启时直接切换到最终状态，运行中开启也立即结束补间。
- 启动页 Logo 只在出现时从上往下一行行打出来一遍（0.5s，像打印头扫过）；减少动效开启时直接显示最终状态。
- 其余克制：悬停 120ms，面板切换 160ms 淡入。没有循环播放的装饰动画。

### 8.8 界面语言（2026-10-27）

- **三种语言**：简体中文（原文）、English、日本語。启动页左上角的语言按钮（地球文字图标 + 当前语言名）切换，界面马上换，不用重启；选择存进 `settings.json`（§2.1），下次启动就是它。浏览器演示模式（没有宿主）存在 `localStorage`。启动语言的顺序：地址里的 `?lang=`（宿主按保存的选择带上）→ `localStorage` → 系统语言。
- **前端**（`web/src/lib/i18n.ts`）：msgid 就是中文原文，`t('中文', { 参数 })` 取当前语言；英文、日文目录在 `web/src/lib/locales/`。`MsgId` 从英文目录推出，日文目录必须一条不差（少译、多译、引用不存在的 msgid，svelte-check 当场报错）；`web/tests/i18n.test.ts` 再核对两份目录的 msgid 完全一致、每条译文的 `{占位}` 和原文一致。同一个中文要译成不同的词时 msgid 带上下文：`文件@@单个`（中文只显示 @@ 前面）。数字参数自动按千分位写；英文单复数写成函数（`plural(n, 'item')`）。用途、角色、类别这些名字表的 key 和中文名不变（前后端契约比的是中文），读 `.name` 时按当前语言翻。
- **后台**（`DiskLens.Core.L`）：发给界面的文字（错误、清理建议、保护原因、删除结果、扫描警告和异常原因、用途依据、清理脚本里的说明）都写成 `L.T(中文, 英文, 日文)`，三种写法摆在一处，插值各写各的语序。`L.Current` 启动时由宿主设好，界面换语言时桥接 `app.setLanguage` 跟着改（只读方法，不排队）；清理建议的缓存按语言分开。用途、角色、证据、所属对象种类的英文日文名是 `NamesEn` / `NamesJa` 并行数组，和界面目录逐字一致（`web/tests/purposes.test.ts` 比对）。清理脚本内嵌给 PS 5.1 编译的那段 C# 保持中文模板，生成时按 `CleanupAdvisor.HelperText` 整句替换（自检核对每条原文都在、英文版不剩中文）。
- **不翻的东西**：文件名、路径、卷标这些用户数据照原样；系统给的错误描述（Win32 消息）是系统界面语言。扫描时就定下来的文字——虚拟节点名（「未统计」「$NTFS 元数据」「$孤立记录」）、扫描警告和异常原因、所属对象名——留在扫描那一刻的语言，换语言后重新扫描才换；按名字认这些虚拟节点的地方三种语言都认（`ScanService.IsUnaccounted`、`PurposeRules.IsMetaGroup`）。
- **自检**：`DiskLens.Check i18n`（启动语言、设置文件读写和损坏、切语言、脚本内嵌段的翻译表）；UiCheck 在启动页依次切到英文、日文再切回中文，核对界面文字、地址里的 `?lang=`、`settings.json`、后台发来的文字。其余自检全按中文断言（`L.Current` 默认中文）。

---

## 9. 界面结构

### 9.1 启动页（G）

- 2026-10-24 按用户选定的「澄净 · 轻盈留白」重构：浅蓝灰页面、白色圆角磁盘卡、蓝色主动作，顶部居中细点阵 DiskLens 与几何标记、轻微偏移阴影（Logo 已于 2026-10-28 换成终端输出，见下）。局部颜色集中在 `web/src/styles/startup.css`，随系统浅/深色适配，减少动态效果时取消悬停位移；不改变扫描后的外壳。
- 本机磁盘每卷一张独立平齐卡，实际数量由 `drives.list` 决定，宽屏三列、中屏两列、窄屏一列。盘符与卷标、文件系统/介质、当前可用容量、已用/总容量与占用条来自真实数据；不可靠容量不显示假零，未就绪卷禁用扫描。每卡只有「扫描 X:」，复用 `startScan(root, 'auto')`；刷新期间禁用扫描并注明容量待刷新。
- 左上角是界面语言按钮（§8.8）。中间的 Logo（2026-10-28 改）是终端字符画：琥珀色的生化危险标志（照标准构造在 21×20 格里取样再手修、左右对称，上下两格并成一个半块字符）和 ANSI Shadow 字体的 DISKLENS（方块加灰色双线阴影）。只有字形本身——没有终端窗口、提示符、光标，也没有底板和辉光，像直接印在页面上，跟着浅色/深色走；方块和双线框字符按字格自己画（同一条路径，字格之间没有接缝），不依赖字体。
- 移除主文件夹/文件夹快捷入口和独立「深度扫描」按钮。权限状态（标准 / 管理员 · 本地分析）和经确认的「以管理员身份重启」按钮放在右上角（2026-10-26 从页脚挪上来）；既有文件夹扫描与提权后端保留。
- 最近扫描读取 `scan.savedSummaries`，按完成时间降序，只列最近一周、最多三条（多了看不过来；更早或排在后面的只写一句还有几条）。展开后是缓存对照（2026-10-26）：扫描方式、文件/文件夹数、用时；比上一次扫描同一范围增减多少；根下面的大项（前六项）和各自比上次的增减（上次不在前列的写明）；旧版本记录没有大项就照实说。只提供重新扫描该范围和打开位置（`shell.openFolder`）；摘要不含可导航的旧 ID。读取中、失败重试、无记录分别显示；无效完成时间记录略过并注明数量。只有当前内存中有有效树，才提供「继续查看当前结果」。
- 新应用与安装位置全部来自扫描记录（缓存对照，冷启动也看得到），只列最近一周对照出来的新应用、最多三条（第一次扫描就有的不算新装）：各记录认出的应用合在一起（同一处在几条记录里只留最近那条），「新出现」的排前面并写明「某次扫描还没有、某次扫描时发现」，其余写「首次记录于」；标「安装位置/识别位置」、角色、证据和占用；可在资源管理器打开，位置已经不在（卸载、搬走）就照实说。上次对照后不见了的应用列在下面（别的记录里还在的不算）。写明对照只在同一范围、同一扫描方式和权限的前后两次之间，时间是扫描发现的时刻、不是安装时刻。

### 9.2 扫描中（G）

- 扫描完成后如果有目录没权限读取（`inaccessibleDirs > 0`）且不是管理员扫的，弹确认框问要不要以管理员身份重新扫描（写明没读到几个目录，确认后重启并自动扫这个范围）；同一范围点过取消，这次运行里不再问。

- 页面中央一张卡（宽 640）：标题「正在扫描 C:\」+ 模式片（「MFT 深度扫描」或「常规扫描」）。
- 三个大数字：文件、目录、已统计（mono 28/600）。
- **用途构成条**是进度的主角：总长代表卷已用空间（扫文件夹时代表已统计），扫描中按 `categoryBytes` 经 §4.7 的 K→U 默认映射分段增长（用途 fill 色 + line 描边），尚未统计的部分是灰色斜纹；随阶段变化：读取/遍历时右端有一个 ink 色光标往前推，构建目录树时各段按用途聚拢排序，核对硬链接时被去重的那一小段闪一下后消失，汇总完成时整条变成最终比例并淡出进入主界面。
- 条下方是阶段步骤（准备 → 读取 MFT/遍历目录 → 构建目录树 → 核对硬链接 → 汇总；常规扫描没有「构建目录树」），当前步骤 ink 色，已完成的打勾。
- 当前路径（mono 12 muted，中间省略）、耗时、速度（文件/秒）、「取消」按钮。
- 文件夹构成条标注“已发现内容构成”，说明不代表完成度；另用无百分比的不确定进度和阶段文字表示仍在扫描。点击取消后立即显示“正在取消…”并禁用重复提交，收到后台终态事件才退出；请求失败则恢复重试。
- 失败：卡片里显示错误原因和「返回」。

### 9.3 主界面

- **外壳**：顶栏 + 紧凑概览 + 资源地图/最近文件，无左侧全局导航。顶部局部按钮保留原生键盘操作与 aria-current。setPage 为统一入口；旧 overview 映射到地图，旧 cleanup 映射到地图清理右栏。
- **顶栏**：字标返回启动页，范围按钮换盘；本地搜索保留高亮、路径、大小、语法提示、方向键、Enter 定位、Esc 与 Ctrl+F。搜索框前有「文件 / Google / Bing」范围下拉，默认文件；选了引擎后打字不查本地、不发出任何内容，按回车才经 `web.search` 在默认浏览器打开，只发输入框里的词（去首尾空白，超过 200 字截断，控制字符拒绝），语法提示随之隐藏。右侧为显示选项、扫描详情、重新扫描（F5）和设置。
  - **显示选项**：实际占用/文件大小、用途/清理着色，继续驱动原查询和布局；外点或 Esc 关闭，恢复按钮焦点。
  - **扫描详情**：范围、完成时间、方式、耗时、文件/目录数、拒绝访问入口、跳过链接/挂载点、读取失败、未解释空间及已有依据。文件夹扫描不估计整卷差额；入口数不冒充内部全部未读目录数。非管理员且有拒绝访问入口时提供经确认的管理员完整重扫；外点或 Esc 关闭，Esc 恢复触发焦点。
- **紧凑概览**：已统计占用和文件/目录数；所在卷可用/总容量；可放心清理与先确认建议的条数，点击打开右栏；卷根显示未解释差额，文件夹显示卷已用减去统计量的范围外容量。加载/失败明确显示，不将估计当已释放。
- **资源地图**：紧凑标题与「详情 / 大文件 / 清理」开关、面包屑、当前目录容量、「目录分布 / 应用占用」维度切换、「列表」开关、嵌套 Canvas 和图例。保留目录链合并、面积比例、悬停、键盘单焦点导航、返回历史、搜索定位、减少动态效果。技术和异常信息移入扫描详情，不再基于差额猜测权限。
  - **目录列表**（dise §5.2，`DirList.svelte`）：按需展开在地图左侧（窄窗口在上方），列当前地图目录的**全部**直接子项（`tree.children` 分页，每页 200，地图并成「其余 N 项」的也在），名称（目录在前）/ 大小 / 修改时间三种排序，大小按当前度量的计入值、和地图同口径。和地图共享当前目录、选中项和悬停：单击选中（详情同步），第二下进入目录，方向键 / Home / End 移动并选中、回车进入目录或打开文件详情（单焦点，只有当前行在 Tab 顺序里）；地图或搜索选中的项在列表里会滚到看得见。换目录、度量、排序重读，删除后刷新，迟到的旧页丢弃。
  - **应用占用**（dise §5.3，`OwnerView.svelte`）：当前地图目录的 `tree.owners`，写明「只算这个范围、不是应用在整台机器上的总占用」；列已归属 / 共享（跨对象硬链接，不重复计入）/ 未归属 / 合计（和地图同口径），每个对象给种类、证据、占比条、最大的三种角色和各处根；定位一处根切回目录分布再走统一导航。
  - **图例**：只列当前视图出现的类别（清理模式列清理组），每项是按钮：悬停或聚焦时其余区域蒙一层浅色、只露出这一类的叶子块（上层画布绘制，不重画场景）；点击固定筛选，再点或「清除筛选」恢复；筛选时写明「在当前目录里占 X / Y（比例，按哪种度量；分母是当前目录，不是整个磁盘）」。换着色方式、换维度时清掉筛选。
- **右侧面板**：默认收起；选中对象或定位展开详情，也可打开大文件/清理。切换保留目录、选中项和勾选；×、Esc、再次按下开关关闭，焦点回到触发点，触发点消失时回地图。
  - **详情**：用途、数据角色、所属对象分三行各带证据等级（用字说明），依据一句话并写明不代表能不能删；文件类型单独一行；完整路径、两种大小（差得明显时说明压缩、稀疏、云端、硬链接或簇零头）、「扫描时读取」的时间、硬链接、保护/警告、资源管理器/搜索/删除动作；「关联」小节（§9.7）与最大子项。
  - **大文件**：按需查询当前目录最大的 100 个文件，单击定位并转为该项详情。
  - **清理建议**：顶部是清理任务区（§9.8），下面复用风险分组、勾选、条目定位、确认、复制脚本和失败重试，地图同时可见。保留立即释放估算、回收站待释放、手动处理及可能重叠说明，不自动执行清理或外部命令；任务未结束时「立即清理…」禁用。

- **浮层（I）**：右键菜单、确认对话框、提示都是白底浮层卡（圆角 12px、line 描边、阴影 `0 8px 24px rgba(0,0,0,.12)`），危险项红字。
  - 右键菜单：打开（目录）· 在资源管理器中显示 · 在网上搜索 · 复制路径 · 复制名称 · 属性 · 查看详情 · —— · 移到回收站 · 永久删除…；虚拟节点和被拦截的节点对应项置灰并显示原因；方向键、Enter、Esc。
  - 确认对话框：delete / cleanup / elevate 三种。列出条目（mono 路径 + 大小）、合计、警告、被拦截项及原因；永久删除要勾选确认框；主按钮写动作和大小（「移到回收站 · 3.2 GB」「永久删除 · 3.2 GB」「立即清理 · 52.5 GB」「以管理员身份重启」）。确认后把当时核对并冻结的清单和令牌交给任务区（§9.8），对话框立即关闭。
  - 「立即清理…」：勾选的建议在确认框里预检后交给任务区依次执行；command / openSettings 类和清空回收站列为「需要单独处理」，各给一个按钮换成它自己的确认框，不随这一批执行。
  - 提示：顶部居中堆叠，限制高度并可滚动，避开右下清理按钮；success/warn/error 用 safe/review/danger 装饰色的左边条，全部可关闭，error 常驻。

### 9.4 快捷键（I）

| 键 | 作用 |
|---|---|
| Ctrl+F | 聚焦搜索框 |
| Esc | 关闭菜单、对话框、搜索浮层、显示选项和设置弹层；都没开时关闭右侧面板（焦点回到打开它的地方） |
| Enter | 进入选中的目录 |
| Backspace / Alt+← / 鼠标侧键 | 后退 |
| Alt+→ | 前进 |
| Alt+↑ | 上一级 |
| Delete | 删除选中项（回收站，先确认） |
| Shift+Delete | 永久删除（先确认） |
| F5 | 重新扫描 |
| Ctrl+C | 复制选中项路径 |

输入框聚焦时不抢 Backspace / Delete / Ctrl+C；扫描中、对话框打开时和生长树盖着地图时禁用大部分快捷键（生长树开着时 Esc 只关生长树）。Enter / Delete / Ctrl+C 只在空间浏览页对地图的选中项生效；后退、前进、上一级在别的页面也能用，会切回空间浏览。

### 9.5 清理勾选状态（集成负责人在 state.svelte.ts 里实现，H/I/F 共用）

- `app.cleanupSel`：`{ [suggestionId]: { on: boolean; items: number[] | null } }`。建议列表刷新后保留仍存在的勾选；新出现的 safe 建议默认勾上——清空回收站除外（无法恢复，要单独确认），「选中安全项」也不带它。
- 派生值：已选合计、已选条数、每个视图节点是否被勾选覆盖（被勾选建议的 NodeIds 或勾选的 items 对应的视图节点及其子孙）。
- 「复制脚本」调用 `cleanup.script`，把 Script 写进剪贴板并提示「已复制清理脚本（N 项 · X GB）」；NeedsAdmin 时追加「其中有需要管理员 PowerShell 的段落」。

---

### 9.6 最近文件（M4 的独立交付）

- 顶部局部切换的「最近文件」打开这一页（复用 `setPage` 和 `RecentFiles.svelte`，不重复实现查询）。
- 默认最近一周、按占用空间降序、整个扫描范围；可选最近一天、按修改时间降序、仅当前地图目录。大小排序跟随顶栏的占用空间 / 文件大小度量。
- 列出名字、所在目录、主用途、角色、所属对象及证据、大小和修改时间；每次最多 100 条，保留总数并明确提示截断。长路径省略显示，悬停查看完整路径。
- 时间窗口以 `tree.recent.asOfMs` 为基准，页脚注明扫描时刻、实际天数和结果不会自动更新，并明确「修改时间不等于写入量，也不代表新增或下载」；提供重新扫描入口。
- 点击「定位」复用 `reveal`：切回空间浏览、进入父目录、选中文件、打开详情面板显示同一文件。再打开最近文件页保留筛选条件；最近文件页不响应针对地图旧选中项的 Delete、Enter、Ctrl+C 快捷键，文本原生复制照常。
- 请求按序号丢弃过期响应；更换筛选、度量、页面时清掉旧列表；重扫使在途请求失效，取消扫描回旧结果时重新读取；清理完成后刷新列表。加载、失败重试、空结果分别显示。
- UiCheck 使用真实 WebView2 和沙箱扫描验证 1 天 / 7 天、排序、目录限定、地图定位，并保存 1100 / 1440 CSS 像素宽度的截图。此交付不包括 M4 的详情改版、关联模块和横向生长树。

### 9.7 关联与生长树（M4，dise §8）

- **详情里的「关联」**（`RelatedSection.svelte`）：`tree.related` 列同一所属对象的模块（关系类型、证据、角色、大小），可逐条在地图中定位；没有就照原话「尚未识别关联对象」；写明只用于浏览，不扩大清理范围。定位离开时记下返回点，之后的详情给「返回「原对象」」，用过即清，换扫描作废。
- **核对口径**（`related.svelte.ts`）：不调 `scan.summary`（它在后台排写许可，删除进行中会卡住）；同一次扫描里节点 ID 不复用、换扫描前端自己知道、删掉的读详情会失败，所以核对 ID + 路径。详情列表和生长树各用一份 `RelatedData`，各自丢弃旧响应；只在详情面板开着、选中的还是起点、扫描没换时有效。
- **生长树**（`RelatedTree.svelte` + `relatedModel.ts`）：盖在资源地图上，右侧详情保持打开。横向主干从当前对象出发，按时间排文件系统创建、修改和本次扫描（读不到的不补，异常顺序照实排，标题写明暂无历史记录），后接一段虚线的「当前关联（无时间依据）」，关联对象以曲线分支上下交替挂在这一段上；所属对象是单独的胶囊节点，目录、文件节点外形不同，已确认实线、推测虚线。布局只由下标决定：默认 6 条分支，展开更多只往右加列，已有分支不动；超过 50 条可读取更多。缩放（按钮、滚轮、加减号，0.4–2.5 倍）、拖动和方向键平移、适应窗口；节点是按钮，画布下方信息卡给名称、路径、两种大小、角色、关系依据，「以它为中心继续探索」「在地图中定位」，「返回上一个」「回到起点」。Esc 或 × 关闭并把焦点还给打开它的按钮；详情关闭、换选中项、换页或换扫描时自动关。

### 9.8 清理任务区（M5，dise §9.2、§11.3）

- **统一入口**：单项删除、单条清理、批量清理都走确认框；确认后 `state.confirmAction` 把核对并冻结的清单和令牌变成任务交给任务区（`cleanupTasks.svelte.ts`，经 `useTaskRunner` 注入，状态模块不直接依赖它），对话框立即关闭、清理右栏打开；全部任务有确定结果才刷新树，Toast 只发一句收尾。
- **执行**：每项一个稳定的 `operationId`，`cleanup.run` / `node.delete` 带上它；同编号重发复用结果、改了参数被拒。按顺序逐项提交；进度走 `operation.progress`，状态和取消走 `operation.status` / `operation.cancel`（不排写许可）。30 秒没有进度或请求超时就标「状态待确认」并查询，不重发；查到终态才继续。后台明确回了错误、又查不到任务记录时确认没执行，按失败收尾并继续后面的。
- **互斥**：任务没结束或状态待确认时，新的删除、清理、扫描、提权和回启动页都被挡住，提示并亮出任务区。
- **展示**：任务区在清理右栏顶部，可收起（运行中总是展开）；确认目标 / 完成 / 失败 / 跳过分开计，处理过的文件和目录另计；只报后台核实的释放量，未知部分写明，移到回收站的注明要清空回收站后才释放；外部工具只报「已打开」。「停止后续项目」不撤销已完成的；结果和「此前任务」保留到关闭应用。概览的清理格显示任务进度或「上次清理任务已结束」，地图的「清理」开关在任务未结束时带圆点。

## 10. 桥接方法表

| 方法 | 参数（Contracts.cs） | 返回 | 说明 |
|---|---|---|---|
| `app.info` | — | `AppInfoDto` | 版本、是否提权、系统版本、自动扫描参数 |
| `app.relaunchAdmin` | `RelaunchRequest` | `OkDto` | UAC 被拒时 ok=false；成功后进程自行退出 |
| `app.setLanguage` | `LanguageRequest` | `OkDto` | 界面语言 zh-CN / en / ja：后台文字跟着换并存进 settings.json；存不下时 ok=true 带一句提示（这次照样生效）。只读、不排队（§8.8） |
| `drives.list` | — | `DriveInfoDto[]` | |
| `dialog.pickFolder` | — | `PathDto` | 取消时 path=null |
| `scan.start` | `ScanStartRequest` | `ScanStartedDto` | 会取消正在进行的扫描；返回解析后的模式 |
| `scan.cancel` | — | `OkDto` | 不排写许可（M5 起，长删除进行中也能及时取消扫描）；没有扫描可取消时 ok=true、message=「没有正在进行的扫描」。「取消中」和终态走事件（§4.1） |
| `scan.summary` | — | `ScanSummaryDto?` | 删除后重新计算（扫描报告的三类计数保持扫描时的数字，§4.6） |
| `scan.issues` | `ScanIssuesRequest` | `ScanIssuesDto` | 异常清单按需翻页（§4.6）；只读，不排队 |
| `scan.savedSummaries` | — | `SavedSummaryDto[]` | 每个范围最近一次成功扫描的摘要，新的在前（§4.8）；只是上次结果，不能导航或删除；只读，不排队 |
| `tree.view` | `ViewRequest` | `ViewDto` | §10.1 |
| `tree.details` | `DetailsRequest` | `NodeDetailsDto` | |
| `tree.largest` | `LargestRequest` | `FileItemDto[]` | |
| `tree.children` | `ChildrenRequest` | `ChildrenDto` | 目录列表（§10.3）；只读，不排队 |
| `tree.recent` | `RecentRequest` | `RecentFilesDto` | 最近文件（§10.2）；只读，不排队 |
| `tree.owners` | `OwnerUsageRequest` | `OwnerUsageDto` | 按所属对象聚合（§10.2）；只读，不排队 |
| `tree.related` | `RelatedRequest` | `RelatedDto` | 关联详情（§10.2）；只读，不排队 |
| `search.query` | `SearchRequest` | `SearchResultDto` | 索引还没建好时会等待它完成 |
| `cleanup.list` | — | `SuggestionDto[]` | 还没算完时会等待 |
| `cleanup.run` | `CleanupRunRequest` | `OpResultDto` | 可带 `operationId`：先登记再排队，同编号复用结果、改参数拒绝（§9.8） |
| `cleanup.script` | `CleanupScriptRequest` | `CleanupScriptDto` | 生成清理脚本（§6.2） |
| `operation.status` | `OperationRequest` | `OperationStatusDto?` | 任务状态；查不到为 null；只读，不排队 |
| `operation.cancel` | `OperationRequest` | `OperationStatusDto` | 停止后续目标，不回滚已完成的；终态重复取消不变；不排队 |
| `node.openInExplorer` | `NodeRequest` | `OkDto` | |
| `shell.openFolder` | `OpenFolderRequest` | `OkDto` | 启动页按路径打开扫描记录里的位置（§4.8）；不是完整路径、不在记录里的拒绝；位置已经不在时 ok=false 加一句人话 |
| `node.searchWeb` | `SearchWebRequest` | `OkDto` | |
| `web.search` | `WebQueryRequest` | `OkDto` | 顶栏外部搜索：引擎只认 google / bing，只发用户提交的词（`ShellOps.BuildQueryUrl`）；不排写许可 |
| `node.properties` | `NodeRequest` | `OkDto` | 系统属性对话框 |
| `node.checkDelete` | `NodeIdsRequest` | `DeleteCheckDto` | |
| `node.delete` | `DeleteRequest` | `OpResultDto` | 可带 `operationId`，同 `cleanup.run` |

`PurposeBytes`（汇总和详情都是）只累计叶子（含「未统计」虚拟叶子，不含硬链接副本），不含目录自身占用，所以 MFT 模式下之和比已统计占用略小，Walk 模式下相等。`DriveInfoDto.SupportsMft` 表示提权后能深度扫描（本地 NTFS），`CanMft` 表示现在就能。

参考图改版新增的字段：`ViewNodeDto.U`（用途）、`NodeDetailsDto.U` 与 `PurposeBytes`、`ScanSummaryDto.PurposeBytes`、`SuggestionDto.DisplayPath`；M1b 加了 `NodeDetailsDto.Role`（角色编号）、`Evidence`（用途证据）、`Owner`（`OwnerDto`：种类、名字、根节点 ID、根路径、这一处归属的证据；没有就是 null）。它们在 C# 里是带默认值的尾部参数，所有构造处都必须显式填真实值。证据等级和所属对象种类是枚举，按 camelCase 字符串传（`"confirmed"`、`"packageManager"`）；它们和角色的编号、名称由 `web/tests/purposes.test.ts` 直接读 `Roles.cs` 比对。

事件：`tree.changed`（`TreeChangeDto`，扫描之后磁盘上的变化，§4.9）；`scan.progress`（`ScanProgressDto`：开始时、每次阶段变化、取消被接受时各立即一条，其余每 120 ms 一条；`cancelling=true` 表示取消已被接受、后台还没停下）、`scan.done`（`ScanSummaryDto`）、`scan.error`（`ScanErrorDto`，取消时 `cancelled=true`，扫描线程全部退出后才发）。终态事件之后不再有进度。

M1c 加了扫描报告：`ScanSummaryDto.ReparseSkipped` 改名 `LinksSkipped`，尾部加 `MountPointsSkipped`、`Errors`、`GapParts`（`GapPartDto`：种类、字节数、依据），`ScanProgressDto` 尾部加 `Cancelling`，新增 `ScanIssuesRequest` / `ScanIssuesDto` / `ScanIssueDto`。`ScanIssueKind`（`denied | link | mountPoint | error`）和 `GapKind`（`unconfirmed | ntfsMetadata`）按 camelCase 字符串传；它们、清单上限和这五个 DTO 的字段名由 `web/tests/scanreport.test.ts` 直接读 `Contracts.cs`、`ScanCounters.cs` 比对，模拟后端少一个字段、多一个字段都会失败。

M1e 在 `ScanSummaryDto` 尾部加了 `CompletedAtMs`（扫描完成时刻，Unix 毫秒；`tree.recent` 的 `asOfMs` 就是它，删除后刷新汇总不变），新增 `SavedSummaryDto`（§4.8）；字段名同样由 `scanreport.test.ts` 比对。`scan.done` 发出时这次的摘要已经存好。

### 10.1 `tree.view` 的裁剪算法（TreeQueries.BuildView）

- 输入：视图根、度量、节点预算 `MaxNodes`（默认 12000）。
- 优先级展开：最大堆按值排序；先放入根；每次弹出值最大的目录，把它的子节点（跳过已删除和值为 0 的）按值降序加入结果；值小于 `total / (MaxNodes * 4)` 的子节点，以及单个目录超过 1500 个之后的子节点，合并成一个聚合节点「其余 N 项」（`id=-1`，F 含 Aggregate，K=0）；子目录推入堆。结果数达到预算后停止。
- 有子节点但没有被展开的目录标记 Unexpanded，前端画成可以继续放大的目录块。
- 补充（E2 实现时确认）：聚合节点按值排进兄弟里（并列时排在实际节点后面）；「其余 N 项」的 N 只数值 > 0 且被合并的子项；放不下任何单独子项的目录保持 Unexpanded，只有视图根例外会放一个孤零零的聚合节点；结果总数严格 ≤ MaxNodes（夹在 100..50000）；同值兄弟按 ID 升序（FileTree 的子节点顺序也是这样）。
- 输出：扁平数组，父节点一定排在子节点前面（`p` 是父节点在数组里的下标，根是 -1）；同一父节点的子节点按 `v` 降序；每个父节点的子节点之和（含聚合节点）等于父节点的值（目录自身的 alloc 也合进聚合节点，否则面积对不上）。
- 节点字段：`id`、`p`、`n`（名字）、`v`（值）、`f`（flags）、`k`（扩展名类别）、`c`（未删除的直接子节点总数）、`u`（用途，聚合节点取父节点的用途）。

### 10.2 最近文件、按所属对象聚合、关联详情（M1d，TreeQueries）

三个都只读树：桥接层先在锁外解析参数，再进 `tree.Sync` 查询，放在 `ReadOnly` 集合里不排队（删除进行中照样及时返回）。作用域、节点不存在或已删除一律 `ArgumentException("节点不存在或已删除")`；`Limit` 都夹在 1..1000；枚举按 camelCase 字符串传。字段名和枚举由 `web/tests/mock.test.ts` 直接读 `Contracts.cs` 比对。

- **最近文件** `tree.recent {days=7, scopeId=null, sort="size", metric="alloc", limit=100}` → `{asOfMs, days, total, items}`（dise §6）。
  - 时间基准 `asOfMs` 是**这棵树那次扫描的完成时刻**，不是查询当下：`ScanService` 在结果换上的那一刻（和 `_tree` 同一把锁里）记 `DateTimeOffset.UtcNow`，写进汇总的 `CompletedAtMs`，桥接层用 `CompletedAtMs(tree)` 按树身份取，拿到树之后又换过扫描就报错，不拿别的扫描的时刻充数。这样程序开着过几天，扫描时算最近的文件也不会被挤出窗口；同一份结果什么时候查都一样。
  - 窗口是闭区间 `[asOfMs − days × 86 400 000, asOfMs]`，`days` 夹在 1..3650、应答带回夹过的值；晚于基准的（时钟不准留下的未来时间）和修改时间未知的不算。只出文件：目录、虚拟节点、硬链接副本（一组只出计入的那一份）不算；作用域是文件时含它自己。
  - `sort`：`size` 按度量降序，`modified` 按修改时间新的在前，并列都按 ID 升序；`total` 是窗口内总数，不受 `limit` 影响。
  - 条目：`id, name, path, dirPath`（所在目录）、`size, alloc, modifiedMs, k, f, u`（主用途）、`role`、`owner`（`OwnerDto`，没有为 null）。修改时间只说明最后一次被改是什么时候，**不等于写入量，也不代表新增、下载或增长**——界面别这么说。
- **按所属对象聚合** `tree.owners {scopeId=null, metric="alloc", limit=100}` → `OwnerUsageDto`（dise §5.3 的「应用占用」维度）。
  - 只算当前范围（字段名里的 `InScope`），不是对象在整台机器上的总占用。每个字节只记一次：节点归往上最近的那个根所属的对象，目录自身的占用也算；硬链接副本不算；计入那一份所在的硬链接组里，活着的链接不全归同一个对象（含一边不归任何对象）时记进 `shared`，不硬塞给某一方。
  - 两种度量各自 `owned + shared + unowned = total`，`total` 是作用域的计入值，和地图上的大小一致；`owned` 是全部对象之和，不受 `limit` 影响；`ownerCount` 是碰到范围的对象个数（计入为 0 的也算），大于 `items.length` 说明截断了。
  - 每个对象：种类、名字、对象本身的证据（几处来源里最强的）、两种计入字节、`roleBytesInScope`（长度 `Roles.Count`、按请求度量，之和等于计入大小）、`roots`（每处根在范围里计入的字节，扣掉嵌在里面的所有别的根；作用域整个落在对象里时，根在作用域上方，和 `OwnerDto.RootId` 同义）。对象按度量降序，并列按名字（不区分大小写）再按种类；根按度量降序，并列按 ID。
- **关联详情** `tree.related {nodeId, metric="alloc", limit=50}` → `{nodeId, owner, total, items, note}`（dise §8.1）。
  - 只出有依据的三种关系，不按名字像、时间近去猜：`root`（节点所在那处根，节点自己就是根时没有）→ `otherRoot`（同一对象的另几处根，按度量降序）→ `roleModule`（所在那处根自己的地盘里——嵌在里面的别的根整棵跳过——角色和上级目录不同的目录，不含节点自己和包含它的目录，角色未知的不算；每种角色只列最大的 3 个，组按组里最大的那个降序）。并列一律按 ID（角色组按角色编号）。
  - 证据：`otherRoot` 取两处根里弱的那个，`root` 和 `roleModule` 用所在那处根的证据。条目的 `size`/`alloc` 是节点在地图上的大小（整棵子树）。
  - `owner` 为 null 时 `items` 一定为空；`items` 为空时 `note` 是「尚未识别关联对象」，否则为 null；`total` 不受 `limit` 和每种角色 3 个的影响。
  - **关联只用于浏览和定位，不扩大清理范围**：属于同一个应用不等于可以整组删除，清理建议和删除保护都不读这里。
- mock 同口径但简化：基准取假数据的 `NOW`（假数据的时间都按它生成），聚合和关联用 mock 自己的简化所属对象数据；字段、取值范围、排序、夹值和守恒和真后端一致。

### 10.3 目录列表（`tree.children`，M3，TreeQueries.Children）

- `tree.children {nodeId, metric="alloc", sort="size", offset=0, limit=200}` → `{nodeId, metric, sort, total, offset, items}`：一个目录的全部未删除直接子项，先按口径全排序再分页；`offset` 夹在 0..total，`limit` 夹在 1..500；节点不存在或已删除抛 `ArgumentException`。`LiveChildren` 是树里共用的数组切片，先复制再排序。
- `sort`（`ChildSort`，camelCase 字符串）：`name` 目录在前、名字按 `OrdinalIgnoreCase`；`size` 按度量的计入值降序；`modified` 修改时间新的在前、未知（0）排最后；并列一律按 ID 升序。名字排序不是「自然排序」（`module-10` 排在 `module-2` 前面），ponytail：要像资源管理器那样排再换比较器。
- 条目 `ChildEntryDto`：`id, name, isDir, value`（按请求度量的计入值，硬链接副本计 0，和地图同口径）、`size, alloc, modifiedMs`（未知为 null）、`k, f, u, childCount`（目录的未删除直接子项数，文件为 0）。
- queries 自检用父指针重建的子项（不走 `LiveChildren`）全排后逐页比对三种排序、两种度量和九个目录，另验夹值、已删除节点和已删除子树不出现；mock 同口径，`mock.test.ts` 比对字段和枚举。

---

## 11. 测试与验收

- `tools/DiskLens.Check`：`dotnet run --project tools/DiskLens.Check -- <area>`，area 为 `tree`、`mft`、`walk`、`search`、`queries`、`purpose`、`cleanup`、`delete`、`service`、`summaries`、`all`。每个 area 是一个静态类的 `Run(CheckContext)`，用 `ctx.Equal`、`ctx.True` 断言，失败时打印并让进程返回非 0。
- 测试沙箱：`<仓库>/.sandbox/<area>/`（git 忽略）。只能在这里创建和删除文件，**绝不能碰沙箱以外的任何文件**。
- 必须覆盖：
  - tree：聚合、硬链接组的计入与转移、MarkDeleted 的净释放、FindByPath、GetPath、Seal 后排序。
  - mft：用代码构造的 FILE 记录（含 fixup、常驻和非常驻 `$DATA`、压缩/稀疏、ADS、多个 `$FILE_NAME`、扩展记录、损坏记录、联接点和卷挂载点的重解析数据）验证解析器，假卷上核对损坏记录、孤立子树、链接和挂载点进了各自的计数和清单、阶段回调各一次；`check mft --volume C:` 在管理员下跑整卷并与 `TotalClusters - FreeClusters` 对账（这一步由用户点 UAC 后执行）。
  - walk：在沙箱里构造已知大小的文件、硬链接（`CreateHardLinkW`）、junction（`mklink /J`）、稀疏文件（`FSCTL_SET_SPARSE`）、压缩文件（`FSCTL_SET_COMPRESSION`），断言 size 和 alloc 与预期一致、硬链接只计一次、junction 没有进入；指向不存在的卷的挂载点（非管理员设得上、进不去、删得掉）和 junction 分开计；icacls 按当前用户 SID 拒绝列目录，拒绝访问的计数、清单和 ScanService 汇总对得上，用完恢复；`check walk --volume D:` 打印对账结果。
  - search：语法解析的各种情况、通配符、过滤、Top-K 排序、作用域；用 200 万节点的合成树测性能。
  - queries：视图裁剪的不变量（父在子前、子之和等于父、预算）、详情、扩展名统计。
  - cleanup：用合成树测每条规则的命中，以及 command 白名单。
  - delete：沙箱里的回收站删除、永久删除、占用文件被跳过、junction 只删链接、保护规则拦截。
  - summaries：摘要保存/读取往返（含换实例）、先落盘再发完成事件、每个范围只留最近一次（不分大小写、晚落盘的旧结果不覆盖）、取消和失败不覆盖、32 个范围和 256 KB 上限丢最旧的、原子替换（残留临时文件不读、别的句柄开着正式文件时要么整份换上要么完整保留）、七种坏文件当没有并留底一个 .bad、坏文件在时照样扫描并存上、两个实例并发保存、写盘失败不影响扫描。
  - service：汇总带完成时刻且刷新不变、汇总刷新的耗时和锁、扫描报告的计数刷新后保持扫描时的数字、未解释容量的拆分（只读问系统盘 `$MFT`）、清单上限与翻页（并发记 10005 条）、换扫描后旧清单失效、小扫描也收到每个阶段的进度、取消中与终态的先后（只读扫 Windows 目录途中取消）。
- 前端：`pnpm check`（svelte-check 零错误）、`pnpm test`（`node --test`，测 squarify 不变量：面积比例、不重叠、不越界；`scanreport.test.ts` 读 C# 源文件比对扫描报告的枚举、清单上限和 DTO 字段名（含 `SavedSummaryDto`），再验 mock 的计数、翻页、取消流程和已保存摘要的口径；`mock.test.ts` 同样比对最近文件、所属对象聚合、关联详情的 DTO 和枚举，再验三个查询的窗口、守恒、排序和夹值）、`pnpm build`。浏览器里用模拟后端预览（`pnpm dev`，没有 `chrome.webview` 时自动启用 mock）。

---

## 12. 并行开发规则

- 每个开发者只改自己名下的文件（下表）。需要改契约（`Contracts.cs`、`Purposes.cs`、`FileTree.cs`、`DtoFactory.cs`、`ScanCounters.cs`、`types.ts`、`api.ts`、`state.svelte.ts`、`mock.ts`、`purposes.ts`、`categories.ts`、`theme.css`）时**不要自己改**，在汇报里写明需要的改动，由集成负责人统一处理；在此之前用自己文件里的私有辅助代码过渡。
- 每个模块都必须能单独编译：骨架里所有公开 API 都有实现或桩，替换自己的那部分即可。
- 不准引入新的 NuGet 包；前端只允许已经装好的依赖。
- 代码注释：只写「为什么」，不写「是什么」，点到为止。

| 代号 | 负责 | 文件 |
|---|---|---|
| A | MFT 深度扫描（§4.4） | `src/DiskLens.Core/Scan/Mft*.cs`、`tools/DiskLens.Check/Mft*.cs` |
| B | 常规扫描与卷信息（§4.3） | `src/DiskLens.Core/Scan/Walk*.cs`、`src/DiskLens.Core/Scan/ScanNative.cs`、`src/DiskLens.Core/Volumes.cs`、`tools/DiskLens.Check/Walk*.cs` |
| C | 搜索（§5） | `src/DiskLens.Core/Search/*`、`tools/DiskLens.Check/Search*.cs` |
| P | 用途分类（§4.7） | `src/DiskLens.Core/Classify/*`、`tools/DiskLens.Check/Purpose*.cs`（并在 Check 的 Program.cs 注册 `purpose`） |
| D1 | 清理建议与脚本（§6） | `src/DiskLens.Core/Cleanup/*`、`tools/DiskLens.Check/Cleanup*.cs` |
| D2 | 删除、保护与外壳操作（§7、R2、R3） | `src/DiskLens.Core/Shell/*`、`tools/DiskLens.Check/Delete*.cs` |
| E1 | 宿主、桥接、扫描编排（§2、§4.1、§4.6、§10） | `src/DiskLens/**`、`src/DiskLens.Core/Scan/ScanService.cs`、`tools/DiskLens.UiCheck/**` |
| E2 | 视图与详情查询（§10.1） | `src/DiskLens.Core/Query/*`、`tools/DiskLens.Check/Query*.cs` |
| F | treemap（§8.5） | `web/src/lib/treemap*`、`web/src/lib/treemap/**`、`web/src/components/Treemap*.svelte`、`web/tests/treemap*` |
| G | 启动页、扫描中（§9.1、§9.2） | `web/src/components/Start*.svelte`、`Scan*.svelte`、`Drive*.svelte` |
| H | 右栏：清理建议、详情、大文件（§9.3） | `web/src/components/RightPanel*.svelte`、`Recommend*.svelte`、`Details*.svelte`、`Largest*.svelte` |
| I | 应用框架：顶栏、搜索、统计卡、资源地图卡外框、图例、右键菜单、对话框、提示、快捷键（§9.3、§9.4） | `web/src/App.svelte`、`TopBar*`、`Search*`、`Stat*`、`Explorer*`、`Legend*`、`ContextMenu*`、`Confirm*`、`Toast*`、`Settings*` |
| 集成负责人 | 契约、状态、模拟后端、主题、合并 | 上面列出的契约文件 + `docs/**` |
