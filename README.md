# CAD 图纸加密保护插件 (CadLockPlugin)

基于 AutoCAD .NET API 开发的图纸防盗、加密与只读锁定插件，具备防炸开、防修改、防复制、防打印、防删除和批量处理能力。

---

## 一、目录结构

```text
D:\安装程序\CAD图纸加密插件lock\CAD图纸加密插件lock\
│
├── CadLockPlugin.sln              # Visual Studio 解决方案文件
├── CadLockPlugin.dll              # 当前编译生成的可用插件库 (Release 版)
├── lockdwg.dll                    # 原插件原始备份
├── 使用说明.txt                   # 原插件说明文档
├── README.md                      # 本项目的开发与维护指南
│
├── src/                           # 源代码目录
│   ├── CadLockPlugin.csproj       # 项目工程文件 (SDK 格式)
│   ├── CadLockOptions.cs          # 参数配置模型
│   ├── CadLockEngine.cs           # 核心加密算法引擎 (无名块、Explodable=false、递归嵌套)
│   ├── CadLockEvents.cs           # 底层安全拦截钩子 (反复制 DeepClone、反删除 ObjectErased)
│   ├── CadLockCommands.cs         # CAD 交互命令 (LOCK、BATLOCK、STOPEX)
│   └── UI/
│       └── LockForm.cs            # WinForms 图形化交互配置面板
│
└── bin/
    └── Release/
        └── CadLockPlugin.dll      # 编译输出目录
```

---

## 二、功能与命令说明

加载方式：在 AutoCAD 命令行输入 `NETLOAD`，选择 `CadLockPlugin.dll`。

| 命令 | 说明 |
| :--- | :--- |
| `LOCK` | **核心主命令**。弹出图形化配置面板，可选择“选择对象加密”、“全图自动加密”或“批量图纸加密”，并设置嵌套次数、分块数与各项限制。 |
| `BATLOCK` | **批量加密快捷命令**。直接弹出文件选择框（支持多选 `.dwg` 文件），后台批量离线转换并自动保存为 `文件名_lock.dwg`。 |
| `STOPEX` | **维护命令**。在当前会话中卸载防删除、防复制的事件拦截钩子。 |

---

## 三、开发与长期维护指南

### 1. 编译构建方法

* **使用 Visual Studio**：
  直接双击打开根目录下的 `CadLockPlugin.sln`，选择 `Release` 或 `Debug` 配置，按 `F6` 或点击“生成解决方案”即可。

* **使用命令行**：
  在当前目录下执行：
  ```powershell
  dotnet build CadLockPlugin.sln -c Release
  ```

### 2. 多版本 AutoCAD 兼容性配置

当前项目默认引用本地已安装的 AutoCAD 2020 运行库（路径为 `D:\Program Files\Autodesk\AutoCAD 2020\`）。

如需切换到其他版本的 AutoCAD 或在其他机器上开发，直接编辑 [src/CadLockPlugin.csproj](file:///D:/安装程序/CAD图纸加密插件lock/CAD图纸加密插件lock/src/CadLockPlugin.csproj)：

```xml
<PropertyGroup>
  <!-- 修改为目标机器上的 AutoCAD 安装目录 -->
  <AutoCADDir>C:\Program Files\Autodesk\AutoCAD 2024</AutoCADDir>
</PropertyGroup>
```

#### 各版本目标框架与依赖对照表：
* **AutoCAD 2014 ~ 2024**：
  * TargetFramework: `net48` (或 `net47`, `net4.5`)
  * 依赖 DLL: `accoremgd.dll`, `acdbmgd.dll`, `acmgd.dll`
* **AutoCAD 2025 及更高版本**：
  * Autodesk 从 AutoCAD 2025 起全面升级至 **.NET 8.0**。
  * 将 `<TargetFramework>net48</TargetFramework>` 改为 `<TargetFramework>net8.0-windows</TargetFramework>` 即可。

### 3. 核心机制设计要点

1. **禁止分解 (Explodable = false)**：
   图元打包进无名块（`*U`）后将 `btr.Explodable = false`，AutoCAD 自身的 `EXPLODE`（快捷键 `X`）命令将拒绝分解。
2. **多层递归嵌套 (RepeatCount)**：
   每次将图元分组打包成块，再将新生成的块参照作为下一次打包的对象。嵌套层数越多，逆向还原难度呈指数级增加。超过 100 层后原生 AutoCAD 极易因递归深度崩溃，而轻量看图工具可正常查阅。
3. **图层安全控制**：
   加密块被放置在独立图层 `ACAD_LOCK_LAYER`，通过设置 `IsPlottable = false` 实现不可打印，设置 `IsLocked = true` 实现不可捕捉。
4. **数据库事件拦截**：
   * `Database.BeginDeepClone`：阻止用户通过剪贴板或跨文件写块提取图形。
   * `Database.ObjectErased`：若检测到加密图元被执行删除，底层自动触发反擦除恢复。
