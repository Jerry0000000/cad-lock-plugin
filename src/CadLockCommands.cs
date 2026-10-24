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
            // 打开已加密图纸时自动恢复图层锁定与复制/删除拦截
            CadLockEvents.Install();

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\n=============================================");
#if ZWCAD
            ed?.WriteMessage("\n CAD 图纸加密保护插件 (中望CAD版) 已成功加载！");
#else
            ed?.WriteMessage("\n CAD 图纸加密保护插件 (AutoCAD版) 已成功加载！");
#endif
            ed?.WriteMessage("\n 输入命令 LOCK 弹出加密/解锁配置面板");
            ed?.WriteMessage("\n 输入命令 BATLOCK 快速批量加密，UNLOCK 一键解锁当前图纸");
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

            // 1. 弹出设置窗体对话框
            using var form = new LockForm();
            var res = CadApp.ShowModalDialog(form);
            if (res != DialogResult.OK || form.UserAction == LockForm.ActionType.None) return;

            var options = form.Options;
            var action = form.UserAction;

            // 2. 批量处理 / 解锁模式
            switch (action)
            {
                case LockForm.ActionType.BatchFiles:
                    RunBatchLock(options);
                    return;
                case LockForm.ActionType.BatchUnlock:
                    RunBatchUnlock(options);
                    return;
                case LockForm.ActionType.UnlockDrawing:
                    UnlockCurrentDrawing(db, doc);
                    return;
            }

            // 3. 当前图纸加密处理
            using (doc.LockDocument())
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    if (CadLockEngine.IsDrawingLocked(db, tr))
                    {
                        CadApp.ShowAlertDialog("【提示】当前图纸已经过加密处理，无法再次加密！\n如需重新加密，请先使用解锁功能还原。");
                        return;
                    }
                    tr.Commit();
                }

                var targetEntities = new List<ObjectId>();

                if (action == LockForm.ActionType.SelectObjects)
                {
                    var pso = new PromptSelectionOptions
                    {
                        MessageForAdding = "\n请框选要加密的图元对象: "
                    };
                    var selRes = ed.GetSelection(pso);
                    if (selRes.Status != PromptStatus.OK) return;

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        targetEntities.AddRange(
                            CadLockEngine.FilterLockableEntities(db, tr, selRes.Value.GetObjectIds()));
                        tr.Commit();
                    }
                }
                else // EntireDrawing 全图自动加密
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var curSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                        foreach (ObjectId id in curSpace)
                        {
                            targetEntities.Add(id);
                        }

                        targetEntities = CadLockEngine.FilterLockableEntities(db, tr, targetEntities);
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

                    if (lockedBlockIds.Count == 0)
                    {
                        ed.WriteMessage("\n[提示] 没有图元可以加密，操作已取消。\n");
                        return;
                    }

                    if (options.DisableCopy || options.DisableDelete)
                    {
                        CadLockEvents.RegisterProtectedIds(db, lockedBlockIds);
                    }

                    CadLockEvents.HookEvents(db, doc, options);
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
                SplitCount = 100,
                RepeatCount = 10
            };
            RunBatchLock(options);
        }

        [CommandMethod("UNLOCK")]
        public void UnlockCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            UnlockCurrentDrawing(doc.Database, doc);
        }

        [CommandMethod("BATUNLOCK")]
        public void BatchUnlock()
        {
            RunBatchUnlock(new CadLockOptions());
        }

        /// <summary>
        /// 解锁当前打开的图纸
        /// </summary>
        private void UnlockCurrentDrawing(Database db, Document doc)
        {
            var ed = doc.Editor;

            using (doc.LockDocument())
            {
                int unlocked;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    unlocked = CadLockEngine.UnlockAllSpaces(db, tr);
                    tr.Commit();
                }

                // 解锁后原本的复制/删除拦截必须撤掉，否则残留钩子会继续拦截
                CadLockEvents.UnhookEvents(db);

                try
                {
                    ed.Regen();
                }
                catch
                {
                    // 某些图纸状态下重生成会失败，不影响解锁结果
                }

                ed.WriteMessage("\n=============================================");
                if (unlocked == 0)
                {
                    ed.WriteMessage("\n[提示] 当前图纸没有可解锁的加密图形。");
                }
                else
                {
                    ed.WriteMessage($"\n[完成] 已解锁 {unlocked} 个加密块，图形已还原为普通图元。");
                    ed.WriteMessage("\n请手动保存(CTRL+S)图纸以生效！");
                }
                ed.WriteMessage("\n=============================================\n");
            }
        }

        private const string BatchFilter =
            "支持的文件 (*.dwg;*.xlsx;*.xls;*.xlsm;*.docx;*.doc;*.docm)|*.dwg;*.xlsx;*.xls;*.xlsm;*.docx;*.doc;*.docm" +
            "|CAD 图纸 (*.dwg)|*.dwg" +
            "|Excel 表格 (*.xlsx;*.xls;*.xlsm)|*.xlsx;*.xls;*.xlsm" +
            "|Word 文档 (*.docx;*.doc;*.docm)|*.docx;*.doc;*.docm";

        private static void SplitFiles(string[] files, out List<string> dwgFiles, out List<string> officeFiles)
        {
            dwgFiles = new List<string>();
            officeFiles = new List<string>();

            foreach (var file in files)
            {
                if (OfficeLockEngine.IsDwgFile(file)) dwgFiles.Add(file);
                else if (OfficeLockEngine.IsOfficeFile(file)) officeFiles.Add(file);
            }
        }

        private void RunBatchLock(CadLockOptions options)
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            using var ofd = new OpenFileDialog
            {
                Title = "请选择要加密的图纸/文档 (可多选)",
                Filter = BatchFilter,
                Multiselect = true
            };

            if (ofd.ShowDialog() != DialogResult.OK || ofd.FileNames.Length == 0) return;

            string[] files = ofd.FileNames;
            SplitFiles(files, out var dwgFiles, out var officeFiles);

            ed?.WriteMessage($"\n开始批量加密，共选择 {files.Length} 份文件（图纸 {dwgFiles.Count} 份，文档 {officeFiles.Count} 份）...\n");
            if (options.ReplaceOriginalFile)
            {
                ed?.WriteMessage("【注意】已勾选「替换原文件」，处理结果将直接覆盖所选文件！\n");
            }

            int ok = 0, skip = 0, fail = 0;

            Action<string, BatchLockResult, string> report = (file, result, msg) =>
            {
                switch (result)
                {
                    case BatchLockResult.Success:
                        ok++;
                        ed?.WriteMessage($"\n[成功] {Path.GetFileName(file)} -> {msg}");
                        break;
                    case BatchLockResult.Skipped:
                        skip++;
                        ed?.WriteMessage($"\n[跳过] {Path.GetFileName(file)}: {msg}");
                        break;
                    default:
                        fail++;
                        ed?.WriteMessage($"\n[失败] {Path.GetFileName(file)}: {msg}");
                        break;
                }
            };

            if (dwgFiles.Count > 0)
            {
                CadLockEngine.BatchLockFiles(dwgFiles, options, report);
            }

            if (officeFiles.Count > 0)
            {
                OfficeLockEngine.BatchProtectFiles(officeFiles, options, report);
            }

            ed?.WriteMessage($"\n批量加密完成：成功 {ok} 份，跳过 {skip} 份，失败 {fail} 份。\n");
        }

        private void RunBatchUnlock(CadLockOptions options)
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            using var ofd = new OpenFileDialog
            {
                Title = "请选择要解锁的图纸/文档 (可多选)",
                Filter = BatchFilter,
                Multiselect = true
            };

            if (ofd.ShowDialog() != DialogResult.OK || ofd.FileNames.Length == 0) return;

            string[] files = ofd.FileNames;
            SplitFiles(files, out var dwgFiles, out var officeFiles);

            ed?.WriteMessage($"\n开始批量解锁，共选择 {files.Length} 份文件（图纸 {dwgFiles.Count} 份，文档 {officeFiles.Count} 份）...\n");
            if (options.ReplaceOriginalFile)
            {
                ed?.WriteMessage("【注意】已勾选「替换原文件」，解锁结果将直接覆盖所选文件！\n");
            }

            int ok = 0, skip = 0, fail = 0;

            Action<string, BatchLockResult, string> report = (file, result, msg) =>
            {
                switch (result)
                {
                    case BatchLockResult.Success:
                        ok++;
                        ed?.WriteMessage($"\n[成功] {Path.GetFileName(file)} -> {msg}");
                        break;
                    case BatchLockResult.Skipped:
                        skip++;
                        ed?.WriteMessage($"\n[跳过] {Path.GetFileName(file)}: {msg}");
                        break;
                    default:
                        fail++;
                        ed?.WriteMessage($"\n[失败] {Path.GetFileName(file)}: {msg}");
                        break;
                }
            };

            if (dwgFiles.Count > 0)
            {
                CadLockEngine.BatchUnlockFiles(dwgFiles, options, report);
            }

            if (officeFiles.Count > 0)
            {
                OfficeLockEngine.BatchUnprotectFiles(officeFiles, options, report);
            }

            ed?.WriteMessage($"\n批量解锁完成：成功 {ok} 份，跳过 {skip} 份，失败 {fail} 份。\n");
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
