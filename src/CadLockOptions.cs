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
        /// 不可捕捉 (将对象放置在锁定图层并禁用捕捉点)
        /// </summary>
        public bool DisableSnap { get; set; } = false;

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
    }
}
