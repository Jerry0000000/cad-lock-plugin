# claude.md — CadLockPlugin 开发与维护指南

> 适用版本：v1.2.0（2026-10-24 对照源码、构建与发布脚本核对）
>
> 本文档面向后续维护者与编码助手，重点说明**当前实现、修改约束和验证流程**。
> 原始 `lockdwg.dll` 的逆向分析保留在[附录 A](#附录-a原始-lockdwg-dll-逆向记录历史)，仅作背景资料，不代表当前实现。
> 行为、命令、数据格式或构建方式发生变化时，必须同步更新本文档和 `README.md`。

## 目录

- [一、项目速览](#一项目速览)
- [二、环境、构建与加载](#二环境构建与加载)
- [三、不可破坏的约定](#三不可破坏的约定)
- [四、代码地图](#四代码地图)
- [五、核心流程与实现细节](#五核心流程与实现细节)
- [六、测试与发布](#六测试与发布)
- [七、兼容性与升级](#七兼容性与升级)
- [八、已知边界与常见问题](#八已知边界与常见问题)
- [九、维护检查清单](#九维护检查清单)
- [附录 A：原始 lockdwg.dll 逆向记录（历史）](#附录-a原始-lockdwg-dll-逆向记录历史)

---

## 一、项目速览

| 项目 | 内容 |
| :--- | :--- |
| 定位 | CAD 图纸“封装锁定 + 运行期防护”插件，不是文件级加密工具 |
| 平台 | AutoCAD（net48 / x64）与中望CAD ZWCAD（net48 / x64），共享同一套源码 |
| 产物 | `CadLockPlugin.dll`（AutoCAD）、`CadLockPlugin.ZWCAD.dll`（中望CAD） |
| 仓库 | `https://github.com/Jerry0000000/cad-lock-plugin`，源码 v1.2.0，最新发布标签 `v1.1.0` |
| 运行时 | .NET Framework 4.8；按 AutoCAD 2020 / ZWCAD 2024 程序集构建通过（运行时行为需手工回归） |
| 批量能力 | DWG、Excel（`.xlsx/.xls/.xlsm`）、Word（`.docx/.doc/.docm`） |
| 界面 | WinForms 模态对话框 `LockForm`，通过 CAD 宿主 `ShowModalDialog` 弹出 |

### 关键结论（先记住）

1. **“加密”本质是匿名块封装**：把图元克隆进多层嵌套的匿名块（`*U`），设置 `Explodable = false`，再把块参照放到专用图层 `ACAD_LOCK_LAYER`。几何没有被加密，只是无法通过 EXPLODE 取出。
2. **本项目是可逆的**：`UNLOCK` / `BATUNLOCK` 会逐层拆回普通图元。旧版说明里的“不可逆”不再适用（Office 的“公式转值”除外，那一步确实不可逆）。
3. **只有“不可炸开”是文件级硬约束**；“不可选中、不可更改、不可打印、不可复制、不可删除”都是**装有本插件时的运行期防护**，防护选项写在图纸 NOD 中，重新打开会自动恢复。
4. 一套源码通过 `#if ZWCAD` 编译出两个平台版本，**禁止复制出第二份业务代码**。
5. **兼容旧版 `lockdwg.dll` 图纸**：识别 `*ACADLOCKLAYER` / `ACADLOCKLAYER` 等旧图层；旧 `_lock.dwg` 只剩空 MINSERT 时，批量解锁会尝试用同名 `.bak` 备份恢复（详见 7.5）。

---

## 二、环境、构建与加载

### 1. 构建环境（已实测）

| 组件 | 版本/路径 |
| :--- | :--- |
| 操作系统 | Windows 11 |
| .NET SDK | 8.0.416（可用 SDK 8 编译 net48 项目） |
| AutoCAD | 2020，`D:\Program Files\Autodesk\AutoCAD 2020` |
| 中望CAD | 2024，`D:\Program Files\ZWSOFT\ZWCAD 2024` |
| 目标框架 | `net48`，`x64`，`LangVersion=latest` |

### 2. 构建命令

```bash
# 双平台 Release（实测 0 警告、0 错误）
dotnet build CadLockPlugin.sln -c Release

# 冒烟测试工程
dotnet build tests/CadLockSmoke/CadLockSmoke.csproj -c Release
```

依赖路径在工程文件中以属性形式提供，可在命令行覆盖，不需要改文件：

```bash
dotnet build CadLockPlugin.sln -c Release \
  -p:AutoCADDir="D:/Program Files/Autodesk/AutoCAD 2024" \
  -p:ZwCadDir="D:/Program Files/ZWSOFT/ZWCAD 2025"
```

> AutoCAD/ZWCAD 程序集必须保持 `<Private>false</Private>`，否则会把 CAD 核心 DLL 复制到输出目录，运行时可能加载到错误版本。

### 3. 输出位置

| 工程 | 输出 |
| :--- | :--- |
| `src/CadLockPlugin.csproj` | `bin/Release/CadLockPlugin.dll` |
| `src/CadLockPlugin.ZWCAD.csproj` | `bin/ZWCAD/Release/CadLockPlugin.ZWCAD.dll` |
| `tests/CadLockSmoke/CadLockSmoke.csproj` | `bin/Smoke/CadLockSmoke.dll`（同时复制插件 DLL） |

仓库根目录的 `CadLockPlugin.dll` / `CadLockPlugin.ZWCAD.dll` 是**发布产物**，普通 `dotnet build` 不会更新它们；只有 `release.sh` 会把新产物复制到根目录并提交。源码与根目录 DLL 可能暂时不同步，发版时以脚本流程为准。

### 4. 加载与命令

在 CAD 命令行执行 `NETLOAD`：

- AutoCAD 选择根目录（或 `bin/Release`）的 `CadLockPlugin.dll`；
- 中望CAD 选择 `CadLockPlugin.ZWCAD.dll`。

| 命令 | 行为 | 备注 |
| :--- | :--- | :--- |
| `LOCK` | 打开 `LockForm` 配置面板 | 选择对象加密 / 全图加密 / 批量 / 解锁当前 / 批量解锁 |
| `BATLOCK` | 直接弹出多文件选择框批量加密 | 使用固定参数：`SplitCount=100`、`RepeatCount=10`、`DisableSelect=true`，不启用打印/复制/删除防护 |
| `UNLOCK` | 解锁当前图纸 | 拆块、移除 NOD 标记、卸载当前图纸的防护钩子，需要用户保存 |
| `BATUNLOCK` | 批量解锁 DWG/Office | 默认输出 `文件名_unlock` 副本；旧版 `lockdwg.dll` 的 `_lock.dwg` 会尝试同名 `.bak` 恢复 |
| `STOPEX` | 卸载当前图纸的防护事件钩子 | 不改变图纸内容，仅停止本进程内的拦截 |

> “全图自动加密”只处理**当前空间**（模型空间或当前布局）。要覆盖模型空间 + 全部布局，请用批量加密（`CadLockEngine.LockAllSpaces`）。

---

## 三、不可破坏的约定

修改代码前逐条确认；这些都是踩过坑后固化下来的约束。

1. **双平台共享源码**
   平台差异只允许出现在 `using`、类型别名和 `#if ZWCAD` 中。新增任何 CAD API 调用前，先确认 ZWCAD 对应程序集是否有同名成员；无法确认时把差异封装到条件编译分支，不要复制整个方法。

2. **工程配置**
   两个 csproj 都必须保持 `net48`、`x64`、`PlatformTarget=x64`、`Private=false`；ZWCAD 工程额外定义 `ZWCAD` 常量并输出 `CadLockPlugin.ZWCAD.dll`。

3. **文档锁 + 事务**
   所有对当前文档的写操作必须包在 `using (doc.LockDocument())` + `StartTransaction()` 中。批处理使用 `new Database(false, true)` 侧数据库，不打开 Document、不弹 CAD 界面。

4. **过滤是安全边界**
   `CadLockEngine.FilterLockableEntities` 必须继续排除：已擦除/无效对象、非 `Entity`、`Viewport`、已在加密图层上的对象、位于锁定图层上的对象。`LockEntities` 内部必须再过滤一次，不能假设调用方已经过滤。

5. **删除拦截只能延迟恢复**
   在 `ObjectErased` 回调里**不要** `UpgradeOpen()`/`Erase(false)`：对象已经被标记擦除，直接操作会抛 `eWasErased`。正确做法是登记到 `PendingUnerase`，在 `CommandEnded` 或 `Application.Idle` 中用新事务、`OpenMode.ForWrite` + `openErased: true` 恢复。

6. **NOD 是唯一持久化真相**
   加密标记与保护选项都写在图纸的 `ACAD_LOCK` 字典中。只有真正创建了加密块才允许写标记/选项；解锁成功后才允许 `RemoveLockMark`。`OPTIONS` Xrecord 的字段顺序不可调整，新字段只能追加到末尾，并让旧记录（字段不足）有默认值。

7. **解锁顺序**
   必须先 `ResetLockLayer` 解除图层锁定，再拆块（锁定图层上的对象无法被擦除）。只拆“位于加密图层上的匿名块”，命名块一律不动；空间必须以 `OpenMode.ForWrite` 打开；逐层循环必须有上限（当前 256），且单空间/单块失败不能影响其他对象。

8. **解锁后必须卸载钩子**
   `UNLOCK` 在拆块后要调用 `CadLockEvents.UnhookEvents(db)`；否则当前进程里残留的 DeepClone/ObjectErased 钩子还会继续拦截普通图元。

9. **事件状态用闭包捕获**
   AutoCAD 事件回调的 `sender` 不保证是注册时的托管 `Database` 实例，`HookState` 通过闭包直接捕获；跨实例识别同一图纸时用 `SameDatabase`（引用比较失败后退化为按文件名比较）。不要在回调里依赖 `sender` 反查。

10. **图层防护要尊重“合法解锁”**
    只有当图纸仍然存在 `ACAD_LOCK` 标记时，才允许把被手动解锁/改成可打印的加密图层恢复；用户执行 `UNLOCK` 后标记被删除，绝不能再锁回去。

11. **界面必须走宿主模态**
    `LockForm` 只能使用 WinForms 类型，不能引用 CAD 命名空间；弹出时使用 `CadApp.ShowModalDialog(form)`，不要直接 `form.ShowDialog()`，否则会抢占 CAD 主窗口焦点、导致快捷键失效。

12. **发布纪律**
    `bin/`、`obj/` 不入库；根目录两个 DLL 是刻意入库的发布产物，由 `release.sh` 更新。`release.sh` 必须是 LF 换行（见 `.gitattributes`），发布前工作区必须干净。

---

## 四、代码地图

| 文件 | 职责 | 关键入口 | 修改注意 |
| :--- | :--- | :--- | :--- |
| `src/CadLockOptions.cs` | 参数模型 | `SplitCount` / `RepeatCount` / 五个防护开关 / `ReplaceOriginalFile` | 默认值：`SplitCount=10`、`RepeatCount=10`、`DisableSelect=true`，其余 `false`；UI 的默认分块数是 100，不要混淆 |
| `src/CadLockEngine.cs` | 领域算法：加密、解锁、NOD、批量 DWG、旧格式恢复 | `LockEntities`、`UnlockAllSpaces`、`FindLockLayerIds`、`BatchLockFiles`、`BatchUnlockFiles`、`TryRecoverLegacyBackup` | 不依赖 UI/Document，可在侧数据库中运行；所有常量（图层名、字典键）在此 |
| `src/CadLockEvents.cs` | 运行期防护：DeepClone、ObjectErased、修改还原、图层守护、持久化恢复 | `Install`、`HookEvents`、`UnhookEvents`、`ApplyPersistedProtection`、`FlushPendingProtection` | 延迟恢复机制不能简化成同步回调；状态列表见 `States` |
| `src/CadLockCommands.cs` | CAD 命令与扩展入口 | `LOCK`、`BATLOCK`、`UNLOCK`、`BATUNLOCK`、`STOPEX`、`CadLockApp` | 命令层负责文档锁、选择、控制台输出；不要在这里写算法 |
| `src/OfficeLockEngine.cs` | Excel/Word COM 批量保护 | `BatchProtectFiles`、`BatchUnprotectFiles` | 依赖本机 Office；固定口令 `CadLock@2024`；公式转值不可逆 |
| `src/UI/LockForm.cs` | 配置面板 | `ActionType`、`Options`、`UserAction` | 纯 WinForms；`ExecuteAction` 负责解析输入并关闭窗口 |
| `src/CadLockPlugin.csproj` | AutoCAD 工程 | `AutoCADDir` | 引用 `accoremgd/acdbmgd/acmgd` |
| `src/CadLockPlugin.ZWCAD.csproj` | ZWCAD 工程 | `ZwCadDir` | 引用 `ZwDatabaseMgd/ZwManaged`，定义 `ZWCAD` |
| `tests/CadLockSmoke/SmokeCommands.cs` | 手工冒烟测试 | `SMOKE1`~`SMOKE13`、`SMOKE9` | 只能在真实 AutoCAD 中运行，依赖 `D:\tmp_cadtest` |
| `release.sh` | 一键发版 | 版本号 → 构建 → 复制产物 → 提交 → 标签 → GitHub Release | 需要 `gh auth login`、干净工作区 |

---

## 五、核心流程与实现细节

### 1. 当前图纸加密（`LOCK` → 选择对象 / 全图）

入口：`CadLockCommands.LockCommand`。

1. 弹出 `LockForm`，用户选择动作并点击按钮；窗体通过 `UserAction` 和 `Options` 回传。
2. 批量/解锁动作在命令层提前分流，不进入当前图纸加密分支。
3. 开启文档锁与事务，先用 `CadLockEngine.IsDrawingLocked` 检查 NOD 标记；已加密则弹提示并退出（防止重复嵌套导致图纸膨胀）。
4. 选择对象模式下用 `Editor.GetSelection` 取选择集；全图模式取 `db.CurrentSpaceId` 里的全部对象。
5. 统一走 `FilterLockableEntities`，剔除视口、已加密图层、锁定图层、无效对象。
6. 在事务中调用 `CadLockEngine.LockEntities(db, tr, targetEntities, options)`，返回顶层加密块 ID。
7. 若启用了“不可复制/不可删除/不可更改”，把顶层块 ID 注册到 `CadLockEvents.RegisterProtectedIds`，再调用 `HookEvents(db, doc, options)` 挂载钩子。
8. 打印完成提示；**不会自动保存**，必须提示用户 `Ctrl+S`。加密前应备份原图。

> 选择对象时，如果图元位于锁定图层，会被静默排除；这是为了保证后续 `Erase` 能成功。控制台提示的是**待处理图元数量**，不是最终块数量；最终顶层块数量以 `LockEntities` 返回值为准。

### 2. 加密算法（`CadLockEngine.LockEntities`）

```text
输入：空间 ID、待加密实体 ID 集合、CadLockOptions
1. FilterLockableEntities 再过滤，空集合直接返回（不建图层、不写标记）
2. GetOrCreateLockLayer：创建/同步 ACAD_LOCK_LAYER 的 IsLocked / IsPlottable
3. 取 BlockTable 与目标空间（ForWrite）
4. 重复 RepeatCount 轮：
   a. 把当前轮 ID 按 idx % SplitCount 轮流分配到 SplitCount 个组
   b. 每个非空组创建一个匿名块定义 Name="*U", Explodable=false
   c. 组内每个实体：Clone -> 放入块定义 -> 原实体 Erase(true)
   d. 在目标空间插入 BlockReference(Point3d.Origin, btrId)，LayerId=加密图层
   e. 本轮生成的块参照 ID 作为下一轮输入
5. 若最终有顶层块：MarkDrawingAsLocked + SaveProtectionOptions
6. 返回最外层块参照 ID 列表
```

要点：

- 块参照统一插入在原点；图元保留原坐标，所以视觉位置不变。
- 第一轮克隆的是原始图元，后续轮克隆的是块参照；**每轮都会新增最多 `SplitCount` 个块定义**。
- 顶层块数量约为 `min(SplitCount, 输入数量)`；块定义数量上限约为 `RepeatCount × SplitCount`，实际受图元数量限制。默认 100 × 10 时，大图纸会产生约 1000 个匿名块，已经足够让普通 CAD 递归解析变慢；`RepeatCount` 超过 200 会明显影响打开速度。
- 加密标记只有在确实生成块后才写入，避免“空加密”把图纸永久标记为已加密。
- 批量加密使用同一个方法，但分别在每个空间（模型空间 + 全部布局）调用 `LockEntities` 重载。

### 3. NOD 数据格式（兼容性核心）

结构：

```text
Database.NamedObjectsDictionary
└── "ACAD_LOCK" (DBDictionary)
    └── "OPTIONS" (Xrecord)
        ├── [0] Int16  DisableSelect
        ├── [1] Int16  DisablePlot
        ├── [2] Int16  DisableCopy
        ├── [3] Int16  DisableDelete
        ├── [4] Int32  SplitCount
        └── [5] Int32  RepeatCount
```

读取规则（`LoadProtectionOptions`）：

- 没有 `ACAD_LOCK` 字典，或没有 `OPTIONS` 记录，返回 `null`；
- 字段少于 4 个，返回 `null`（视为旧版/损坏记录）；
- 第 4、5 个字段缺失时，`SplitCount` / `RepeatCount` 回退为 1；
- 重新打开图纸时，`CadLockEvents.ApplyPersistedProtection` 若发现标记存在但没有可读选项，使用 `{ DisableSelect=true, DisableDelete=true }` 作为兼容默认值。

维护要求：

- **不要改变字段顺序或类型**；新字段追加到末尾。
- `MarkDrawingAsLocked` 只负责创建字典，不负责写选项；`SaveProtectionOptions` 要求字典已存在。
- 其他 CAD 会把未知 NOD 当作普通字典忽略；删除该字典等于让插件认为图纸未加密。

### 4. 解锁（`CadLockEngine.UnlockSpaces` / `UnlockAllSpaces`）

```text
1. 找不到加密图层 -> 返回 0
2. ResetLockLayer：先解锁图层、恢复可打印（否则锁定图层上的块无法擦除）
3. 对每个空间循环（最多 256 层）：
   a. 收集该空间中所有位于加密图层（当前 ACAD_LOCK_LAYER + 旧版 *ACADLOCKLAYER 等）上的 BlockReference
   b. 逐个 ExplodeLockBlock：
      - 只处理匿名块（btr.IsAnonymous）
      - 克隆块内每个子实体，TransformBy(br.BlockTransform)
      - 追加到原空间，擦除块参照与其块定义
   c. 一层拆完后，新暴露的内层加密块会进入下一轮循环
4. 只要成功拆过至少一个块，RemoveLockMark
5. 返回拆掉的加密块数量（不是图元数量）
```

调用方（`UnlockCurrentDrawing`）在事务提交后还会：

- `CadLockEvents.UnhookEvents(db)` 卸载防复制/防删除钩子；
- 尝试 `ed.Regen()`（失败可忽略）；
- 提示用户保存。

边界：

- 解锁会为图元创建克隆副本，原对象 ID 不会保留；封装时新增的匿名块嵌套会被拆除，但依赖原对象 ID 的关联（约束、字段、反应器等）需要人工复查。
- 只拆匿名块，命名块即使位于加密图层上也不会被破坏。
- 单个空间/块失败会被吞掉并继续；如果图纸结构异常，可能只解锁一部分，此时 NOD 标记仍会因 `count > 0` 被移除，需要人工检查。
- 旧版 `_lock.dwg` 的空 `MINSERT` 不是正常 BlockReference，拆不动；批量解锁会走同名 `.bak` 恢复，单张 `UNLOCK` 只提示没有可还原图形。

### 5. 运行期防护（`CadLockEvents`）

#### 5.1 状态模型

每张打开的图纸对应一个 `HookState`：

| 字段 | 作用 |
| :--- | :--- |
| `Db` / `Doc` | 目标数据库与文档 |
| `ProtectedIds` | 受保护的顶层加密块 ID（内存集合） |
| `PendingUnerase` | 已提交删除、等待命令结束/空闲恢复的对象 |
| `ModifySnapshots` | 加密块被修改前的原始位置/角度/比例/图层（“不可更改”快照） |
| `PendingModifyRestore` | 已被修改、等待命令结束/空闲还原的加密块 |
| `RestoringModifies` | 还原执行中标记，屏蔽还原自身触发的事件 |
| `Options` | 该图纸的防护选项（来自 NOD） |
| `LayerTampered` | 加密图层被解锁/改为可打印，等待恢复 |
| `*Handler` | 各事件处理器，便于 `UnhookEvents` 精确注销 |

#### 5.2 事件表

| 事件 | 触发条件 | 行为 |
| :--- | :--- | :--- |
| `Database.BeginDeepClone` | `DisableCopy=true` | 把受保护 ID 从 `IdMapping` 中删除，阻止 COPYCLIP / WBLOCK / 跨图拖放 |
| `Database.ObjectErased` | `DisableDelete=true` | 只登记到 `PendingUnerase`，**不在回调内修改对象** |
| `Database.ObjectModified` | `DisableSelect` / `DisablePlot` / `DisableModify` | 检查加密图层是否被解锁/改成可打印（`LayerTampered`）；检查加密块是否被移动/旋转/缩放（`PendingModifyRestore`） |
| `Database.ObjectOpenedForModify` | `DisableModify` | 在修改发生前把块的插入点/角度/比例/图层记入 `ModifySnapshots` |
| `Document.CommandEnded` | 需要恢复删除、图层或修改时 | 执行 `RestorePendingErases`，再 `FlushPendingProtection` |
| `Application.Idle` | 安装过任意钩子后 | `FlushPendingProtection`，覆盖 LISP 嵌套命令等不触发 `CommandEnded` 的场景 |
| `DocumentManager.DocumentActivated` | 插件加载后 | 对激活文档调用 `ApplyPersistedProtection` |

#### 5.3 恢复时机与原因

- `ObjectErased` 回调中对象已处于擦除状态，直接恢复会抛 `eWasErased`；必须等原命令事务结束。
- `CommandEnded` 覆盖普通交互命令；`Application.Idle` 覆盖 LISP 的 `(command ...)` 嵌套调用，也供 `accoreconsole` 等无 UI 环境手动调用 `FlushPendingProtection`。
- 图层恢复只在图纸仍有 `ACAD_LOCK` 标记时执行，避免把用户合法解锁的图纸重新锁死。
- “不可更改”的还原顺序是：先还原加密块，再锁回图层；否则图层一旦锁上，再以写方式打开块参照可能被 CAD 拒绝。还原期间用 `RestoringModifies` 屏蔽自身触发的事件。
- `ApplyPersistedProtection` 打开图纸时：
  1. 读取标记与选项；
  2. 按选项同步加密图层的 `IsLocked` / `IsPlottable`；
  3. 用 `GetLockedBlockIds` 重新收集顶层块并注册；
  4. 重新挂载钩子。
  因此防护**不依赖内存状态持久化**，重启 CAD 后只要插件加载就会恢复。

#### 5.4 处理函数边界

- `UnhookEvents` 会注销事件并移除 `HookState`；`STOPEX` 和 `UNLOCK` 都会调用它。
- `UnhookEvents` 不会删除 NOD 标记；图纸仍被视为已加密，重新打开时防护会再次安装。
- `CadLockApp.Terminate` 目前为空；如果将来支持插件热卸载/热重载，需要在这里注销 `DocumentActivated`、`Idle` 并清理 `States`。
- `Application.Idle` 与 `DocumentManager.DocumentActivated` 是进程级监听，`STOPEX` 只移除图纸级钩子，不会注销这两个全局监听（它们在没有 `HookState` 时基本空转）。
- 因此 `STOPEX` 不是永久禁用：只要图纸仍有 `ACAD_LOCK` 标记，切换文档再激活时 `DocumentActivated` 会重新安装钩子；要彻底停用需执行 `UNLOCK`（移除标记）。
- 新建但未保存的图纸 `db.Filename` 可能为空，`SameDatabase` 的文件名回退会失效；事件闭包本身仍绑定正确状态，但新增按数据库查找的逻辑时要注意这一点。

### 6. 批量 DWG（`BatchLockFiles` / `BatchUnlockFiles`）

流程：

1. 校验文件存在；不存在标记 `Failed`。
2. `using var db = new Database(false, true)`，`ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, "")`，随后 `CloseInput(true)`。
3. 开事务：
   - 加密：已有 `ACAD_LOCK` 标记 → `Skipped`；否则 `LockAllSpaces`；没有任何可加密图元 → `Skipped`（附原因）。
   - 解锁：既无标记也无加密图层 → `Skipped`；`UnlockAllSpaces` 返回 0 → 若检测到旧版 `*ACADLOCKLAYER`，尝试同名 `.bak` 恢复；否则 `Skipped`。
4. `WriteDatabase`：
   - 非替换模式：输出 `原文件名_lock.dwg` / `原文件名_unlock.dwg`；同名已存在时自动追加 `(1)`、`(2)`。
   - 替换模式：先写同目录隐藏临时文件 `.<guid>.dwg`，数据库释放后再 `CommitReplace` 覆盖原文件。
5. 保存版本优先使用 `db.OriginalFileVersion`，失败才回退 `DwgVersion.Current`，避免低版本图纸被静默升级。
6. 每个文件独立 try/catch，通过 `Action<string, BatchLockResult, string>` 回调报告；失败不影响后续文件。

替换模式的额外约束：

- 原文件只读时直接报错“原文件为只读属性，无法替换”；
- `File.Replace` 失败（跨卷、占用）时回退 `File.Copy(temp, target, true)`；
- 临时文件在 `finally` 中清理，清理失败不影响结果；
- 覆盖必须在数据库 `Dispose` 之后执行，否则 AutoCAD 的文件映射可能失效（`SMOKE7` 就是该问题的回归测试）。

批量加密不会挂载运行期钩子（没有 Document）；文件重新在装了插件的 CAD 中打开时，才会由 `ApplyPersistedProtection` 恢复。

旧格式补充：旧版 `lockdwg.dll` 生成的 `_lock.dwg` 常见结构是 `*ACADLOCKLAYER` 图层 + 空 `MINSERT`，当前插件识别该图层并把 `.bak` 作为恢复来源；没有可用 `.bak` 时不会强行修改旧文件，会明确报告 `Skipped`。

### 7. Excel / Word（`OfficeLockEngine`）

| 类型 | 加密做的事 | 解锁做的事 |
| :--- | :--- | :--- |
| Excel | 所有公式单元格转成当前计算值；清除每个工作表页眉页脚的文字与 `&G` 签名图片；删除文档数字签名；保护工作表与工作簿结构 | 解除工作表/工作簿保护；**不恢复公式，也不恢复签名** |
| Word | 清除所有节页眉页脚文字与签名图片；删除数字签名；设置 `wdAllowOnlyReading` 只读保护 | 解除只读保护；**不恢复签名与页眉页脚内容** |

要点：

- 口令固定为 `OfficeLockEngine.ProtectPassword = "CadLock@2024"`。改动它会导致旧文档无法用新版本解锁；如确需更换，必须保留旧口令的回退尝试。
- Office 通过 COM 晚绑定调用（`Type.GetTypeFromProgID`），要求本机安装 Microsoft Office；未安装时该文件标记 `Failed` 并提示。
- 处理期间强制 `Visible=false`、`DisplayAlerts=false`；文件被占用、只读、加密文档异常会通过 `Describe(Exception)` 转成中文提示。
- 批量输出同样遵循 `_lock` / `_unlock` 后缀与 `(1)` 自动改名；勾选替换时直接 `Save` 覆盖原文件。
- 公式转值是**不可逆**的；页眉页脚签名图片是被真正删除的，不是隐藏。操作前必须备份。
- Office 自动化在 CAD 主线程同步执行，批量大文件会卡住界面；不要在 Office 已打开同一文件时运行。
- `ProtectWord` 复用 `ExcelSuffix` 常量（值为 `_lock`），`UnprotectWord` 使用 `UnlockSuffix`；不要被常量名误导，以为只适用于 Excel。

### 8. 界面（`LockForm`）

- `ActionType`：`None`、`SelectObjects`、`EntireDrawing`、`BatchFiles`、`UnlockDrawing`、`BatchUnlock`。
- 默认值：分块数 `100`、加密次数 `10`；`不可炸开` 强制勾选且禁用；`不可选中` 默认勾选；`不可打印/不可复制/不可删除/不可更改` 默认不勾选；`直接替换原文件` 默认不勾选。
- 输入解析失败或小于 1 时回退 100 / 10（`ExecuteAction`）。
- `直接替换原文件` 只对批量加密/批量解锁生效；当前图纸加密/解锁忽略该开关。
- 点击任一操作按钮都会设置 `UserAction`、`DialogResult=OK` 并关闭窗口；`LOCK` 命令据此分流。
- 保持 `LockForm` 为纯 WinForms：不要 `using Autodesk...` 或 `ZwSoft...`，否则双平台工程需要额外条件编译。

---

## 六、测试与发布

### 1. 日常开发循环

1. 修改源码；
2. `dotnet build CadLockPlugin.sln -c Release`，确保双平台 0 错误；
3. 在 AutoCAD 中 `NETLOAD` 新产物，手工验证：加密 → 保存 → 重开 → 解锁；
4. 涉及引擎/事件/NOD 的修改，必须跑冒烟测试；
5. ZWCAD 版没有自动化测试工程，涉及平台 API 的修改必须在中望CAD 中手工回归。

### 2. 冒烟测试（真实 AutoCAD，手工）

```bash
dotnet build tests/CadLockSmoke/CadLockSmoke.csproj -c Release
```

在 AutoCAD 中：

1. `NETLOAD` `bin/Smoke/CadLockPlugin.dll`（先加载插件）；
2. `NETLOAD` `bin/Smoke/CadLockSmoke.dll`；
3. 新建空白图纸，按依赖顺序执行命令，命令行会输出 `[PASS]/[FAIL]`；
4. 最后执行 `SMOKE9` 输出汇总（`PASS=n FAIL=m`）。

| 命令 | 覆盖内容 | 依赖 |
| :--- | :--- | :--- |
| `SMOKE1` | 过滤锁定图层、加密轮次/分块、`Explodable=false`、NOD 标记、原图元擦除、空集合不加密 | 先生成 `D:\tmp_cadtest\base.dwg` |
| `SMOKE2` | 批量加密、`_lock` 命名、重复加密跳过、同名自动改名 | `SMOKE1` |
| `SMOKE3` | 删除保护：登记并提交删除 | `SMOKE1` |
| `SMOKE4` | 删除保护：命令结束后自动恢复 | `SMOKE3` |
| `SMOKE5` | 替换模式批量加密/解锁、原文件读回、未加密文件解锁跳过 | `SMOKE1` |
| `SMOKE7` | `File.Replace` 覆盖后同进程重新读取的回归测试 | `SMOKE1` |
| `SMOKE11` | 生成“图层已被手动解锁”的加密图纸 `guard_unlocked.dwg` | `SMOKE1` |
| `SMOKE12` | 打开 `guard_unlocked.dwg` 后验证标记、选项、图层自动锁回 | `SMOKE11` + 手动打开该文件 |
| `SMOKE13` | 手动解锁图层后调用 `FlushPendingProtection` 验证恢复 | `SMOKE12` |
| `SMOKE14` | 不可更改：程序化移动/旋转/缩放加密块，`FlushPendingProtection` 后验证插入点/角度/比例还原 | 无（独立新建加密块） |
| `SMOKE9` | 打印 PASS/FAIL 汇总 | 最后执行 |

注意：

- 测试固定使用 `D:\tmp_cadtest`，会创建/覆盖测试文件；不要在存放正式图纸的机器上随意运行。
- 这是手工测试，没有 CI、没有退出码；断言结果只能看命令行输出。
- `SMOKE11` 生成的文件需要用户在 AutoCAD 中打开，`SMOKE12` 才有意义；这不是脚本能自动完成的步骤。
- 旧格式回归（手工）：准备一份旧版 `_lock.dwg` 和同名 `.bak`，执行 `BATUNLOCK`；预期输出 `_unlock.dwg`，打开后图元数量应与 `.bak` 一致，且 `*ACADLOCKLAYER` 不再锁定/不再拦截。没有 `.bak` 时应得到明确的旧格式 `Skipped` 提示。

### 3. 发布（`release.sh`）

```bash
./release.sh 1.2.0                    # 完整发布
./release.sh 1.2.0 --dry-run          # 只演练
./release.sh 1.2.0 --notes-file n.md  # 自定义 Release 说明
```

脚本步骤：

1. 校验版本号格式、`git`/`dotnet`/`gh`、工作区干净、标签未被占用；
2. 写入两个 csproj 的 `AssemblyVersion` / `FileVersion`（`x.y.z.0`）；
3. 构建解决方案 + 冒烟测试工程；
4. 复制两个 DLL 到仓库根目录，计算 SHA256 与大小；
5. `git commit -m "chore(release): vX.Y.Z 编译产物"`；
6. 打附注标签 `vX.Y.Z`，推送分支与标签；
7. `gh release create` 上传两个 DLL，说明由上一个标签以来的提交自动生成。

发布前检查：

- 工作区必须干净（未跟踪文件不会被 `-uno` 检测，但仍建议清理）；
- `gh auth login` 已完成；
- `.gitattributes` 保证 `*.sh` 使用 LF，否则 Windows 上可能无法执行；
- 根目录 DLL 与源码必须来自同一次构建；不要手工复制旧 DLL 提交；
- 标签一旦推送不要删除重打，版本号只能递增。

---

## 七、兼容性与升级

### 1. AutoCAD

- 编译验证：AutoCAD 2020（net48）；运行时行为需按第六节手工回归。
- 2021–2024 预计兼容，但仍需用对应版本的 `accoremgd/acdbmgd/acmgd` 重新构建并回归。
- AutoCAD 2025 起迁移到 .NET 8：需要把 `TargetFramework` 改为 `net8.0-windows`，引用 2025 自带托管程序集，并启用 WinForms（`UseWindowsForms`）。**不要假设代码零修改**：API 命名空间、程序集名称、WinForms 宿主行为都可能变化，必须重新跑冒烟测试。
- 依赖 `.NET Framework 4.8` 运行时（net48 构建）。

### 2. 中望CAD

- 编译验证：ZWCAD 2024；依赖 `ZwDatabaseMgd.dll` + `ZwManaged.dll`，运行时需手工回归。
- 命名空间映射：`Autodesk.AutoCAD.*` → `ZwSoft.ZwCAD.*`。
- 新版本升级时先确认程序集名称与类型是否仍在原命名空间；如有差异，在 `#if ZWCAD` 分支内适配，不要复制业务逻辑。
- 没有 ZWCAD 版冒烟测试工程，平台相关改动必须手工验证：加载、加密、解锁、重开恢复、批量。

### 3. Office

- 依赖本机已安装 Microsoft Office（Excel/Word），通过 COM 调用；WPS 等替代品不保证兼容。
- CAD 与 Office 的位数应匹配（当前工程为 x64）。
- 文件被 Office 占用、只读、受密码保护时会失败；错误信息由 `Describe` 归一化。

### 4. 图纸兼容性

- 保存时优先保持原 DWG 版本（`OriginalFileVersion`）。
- 其他 CAD 会忽略未知 NOD 字典 `ACAD_LOCK`；但加密图层 `ACAD_LOCK_LAYER` 是普通图层，若用户图纸本身已有同名图层会被复用（包括其原有颜色/线型设置）。
- 如果第三方工具删除 `ACAD_LOCK` 字典，插件会认为图纸未加密，图层守护也会停止；这是设计上的“标记即真相”。

### 5. 旧版 lockdwg.dll 图纸兼容

旧版工具生成的 `_lock.dwg` 与当前格式不同，典型特征是：

- 锁定图层名为 `*ACADLOCKLAYER`（也可能出现 `ACADLOCKLAYER` / `*ACADLOCKLAYERM` / `ACADLOCKLAYERM`）；
- 顶层可能只剩空 `MINSERT`，真正的图元留在块表/同名 `.bak` 中；
- 可能没有 `OPTIONS` 记录，重新打开时只能按兼容默认值恢复防护。

当前处理方式：

1. `FindLockLayerIds` 同时返回当前格式与旧格式图层，图层守护、`GetLockedBlockIds`、解锁都会覆盖旧图层；
2. 旧文件里如果仍有**可用的匿名块参照**，`UnlockAllSpaces` 会正常拆解；
3. 如果只剩空 `MINSERT`（拆不动），`BATUNLOCK` 会检查同目录同名 `.bak`（例如 `图纸_lock.dwg` → `图纸_lock.bak`）：
   - `.bak` 内有当前格式的加密块 → 解锁 `.bak`，输出 `原文件名_unlock.dwg`（勾选覆盖则替换原文件）；
   - `.bak` 不存在/不可用 → 标记 `Skipped`，提示使用原始 DWG 重新加密；
4. `BATLOCK` 遇到旧格式的 `ACAD_LOCK` 标记会跳过，并明确提示旧版格式，不会在损坏文件上继续加密。

注意：

- 不会自动把未引用的 `*U` 块全部插回图纸（可能重复/错位，风险高）；
- 单张当前图纸的 `UNLOCK` 不会自动读取 `.bak`，需要走批量解锁或手动把 `.bak` 改名为 `.dwg` 后再解锁；
- 旧工具可能已经破坏文件结构（块名/引用丢失），任何恢复前都必须备份。

### 6. 不能随意改的字符串

| 常量 | 值 | 影响 |
| :--- | :--- | :--- |
| `CadLockEngine.LockDictionaryKey` | `ACAD_LOCK` | 已发布图纸的加密标记 |
| `CadLockEngine.LockLayerName` | `ACAD_LOCK_LAYER` | 已发布图纸的加密图层 |
| `CadLockEngine.LegacyLockLayerNames` | `*ACADLOCKLAYER` / `ACADLOCKLAYER` / `*ACADLOCKLAYERM` / `ACADLOCKLAYERM` | 旧版 lockdwg.dll 图纸识别；不要删除 |
| `CadLockEngine.LockOptionsKey` | `OPTIONS` | 持久化选项记录名 |
| `OfficeLockEngine.ProtectPassword` | `CadLock@2024` | 已保护 Office 文档的解除口令 |

---

## 八、已知边界与常见问题

| 现象 | 原因 | 处理/说明 |
| :--- | :--- | :--- |
| 加密后图纸打开很卡 | 嵌套轮数/分块数过大，匿名块数量约为 `RepeatCount × SplitCount` | 降低参数；默认 100×10 已较大，超过 200 轮会显著变慢 |
| 批量加密提示“未找到可加密图元” | 空图纸，或图元全部位于锁定/加密图层，或只有视口 | 检查图层状态；这是 `Skipped`，不是失败 |
| 当前图纸“全图加密”后布局没变化 | `LOCK` 的全图只处理当前空间 | 用批量加密覆盖模型空间 + 全部布局 |
| 无法再次加密 | 图纸 NOD 中已有 `ACAD_LOCK` 标记 | 先 `UNLOCK`；批量模式会跳过并提示；旧版文件请用原始 DWG 或 `.bak` 恢复 |
| 旧版 `_lock.dwg` 批量解锁提示“旧版格式”或没有可还原图形 | 旧工具只留下 `*ACADLOCKLAYER` + 空 `MINSERT`，没有可拆的匿名块 | 用同目录同名 `.bak`（批量解锁会自动尝试）；没有 `.bak` 时用原始 DWG；不要直接改旧文件 |
| 旧版文件手动解锁 `*ACADLOCKLAYER` 后又自己锁回 | 插件已识别旧图层，图层守护在命令结束/空闲时恢复 | 这是预期防护；要彻底解锁请走 `BATUNLOCK` 或 `UNLOCK` |
| 对旧版 `_lock.dwg` 再执行 `BATLOCK` 被跳过 | 文件仍有 `ACAD_LOCK` 标记，且是旧格式 | 先恢复出可编辑图纸（原始 DWG / `.bak`），再重新加密 |
| 解锁后图层仍锁定 | 旧图纸/异常中断，或只解锁了部分空间 | 再执行 `UNLOCK`；必要时手动解锁图层或 `STOPEX` |
| 复制/删除仍被拦截 | 当前进程仍挂着钩子 | `STOPEX` 或执行 `UNLOCK`（会卸载钩子）；关闭 CAD 也会清除 |
| `STOPEX` 后切换图纸再回来又被拦截 | `DocumentActivated` 按图纸内的 `ACAD_LOCK` 标记重新挂载 | `STOPEX` 只适合临时调试；要持久停用请 `UNLOCK` |
| `UNLOCK` 提示没有可还原图形，但仍无法再次加密 | 加密图层被删除，只剩 NOD 标记；`UnlockSpaces` 找不到图层时直接返回 0 | 先备份，再手动删除 `ACAD_LOCK` 字典或恢复 `ACAD_LOCK_LAYER` 后重试 |
| 没装插件的 CAD 里还能移动/删除加密块 | 运行期防护依赖插件；只有 `Explodable=false` 是文件级约束 | 这是能力边界，不是 bug；需要文件级保护请使用 DWG 密码/专用加密 |
| 加密块仍能被对象捕捉 | 图层锁定不屏蔽 OSNAP，AutoCAD 没有图层级捕捉开关 | 不宣称“不可捕捉”；如需彻底关闭需自定义实体重写 `GetOsnapPoints` |
| 图纸被整体另存/截图/第三方工具读取 | 防复制只拦截 DeepClone，不拦截文件外发 | 插件解决的是图元提取与编辑，不是文件防扩散 |
| 有插件的人可以解锁 | `UNLOCK` 无口令，拆块逻辑是公开的 | 加密是“提高提取成本”，不是密码学保护 |
| Office 处理失败 | 未安装 Office、COM 未注册、文件被占用/只读、位数不匹配 | 看命令行错误；关闭 Office 后重试 |
| Excel 公式没了 | 设计上转成计算值，且不可逆 | 操作前备份；解锁不会恢复公式 |
| Office 页眉签名图片消失 | 图片被真正删除，不是隐藏 | 不可恢复，操作前备份 |
| 批量替换原文件失败 | 原文件只读、被占用、跨卷替换失败 | 先取消只读/关闭占用；脚本会回退复制，仍失败则报告 |
| 根目录 DLL 与源码不一致 | 普通构建只写 `bin/`，根目录由 `release.sh` 更新 | 以最新 Release/标签为准，或重新执行发布脚本 |
| 冒烟测试没有自动结果 | 测试是 CAD 内手工命令，依赖 UI 与固定目录 | 看 `[PASS]/[FAIL]` 与 `SMOKE9` 汇总；不要指望 CI |

---

## 九、维护检查清单

### 提交前

- [ ] `dotnet build CadLockPlugin.sln -c Release` 双平台 0 错误；
- [ ] 涉及引擎/事件/NOD：跑 `SMOKE1`~`SMOKE13` 相关命令，确认无 `[FAIL]`；
- [ ] 涉及 ZWCAD API：在中望CAD 中手工加载并完成加密/解锁/重开；
- [ ] 加密 → 保存 → 重开：标记、图层锁定/不打印、选项恢复正确；
- [ ] `UNLOCK`：几何还原、图层恢复、钩子卸载、保存后重开不再锁定；
- [ ] 批量：非替换命名、同名 `(1)`、替换模式、只读文件、已加密跳过；
- [ ] 旧版兼容：旧 `_lock.dwg` + 同名 `.bak` 走 `BATUNLOCK`，确认输出图元正确、旧图层 `*ACADLOCKLAYER` 不再被当作普通图层；
- [ ] Office：有/无 Office、文件占用、公式转值提示、解锁行为；
- [ ] 没有把 `bin/`、`obj/`、测试 DWG 或临时文件加入提交；
- [ ] 行为/命令/格式变化已同步到 `README.md` 与本文档。

### 发版前

- [ ] 版本号只在 `release.sh` 中更新，两个 csproj 保持一致；
- [ ] 工作区干净，`gh auth status` 正常；
- [ ] 先跑 `--dry-run`，确认构建、产物复制、提交步骤无误；
- [ ] 根目录 DLL 来自本次构建，SHA256 已记录；
- [ ] 发布后检查 Release 附件与标签，必要时补充发布说明。

---

## 附录 A：原始 lockdwg.dll 逆向记录（历史）

> 以下内容来自项目初期的逆向分析，用于解释“为什么这样设计”。原工具仅作背景参考，当前实现与其存在差异，维护时以正文为准。

### 1. 元数据与依赖

原 `lockdwg.dll`（约 76KB）目标框架为 .NET Framework 4.0~4.8，引用：

- `Acdbmgd.dll`（数据库核心）
- `accoremgd.dll`（运行时控制）
- `Acmgd.dll`（主程序交互）
- `System.Windows.Forms`

二进制带 `DotfuscatorAttribute`，类名/方法名被混淆为 `ᜀ`、`ᜁ` 等 Unicode 字符，字符串常量被加密。

### 2. 字符串解密算法

在 `lock.MyPlugin` 中找到静态方法 `b(string, int)`，还原出的逻辑：

1. `key = 0x53FE74AD + arg_key`；
2. 遍历字符数组；
3. 每个字符取低字节与高字节，分别在与密钥异或后令 `key++`；
4. 字节翻转：`chars[i] = (char)((low << 8) | high)`；
5. 重组为明文。

由此解出的关键常量包括：`ACAD_LOCK`、`ACADLOCKLAYER` / `ACADLOCKLAYERM`、`"此图字典已加密，无法再次加密"`、`"选择对象加密"`、`"全图自动加密"`、`"批量图纸加密"`、`"分块数量"`、`"加密次数"`、`"就是针对加密部分不可打印"`。

### 3. 外部 API 调用链

- `BlockTableRecord.set_Explodable(bool)`：禁止分解；
- `Database.add_BeginDeepClone` / `BeginDeepCloneTranslation`：复制拦截；
- `Database.add_ObjectErased` / `DBObject.Erase(bool)`：删除拦截与恢复；
- `Database.ReadDwgFile` / `SaveAs`：离线批量处理。

### 4. 原始六重防护

1. **禁止炸开**：匿名块 + `Explodable=false`；
2. **分块 + 多层递归嵌套**：SplitCount 分散，RepeatCount 增加深度；
3. **图层级控制**：专用图层 + `IsPlottable=false` + `IsLocked=true`；原工具宣称“不可捕捉”，但图层锁定并不屏蔽 OSNAP；
4. **数据库事件拦截**：DeepClone 删除映射、ObjectErased 反擦除；
5. **NOD 防重复加密**：`ACAD_LOCK` 字典；
6. **无界面批处理**：侧数据库 `ReadDwgFile` / `SaveAs`。

### 5. 原工具与本项目的差异

| 方面 | 原 lockdwg.dll | 当前 CadLockPlugin |
| :--- | :--- | :--- |
| 解锁 | 说明强调不可逆，未提供解锁入口 | 提供 `UNLOCK` / `BATUNLOCK`，可逐层拆回几何 |
| 保护选项持久化 | 仅加密标记 | NOD 中额外保存 `OPTIONS`，重开自动恢复图层与钩子 |
| 删除恢复 | 回调内直接恢复（易抛 `eWasErased`） | 延迟到 `CommandEnded` / `Idle`，用 `openErased: true` 恢复 |
| 图层守护 | 无 | 检测手动解锁/改可打印，自动锁回（图纸仍有标记时） |
| 防修改 | 无 | `ObjectOpenedForModify` 记录快照，命令结束/空闲时还原插入点、角度、比例与图层 |
| 批量对象 | 仅 DWG | DWG + Excel + Word（COM） |
| 平台 | AutoCAD | AutoCAD + 中望CAD，共享源码 |
| 图层名 | `ACADLOCKLAYER` / `ACADLOCKLAYERM` | `ACAD_LOCK_LAYER`（已发布格式，勿改） |
| 复制/删除防护 | 进程内事件 | 进程内事件 + 重新打开自动重挂 |
