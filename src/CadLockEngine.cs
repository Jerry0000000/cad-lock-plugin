using System;
using System.Collections.Generic;
using System.IO;

#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace CadLockPlugin
{
    public static class CadLockEngine
    {
        public const string LockDictionaryKey = "ACAD_LOCK";
        public const string LockLayerName = "ACAD_LOCK_LAYER";

        /// <summary>
        /// 检查图纸是否已经被加密锁定
        /// </summary>
        public static bool IsDrawingLocked(Database db, Transaction tr)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            return nod.Contains(LockDictionaryKey);
        }

        /// <summary>
        /// 在命名对象字典(NOD)中标记图纸已加密，防止重复加密
        /// </summary>
        public static void MarkDrawingAsLocked(Database db, Transaction tr)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
            if (!nod.Contains(LockDictionaryKey))
            {
                var dict = new DBDictionary();
                nod.SetAt(LockDictionaryKey, dict);
                tr.AddNewlyCreatedDBObject(dict, true);
            }
        }

        /// <summary>
        /// 获取或创建加密专用图层 (可控是否打印、是否锁定/不可捕捉)
        /// </summary>
        public static ObjectId GetOrCreateLockLayer(Database db, Transaction tr, CadLockOptions options)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(LockLayerName))
            {
                ObjectId layerId = lt[LockLayerName];
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                if (options.DisablePlot)
                {
                    layer.IsPlottable = false;
                }
                if (options.DisableSnap)
                {
                    layer.IsLocked = true;
                }
                return layerId;
            }
            else
            {
                lt.UpgradeOpen();
                var layer = new LayerTableRecord
                {
                    Name = LockLayerName,
                    IsPlottable = !options.DisablePlot,
                    IsLocked = options.DisableSnap
                };
                ObjectId layerId = lt.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
                return layerId;
            }
        }

        /// <summary>
        /// 核心加密方法：对指定的实体集合进行分块与多层嵌套不可炸开块包装
        /// </summary>
        public static List<ObjectId> LockEntities(Database db, Transaction tr, IEnumerable<ObjectId> initialEntityIds, CadLockOptions options)
        {
            ObjectId lockLayerId = GetOrCreateLockLayer(db, tr, options);
            var currentSpace = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);

            List<ObjectId> currentRoundIds = new List<ObjectId>(initialEntityIds);
            if (currentRoundIds.Count == 0) return currentRoundIds;

            int repeatCount = Math.Max(1, options.RepeatCount);
            int splitCount = Math.Max(1, options.SplitCount);

            for (int r = 0; r < repeatCount; r++)
            {
                var nextRoundIds = new List<ObjectId>();

                // 将当前轮次的图元分块分散为 splitCount 组
                var groups = new List<List<ObjectId>>();
                for (int i = 0; i < splitCount; i++)
                {
                    groups.Add(new List<ObjectId>());
                }

                int idx = 0;
                foreach (var id in currentRoundIds)
                {
                    groups[idx % splitCount].Add(id);
                    idx++;
                }

                // 对每一个分组创建独立的不可炸开匿名块 (Explodable = false)
                for (int g = 0; g < splitCount; g++)
                {
                    var groupIds = groups[g];
                    if (groupIds.Count == 0) continue;

                    // 创建无名块 (*U) 块定义
                    var btr = new BlockTableRecord
                    {
                        Name = "*U",        // 匿名块
                        Explodable = false  // 核心：CAD 炸开命令 EXPLODE 拒绝炸开！
                    };

                    ObjectId btrId = bt.Add(btr);
                    tr.AddNewlyCreatedDBObject(btr, true);

                    // 将组内图元移入该块定义
                    foreach (var entId in groupIds)
                    {
                        var ent = (Entity)tr.GetObject(entId, OpenMode.ForWrite);
                        var clone = (Entity)ent.Clone();
                        btr.AppendEntity(clone);
                        tr.AddNewlyCreatedDBObject(clone, true);

                        // 从当前空间擦除原图元
                        ent.Erase(true);
                    }

                    // 创建块参照插入当前空间
                    var br = new BlockReference(Point3d.Origin, btrId)
                    {
                        LayerId = lockLayerId
                    };

                    ObjectId brId = currentSpace.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    nextRoundIds.Add(brId);
                }

                currentRoundIds = nextRoundIds;
            }

            MarkDrawingAsLocked(db, tr);
            return currentRoundIds;
        }

        /// <summary>
        /// 批量加密离线 DWG 文件 (后台静默读取与保存)
        /// </summary>
        public static void BatchLockFiles(IEnumerable<string> filePaths, CadLockOptions options, Action<string, bool, string>? progressCallback = null)
        {
            foreach (var filePath in filePaths)
            {
                if (!File.Exists(filePath)) continue;

                string dir = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                string ext = Path.GetExtension(filePath);
                string outPath = Path.Combine(dir, $"{fileName}_lock{ext}");

                try
                {
                    using (var db = new Database(false, true))
                    {
                        db.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                        db.CloseInput(true);

                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            var modelSpace = (BlockTableRecord)tr.GetObject(
                                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                            var entityIds = new List<ObjectId>();
                            foreach (ObjectId id in modelSpace)
                            {
                                entityIds.Add(id);
                            }

                            if (entityIds.Count > 0)
                            {
                                LockEntities(db, tr, entityIds, options);
                            }

                            tr.Commit();
                        }

                        db.SaveAs(outPath, DwgVersion.Current);
                    }

                    progressCallback?.Invoke(filePath, true, outPath);
                }
                catch (Exception ex)
                {
                    progressCallback?.Invoke(filePath, false, ex.Message);
                }
            }
        }
    }
}
