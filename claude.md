# CAD 图纸加密保护插件 (CadLockPlugin) 逆向分析与开发全记录

本文档详细记录了针对原始 `lockdwg.dll` 的逆向剖析过程、底层核心防护机制解密，以及使用 C# 和 AutoCAD .NET API 从零重构完整开源项目的完整编程历程。

---

## 目录
- [一、项目背景与目标](#一项目背景与目标)
- [二、原始 DLL 逆向分析过程](#二原始-dll-逆向分析过程)
  - [1. 元数据与依赖探测](#1-元数据与依赖探测)
  - [2. 混淆对抗与字符串解密算法还原](#2-混淆对抗与字符串解密算法还原)
  - [3. 外部 API 调用链与特征捕捉](#3-外部-api-调用链与特征捕捉)
- [三、核心技术机制全揭秘](#三核心技术机制全揭秘)
  - [1. 禁止炸开机制 (Explodable = false)](#1-禁止炸开机制-explodable--false)
  - [2. 分块与多层递归嵌套 (Split & Repeat)](#2-分块与多层递归嵌套-split--repeat)
  - [3. 图层级安全控制 (不可打印与不可捕捉)](#3-图层级安全控制-不可打印与不可捕捉)
  - [4. 数据库事件拦截 (反复制与反删除)](#4-数据库事件拦截-反复制与反删除)
  - [5. 命名对象字典 (NOD) 防重复加密](#5-命名对象字典-nod-防重复加密)
  - [6. 无界面离线批处理 (Headless Database)](#6-无界面离线批处理-headless-database)
- [四、架构设计与重构开发历程](#四架构设计与重构开发历程)
  - [1. 模块化划分](#1-模块化划分)
  - [2. 核心代码编写](#2-核心代码编写)
  - [3. 编译问题排查与解决](#3-编译问题排查与解决)
- [五、多版本兼容性与长期维护规范](#五多版本兼容性与长期维护规范)

---

## 一、项目背景与目标

原始工具为一个名为 `lockdwg.dll` 的 AutoCAD 插件，配套文件包含：
* `lockdwg.dll`：插件二进制文件（约 76KB）；
* `使用说明.txt`：说明其具备“选择对象加密”、“全图加密”、“批量加密”、“不可捕捉”、“不可打印”、“不可复制”、“不可删除”等功能，并提示“不可逆，加密前务必备份”。

**核心任务**：
1. 深入探究该 DLL 的底层实现逻辑，搞清楚其图纸锁定和保护的原理；
2. 构建一套完全独立、无混淆、可扩展且便于长期维护的 C# 源码工程；
3. 输出包含完整解决方案文件（.sln）、项目文件（.csproj）、UI 界面与编译成品的标准项目。

---

## 二、原始 DLL 逆向分析过程

### 1. 元数据与依赖探测
通过 .NET 底层元数据读取器（`System.Reflection.Metadata` 与 `PEReader`）对其 PE 结构进行检查：
* **目标框架**：.NET Framework 4.0 ~ 4.8；
* **引用程序集**：
  * `Acdbmgd.dll`（AutoCAD 数据库核心库）
  * `accoremgd.dll`（AutoCAD 运行时控制核心库）
  * `Acmgd.dll`（AutoCAD 主程序交互库）
  * `System.Windows.Forms`（图形界面库）
* **混淆特征**：包含 `DotfuscatorAttribute`，类名与方法名大量被混淆为 `ᜀ`、`ᜁ`、`ᜂ` 等 Unicode 字符，且字符串常量池中的明文字符串均被加密为不可读乱码。

### 2. 混淆对抗与字符串解密算法还原
在 `lock.MyPlugin` 类中发现一个名为 `b(string, int)` 的静态字符串解密方法。反汇编其 IL 指令后，成功还原出 Dotfuscator 的动态解密算法：
1. 取出初始整型密钥：`key = 0x53FE74AD + arg_key`；
2. 将输入字符串转为字符数组 `char[]`；
3. 循环遍历每个字符：
   * 取出低字节，密钥加 1 并进行异或运算；
   * 取出高字节，密钥再加 1 并进行异或运算；
   * **字节翻转**：将异或后的低字节左移 8 位作为新高字节，新低字节为原高字节（`chars[i] = (char)((low << 8) | high)`）；
4. 重新组合为明文字符串。

通过该算法，成功解密出插件内部的关键常量：
* `"ACAD_LOCK"`（加密字典标记）
* `"ACADLOCKLAYER"` / `"ACADLOCKLAYERM"`（专用锁定图层名称）
* `"此图字典已加密，无法再次加密"`
* `"选择对象加密"`、`"全图自动加密"`、`"批量图纸加密"`
* `"分块数量"`、`"加密次数"`、`"就是针对加密部分不可打印"` 等。

### 3. 外部 API 调用链与特征捕捉
通过提取 MemberReferences 表，捕获其调用的关键 AutoCAD API：
* `BlockTableRecord.set_Explodable(bool)`：禁止图块分解；
* `Database.add_BeginDeepClone` / `BeginDeepCloneTranslation`：监听深克隆事件；
* `Database.add_ObjectErased` / `DBObject.Erase(bool)`：监听图元删除事件与反擦除；
* `Database.ReadDwgFile` / `Database.SaveAs`：离线图纸无界面批处理。

---

## 三、核心技术机制全揭秘

经过反编译与逆向分析，所谓的“CAD 图纸加密”在行业中实际上是**几何图形的封装与保护锁定**。该插件采用了六重纵深防护机制：

### 1. 禁止炸开机制 (Explodable = false)
在 AutoCAD 中，用户破解图纸保护最常见的手段就是执行 `EXPLODE`（分解/炸开）命令。
* **原理**：CAD 中的块定义对象 `BlockTableRecord` 有一个原生属性 `Explodable`（布尔值）。
* **防护**：将原图元打包进无名块（`*U`）后，强制将 `btr.Explodable = false`。此时在 CAD 中即使选中图块并输入 `EXPLODE` 或 `X`，CAD 核心引擎会直接提示“无法分解”，杜绝常规的图元提取手段。

### 2. 分块与多层递归嵌套 (Split & Repeat)
* **分块 (SplitCount)**：若全图所有图元仅打成一个大块，破解者只要用第三方工具修改一次块属性即可攻破。因此插件将图元散列分配到 $N$ 个独立的块中（例如分 10 组）。
* **多层递归嵌套 (RepeatCount)**：这是该插件增加逆向难度的核心。
  * 第 1 轮：将原始图元分 10 组，生成 10 个不可炸开的块参照；
  * 第 2 轮：将这 10 个块参照再分组，重新打包成新的不可炸开块；
  * ...
  * 第 $K$ 轮（如 10~100 轮）：形成深度为 $K$ 的多叉树嵌套块。
* **效果**：嵌套层数达到数十甚至上百层时，普通 CAD 尝试递归解析图形结构时会出现性能急剧下降甚至栈溢出崩溃；而“广联达快速看图”、“CAD迷你看图”等专业看图软件底层使用轻量流式渲染，能够流畅展示与测量。

### 3. 图层级安全控制 (不可打印与不可捕捉)
* **不可打印 (DisablePlot)**：
  将加密生成的块参照归入专用图层（如 `ACAD_LOCK_LAYER`），并将 `LayerTableRecord.IsPlottable` 设置为 `false`。CAD 界面显示完全正常，但执行打印出图、批量转 PDF 时该图层内容全部被跳过。
* **不可捕捉 (DisableSnap)**：
  将图层属性设为 `LayerTableRecord.IsLocked = true`（图层锁定）。在锁定状态下，常规绘图工具无法吸附其端点、垂足或交点。

### 4. 数据库事件拦截 (反复制与反删除)
常规 CAD 命令往往无法通过静态属性直接限制，插件通过挂接数据库运行时事件钩子实现主动防护：
* **反复制 (Anti-Copy)**：
  监听 `Database.BeginDeepClone`。当用户执行 `COPYCLIP`（Ctrl+C）、跨文档拖放或 `WBLOCK`（写块另存）时，底层会触发 `IdMapping`。插件拦截该事件并将受保护的图块 ID 从 `IdMapping` 映射表中删除（`idMap.Delete(id)`），导致复制到新图纸或剪贴板的内容为空。
* **反删除 (Anti-Delete)**：
  监听 `Database.ObjectErased`。当受保护的图块被执行 `ERASE` 删除时，事件钩子在底层立即将其 `UpgradeOpen()`，并调用 `obj.Erase(false)` 执行反向恢复（撤销删除）。

### 5. 命名对象字典 (NOD) 防重复加密
图纸多次重复进行多重嵌套加密会导致图形数据膨胀甚至损坏。
* 插件在当前图纸的命名对象字典（`NamedObjectsDictionaryId`）中存入扩展字典 `ACAD_LOCK`。
* 每次执行前通过 `nod.Contains("ACAD_LOCK")` 检查，若存在则拦截操作并给出安全警告。

### 6. 无界面离线批处理 (Headless Database)
对于批量图纸加密，不需要在 CAD 窗口中反复打开关闭文档，而是利用 AutoCAD 的侧数据库技术：
```csharp
using var db = new Database(false, true);
db.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
// 执行加密事务...
db.SaveAs(outPath, DwgVersion.Current);
```
此方法绕过了 UI 重绘和文档切换开销，数十张大型图纸可在数秒内完成后台批量加密。

---

## 四、架构设计与重构开发历程

基于以上逆向成果，在当前工作目录下构建了干净、可维护的开源工程 `CadLockPlugin`。

### 1. 模块化划分

```text
CadLockPlugin/
├── CadLockOptions.cs     # 配置实体类（分块数、嵌套轮数、安全开关）
├── CadLockEngine.cs      # 纯粹的领域算法与图形处理引擎
├── CadLockEvents.cs      # 独立的安全事件挂载与拦截管理模块
├── CadLockCommands.cs    # 命令注册与交互调度（IExtensionApplication 与 CommandMethod）
└── UI/
    └── LockForm.cs       # 基于 WinForms 的图形化交互控制面板
```

### 2. 核心代码编写

#### ① 核心加密算法 (节选自 `CadLockEngine.cs`)
```csharp
public static List<ObjectId> LockEntities(Database db, Transaction tr, IEnumerable<ObjectId> initialEntityIds, CadLockOptions options)
{
    ObjectId lockLayerId = GetOrCreateLockLayer(db, tr, options);
    var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);

    List<ObjectId> currentRoundIds = new List<ObjectId>(initialEntityIds);
    int repeatCount = Math.Max(1, options.RepeatCount);
    int splitCount = Math.Max(1, options.SplitCount);

    for (int r = 0; r < repeatCount; r++)
    {
        var nextRoundIds = new List<ObjectId>();
        var groups = new List<List<ObjectId>>();
        for (int i = 0; i < splitCount; i++) groups.Add(new List<ObjectId>());

        int idx = 0;
        foreach (var id in currentRoundIds)
        {
            groups[idx % splitCount].Add(id);
            idx++;
        }

        for (int g = 0; g < splitCount; g++)
        {
            var groupIds = groups[g];
            if (groupIds.Count == 0) continue;

            // 1. 创建不可炸开无名块
            var btr = new BlockTableRecord { Name = "*U", Explodable = false };
            ObjectId btrId = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);

            // 2. 迁移图元
            foreach (var entId in groupIds)
            {
                var ent = (Entity)tr.GetObject(entId, OpenMode.ForWrite);
                var clone = (Entity)ent.Clone();
                btr.AppendEntity(clone);
                tr.AddNewlyCreatedDBObject(clone, true);
                ent.Erase(true);
            }

            // 3. 插入块参照
            var br = new BlockReference(Point3d.Origin, btrId) { LayerId = lockLayerId };
            ObjectId brId = currentSpace.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            nextRoundIds.Add(brId);
        }
        currentRoundIds = nextRoundIds;
    }

    MarkDrawingAsLocked(db, tr);
    return currentRoundIds;
}
```

#### ② 运行时拦截钩子 (节选自 `CadLockEvents.cs`)
```csharp
// 拦截深克隆：阻止 Ctrl+C 复制与 WBLOCK 导出
private static void OnBeginDeepClone(object sender, IdMappingEventArgs e)
{
    var idMap = e.IdMapping;
    foreach (var id in ProtectedObjectIds)
    {
        if (idMap.Contains(id)) idMap.Delete(id);
    }
}

// 拦截删除：图元被 ERASE 时自动反擦除
private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
{
    if (e.Erased && ProtectedObjectIds.Contains(e.DBObject.ObjectId))
    {
        var obj = e.DBObject;
        if (!obj.IsWriteEnabled) obj.UpgradeOpen();
        obj.Erase(false); // 取消删除
    }
}
```

### 3. 编译问题排查与解决

在开发与编译过程中，解决了以下具体技术细节：
1. **`FileOpenMode` 枚举定义**：
   * 在使用 `db.ReadDwgFile` 时，旧代码可能会使用 `OpenForReadAndWriteShare`，在标准 AutoCAD .NET API 中应使用 `FileOpenMode.OpenForReadAndAllShare`。
2. **WinForms 窗体模态宿主**：
   * AutoCAD 内部弹窗必须使用宿主调度方法 `Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form)`，若直接调用 `form.ShowDialog()` 会导致 CAD 主窗口焦点抢占或快捷键失效。
3. **新式 SDK csproj 与 .NET Framework 4.8 引用**：
   * 项目采用标准 SDK 格式，支持通过 `<AutoCADDir>` 宏动态定位本地已安装的 AutoCAD 路径，并将依赖程序集的 `<Private>false</Private>` 属性开启，避免编译时把 CAD 核心动态库拷贝到输出目录而污染运行时环境。

---

## 五、多版本兼容性与长期维护规范

项目已针对本地 **AutoCAD 2020** 编译验证通过。对于后续长期维护与升级，请遵循以下规范：

### 1. 修改适配不同版本的 AutoCAD 路径
打开 `src/CadLockPlugin.csproj`，修改 `<AutoCADDir>` 节点：
```xml
<PropertyGroup>
  <!-- 本地路径示例 -->
  <AutoCADDir>D:\Program Files\Autodesk\AutoCAD 2020</AutoCADDir>
</PropertyGroup>
```

### 2. 升级至 AutoCAD 2025+ (.NET 8.0)
AutoCAD 2025 发生了重大变革，从 .NET Framework 迁移到了 **.NET 8.0**：
* 维护时只需将 `<TargetFramework>net48</TargetFramework>` 替换为：
  ```xml
  <TargetFramework>net8.0-windows</TargetFramework>
  ```
* 依赖的 `accoremgd.dll`、`acdbmgd.dll`、`acmgd.dll` 接口保持一致，代码无需改动。

### 3. 推荐使用流程
1. **日常维护与修改**：使用 Visual Studio 打开根目录下的 `CadLockPlugin.sln` 即可享受完整的 IntelliSense 与调试功能；
2. **构建发布**：执行 `dotnet build CadLockPlugin.sln -c Release`，编译生成的 DLL 自动输出到 `bin/Release/CadLockPlugin.dll` 及项目根目录；
3. **版本控制**：当前工程已初始化标准 Git 仓库，编译产物与临时文件已被 `.gitignore` 过滤，随时可通过 `git commit` 跟踪每一次修改。


---

## 六、中望CAD (ZWCAD) 双平台无缝适配扩展

为了支持国产 CAD 软件（中望CAD ZWCAD），我们在现有架构基础上进行了双平台跨端升级：

### 1. ZWCAD .NET API 特征分析
探测本地 `D:\Program Files\ZWSOFT\ZWCAD 2024` 的程序集架构：
* 依赖库：
  * `ZwDatabaseMgd.dll`（对应 AutoCAD 的 `acdbmgd.dll`）
  * `ZwManaged.dll`（对应 AutoCAD 的 `accoremgd.dll` + `acmgd.dll`）
* 命名空间对照：
  * `Autodesk.AutoCAD.Runtime` -> `ZwSoft.ZwCAD.Runtime`
  * `Autodesk.AutoCAD.DatabaseServices` -> `ZwSoft.ZwCAD.DatabaseServices`
  * `Autodesk.AutoCAD.Geometry` -> `ZwSoft.ZwCAD.Geometry`
  * `Autodesk.AutoCAD.ApplicationServices` -> `ZwSoft.ZwCAD.ApplicationServices`
  * `Autodesk.AutoCAD.EditorInput` -> `ZwSoft.ZwCAD.EditorInput`

### 2. 单一代码库（Zero Code Duplication）架构
通过条件编译符号 `#if ZWCAD`，使所有核心代码（算法引擎、事件监听、命令调度）保持 100% 共享，完全消除重复代码维护成本。

### 3. 多工程并行管理
在 `CadLockPlugin.sln` 解决方案中挂载两个工程：
1. `src/CadLockPlugin.csproj` -> 编译生成 `CadLockPlugin.dll`（AutoCAD 版）；
2. `src/CadLockPlugin.ZWCAD.csproj` -> 编译生成 `CadLockPlugin.ZWCAD.dll`（中望CAD 版）。
编译时执行一次 `dotnet build CadLockPlugin.sln -c Release` 即可同时生成两个平台的最新二进制产物。


---

## 七、WinForms 交互对话框 (LockForm) 深度还原与增强

通过对原版 `lockdwg.dll` 中 `lock.Form1` 的逆向，提取出其完整的控件、默认值与 ToolTip 提示文本，重构了更加美观、符合 CAD 用户操作习惯的图形对话框 [LockForm.cs](file:///D:/安装程序/CAD图纸加密插件lock/CAD图纸加密插件lock/src/UI/LockForm.cs)：

1. **参数设置组**：
   * **分块数量**：默认 100，鼠标悬停 ToolTip 提示；
   * **加密次数**：默认 10，鼠标悬停 ToolTip 提示。
2. **安全保护复选区**：
   * `[√] 不可炸开 (核心)`：默认勾选且锁定不可取消（Explodable=false 核心技术）；
   * `[ ] 不可捕捉`：悬浮提示“选不中，也无法捕捉点，仅能查看”；
   * `[ ] 不可打印`：悬浮提示“针对加密部分不可打印，PLOT 输出空白”；
   * `[ ] 不可复制`：悬浮提示“禁止通过剪贴板或写块将加密图元提取出去”；
   * `[ ] 不可删除`：悬浮提示“对加密图元启用删除保护，执行 ERASE 时底层自动反擦除”。
3. **三大一键操作大按钮**：
   * `① 选择对象加密`（点击后返回视口手动框选局部图元）；
   * `② 全图自动加密`（点击直接对当前模型空间所有图元打包加密）；
   * `③ 批量图纸加密`（点击弹出文件多选框后台离线批量转换）。
4. **警示提示框**：
   * 橙黄色警示背景框，提醒用户操作前先备份原图纸。
