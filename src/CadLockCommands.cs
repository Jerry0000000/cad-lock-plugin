using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using CadLockPlugin.UI;

#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Runtime;
using Application = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

[assembly: ExtensionApplication(typeof(CadLockPlugin.CadLockApp))]
[assembly: CommandClass(typeof(CadLockPlugin.CadLockCommands))]

namespace CadLockPlugin
{
    public class CadLockApp : IExtensionApplication
    {
        public void Initialize()
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\n=============================================");
#if ZWCAD
            ed?.WriteMessage("\n CAD 图纸加密保护插件 (中望CAD版) 已成功加载！");
#else
            ed?.WriteMessage("\n CAD 图纸加密保护插件 (AutoCAD版) 已成功加载！");
#endif
            ed?.WriteMessage("\n 输入命令 LOCK 弹出加密配置面板");
            ed?.WriteMessage("\n 输入命令 BATLOCK 快速批量加密");
            ed?.WriteMessage("\n 输入命令 STOPEX 卸载防护事件钩子");
            ed?.WriteMessage("\n=============================================\n");
        }

        public void Terminate()
        {
        }
    }

    public class CadLockCommands
    {
        [CommandMethod("LOCK")]
        public void LockCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            // 1. 弹出设置窗体
            using var form = new LockForm();
            var res = CadApp.ShowModalDialog(form);
            if (res != DialogResult.OK) return;

            var options = form.Options;
            var mode = form.SelectedMode;

            // 2. 批量处理模式
            if (mode == LockForm.LockMode.BatchFiles)
            {
                RunBatchLock(options);
                return;
            }

            // 3. 当前图纸加密处理
            using (doc.LockDocument())
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    if (CadLockEngine.IsDrawingLocked(db, tr))
                    {
                        CadApp.ShowAlertDialog("【提示】当前图纸已经过加密处理，无法再次加密！");
                        return;
                    }
                    tr.Commit();
                }

                var targetEntities = new List<ObjectId>();

                if (mode == LockForm.LockMode.SelectObjects)
                {
                    var pso = new PromptSelectionOptions
                    {
                        MessageForAdding = "\n请框选要加密的图元对象: "
                    };
                    var selRes = ed.GetSelection(pso);
                    if (selRes.Status != PromptStatus.OK) return;
                    targetEntities.AddRange(selRes.Value.GetObjectIds());
                }
                else // EntireDrawing
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var curSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                        foreach (ObjectId id in curSpace)
                        {
                            targetEntities.Add(id);
                        }
                        tr.Commit();
                    }
                }

                if (targetEntities.Count == 0)
                {
                    ed.WriteMessage("\n[提示] 未获取到有效图元，已退出。\n");
                    return;
                }

                ed.WriteMessage($"\n正在加密处理中，共 {targetEntities.Count} 个图元...\n");

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lockedBlockIds = CadLockEngine.LockEntities(db, tr, targetEntities, options);
                    tr.Commit();

                    if (options.DisableCopy || options.DisableDelete)
                    {
                        CadLockEvents.RegisterProtectedIds(lockedBlockIds);
                        CadLockEvents.HookEvents(db, doc, options);
                    }
                }

                ed.WriteMessage("\n=============================================");
                ed.WriteMessage("\n[完成] 图纸加密完成！请务必手动保存(CTRL+S)图纸以生效！");
                ed.WriteMessage("\n注意：加密操作不可逆，请确认已备份原图纸。");
                ed.WriteMessage("\n=============================================\n");
            }
        }

        [CommandMethod("BATLOCK")]
        public void BatchLock()
        {
            var options = new CadLockOptions
            {
                SplitCount = 10,
                RepeatCount = 10
            };
            RunBatchLock(options);
        }

        private void RunBatchLock(CadLockOptions options)
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            using var ofd = new OpenFileDialog
            {
                Title = "请选择要加密的 DWG 图纸 (可多选)",
                Filter = "CAD 图纸 (*.dwg)|*.dwg",
                Multiselect = true
            };

            if (ofd.ShowDialog() != DialogResult.OK || ofd.FileNames.Length == 0) return;

            string[] files = ofd.FileNames;
            ed?.WriteMessage($"\n开始批量加密，共选择 {files.Length} 份图纸...\n");

            CadLockEngine.BatchLockFiles(files, options, (file, success, msg) =>
            {
                if (success)
                {
                    ed?.WriteMessage($"\n[成功] {Path.GetFileName(file)} -> 生成 {Path.GetFileName(msg)}");
                }
                else
                {
                    ed?.WriteMessage($"\n[失败] {Path.GetFileName(file)}: {msg}");
                }
            });

            ed?.WriteMessage("\n批量加密全部完成！\n");
        }

        [CommandMethod("STOPEX")]
        public void StopProtectionEvents()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                CadLockEvents.UnhookEvents(doc.Database);
                doc.Editor.WriteMessage("\n[提示] 保护拦截钩子已卸载。\n");
            }
        }
    }
}
