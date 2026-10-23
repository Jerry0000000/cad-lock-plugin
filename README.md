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
| `LOCK` | **核心主命令**。弹出图形化配置面板，可选择“选择对象加密”、“全图自动加密”或“批量图纸加密”，并设置嵌套次数、分块数与各项限制。 |
| `BATLOCK` | **批量加密快捷命令**。直接弹出文件选择框（支持多选 `.dwg` 文件），后台批量离线转换并自动保存为 `文件名_lock.dwg`。 |
| `STOPEX` | **维护命令**。在当前会话中卸载防删除、防复制的事件拦截钩子。 |

---

## 四、双平台维护与构建说明

### 1. 一键编译双平台
在根目录执行以下命令，即可同时生成两个平台的插件：
```powershell
dotnet build CadLockPlugin.sln -c Release
```
或者在 Visual Studio 中打开 `CadLockPlugin.sln`，点击“生成解决方案”即可。

### 2. 路径配置
* AutoCAD 依赖路径在 [src/CadLockPlugin.csproj](file:///D:/安装程序/CAD图纸加密插件lock/CAD图纸加密插件lock/src/CadLockPlugin.csproj) 中的 `<AutoCADDir>` 配置（默认 `D:\Program Files\Autodesk\AutoCAD 2020`）；
* 中望CAD 依赖路径在 [src/CadLockPlugin.ZWCAD.csproj](file:///D:/安装程序/CAD图纸加密插件lock/CAD图纸加密插件lock/src/CadLockPlugin.ZWCAD.csproj) 中的 `<ZwCadDir>` 配置（默认 `D:\Program Files\ZWSOFT\ZWCAD 2024`）。
