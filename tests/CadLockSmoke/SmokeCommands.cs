using System;
using System.Collections.Generic;
using System.IO;
using CadLockPlugin;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CadLockSmoke.SmokeCommands))]

namespace CadLockSmoke
{
    public static class TestLog
    {
        public static int Passed;
        public static int Failed;

        public static void Check(bool ok, string name)
        {
            if (ok) Passed++; else Failed++;
            Editor().WriteMessage((ok ? "\n[PASS] " : "\n[FAIL] ") + name);
        }

        public static void Info(string msg) => Editor().WriteMessage("\n[INFO] " + msg);

        private static Editor Editor() => Application.DocumentManager.MdiActiveDocument.Editor;
    }

    public class SmokeCommands
    {
        private const string TempDir = @"D:\tmp_cadtest";
        private static readonly string BasePath = Path.Combine(TempDir, "base.dwg");

        private static readonly CadLockOptions Options = new CadLockOptions
        {
            SplitCount = 3,
            RepeatCount = 2,
            DisablePlot = true,
            DisableSelect = false
        };

        private static List<ObjectId> _protectedIds = new List<ObjectId>();

        [CommandMethod("SMOKE1")]
        public void Smoke1()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            Directory.CreateDirectory(TempDir);

            List<ObjectId> sourceIds;
            ObjectId circleId;

            using (doc.LockDocument())
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    sourceIds = new List<ObjectId>();
                    for (int i = 0; i < 5; i++)
                    {
                        var line = new Line(new Point3d(0, i * 10, 0), new Point3d(100, i * 10, 0));
                        sourceIds.Add(ms.AppendEntity(line));
                        tr.AddNewlyCreatedDBObject(line, true);
                    }

                    // 锁定图层上的图元，应被 FilterLockableEntities 排除
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    var lockedLayer = new LayerTableRecord { Name = "TEST_LOCKED_LAYER" };
                    var lockedLayerId = lt.Add(lockedLayer);
                    tr.AddNewlyCreatedDBObject(lockedLayer, true);
                    lockedLayer.IsLocked = true;

                    var circle = new Circle(new Point3d(50, 50, 0), Vector3d.ZAxis, 5.0) { LayerId = lockedLayerId };
                    circleId = ms.AppendEntity(circle);
                    tr.AddNewlyCreatedDBObject(circle, true);

                    var all = new List<ObjectId>(sourceIds) { circleId };
                    var filtered = CadLockEngine.FilterLockableEntities(db, tr, all);
                    TestLog.Check(filtered.Count == 5, "过滤锁定图层图元：期望 5，实际 " + filtered.Count);
                    TestLog.Check(!filtered.Contains(circleId), "锁定图层上的图元被排除");
                    TestLog.Check(CadLockEngine.IsLockLayerName(CadLockEngine.LockLayerName), "识别当前加密图层名");
                    TestLog.Check(CadLockEngine.IsLockLayerName("*ACADLOCKLAYER"), "识别旧版加密图层名");

                    tr.Commit();
                }

                // 保存未加密的基准图（2010 格式），供批量测试使用
                db.SaveAs(BasePath, DwgVersion.AC1024);
                TestLog.Check(File.Exists(BasePath), "基准图纸已保存 (AC1024)");

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    var ids = new List<ObjectId>();
                    foreach (ObjectId id in ms) ids.Add(id);
                    TestLog.Check(ids.Count == 6, "加密前模型空间图元：期望 6，实际 " + ids.Count);

                    var lockedIds = CadLockEngine.LockEntities(db, tr, ids, Options);
                    TestLog.Check(lockedIds.Count == 3, "2 轮 x 3 分组 -> 3 个顶层块参照，实际 " + lockedIds.Count);
                    TestLog.Check(CadLockEngine.IsDrawingLocked(db, tr), "NOD 已写入 ACAD_LOCK 标记");
                    TestLog.Check(CadLockEngine.LockEntities(db, tr, new List<ObjectId>(), Options).Count == 0,
                        "空集加密不产生块、不写加密标记");

                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    TestLog.Check(lt.Has(CadLockEngine.LockLayerName), "加密图层已创建");
                    var layer = (LayerTableRecord)tr.GetObject(lt[CadLockEngine.LockLayerName], OpenMode.ForRead);
                    TestLog.Check(!layer.IsPlottable, "DisablePlot=true -> 图层不可打印");
                    TestLog.Check(!layer.IsLocked, "DisableSelect=false -> 图层未锁定");

                    bool layerOk = true, noExplode = true, hasNestedRef = false;
                    foreach (var id in lockedIds)
                    {
                        var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                        if (br.LayerId != lt[CadLockEngine.LockLayerName]) layerOk = false;

                        var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        if (btr.Explodable) noExplode = false;
                        foreach (ObjectId inner in btr)
                        {
                            if (tr.GetObject(inner, OpenMode.ForRead) is BlockReference) hasNestedRef = true;
                        }
                    }
                    TestLog.Check(layerOk, "加密块参照全部位于加密图层");
                    TestLog.Check(noExplode, "所有块定义 Explodable=false");
                    TestLog.Check(hasNestedRef, "存在第二轮嵌套块参照");

                    bool allErased = true;
                    foreach (var id in sourceIds)
                    {
                        if (!tr.GetObject(id, OpenMode.ForRead, true).IsErased) allErased = false;
                    }
                    TestLog.Check(allErased, "原始 5 个图元已被擦除");
                    TestLog.Check(!tr.GetObject(circleId, OpenMode.ForRead, true).IsErased, "锁定图层图元未被加密/擦除");

                    tr.Commit();
                }
            }
        }

        [CommandMethod("SMOKE2")]
        public void Smoke2()
        {
            var work = Path.Combine(TempDir, "work.dwg");
            var work2 = Path.Combine(TempDir, "work2.dwg");

            foreach (var f in new[] { "work_lock.dwg", "work_lock(1).dwg", "work2.dwg", "work2_lock.dwg", "work2_lock(1).dwg" })
            {
                var p = Path.Combine(TempDir, f);
                if (File.Exists(p)) File.Delete(p);
            }

            File.Copy(BasePath, work, true);

            BatchLockResult r1 = BatchLockResult.Failed;
            string out1 = null;
            CadLockEngine.BatchLockFiles(new[] { work }, Options, (f, r, m) => { r1 = r; out1 = m; });
            TestLog.Check(r1 == BatchLockResult.Success, "首次批量加密返回 Success，实际 " + r1);
            TestLog.Check(out1 != null && File.Exists(out1), "批量输出文件已生成");
            TestLog.Check(out1 != null && Path.GetFileName(out1) == "work_lock.dwg", "输出命名为 work_lock.dwg，实际 " + (out1 == null ? "null" : Path.GetFileName(out1)));

            if (out1 != null && File.Exists(out1))
            {
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(out1, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                    db.CloseInput(true);
                    TestLog.Check(db.OriginalFileVersion == DwgVersion.AC1024,
                        "输出保持原图纸版本 AC1024，实际 " + db.OriginalFileVersion);

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        TestLog.Check(CadLockEngine.IsDrawingLocked(db, tr), "批量输出带有 ACAD_LOCK 标记");

                        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                        int refs = 0;
                        foreach (ObjectId id in ms)
                        {
                            if (tr.GetObject(id, OpenMode.ForRead) is BlockReference) refs++;
                        }
                        TestLog.Check(refs == 3, "批量输出包含 3 个顶层加密块，实际 " + refs);
                        tr.Commit();
                    }
                }
            }

            BatchLockResult r2 = BatchLockResult.Failed;
            CadLockEngine.BatchLockFiles(new[] { out1 ?? work }, Options, (f, r, m) => r2 = r);
            TestLog.Check(r2 == BatchLockResult.Skipped, "重复批量加密被跳过，实际 " + r2);
            TestLog.Check(!File.Exists(Path.Combine(TempDir, "work_lock(1).dwg")), "跳过后未产生额外文件");

            File.Copy(BasePath, work2, true);
            File.WriteAllText(Path.Combine(TempDir, "work2_lock.dwg"), "placeholder");

            BatchLockResult r3 = BatchLockResult.Failed;
            string out3 = null;
            CadLockEngine.BatchLockFiles(new[] { work2 }, Options, (f, r, m) => { r3 = r; out3 = m; });
            TestLog.Check(r3 == BatchLockResult.Success && out3 != null && Path.GetFileName(out3) == "work2_lock(1).dwg",
                "同名输出自动改名 work2_lock(1).dwg，实际 " + (out3 == null ? "null" : Path.GetFileName(out3)));
        }

        [CommandMethod("SMOKE3")]
        public void Smoke3()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            {
                var encrypted = new List<ObjectId>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is BlockReference) encrypted.Add(id);
                    }
                    tr.Commit();
                }

                TestLog.Check(encrypted.Count == 3, "删除保护：找到 3 个加密块，实际 " + encrypted.Count);
                CadLockEvents.RegisterProtectedIds(db, encrypted);
                CadLockEvents.HookEvents(db, doc, new CadLockOptions { DisableDelete = true });
                _protectedIds = encrypted;

                // 模拟用户批量选中并删除加密块
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var id in encrypted)
                    {
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
                    }
                    tr.Commit();
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    int erased = 0;
                    foreach (var id in encrypted)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead, true).IsErased) erased++;
                    }
                    TestLog.Check(erased == 3, "删除已提交，3 个对象均处于擦除状态，实际 " + erased);
                    tr.Commit();
                }
            }

            TestLog.Info("等待 CommandEnded 触发自动恢复...");
        }

        [CommandMethod("SMOKE4")]
        public void Smoke4()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                int restored = 0;
                foreach (var id in _protectedIds)
                {
                    if (tr.GetObject(id, OpenMode.ForRead, true).IsErased == false) restored++;
                }
                TestLog.Check(restored == 3, "删除保护：3 个加密块已被自动恢复，实际 " + restored);
                tr.Commit();
            }

            CadLockEvents.UnhookEvents(db);
            TestLog.Info("已卸载事件钩子");
        }

        private static CadLockOptions ProbeOptions(bool replace)
        {
            return new CadLockOptions { SplitCount = 3, RepeatCount = 2, ReplaceOriginalFile = replace };
        }

        private static void ProbeReadBack(string path, string tag, bool expectLocked, int expectRefs, int expectOthers)
        {
            TestLog.Info(tag + " 打开 " + path);
            var db = new Database(false, true);
            try
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    bool locked = CadLockEngine.IsDrawingLocked(db, tr);
                    TestLog.Check(locked == expectLocked, tag + " 加密标记=" + locked + "，期望 " + expectLocked);

                    var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    int refs = 0, others = 0;
                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is BlockReference) refs++;
                        else others++;
                    }
                    TestLog.Check(refs == expectRefs, tag + " 模型空间块参照=" + refs + "，期望 " + expectRefs);
                    TestLog.Check(others == expectOthers, tag + " 模型空间普通图元=" + others + "，期望 " + expectOthers);
                    tr.Commit();
                }
                TestLog.Info(tag + " 回读完成");
            }
            finally
            {
                db.Dispose();
            }
        }

        private static void ReadCount(string path, string tag)
        {
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                db.CloseInput(true);
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    int n = 0;
                    foreach (ObjectId id in ms) n++;
                    TestLog.Info(tag + " 图元数=" + n);
                    tr.Commit();
                }
            }
            TestLog.Info(tag + " 读取并释放 ok");
        }

        [CommandMethod("SMOKE7")]
        public void Smoke7()
        {
            // 复现测试：数据库读过某路径后，用 File.Replace 覆盖该路径，再在同一进程内重新读该路径
            var q = Path.Combine(TempDir, "reopen.dwg");
            var tmp = Path.Combine(TempDir, "reopen.tmp.dwg");
            if (File.Exists(q)) File.Delete(q);
            if (File.Exists(tmp)) File.Delete(tmp);

            File.Copy(BasePath, q, true);
            ReadCount(q, "第一次读");

            File.Copy(BasePath, tmp, true);
            File.Replace(tmp, q, null, true);
            TestLog.Info("File.Replace 覆盖完成");

            ReadCount(q, "替换后重新读");
            TestLog.Info("SMOKE7 END");
        }

        [CommandMethod("SMOKE5")]
        public void Smoke5()
        {
            var work = Path.Combine(TempDir, "replace.dwg");
            var workLockCopy = Path.Combine(TempDir, "replace_lock.dwg");
            var workUnlockCopy = Path.Combine(TempDir, "replace_unlock.dwg");
            foreach (var f in new[] { work, workLockCopy, workUnlockCopy, workLockCopy + ".check.dwg", workUnlockCopy + ".check.dwg" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            File.Copy(BasePath, work, true);

            var replaceOptions = ProbeOptions(true);

            // 1) 勾选「替换原文件」批量加密 -> 覆盖原文件、不产生 _lock 副本
            BatchLockResult r1 = BatchLockResult.Failed;
            CadLockEngine.BatchLockFiles(new[] { work }, replaceOptions, (f, r, m) => r1 = r);
            TestLog.Check(r1 == BatchLockResult.Success, "替换模式批量加密返回 Success，实际 " + r1);
            TestLog.Check(!File.Exists(workLockCopy), "替换模式未生成 _lock 副本");
            // 基准图里还有 1 个位于锁定图层上的圆，按设计不会被加密，因此普通图元为 1
            ProbeReadBack(work, "加密后原文件", true, 3, 1);

            // 2) 勾选「替换原文件」批量解锁 -> 覆盖原文件、不产生 _unlock 副本
            BatchLockResult r2 = BatchLockResult.Failed;
            CadLockEngine.BatchUnlockFiles(new[] { work }, replaceOptions, (f, r, m) => r2 = r);
            TestLog.Check(r2 == BatchLockResult.Success, "替换模式批量解锁返回 Success，实际 " + r2);
            TestLog.Check(!File.Exists(workUnlockCopy), "替换模式未生成 _unlock 副本");
            ProbeReadBack(work, "解锁后原文件", false, 0, 6);

            // 3) 已解锁的文件再次解锁应被跳过
            BatchLockResult r3 = BatchLockResult.Failed;
            CadLockEngine.BatchUnlockFiles(new[] { work }, replaceOptions, (f, r, m) => r3 = r);
            TestLog.Check(r3 == BatchLockResult.Skipped, "未加密文件解锁被跳过，实际 " + r3);
        }

        [CommandMethod("SMOKE11")]
        public void Smoke11()
        {
            // 准备一张"用户已手动解锁图层"的加密图纸，供后续打开时验证自动恢复
            var g = Path.Combine(TempDir, "guard.dwg");
            var outPath = Path.Combine(TempDir, "guard_unlocked.dwg");
            foreach (var f in new[] { g, outPath })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            File.Copy(BasePath, g, true);

            var options = ProbeOptions(true);
            options.DisableSelect = true;
            options.DisableDelete = true;
            options.DisableModify = true;

            BatchLockResult r = BatchLockResult.Failed;
            CadLockEngine.BatchLockFiles(new[] { g }, options, (f, res, m) => r = res);
            TestLog.Check(r == BatchLockResult.Success, "保护选项加密成功，实际 " + r);

            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(g, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var saved = CadLockEngine.LoadProtectionOptions(db, tr);
                    TestLog.Check(saved != null && saved.DisableSelect && saved.DisableDelete && saved.DisableModify,
                        "保护选项已随图纸保存（含不可更改）");

                    ObjectId layerId = CadLockEngine.FindLockLayerId(db, tr);
                    var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                    TestLog.Check(layer.IsLocked, "加密后图层为锁定状态");

                    // 模拟用户在无插件环境下把图层解锁
                    layer.IsLocked = false;
                    tr.Commit();
                }

                db.SaveAs(outPath, DwgVersion.Current);
            }

            TestLog.Check(File.Exists(outPath), "已生成 guard_unlocked.dwg（图层已被解锁的加密图纸）");
        }

        [CommandMethod("SMOKE12")]
        public void Smoke12()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                TestLog.Check(CadLockEngine.IsDrawingLocked(db, tr), "打开后 ACAD_LOCK 标记仍在");

                var saved = CadLockEngine.LoadProtectionOptions(db, tr);
                TestLog.Check(saved != null && saved.DisableSelect, "打开后读取到保存的保护选项");

                ObjectId layerId = CadLockEngine.FindLockLayerId(db, tr);
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                TestLog.Check(layer.IsLocked, "打开图纸后加密图层被自动重新锁定，实际 IsLocked=" + layer.IsLocked);
                tr.Commit();
            }

        }

        [CommandMethod("SMOKE13")]
        public void Smoke13()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            // accoreconsole 没有 UI 消息循环，Idle 不会触发，这里手动执行一次（真实 CAD 中由 Idle 自动完成）
            CadLockEvents.FlushPendingProtection();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = CadLockEngine.FindLockLayerId(db, tr);
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                TestLog.Check(layer.IsLocked, "解锁命令执行后图层被自动恢复锁定，实际 IsLocked=" + layer.IsLocked);
                tr.Commit();
            }
        }

        [CommandMethod("SMOKE14")]
        public void Smoke14()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            {
                var options = new CadLockOptions
                {
                    SplitCount = 1,
                    RepeatCount = 1,
                    DisableSelect = false,
                    DisableModify = true
                };

                ObjectId blockId;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    var line = new Line(new Point3d(0, 0, 0), new Point3d(10, 0, 0));
                    var lineId = ms.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    var ids = CadLockEngine.LockEntities(db, tr, new[] { lineId }, options);
                    if (ids.Count == 0)
                    {
                        tr.Abort();
                        TestLog.Check(false, "不可更改：未能生成加密块");
                        return;
                    }
                    blockId = ids[0];
                    tr.Commit();
                }

                CadLockEvents.RegisterProtectedIds(db, new[] { blockId });
                CadLockEvents.HookEvents(db, doc, options);

                Point3d originalPosition;
                double originalRotation;
                Scale3d originalScale;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var br = (BlockReference)tr.GetObject(blockId, OpenMode.ForRead);
                    originalPosition = br.Position;
                    originalRotation = br.Rotation;
                    originalScale = br.ScaleFactors;
                    tr.Commit();
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var br = (BlockReference)tr.GetObject(blockId, OpenMode.ForWrite);
                    br.Position = originalPosition + new Vector3d(100, 50, 0);
                    br.Rotation = originalRotation + Math.PI / 2;
                    br.ScaleFactors = new Scale3d(2, 2, 2);
                    tr.Commit();
                }

                CadLockEvents.FlushPendingProtection();

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var br = (BlockReference)tr.GetObject(blockId, OpenMode.ForRead);
                    TestLog.Check(br.Position.IsEqualTo(originalPosition, new Tolerance(1e-6, 1e-6)),
                        "不可更改：插入点已还原");
                    TestLog.Check(Math.Abs(br.Rotation - originalRotation) < 1e-6,
                        "不可更改：旋转角已还原");
                    TestLog.Check(Math.Abs(br.ScaleFactors.X - originalScale.X) < 1e-6 &&
                                  Math.Abs(br.ScaleFactors.Y - originalScale.Y) < 1e-6 &&
                                  Math.Abs(br.ScaleFactors.Z - originalScale.Z) < 1e-6,
                        "不可更改：比例已还原");
                    tr.Commit();
                }

                CadLockEvents.UnhookEvents(db);
            }
        }

        [CommandMethod("SMOKE9")]
        public void Smoke9()
        {
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                "\n[SUMMARY] PASS=" + TestLog.Passed + " FAIL=" + TestLog.Failed + "\n");
        }
    }
}
