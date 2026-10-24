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
        /// 旧版 lockdwg.dll 使用的锁定图层名。旧版文件里实际见到的是带星号的
        /// "*ACADLOCKLAYER"；同时兼容不带星号和 M 后缀的变体，方便读取早期图纸。
        /// </summary>
        private static readonly string[] LegacyLockLayerNames =
        {
            "*ACADLOCKLAYER",
            "ACADLOCKLAYER",
            "*ACADLOCKLAYERM",
            "ACADLOCKLAYERM"
        };

        /// <summary>加密时使用的保护选项，随图纸保存，重新打开后据此恢复防护</summary>
        public const string LockOptionsKey = "OPTIONS";

        private const string LockSuffix = "_lock";
        private const string UnlockSuffix = "_unlock";

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
        /// 移除加密标记，使图纸可以重新加密
        /// </summary>
        public static void RemoveLockMark(Database db, Transaction tr)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(LockDictionaryKey)) return;

            ObjectId entryId = (ObjectId)nod[LockDictionaryKey];
            nod.UpgradeOpen();
            nod.Remove(LockDictionaryKey);

            var entry = tr.GetObject(entryId, OpenMode.ForWrite, false);
            entry?.Erase();
        }

        /// <summary>
        /// 把保护选项写进图纸的 ACAD_LOCK 字典；重新打开图纸时据此恢复图层状态与拦截钩子
        /// </summary>
        public static void SaveProtectionOptions(Database db, Transaction tr, CadLockOptions options)
        {
            if (options == null) return;

            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead, false);
            if (nod == null || !nod.Contains(LockDictionaryKey)) return;

            var dict = tr.GetObject((ObjectId)nod[LockDictionaryKey], OpenMode.ForWrite, false) as DBDictionary;
            if (dict == null) return;

            var data = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Int16, options.DisableSelect ? 1 : 0),
                new TypedValue((int)DxfCode.Int16, options.DisablePlot ? 1 : 0),
                new TypedValue((int)DxfCode.Int16, options.DisableCopy ? 1 : 0),
                new TypedValue((int)DxfCode.Int16, options.DisableDelete ? 1 : 0),
                new TypedValue((int)DxfCode.Int32, options.SplitCount),
                new TypedValue((int)DxfCode.Int32, options.RepeatCount),
                // 追加在末尾：旧版本插件只读前 6 项，不会把新增项误当成分块数量
                new TypedValue((int)DxfCode.Int16, options.DisableModify ? 1 : 0)
            };

            var xrec = new Xrecord { Data = new ResultBuffer(data) };
            dict.SetAt(LockOptionsKey, xrec);
            tr.AddNewlyCreatedDBObject(xrec, true);
        }

        /// <summary>
        /// 读取图纸中保存的保护选项，未加密或无记录时返回 null
        /// </summary>
        public static CadLockOptions? LoadProtectionOptions(Database db, Transaction tr)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead, false);
            if (nod == null || !nod.Contains(LockDictionaryKey)) return null;

            var dict = tr.GetObject((ObjectId)nod[LockDictionaryKey], OpenMode.ForRead, false) as DBDictionary;
            if (dict == null || !dict.Contains(LockOptionsKey)) return null;

            var xrec = tr.GetObject((ObjectId)dict[LockOptionsKey], OpenMode.ForRead, false) as Xrecord;
            if (xrec == null || xrec.Data == null) return null;

            TypedValue[] values = xrec.Data.AsArray();
            if (values == null || values.Length < 4) return null;

            return new CadLockOptions
            {
                DisableSelect = ToInt(values[0]) != 0,
                DisablePlot = ToInt(values[1]) != 0,
                DisableCopy = ToInt(values[2]) != 0,
                DisableDelete = ToInt(values[3]) != 0,
                DisableModify = values.Length > 6 && ToInt(values[6]) != 0,
                SplitCount = values.Length > 4 ? ToInt(values[4]) : 1,
                RepeatCount = values.Length > 5 ? ToInt(values[5]) : 1
            };
        }

        private static int ToInt(TypedValue value)
        {
            try
            {
                return Convert.ToInt32(value.Value);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 取出图纸中所有位于加密图层上的块参照（即受保护的顶层对象）
        /// </summary>
        public static List<ObjectId> GetLockedBlockIds(Database db, Transaction tr)
        {
            var result = new List<ObjectId>();
            List<ObjectId> lockLayerIds = FindLockLayerIds(db, tr);
            if (lockLayerIds.Count == 0) return result;
            var lockLayerSet = new HashSet<ObjectId>(lockLayerIds);

            foreach (var spaceId in GetAllSpaceIds(db, tr))
            {
                foreach (var id in GetSpaceEntityIds(tr, spaceId))
                {
                    try
                    {
                        var br = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                        if (br != null && lockLayerSet.Contains(br.LayerId))
                        {
                            result.Add(id);
                        }
                    }
                    catch
                    {
                        // 单个对象异常跳过
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 查找所有可识别的加密图层：当前格式 + 旧版 lockdwg.dll 格式。
        /// 顺序为当前格式优先，旧格式在后。
        /// </summary>
        public static List<ObjectId> FindLockLayerIds(Database db, Transaction tr)
        {
            var result = new List<ObjectId>();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            AddLayerIfExists(lt, LockLayerName, result);
            foreach (string name in LegacyLockLayerNames)
            {
                AddLayerIfExists(lt, name, result);
            }
            return result;
        }

        /// <summary>
        /// 只查找旧版 lockdwg.dll 图层，用于判断是否为旧格式文件。
        /// </summary>
        public static List<ObjectId> FindLegacyLockLayerIds(Database db, Transaction tr)
        {
            var result = new List<ObjectId>();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (string name in LegacyLockLayerNames)
            {
                AddLayerIfExists(lt, name, result);
            }
            return result;
        }

        private static void AddLayerIfExists(LayerTable lt, string name, List<ObjectId> result)
        {
            if (!lt.Has(name)) return;
            ObjectId id = lt[name];
            if (id.IsNull || result.Contains(id)) return;
            result.Add(id);
        }

        /// <summary>
        /// 图层名是否属于加密图层（当前格式或旧格式）。
        /// </summary>
        public static bool IsLockLayerName(string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return false;
            if (string.Equals(layerName, LockLayerName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string name in LegacyLockLayerNames)
            {
                if (string.Equals(layerName, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 兼容旧调用：返回第一个加密图层（当前格式优先）。
        /// 需要同时处理新旧图层时请使用 FindLockLayerIds。
        /// </summary>
        public static ObjectId FindLockLayerId(Database db, Transaction tr)
        {
            List<ObjectId> ids = FindLockLayerIds(db, tr);
            return ids.Count > 0 ? ids[0] : ObjectId.Null;
        }

        /// <summary>
        /// 获取或创建加密专用图层 (可控是否打印、是否锁定/不可选中)
        /// 图层的 IsLocked 只影响选中与编辑，不影响对象捕捉
        /// </summary>
        public static ObjectId GetOrCreateLockLayer(Database db, Transaction tr, CadLockOptions options)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(LockLayerName))
            {
                ObjectId layerId = lt[LockLayerName];
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                // 每次按当前选项同步，避免上一次加密的图层状态残留
                layer.IsPlottable = !options.DisablePlot;
                layer.IsLocked = options.DisableSelect;
                return layerId;
            }
            else
            {
                lt.UpgradeOpen();
                var layer = new LayerTableRecord
                {
                    Name = LockLayerName,
                    IsPlottable = !options.DisablePlot,
                    IsLocked = options.DisableSelect
                };
                ObjectId layerId = lt.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
                return layerId;
            }
        }

        /// <summary>
        /// 解锁后恢复加密图层的正常状态
        /// </summary>
        public static void ResetLockLayer(Database db, Transaction tr)
        {
            foreach (ObjectId layerId in FindLockLayerIds(db, tr))
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite, false);
                if (layer == null) continue;
                layer.IsLocked = false;
                layer.IsPlottable = true;
            }
        }

        /// <summary>
        /// 获取图纸中所有可加密空间：模型空间 + 全部布局(图纸空间)
        /// 只处理模型空间会导致「整张图都在布局里」的图纸加密后毫无变化
        /// </summary>
        public static List<ObjectId> GetAllSpaceIds(Database db, Transaction tr)
        {
            var spaces = new List<ObjectId>();

            try
            {
                ObjectId msId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                if (!msId.IsNull) spaces.Add(msId);
            }
            catch
            {
                // 极端损坏的图纸忽略模型空间
            }

            try
            {
                var layoutDict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead, false);
                if (layoutDict != null)
                {
                    foreach (DBDictionaryEntry entry in layoutDict)
                    {
                        var layout = tr.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                        if (layout == null) continue;
                        ObjectId btrId = layout.BlockTableRecordId;
                        if (btrId.IsNull || spaces.Contains(btrId)) continue;
                        spaces.Add(btrId);
                    }
                }
            }
            catch
            {
                // 布局字典异常时仅处理模型空间
            }

            return spaces;
        }

        /// <summary>
        /// 获取某个空间(块表记录)中的全部对象ID
        /// </summary>
        public static List<ObjectId> GetSpaceEntityIds(Transaction tr, ObjectId spaceId)
        {
            var ids = new List<ObjectId>();
            var btr = tr.GetObject(spaceId, OpenMode.ForRead, false) as BlockTableRecord;
            if (btr == null) return ids;
            foreach (ObjectId id in btr)
            {
                ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// 过滤掉无法安全封装进块定义的对象：
        /// 布局视口、已在加密图层上的对象、锁定图层上的对象、非实体对象
        /// </summary>
        public static List<ObjectId> FilterLockableEntities(Database db, Transaction tr, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            if (ids == null) return result;

            List<ObjectId> lockLayerIds = FindLockLayerIds(db, tr);
            var lockLayerSet = new HashSet<ObjectId>(lockLayerIds);
            var layerLockCache = new Dictionary<ObjectId, bool>();

            foreach (var id in ids)
            {
                if (id.IsNull || id.IsErased || !id.IsValid) continue;

                DBObject obj;
                try
                {
                    obj = tr.GetObject(id, OpenMode.ForRead, false);
                }
                catch
                {
                    continue;
                }

                if (obj == null || obj.IsErased) continue;
                if (!(obj is Entity ent)) continue;

                // 视口等对象不允许被 AppendEntity 到块定义中
                if (ent is Viewport) continue;

                // 已经在加密图层上的对象（含旧版图层），避免重复封装
                if (lockLayerSet.Contains(ent.LayerId)) continue;

                // 锁定图层上的对象无法被擦除/克隆
                if (IsLayerLocked(tr, ent.LayerId, layerLockCache)) continue;

                result.Add(id);
            }

            return result;
        }

        private static bool IsLayerLocked(Transaction tr, ObjectId layerId, Dictionary<ObjectId, bool> cache)
        {
            if (layerId.IsNull) return false;

            if (cache.TryGetValue(layerId, out bool locked)) return locked;

            try
            {
                var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                locked = layer.IsLocked;
            }
            catch
            {
                locked = false;
            }

            cache[layerId] = locked;
            return locked;
        }

        /// <summary>
        /// 对当前空间执行加密
        /// </summary>
        public static List<ObjectId> LockEntities(Database db, Transaction tr, IEnumerable<ObjectId> initialEntityIds, CadLockOptions options)
        {
            return LockEntities(db, tr, db.CurrentSpaceId, initialEntityIds, options);
        }

        /// <summary>
        /// 核心加密方法：对指定空间内的实体集合进行分块与多层嵌套不可炸开块包装
        /// 返回最外层生成的加密块参照ID，为空表示本次没有任何图元被加密
        /// </summary>
        public static List<ObjectId> LockEntities(Database db, Transaction tr, ObjectId spaceId, IEnumerable<ObjectId> initialEntityIds, CadLockOptions options)
        {
            // 先剔除不能封装进块定义的对象，避免中途抛异常导致整张图纸加密失败
            List<ObjectId> currentRoundIds = FilterLockableEntities(db, tr, initialEntityIds);
            if (currentRoundIds.Count == 0) return new List<ObjectId>();

            ObjectId lockLayerId = GetOrCreateLockLayer(db, tr, options);
            var currentSpace = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForWrite);
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);

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

            if (currentRoundIds.Count > 0)
            {
                MarkDrawingAsLocked(db, tr);
                SaveProtectionOptions(db, tr, options);
            }

            return currentRoundIds;
        }

        /// <summary>
        /// 对模型空间与全部布局执行加密，返回生成的加密块参照总数
        /// </summary>
        public static List<ObjectId> LockAllSpaces(Database db, Transaction tr, CadLockOptions options)
        {
            var created = new List<ObjectId>();

            foreach (var spaceId in GetAllSpaceIds(db, tr))
            {
                try
                {
                    var ids = GetSpaceEntityIds(tr, spaceId);
                    if (ids.Count == 0) continue;
                    created.AddRange(LockEntities(db, tr, spaceId, ids, options));
                }
                catch
                {
                    // 单个布局加密失败不影响其它空间，最终由调用方按加密块数量判定结果
                }
            }

            return created;
        }

        /// <summary>
        /// 解锁指定空间：把加密匿名块中的图元还原回原空间，并删除加密块
        /// 返回成功还原的加密块数量
        /// </summary>
        public static int UnlockSpaces(Database db, Transaction tr, IEnumerable<ObjectId> spaceIds)
        {
            List<ObjectId> lockLayerIds = FindLockLayerIds(db, tr);
            if (lockLayerIds.Count == 0) return 0;

            // 先解除图层锁定：锁定图层上的对象无法被擦除，否则加密块拆不掉
            ResetLockLayer(db, tr);
            var lockLayerSet = new HashSet<ObjectId>(lockLayerIds);

            int count = 0;

            foreach (var spaceId in spaceIds)
            {
                try
                {
                    count += UnlockSpace(tr, spaceId, lockLayerSet);
                }
                catch
                {
                    // 单个空间解锁失败不影响其它空间
                }
            }

            if (count > 0)
            {
                RemoveLockMark(db, tr);
            }

            return count;
        }

        /// <summary>
        /// 解锁全部空间 (模型空间 + 布局)
        /// </summary>
        public static int UnlockAllSpaces(Database db, Transaction tr)
        {
            return UnlockSpaces(db, tr, GetAllSpaceIds(db, tr));
        }

        private static int UnlockSpace(Transaction tr, ObjectId spaceId, HashSet<ObjectId> lockLayerIds)
        {
            // 必须 ForWrite：还原时要往该空间 AppendEntity，ForRead 会抛 eNotOpenForWrite
            var space = tr.GetObject(spaceId, OpenMode.ForWrite, false) as BlockTableRecord;
            if (space == null) return 0;

            int count = 0;

            // 加密是多层嵌套的，每拆一层都会暴露下一层加密块，必须循环到该空间内不再有加密块
            for (int depth = 0; depth < 256; depth++)
            {
                var targets = new List<ObjectId>();
                foreach (ObjectId id in space)
                {
                    BlockReference? br;
                    try
                    {
                        br = tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                    }
                    catch
                    {
                        continue;
                    }

                    if (br == null) continue;
                    // 只处理放在加密图层上的块参照，避免误伤普通图块
                    if (!lockLayerIds.Contains(br.LayerId)) continue;
                    targets.Add(id);
                }

                if (targets.Count == 0) break;

                bool any = false;
                foreach (var brId in targets)
                {
                    if (ExplodeLockBlock(tr, space, brId))
                    {
                        count++;
                        any = true;
                    }
                }

                // 一层都拆不动时退出，防止死循环
                if (!any) break;
            }

            return count;
        }

        /// <summary>
        /// 把加密块拆解回普通图元 (仅匿名块)，成功返回 true
        /// </summary>
        private static bool ExplodeLockBlock(Transaction tr, BlockTableRecord space, ObjectId brId)
        {
            var clones = new List<Entity>();
            BlockReference? br = null;
            BlockTableRecord? btr = null;

            try
            {
                br = tr.GetObject(brId, OpenMode.ForRead, false) as BlockReference;
                if (br == null) return false;

                btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                // 只拆本插件生成的匿名块，命名块一律不动
                if (btr == null || !btr.IsAnonymous) return false;

                foreach (ObjectId childId in btr)
                {
                    var child = tr.GetObject(childId, OpenMode.ForRead, false) as Entity;
                    if (child == null) continue;

                    var clone = (Entity)child.Clone();
                    clone.TransformBy(br.BlockTransform);
                    clones.Add(clone);
                }
            }
            catch
            {
                foreach (var c in clones) c.Dispose();
                return false;
            }

            try
            {
                foreach (var c in clones)
                {
                    space.AppendEntity(c);
                    tr.AddNewlyCreatedDBObject(c, true);
                }

                br.UpgradeOpen();
                br.Erase(true);

                btr.UpgradeOpen();
                btr.Erase(true);
                return true;
            }
            catch
            {
                foreach (var c in clones)
                {
                    if (!c.IsErased && !c.IsDisposed) c.Dispose();
                }
                return false;
            }
        }

        /// <summary>
        /// 批量加密离线 DWG 文件 (后台静默读取与保存)
        /// options.ReplaceOriginalFile = true 时直接覆盖原文件，不再生成 _lock 副本
        /// </summary>
        public static void BatchLockFiles(IEnumerable<string> filePaths, CadLockOptions options, Action<string, BatchLockResult, string>? progressCallback = null)
        {
            if (filePaths == null || options == null) return;

            foreach (var filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    progressCallback?.Invoke(filePath ?? string.Empty, BatchLockResult.Failed, "文件不存在");
                    continue;
                }

                try
                {
                    string savedPath;

                    using (var db = new Database(false, true))
                    {
                        db.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                        db.CloseInput(true);

                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            if (IsDrawingLocked(db, tr))
                            {
                                bool legacy = FindLegacyLockLayerIds(db, tr).Count > 0;
                                progressCallback?.Invoke(filePath, BatchLockResult.Skipped,
                                    legacy
                                        ? "检测到旧版 lockdwg.dll 格式，无法直接重新加密；请使用原始 DWG 或先恢复旧文件"
                                        : "图纸已经过加密处理，已跳过");
                                continue;
                            }

                            var created = LockAllSpaces(db, tr, options);
                            if (created.Count == 0)
                            {
                                // 以前这里会静默保存并报告成功，实际什么都没锁
                                progressCallback?.Invoke(filePath, BatchLockResult.Skipped,
                                    "未找到可加密图元（空图纸，或图元全部位于锁定/加密图层）");
                                continue;
                            }

                            tr.Commit();
                        }

                        savedPath = WriteDatabase(db, filePath, options.ReplaceOriginalFile, LockSuffix);
                    }

                    if (options.ReplaceOriginalFile)
                    {
                        // 原文件必须在数据库释放后再覆盖：数据库仍打开时替换会让 AutoCAD 的文件映射失效
                        string tempPath = savedPath;
                        savedPath = filePath;
                        CommitReplace(tempPath, filePath);
                    }

                    progressCallback?.Invoke(filePath, BatchLockResult.Success,
                        options.ReplaceOriginalFile ? "已加密并覆盖原文件" : savedPath);
                }
                catch (Exception ex)
                {
                    progressCallback?.Invoke(filePath, BatchLockResult.Failed, ex.Message);
                }
            }
        }

        /// <summary>
        /// 批量解锁离线 DWG 文件
        /// options.ReplaceOriginalFile = true 时直接覆盖原文件，不再生成 _unlock 副本
        /// </summary>
        public static void BatchUnlockFiles(IEnumerable<string> filePaths, CadLockOptions options, Action<string, BatchLockResult, string>? progressCallback = null)
        {
            if (filePaths == null || options == null) return;
            foreach (var filePath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    progressCallback?.Invoke(filePath ?? string.Empty, BatchLockResult.Failed, "文件不存在");
                    continue;
                }
                try
                {
                    bool legacy = false;
                    bool fallback = false;
                    string savedPath = string.Empty;
                    using (var db = new Database(false, true))
                    {
                        db.ReadDwgFile(filePath, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                        db.CloseInput(true);
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            bool marked = IsDrawingLocked(db, tr);
                            List<ObjectId> lockLayerIds = FindLockLayerIds(db, tr);
                            bool hasLayer = lockLayerIds.Count > 0;
                            legacy = FindLegacyLockLayerIds(db, tr).Count > 0;
                            if (!marked && !hasLayer)
                            {
                                progressCallback?.Invoke(filePath, BatchLockResult.Skipped, "图纸未被加密，无需解锁");
                                continue;
                            }

                            int unlocked = UnlockAllSpaces(db, tr);
                            if (unlocked == 0)
                            {
                                tr.Abort();
                                // 旧版 lockdwg.dll 的 _lock.dwg 可能只剩空 MINSERT 占位，
                                // 原内容往往保存在同名 .bak 中；仅旧格式才尝试该恢复路径。
                                fallback = legacy;
                                if (!fallback)
                                {
                                    progressCallback?.Invoke(filePath, BatchLockResult.Skipped, "未找到可还原的加密图形");
                                    continue;
                                }
                            }
                            else
                            {
                                tr.Commit();
                            }
                        }

                        // 旧格式需要先释放源数据库，再由 .bak 恢复；否则 replace 覆盖会被源文件占用。
                        if (!fallback)
                        {
                            savedPath = WriteDatabase(db, filePath, options.ReplaceOriginalFile, UnlockSuffix);
                        }
                    }

                    if (fallback)
                    {
                        if (TryRecoverLegacyBackup(filePath, options.ReplaceOriginalFile, out string recoveredPath, out string recoverMessage))
                        {
                            progressCallback?.Invoke(filePath, BatchLockResult.Success, recoverMessage);
                        }
                        else
                        {
                            progressCallback?.Invoke(filePath, BatchLockResult.Skipped,
                                "旧版 lockdwg.dll 格式：没有可拆的加密块，且未找到可用的同名 .bak 备份；请使用原始 DWG 重新加密");
                        }
                        continue;
                    }

                    if (options.ReplaceOriginalFile)
                    {
                        string tempPath = savedPath;
                        savedPath = filePath;
                        CommitReplace(tempPath, filePath);
                    }
                    progressCallback?.Invoke(filePath, BatchLockResult.Success,
                        options.ReplaceOriginalFile ? "已解锁并覆盖原文件" : savedPath);
                }
                catch (Exception ex)
                {
                    progressCallback?.Invoke(filePath, BatchLockResult.Failed, ex.Message);
                }
            }
        }

        /// <summary>
        /// 旧格式恢复：旧版 lockdwg.dll 写出的 _lock.dwg 常常只剩空 MINSERT，
        /// 但 AutoCAD 在覆盖保存前会生成同名 .bak。若 .bak 仍是可解锁的块结构，
        /// 就把 .bak 解锁后的结果输出为 原文件名_unlock.dwg（或按选项覆盖原文件）。
        /// 只在旧格式检测成立时调用，不会影响当前格式的正常解锁。
        /// </summary>
        private static bool TryRecoverLegacyBackup(string sourcePath, bool replaceOriginal, out string savedPath, out string message)
        {
            savedPath = string.Empty;
            message = string.Empty;
            string tempPath = string.Empty;

            string backupPath = Path.ChangeExtension(sourcePath, ".bak");
            if (!File.Exists(backupPath)) return false;

            try
            {
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(backupPath, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                    db.CloseInput(true);

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        if (FindLockLayerIds(db, tr).Count == 0) return false;
                        if (GetLockedBlockIds(db, tr).Count == 0) return false;

                        int unlocked = UnlockAllSpaces(db, tr);
                        if (unlocked == 0) return false;
                        tr.Commit();
                    }

                    savedPath = WriteDatabase(db, sourcePath, replaceOriginal, UnlockSuffix);
                    if (replaceOriginal)
                    {
                        tempPath = savedPath;
                    }
                }
                if (replaceOriginal)
                {
                    savedPath = sourcePath;
                    CommitReplace(tempPath, sourcePath);
                }

                message = "旧版格式无法直接解锁，已从同名 .bak 备份恢复 " +
                          (replaceOriginal ? "并覆盖原文件" : "-> " + savedPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 保存数据库：replaceOriginal=true 时先写到同目录临时文件(稍后由 CommitReplace 覆盖原图)，
        /// 否则写入带后缀的新文件。返回实际写入的路径
        /// </summary>
        private static string WriteDatabase(Database db, string sourcePath, bool replaceOriginal, string suffix)
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            string ext = Path.GetExtension(sourcePath);

            // 尽量按原图纸版本保存，避免低版本图纸被静默升级
            DwgVersion version;
            try
            {
                version = db.OriginalFileVersion;
            }
            catch
            {
                version = DwgVersion.Current;
            }

            if (!replaceOriginal)
            {
                string outPath = GetUniqueOutputPath(dir, fileName + suffix, ext);
                db.SaveAs(outPath, version);
                return outPath;
            }

            string tempPath = Path.Combine(dir, "." + fileName + "." + Guid.NewGuid().ToString("N") + ext);
            db.SaveAs(tempPath, version);
            return tempPath;
        }

        /// <summary>
        /// 用临时文件覆盖目标文件（必须在数据库 Dispose 之后调用），并清理临时文件
        /// </summary>
        private static void CommitReplace(string tempPath, string targetPath)
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    var attrs = File.GetAttributes(targetPath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        throw new IOException("原文件为只读属性，无法替换，请先取消只读");
                    }

                    try
                    {
                        File.Replace(tempPath, targetPath, null, true);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // 回退到复制覆盖
                    }
                    catch (IOException)
                    {
                        // 跨卷或占用时回退到复制覆盖，仍失败则抛给上层报告
                    }
                }

                File.Copy(tempPath, targetPath, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* 临时文件清理失败不影响结果 */ }
                }
            }
        }

        /// <summary>
        /// 输出文件名已存在时自动追加序号，避免静默覆盖上一次的结果
        /// </summary>
        private static string GetUniqueOutputPath(string dir, string fileName, string ext)
        {
            string path = Path.Combine(dir, fileName + ext);
            int index = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(dir, fileName + "(" + index + ")" + ext);
                index++;
            }
            return path;
        }
    }

    public enum BatchLockResult
    {
        Success,
        Skipped,
        Failed
    }
}
