using System;

namespace ArcherReposition
{
    /// <summary>
    /// 主 DLL 反射调用的入口（零参数、公开静态）。
    ///
    /// 关键认知（本次 NullReferenceException 的根因）：
    /// ArcherRepositionMcmPage.Instance **不是自建单例**，而是 MCM 提供的「已注册设置查询」入口，
    /// MCMv5 源码等价于：
    ///     BaseSettingsProvider.Instance?.GetSettings(id) as T
    /// 即：它只会在 MCM 完成设置页发现之后才拿到实例，之前返回 null
    /// （MCM 官方软依赖文档也明确写了 "Instance will return null if something unexpected happened"）。
    /// 本程序集是主 DLL 在 OnBeforeInitialModuleScreenSetAsRoot 里按需 LoadFrom 的，
    /// 那一刻 MCM 的设置容器尚未构建/发现本页，于是 Instance == null，
    /// 链式调用 .SyncFromConfig() 直接 NullReferenceException。
    /// </summary>
    public static class ArcherRepositionMcmAdapter
    {
        private const string Tag = "[ArcherReposition]";

        /// <summary>首次尝试同步（XML→MCM 属性）。同步成功返回 true；MCM 未就绪返回 false，由主 DLL 安排重试。</summary>
        public static bool Register() => TrySync("首次");

        /// <summary>延迟重试入口：主 DLL 每帧 tick 调用，直到同步成功。</summary>
        public static bool TrySyncLater() => TrySync("重试");

        private static bool TrySync(string stage)
        {
            ArcherRepositionMcmPage? page;
            try
            {
                // Instance 的 getter 内部会 new T() 取 Id，MCM 未就绪时既可能返回 null，也可能直接抛异常
                page = ArcherRepositionMcmPage.Instance;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Tag} MCM 尚未就绪，暂不同步（{stage}）：{ex.GetType().Name}：{ex.Message}");
                return false;
            }

            if (page == null)
                return false;

            try
            {
                page.SyncFromConfig();
                Console.WriteLine($"{Tag} MCM 设置页已按 XML 同步初始值（{stage}）");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Tag} MCM 设置页同步失败（{stage}）：{ex}");
                return true;   // 实例已拿到，是同步过程本身出错，再重试也没有意义
            }
        }
    }
}
