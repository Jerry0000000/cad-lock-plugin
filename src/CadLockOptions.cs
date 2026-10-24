using System;

namespace CadLockPlugin
{
    public class CadLockOptions
    {
        /// <summary>
        /// 分块数量 (将图形分散打包进不同块的数量)
        /// </summary>
        public int SplitCount { get; set; } = 10;

        /// <summary>
        /// 加密/循环嵌套次数 (层层嵌套增加炸开和逆向难度)
        /// </summary>
        public int RepeatCount { get; set; } = 10;

        /// <summary>
        /// 不可选中 (将加密块放置在锁定图层，从而无法被选中、编辑或删除)
        /// 注意：图层锁定不影响对象捕捉(OSNAP)，AutoCAD 没有图层级捕捉开关
        /// 默认开启：图层锁定是 AutoCAD 原生行为，锁状态随图纸保存，未装插件的 CAD 里同样选不中
        /// </summary>
        public bool DisableSelect { get; set; } = true;

        /// <summary>
        /// 不可打印 (设置图层为不打印)
        /// </summary>
        public bool DisablePlot { get; set; } = false;

        /// <summary>
        /// 不可复制 (拦截跨图/剪贴板复制 DeepClone 事件)
        /// </summary>
        public bool DisableCopy { get; set; } = false;

        /// <summary>
        /// 不可删除 (拦截 ObjectErased 事件)
        /// </summary>
        public bool DisableDelete { get; set; } = false;

        /// <summary>
        /// 批量处理时直接替换原文件 (不生成 _lock / _unlock 副本)
        /// </summary>
        public bool ReplaceOriginalFile { get; set; } = false;
    }
}
