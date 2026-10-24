# CAD 图纸加密保护插件 (AutoCAD & 中望CAD 双平台支持)

基于 AutoCAD & 中望CAD (ZWCAD) .NET API 开发的图纸防盗、加密与只读锁定插件，具备防炸开、防修改、防复制、防打印、防删除和批量处理能力。

一套核心代码同时支持 **AutoCAD** 和 **中望CAD (ZWCAD)** 两个主流平台。

---

## 一、目录结构与产物

```text
D:\安装程序\CAD图纸加密插件lock\CAD图纸加密插件lock\
│
├── CadLockPlugin.sln              # Visual Studio 解决方案 (包含 AutoCAD 和 ZWCAD 两个工程)
├── CadLockPlugin.dll              # 【AutoCAD 版】编译生成的可直接使用插件
├── CadLockPlugin.ZWCAD.dll        # 【中望CAD 版】编译生成的可直接使用插件
├── README.md                      # 开发与维护指南
├── claude.md                      # 逆向剖析与编程全过程记录文档
├── lockdwg.dll                    # 原插件备份
├── 使用说明.txt                   # 原插件说明文档
│
├── src/                           # 共享核心源代码
│   ├── CadLockPlugin.csproj       # AutoCAD 项目工程文件
│   ├── CadLockPlugin.ZWCAD.csproj # 中望CAD 项目工程文件
│   ├── CadLockOptions.cs          # 参数配置模型
│   ├── CadLockEngine.cs           # 核心加密算法引擎 (无名块、Explodable=false、递归嵌套)
│   ├── CadLockEvents.cs           # 底层安全拦截钩子 (反复制 DeepClone、反删除 ObjectErased)
│   ├── CadLockCommands.cs         # CAD 交互命令 (LOCK、BATLOCK、STOPEX)
│   └── UI/
│       └── LockForm.cs            # WinForms 图形化交互配置面板
│
└── bin/                           # 编译输出目录
    ├── Release/CadLockPlugin.dll
    └── ZWCAD/Release/CadLockPlugin.ZWCAD.dll
```

---

## 二、使用指南

### 1. AutoCAD 中加载
* 打开 AutoCAD；
* 命令行输入：`NETLOAD`；
* 选择根目录下的 `CadLockPlugin.dll`；
* 命令行输入：`LOCK`。

### 2. 中望CAD (ZWCAD) 中加载
* 打开 中望CAD（ZWCAD 2020 ~ 2024 / 2025）；
* 命令行输入：`NETLOAD`；
* 选择根目录下的 `CadLockPlugin.ZWCAD.dll`；
* 命令行输入：`LOCK`。

---

## 三、命令功能一览

| 命令 | 说明 |
| :--- | :--- |
| `LOCK` | **核心主命令**。弹出图形化配置面板，可选择“选择对象加密”“全图自动加密”“批量加密(图纸/文档)”“解锁当前图纸”“批量文件解锁”，并设置嵌套次数、分块数、各防护项与是否替换原文件。 |
| `BATLOCK` | **批量加密快捷命令**。直接弹出文件选择框（支持多选 `.dwg` 与 Excel/Word 文档），后台批量离线处理。默认输出 `文件名_lock`；勾选「直接替换原文件」时直接覆盖原文件。 |
| `UNLOCK` | **解锁命令**。把当前图纸中的加密块还原为普通图元，并移除加密标记与防护钩子，执行后需保存图纸。 |
| `BATUNLOCK` | **批量解锁命令**。多选 `.dwg` 文件后台批量还原，默认输出 `文件名_unlock.dwg`，勾选替换时直接覆盖原文件。 |
| `STOPEX` | **维护命令**。在当前会话中卸载防删除、防复制的事件拦截钩子。 |

> 说明：批量加密/解锁会同时处理**模型空间与全部布局(图纸空间)**，避免“整图在布局里”的图纸批量加密后毫无变化。
> 未找到可加密图元的文件会被明确标记为「跳过」并给出原因，不再静默报告成功。

---

## 四、防护机制说明（请务必了解）

| 选项 | 实现方式 | 持久化 |
| :--- | :--- | :--- |
| 不可炸开（核心） | 把图元封装进多层嵌套的匿名块，并设置 `Explodable = false`，CAD 的 EXPLODE / XPLODE 均拒绝执行，内容无法被编辑或提取 | 随图纸保存 |
| 不可选中 | 把加密块放到专用图层 `ACAD_LOCK_LAYER` 并锁定该图层；图层被手动解锁时，插件在命令结束/空闲时自动恢复锁定 | 是，选项写入图纸 NOD，重新打开自动恢复 |
| 不可打印 | 把该图层设为不打印，打印/导出 PDF 时加密内容为空白 | 同上 |
| 不可复制 | 拦截 `Database.BeginDeepClone`，COPYCLIP / WBLOCK / 跨图拖放都会被阻止 | 同上，重新打开自动重新挂钩 |
| 不可删除 | 拦截 `Database.ObjectErased`，被删除的加密对象在命令结束后自动恢复 | 同上 |

**能力边界（必须知道）**：DWG 本身没有“只读图元”这种标志位，所以

* 加密内容靠“不可炸开的嵌套块”保护 —— 这部分是硬约束，别人拿到图纸也 **看不到、改不了** 里面画的线；
* “不可选中 / 不可删除 / 不可复制” 是 **在装有本插件的 CAD 里生效** 的运行期防护。别人用一台没有加载插件的 CAD 打开，仍然可以把块整体移动或删除（但依然炸不开、看不到内容），也可以用 LISP/ARX 强行把图层解锁；
* 因此本插件解决的是“**图纸内容被复制、修改、外流**”，不是“文件绝对不可操作”。如果需要“没密码就打不开”级别的保护，应改用 DWG 密码或专用加密格式。

---

## 五、Excel / Word 批量处理

批量加密窗口除 DWG 外，还支持 `.xlsx / .xls / .xlsm` 与 `.docx / .doc / .docm`：

| 类型 | 处理内容 | 效果 |
| :--- | :--- | :--- |
| Excel | **清除页眉页脚里的签名**（姓名/公司名文字，以及 `&G` 引用的签名图片）；删除文档数字签名；把所有公式单元格替换为当前计算结果（公式不再保留）；保护每个工作表与工作簿结构 | 打开后只能查看，文字与数字都改不了，也无法增删工作表；页眉页脚里看不到签名 |
| Word | **清除所有节页眉页脚里的签名**（文字、嵌入式与浮动签名图片）；删除文档数字签名；设置只读保护（wdAllowOnlyReading） | 打开后只能查看，不能编辑文字与数字；页眉页脚里看不到签名 |

* 签名图片是**从文件里真正删除**的（同时清空 `*HeaderPicture.Filename` 并保存），处理后的 xlsx/docx 里不再有 `xl/media`、`word/media` 残留图片，不是只隐藏；
* 保护口令固定为 `CadLock@2024`（写在程序内），需要手工解除时可到 Excel「审阅-撤销工作表保护」/ Word「审阅-限制编辑-停止保护」输入该口令；
* 勾选「直接替换原文件」时覆盖原文件，否则另存为 `文件名_lock.xlsx / 文件名_lock.docx`；
* 批量解锁会把工作表/文档保护解除（不还原公式，公式转值不可逆）；
* 依赖本机已安装 Microsoft Office（通过 COM 调用，未安装时该文件会被标记为失败并给出提示）。

---

## 六、双平台维护与构建说明

### 1. 一键编译双平台
在根目录执行以下命令，即可同时生成两个平台的插件：
```powershell
dotnet build CadLockPlugin.sln -c Release
```
或者在 Visual Studio 中打开 `CadLockPlugin.sln`，点击“生成解决方案”即可。

### 2. 发布产物
根目录的 `CadLockPlugin.dll` / `CadLockPlugin.ZWCAD.dll` 是随仓库发布的 Release 产物（v1.1.0 起入库）。重新编译后请把这两个文件一并提交，避免源码与发布件脱节。

### 3. 一键发布脚本

仓库根目录的 `release.sh`（Git Bash 执行）把发版流程串起来：

```bash
./release.sh 1.2.0                    # 完整发布
./release.sh 1.2.0 --dry-run          # 只演练，不改仓库、不推送
./release.sh 1.2.0 --notes-file n.md  # 用自定义发布说明
```

它会依次完成：

1. 把 `x.y.z` 写入两个 csproj 的 `AssemblyVersion` / `FileVersion`；
2. `dotnet build` 双平台 + 冒烟测试工程；
3. 复制两个 DLL 到根目录并打印 SHA256（同一份校验值会写进 Release 说明）；
4. 提交版本号与产物（`chore(release): vX.Y.Z 编译产物`）；
5. 打附注标签 `vX.Y.Z` 并推送分支与标签；
6. `gh release create` 创建 Release 并上传两个 DLL（说明由上个标签以来的提交自动汇总）。

前置条件：`dotnet`、`gh`（已 `gh auth login`）、工作区无未提交改动、标签未被占用；任一不满足会直接报错退出。

### 4. 路径配置
* AutoCAD 依赖路径在 [src/CadLockPlugin.csproj](file:///D:/安装程序/CAD图纸加密插件lock/CAD图纸加密插件lock/src/CadLockPlugin.csproj) 中的 `<AutoCADDir>` 配置（默认 `D:\Program Files\Autodesk\AutoCAD 2020`）；
* 中望CAD 依赖路径在 [src/CadLockPlugin.ZWCAD.csproj](file:///D:/安装程序/CAD图纸加密插件lock/CAD图纸加密插件lock/src/CadLockPlugin.ZWCAD.csproj) 中的 `<ZwCadDir>` 配置（默认 `D:\Program Files\ZWSOFT\ZWCAD 2024`）。
