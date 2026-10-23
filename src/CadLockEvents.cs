using System;
using System.Collections.Generic;

#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#endif

namespace CadLockPlugin
{
    public static class CadLockEvents
    {
        private static readonly HashSet<ObjectId> ProtectedObjectIds = new HashSet<ObjectId>();
        private static bool _eventsHooked = false;

        public static void RegisterProtectedIds(IEnumerable<ObjectId> ids)
        {
            foreach (var id in ids)
            {
                ProtectedObjectIds.Add(id);
            }
        }

        public static void HookEvents(Database db, Document doc, CadLockOptions options)
        {
            if (_eventsHooked) return;

            if (options.DisableCopy)
            {
                db.BeginDeepClone += OnBeginDeepClone;
            }

            if (options.DisableDelete)
            {
                db.ObjectErased += OnObjectErased;
            }

            _eventsHooked = true;
        }

        public static void UnhookEvents(Database db)
        {
            if (!_eventsHooked) return;

            db.BeginDeepClone -= OnBeginDeepClone;
            db.ObjectErased -= OnObjectErased;
            _eventsHooked = false;
        }

        /// <summary>
        /// 拦截复制事件 (剪贴板复制 COPYCLIP、WBLOCK 等都会触发 DeepClone)
        /// </summary>
        private static void OnBeginDeepClone(object sender, IdMappingEventArgs e)
        {
            try
            {
                var idMap = e.IdMapping;
                // 如果克隆的是受保护的对象，将其从映射表中移除以阻止复制
                foreach (var id in ProtectedObjectIds)
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
        /// 拦截删除事件 (当图纸内的加密块被执行 ERASE 时，自动恢复/反擦除)
        /// </summary>
        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            try
            {
                if (e.Erased && ProtectedObjectIds.Contains(e.DBObject.ObjectId))
                {
                    var obj = e.DBObject;
                    if (!obj.IsWriteEnabled)
                    {
                        obj.UpgradeOpen();
                    }
                    obj.Erase(false); // 恢复删除
                }
            }
            catch
            {
                // 忽略异常保证稳定
            }
        }
    }
}
