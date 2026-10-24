using System;
using System.Collections.Generic;

#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace CadLockPlugin
{
    /// <summary>
    /// 加密图元的运行期防护：拦截复制(DeepClone)、拦截删除(ObjectErased)、
    /// 阻止加密图层被解锁，并在图纸重新打开时按图纸内保存的选项自动恢复防护。
    /// </summary>
    public static class CadLockEvents
    {
        /// <summary>
        /// 单张图纸的防护状态。
        /// 注意：AutoCAD 事件回调里的 sender 不保证与注册事件时的 Database 是同一个托管实例，
        /// 因此这里用闭包直接捕获状态，而不是靠 sender 反查字典。
        /// </summary>
        private sealed class HookState
        {
            public Database Db = null!;
            public Document? Doc;

            public readonly HashSet<ObjectId> ProtectedIds = new HashSet<ObjectId>();

            /// <summary>已提交删除、等待命令结束后恢复的对象</summary>
            public readonly HashSet<ObjectId> PendingUnerase = new HashSet<ObjectId>();

            /// <summary>加密块被修改前的原始位置/角度/比例（不可更改）</summary>
            public readonly Dictionary<ObjectId, ModifySnapshot> ModifySnapshots = new Dictionary<ObjectId, ModifySnapshot>();

            /// <summary>已被修改、等待命令结束后还原的加密对象</summary>
            public readonly HashSet<ObjectId> PendingModifyRestore = new HashSet<ObjectId>();

            /// <summary>正在执行还原，忽略还原本身触发的修改事件</summary>
            public bool RestoringModifies;

            public CadLockOptions? Options;

            /// <summary>加密图层被解锁/改成可打印，等待空闲时恢复</summary>
            public bool LayerTampered;

            public IdMappingEventHandler? CloneHandler;
            public ObjectErasedEventHandler? ErasedHandler;
            public ObjectEventHandler? ModifiedHandler;
            public ObjectEventHandler? OpenedHandler;
            public CommandEventHandler? CommandEndedHandler;
        }

        private static readonly List<HookState> States = new List<HookState>();
        private static readonly object SyncRoot = new object();

        private static bool _installed;
        private static bool _idleHooked;

        // ---------------------------------------------------------------- 注册 / 注销

        public static void RegisterProtectedIds(Database db, IEnumerable<ObjectId> ids)
        {
            if (db == null || ids == null) return;
            lock (SyncRoot)
            {
                var state = GetOrCreateState(db);
                foreach (var id in ids)
                {
                    state.ProtectedIds.Add(id);
                }
            }
        }

        public static void HookEvents(Database db, Document? doc, CadLockOptions options)
        {
            if (db == null || options == null) return;

            lock (SyncRoot)
            {
                PruneClosedDatabases();

                var state = GetOrCreateState(db);
                state.Options = options;
                if (doc != null) state.Doc = doc;

                if (options.DisableCopy && state.CloneHandler == null)
                {
                    state.CloneHandler = (s, e) => OnBeginDeepClone(state, e);
                    db.BeginDeepClone += state.CloneHandler;
                }

                if (options.DisableDelete && state.ErasedHandler == null)
                {
                    state.ErasedHandler = (s, e) => OnObjectErased(state, e);
                    db.ObjectErased += state.ErasedHandler;
                }

                // 图层锁定是「不可选中」的唯一实现手段，必须防止用户直接把图层解锁；
                // “不可更改”也复用这组事件来记录并还原对加密块的修改
                if ((options.DisableSelect || options.DisablePlot || options.DisableModify) && state.ModifiedHandler == null)
                {
                    state.ModifiedHandler = (s, e) => OnObjectModified(state, e);
                    state.OpenedHandler = (s, e) => OnObjectOpenedForModify(state, e);
                    db.ObjectModified += state.ModifiedHandler;
                    db.ObjectOpenedForModify += state.OpenedHandler;
                }

                // 删除恢复与图层恢复都依赖「命令结束」这个时机
                if (doc != null && state.CommandEndedHandler == null &&
                    (options.DisableDelete || options.DisableSelect || options.DisablePlot || options.DisableModify))
                {
                    var capturedDoc = doc;
                    state.CommandEndedHandler = (s, e) => OnCommandEnded(state, capturedDoc);
                    doc.CommandEnded += state.CommandEndedHandler;
                }

                EnsureIdleHook();
            }
        }

        public static void UnhookEvents(Database db)
        {
            if (db == null) return;
            lock (SyncRoot)
            {
                var state = FindState(db);
                if (state == null) return;
                Detach(state);
                States.Remove(state);
            }
        }

        private static HookState GetOrCreateState(Database db)
        {
            var state = FindState(db);
            if (state == null)
            {
                state = new HookState { Db = db };
                States.Add(state);
            }
            return state;
        }

        /// <summary>
        /// AutoCAD 每次取 doc.Database 都可能返回不同的托管实例，
        /// 因此不能只用引用比较，需要退化到按文件名识别同一张图纸。
        /// </summary>
        private static bool SameDatabase(Database a, Database b)
        {
            if (ReferenceEquals(a, b)) return true;

            string fa = SafeFileName(a);
            string fb = SafeFileName(b);
            if (fa.Length == 0 || fb.Length == 0) return false;
            return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeFileName(Database db)
        {
            try
            {
                return db.Filename ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static HookState? FindState(Database? db)
        {
            if (db == null) return null;
            foreach (var state in States)
            {
                if (SameDatabase(state.Db, db)) return state;
            }
            return null;
        }

        private static void Detach(HookState state)
        {
            var db = state.Db;

            try
            {
                if (state.CloneHandler != null)
                {
                    db.BeginDeepClone -= state.CloneHandler;
                    state.CloneHandler = null;
                }

                if (state.ErasedHandler != null)
                {
                    db.ObjectErased -= state.ErasedHandler;
                    state.ErasedHandler = null;
                }

                if (state.ModifiedHandler != null)
                {
                    db.ObjectModified -= state.ModifiedHandler;
                    state.ModifiedHandler = null;
                }

                if (state.OpenedHandler != null)
                {
                    db.ObjectOpenedForModify -= state.OpenedHandler;
                    state.OpenedHandler = null;
                }

                if (state.CommandEndedHandler != null && state.Doc != null)
                {
                    state.Doc.CommandEnded -= state.CommandEndedHandler;
                    state.CommandEndedHandler = null;
                }
            }
            catch
            {
                // 图纸已关闭时取消订阅可能失败，忽略
            }

            lock (SyncRoot)
            {
                state.PendingUnerase.Clear();
                state.PendingModifyRestore.Clear();
                state.ModifySnapshots.Clear();
                state.RestoringModifies = false;
                state.LayerTampered = false;
                state.Options = null;
            }
        }

        /// <summary>
        /// 清理已关闭图纸残留的状态，防止 Database 对象被长期引用
        /// </summary>
        private static void PruneClosedDatabases()
        {
            try
            {
                var alive = new List<Database>();
                foreach (Document doc in Application.DocumentManager)
                {
                    try
                    {
                        alive.Add(doc.Database);
                    }
                    catch
                    {
                        // 文档正在关闭
                    }
                }

                var closed = new List<HookState>();
                foreach (var state in States)
                {
                    bool found = false;
                    foreach (var db in alive)
                    {
                        if (SameDatabase(db, state.Db))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found) closed.Add(state);
                }

                foreach (var state in closed)
                {
                    Detach(state);
                    States.Remove(state);
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        // ---------------------------------------------------------------- 打开图纸时恢复防护

        /// <summary>
        /// 插件加载时调用：图纸一旦被打开(激活)，就按图纸里保存的选项重新施加防护
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                Application.DocumentManager.DocumentActivated += OnDocumentActivated;
            }
            catch
            {
                // 个别版本不支持该事件时只做当前图纸的一次性恢复
            }

            try
            {
                foreach (Document doc in Application.DocumentManager)
                {
                    ApplyPersistedProtection(doc);
                }
            }
            catch
            {
                // 忽略，保证插件加载不被阻断
            }
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                ApplyPersistedProtection(e.Document);
            }
            catch
            {
                // 打开图纸期间出错不影响图纸本身
            }
        }

        /// <summary>
        /// 依据图纸中的 ACAD_LOCK 标记与保存的选项，恢复图层锁定状态与复制/删除拦截钩子。
        /// 用户即使把加密图层解锁后保存，下次打开也会自动恢复。
        /// </summary>
        public static void ApplyPersistedProtection(Document? doc)
        {
            if (doc == null) return;

            Database db;
            try
            {
                db = doc.Database;
            }
            catch
            {
                return;
            }

            bool locked = false;
            CadLockOptions? options = null;

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    locked = CadLockEngine.IsDrawingLocked(db, tr);
                    if (!locked)
                    {
                        tr.Abort();
                    }
                    else
                    {
                        options = CadLockEngine.LoadProtectionOptions(db, tr)
                                  ?? new CadLockOptions { DisableSelect = true, DisableDelete = true };

                        // 同时处理当前格式和旧版 lockdwg.dll 图层；旧图纸里可能只有 *ACADLOCKLAYER
                        foreach (ObjectId layerId in CadLockEngine.FindLockLayerIds(db, tr))
                        {
                            var layer = tr.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                            if (layer != null &&
                                (layer.IsLocked != options.DisableSelect || layer.IsPlottable != !options.DisablePlot))
                            {
                                layer.IsLocked = options.DisableSelect;
                                layer.IsPlottable = !options.DisablePlot;
                            }
                        }

                        tr.Commit();
                    }
                }
            }
            catch
            {
                return;
            }

            if (!locked || options == null) return;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    RegisterProtectedIds(db, CadLockEngine.GetLockedBlockIds(db, tr));
                    tr.Abort();
                }

                HookEvents(db, doc, options);
            }
            catch
            {
                // 恢复钩子失败不阻断打开图纸
            }
        }

        // ---------------------------------------------------------------- 防护拦截

        /// <summary>
        /// 拦截复制 (剪贴板 COPYCLIP、WBLOCK、跨图拖放都会触发 DeepClone)
        /// </summary>
        private static void OnBeginDeepClone(HookState state, IdMappingEventArgs e)
        {
            try
            {
                var idMap = e.IdMapping;
                ObjectId[] ids;
                lock (SyncRoot)
                {
                    if (state.ProtectedIds.Count == 0) return;
                    ids = new ObjectId[state.ProtectedIds.Count];
                    state.ProtectedIds.CopyTo(ids);
                }

                foreach (var id in ids)
                {
                    if (idMap.Contains(id))
                    {
                        idMap.Delete(id);
                    }
                }
            }
            catch
            {
                // 忽略异常保证稳定
            }
        }

        /// <summary>
        /// 记录被删除的加密对象（回调内只登记，不做修改，避免 eNotOpenForWrite）
        /// </summary>
        private static void OnObjectErased(HookState state, ObjectErasedEventArgs e)
        {
            try
            {
                if (!e.Erased) return; // 恢复删除自身也会触发该事件，直接忽略

                var id = e.DBObject.ObjectId;
                if (id.IsNull) return;

                lock (SyncRoot)
                {
                    if (state.ProtectedIds.Contains(id))
                    {
                        state.PendingUnerase.Add(id);
                    }
                }
            }
            catch
            {
                // 忽略异常保证稳定
            }
        }

        /// <summary>
        /// 对象即将被修改时先记住原始位置/角度/比例，供“不可更改”还原。
        /// 此时修改尚未发生，读到的就是修改前的状态。
        /// </summary>
        private static void OnObjectOpenedForModify(HookState state, ObjectEventArgs e)
        {
            try
            {
                var options = state.Options;
                if (options == null || !options.DisableModify) return;
                if (!(e.DBObject is BlockReference br)) return;

                var id = br.ObjectId;
                if (id.IsNull) return;

                lock (SyncRoot)
                {
                    if (!state.ProtectedIds.Contains(id)) return;
                    if (state.ModifySnapshots.ContainsKey(id)) return;
                    state.ModifySnapshots[id] = ModifySnapshot.Capture(br);
                }
            }
            catch
            {
                // 忽略异常保证稳定
            }
        }

        /// <summary>
        /// 图层被解锁/改成可打印、加密块被移动/旋转/缩放时只做登记，
        /// 真正的恢复放到命令结束或空闲时执行（回调内不能安全写入）
        /// </summary>
        private static void OnObjectModified(HookState state, ObjectEventArgs e)
        {
            try
            {
                var options = state.Options;
                if (options == null) return;

                if (e.DBObject is LayerTableRecord layer &&
                    CadLockEngine.IsLockLayerName(layer.Name) &&
                    NeedsLayerGuard(options))
                {
                    if (layer.IsLocked != options.DisableSelect || layer.IsPlottable != !options.DisablePlot)
                    {
                        lock (SyncRoot)
                        {
                            state.LayerTampered = true;
                        }
                    }
                    return;
                }

                if (!options.DisableModify) return;
                if (!(e.DBObject is BlockReference br)) return;

                var id = br.ObjectId;
                if (id.IsNull) return;

                lock (SyncRoot)
                {
                    if (state.RestoringModifies) return;
                    if (!state.ProtectedIds.Contains(id)) return;
                    if (!state.ModifySnapshots.TryGetValue(id, out var snapshot)) return;
                    if (snapshot.Matches(br)) return;
                    state.PendingModifyRestore.Add(id);
                }
            }
            catch
            {
                // 忽略异常保证稳定
            }
        }

        // ---------------------------------------------------------------- 延迟恢复

        private static bool NeedsLayerGuard(CadLockOptions? options)
        {
            return options != null && (options.DisableSelect || options.DisablePlot);
        }

        /// <summary>
        /// Idle 是唯一能覆盖 LISP 嵌套命令的时机：(command "_.-LAYER" ...) 不会触发 CommandEnded
        /// </summary>
        private static void EnsureIdleHook()
        {
            if (_idleHooked) return;
            try
            {
                Application.Idle += OnApplicationIdle;
                _idleHooked = true;
            }
            catch
            {
                // 不支持 Idle 时退回仅 CommandEnded
            }
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            FlushPendingProtection();
        }

        /// <summary>
        /// 立即执行待处理的图层恢复。Idle 在主界面空闲时调用；
        /// 没有 UI 消息循环的环境(accoreconsole)或需要即时生效时可手动调用。
        /// </summary>
        public static void FlushPendingProtection()
        {
            try
            {
                List<HookState> targets = new List<HookState>();
                List<HookState> pendingErase = new List<HookState>();
                List<HookState> pendingModify = new List<HookState>();

                lock (SyncRoot)
                {
                    if (States.Count == 0) return;
                    foreach (var state in States)
                    {
                        if (state.LayerTampered && state.Doc != null) targets.Add(state);
                        if (state.PendingUnerase.Count > 0 && state.Doc != null) pendingErase.Add(state);
                        if (state.PendingModifyRestore.Count > 0 && state.Doc != null) pendingModify.Add(state);
                    }
                }

                // 先还原被移动/旋转/缩放的加密块，再锁回图层：
                // 图层一旦锁上，再以写方式打开块参照可能被 CAD 拒绝
                foreach (var state in pendingModify)
                {
                    if (state.Doc != null)
                    {
                        RestorePendingModifies(state, state.Doc);
                    }
                }

                foreach (var state in targets)
                {
                    RestoreLayerLock(state);
                }

                // 删除恢复同样要走延迟路径：accoreconsole 等环境不会触发 CommandEnded
                foreach (var state in pendingErase)
                {
                    if (state.Doc != null)
                    {
                        RestorePendingErases(state, state.Doc);
                    }
                }
            }
            catch
            {
                // 延迟执行路径不向外抛异常
            }
        }

        private static void OnCommandEnded(HookState state, Document doc)
        {
            try
            {
                RestorePendingErases(state, doc);
            }
            catch
            {
                // 忽略异常保证稳定
            }

            FlushPendingProtection();
        }

        /// <summary>
        /// 命令结束后统一恢复被删除的加密对象（此时原命令的事务已提交，可安全写入）
        /// </summary>
        private static void RestorePendingErases(HookState state, Document doc)
        {
            ObjectId[] pending;
            lock (SyncRoot)
            {
                if (state.PendingUnerase.Count == 0) return;
                pending = new ObjectId[state.PendingUnerase.Count];
                state.PendingUnerase.CopyTo(pending);
                state.PendingUnerase.Clear();
            }

            var db = state.Db;
            int restored = 0;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var id in pending)
                {
                    try
                    {
                        if (id.IsNull) continue;

                        // 已擦除对象的 ObjectId.IsValid 会返回 false，不能拿它当过滤条件
                        var obj = tr.GetObject(id, OpenMode.ForWrite, true);
                        if (obj == null || !obj.IsErased) continue;

                        obj.Erase(false); // 恢复删除
                        restored++;
                    }
                    catch
                    {
                        // 单个对象恢复失败不影响其余对象
                    }
                }

                if (restored > 0)
                {
                    tr.Commit();
                }
            }

            if (restored > 0)
            {
                try
                {
                    doc.Editor?.WriteMessage($"\n[保护] 检测到删除加密对象，已自动恢复 {restored} 个对象。\n");
                }
                catch
                {
                    // 编辑器不可用时忽略提示
                }
            }
        }

        /// <summary>
        /// 命令结束后把被移动/旋转/缩放/改图层的加密块还原到修改前的状态
        /// </summary>
        private static void RestorePendingModifies(HookState state, Document doc)
        {
            ObjectId[] pending;
            lock (SyncRoot)
            {
                if (state.PendingModifyRestore.Count == 0) return;
                pending = new ObjectId[state.PendingModifyRestore.Count];
                state.PendingModifyRestore.CopyTo(pending);
                state.PendingModifyRestore.Clear();
                state.RestoringModifies = true;
            }

            var db = state.Db;
            int restored = 0;

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var id in pending)
                    {
                        try
                        {
                            if (id.IsNull) continue;

                            ModifySnapshot? snapshot = null;
                            lock (SyncRoot)
                            {
                                if (state.ModifySnapshots.TryGetValue(id, out var found))
                                {
                                    snapshot = found;
                                }
                            }
                            if (snapshot == null) continue;

                            var br = tr.GetObject(id, OpenMode.ForWrite, false) as BlockReference;
                            if (br == null || br.IsErased) continue;
                            if (snapshot.Matches(br)) continue;

                            snapshot.Restore(br);
                            restored++;
                        }
                        catch
                        {
                            // 单个对象还原失败不影响其余对象
                        }
                    }

                    if (restored > 0) tr.Commit(); else tr.Abort();
                }
            }
            finally
            {
                lock (SyncRoot)
                {
                    state.RestoringModifies = false;
                }
            }

            if (restored > 0)
            {
                try
                {
                    doc.Editor?.WriteMessage($"\n[保护] 检测到修改加密对象，已自动还原 {restored} 个对象。\n");
                }
                catch
                {
                    // 编辑器不可用时忽略提示
                }
            }
        }

        /// <summary>
        /// 加密块修改前的原始位置/角度/比例/图层，用于自动还原
        /// </summary>
        private sealed class ModifySnapshot
        {
            private Point3d _position;
            private double _rotation;
            private Scale3d _scale;
            private Vector3d _normal;
            private ObjectId _layerId;

            public static ModifySnapshot Capture(BlockReference br)
            {
                return new ModifySnapshot
                {
                    _position = br.Position,
                    _rotation = br.Rotation,
                    _scale = br.ScaleFactors,
                    _normal = br.Normal,
                    _layerId = br.LayerId
                };
            }

            public bool Matches(BlockReference br)
            {
                return SamePoint(_position, br.Position)
                       && Near(_rotation, br.Rotation)
                       && SameScale(_scale, br.ScaleFactors)
                       && SameVector(_normal, br.Normal)
                       && _layerId == br.LayerId;
            }

            public void Restore(BlockReference br)
            {
                if (!SamePoint(_position, br.Position)) br.Position = _position;
                if (!Near(_rotation, br.Rotation)) br.Rotation = _rotation;
                if (!SameScale(_scale, br.ScaleFactors)) br.ScaleFactors = _scale;
                if (!SameVector(_normal, br.Normal)) br.Normal = _normal;

                if (_layerId.IsValid && br.LayerId != _layerId)
                {
                    try
                    {
                        br.LayerId = _layerId;
                    }
                    catch
                    {
                        // 原图层已被删除时保留当前图层
                    }
                }
            }

            private static bool Near(double a, double b)
            {
                return Math.Abs(a - b) < 1e-9;
            }

            private static bool SamePoint(Point3d a, Point3d b)
            {
                return Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
            }

            private static bool SameVector(Vector3d a, Vector3d b)
            {
                return Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
            }

            private static bool SameScale(Scale3d a, Scale3d b)
            {
                return Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
            }
        }

        /// <summary>
        /// 把被解锁/改成可打印的加密图层恢复原状
        /// </summary>
        private static void RestoreLayerLock(HookState state)
        {
            CadLockOptions? options;
            lock (SyncRoot)
            {
                if (!state.LayerTampered) return;
                state.LayerTampered = false;
                options = state.Options;
            }

            if (!NeedsLayerGuard(options) || options == null || state.Doc == null) return;

            var doc = state.Doc;
            var db = state.Db;
            bool applied = false;

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // 图纸已经被合法解锁(标记被删除)时不再干预
                    if (!CadLockEngine.IsDrawingLocked(db, tr))
                    {
                        tr.Abort();
                        return;
                    }

                    foreach (ObjectId layerId in CadLockEngine.FindLockLayerIds(db, tr))
                    {
                        var layer = tr.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                        if (layer == null) continue;

                        if (layer.IsLocked != options.DisableSelect)
                        {
                            layer.IsLocked = options.DisableSelect;
                            applied = true;
                        }
                        if (layer.IsPlottable != !options.DisablePlot)
                        {
                            layer.IsPlottable = !options.DisablePlot;
                            applied = true;
                        }
                    }
                    if (applied) tr.Commit(); else tr.Abort();
                }
            }
            catch
            {
                return;
            }

            if (applied)
            {
                try
                {
                    doc.Editor?.WriteMessage("\n[保护] 加密图层受保护，已自动恢复锁定状态。\n");
                }
                catch
                {
                    // 编辑器不可用时忽略
                }
            }
        }
    }
}
