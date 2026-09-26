using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace ArcherReposition
{
    /// <summary>
    /// 配置持久化 + MCM 接入（双模式）。
    ///
    /// - 装有 MCM（Mod Configuration Menu v5）：
    ///   主菜单 → Mod Options → ArcherReposition（独立选项页），改动实时生效并回写 XML。
    /// - 无 MCM：直接用文本编辑器修改 ModuleData/ArcherRepositionSettings.xml，重启游戏生效。
    ///
    /// 本文件零 MCM 依赖（无 using MCM）；对 MCM 类型的访问全部隔离在 RegisterMcmInternal()
    /// —— .NET JIT 是方法级惰性编译，未安装 MCM 时该方法体根本不会被 JIT。
    /// </summary>
    internal static class ArcherRepositionSettingsManager
    {
        // 与 "../../Modules/..." 相对路径约定一致（cwd = Bannerlord/bin/Win64_Shipping_Client）
        private const string XmlPath = "../../Modules/ArcherReposition/ModuleData/ArcherRepositionSettings.xml";

        private static bool _loaded;

        // Adapter DLL 载入后的延迟同步状态（见 TickMcmSync 注释）
        private static MethodInfo? _mcmSyncLater;
        private static bool _mcmPending;
        private static int _mcmRetries;
        private const int MaxMcmRetries = 60 * 300;   // ≈5 分钟（按 60fps 的 tick 计）

        /// <summary>启动时加载 XML → 写入 Config（OnSubModuleLoad 调用）。文件不存在时按 Config 默认值生成模板。</summary>
        public static void Load()
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (File.Exists(XmlPath))
                    ReadIntoConfig();
                else
                    Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ArcherReposition] 配置加载失败，使用默认值: " + ex.Message);
            }
        }

        /// <summary>把 Config 当前值写回 XML（带中文注释；MCM 改动亦回写此文件）。</summary>
        public static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(XmlPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                XDocument doc = new XDocument(
                    new XComment(" 射手AI「防发呆」重定位 —— 独立测试模块配置"),
                    new XComment(" 装有 MCM 时：主菜单 → Mod Options → ArcherReposition 页面修改，改动会自动回写本文件"),
                    new XComment(" 未装 MCM 时：直接用文本编辑器修改以下值，重启游戏生效"),
                    new XElement("ArcherRepositionSettings",
                        new XComment(" 总开关：false 时本功能完全休眠"),
                        new XElement("EnableSwitchTarget", ArcherRepositionConfig.EnableSwitchTarget.ToString().ToLowerInvariant()),
                        new XComment(" true = 仅玩家队伍（含友方队伍）的射手生效；false = 敌我都生效"),
                        new XElement("OnlyPlayerTeam", ArcherRepositionConfig.OnlyPlayerTeam.ToString().ToLowerInvariant()),
                        new XComment(" true = 仅散阵（Loose）阵型时生效；false = 任意阵型都生效"),
                        new XElement("OnlyLooseFormation", ArcherRepositionConfig.OnlyLooseFormation.ToString().ToLowerInvariant())));
                doc.Save(XmlPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ArcherReposition] 配置保存失败: " + ex.Message);
            }
        }

        private static void ReadIntoConfig()
        {
            XElement root = XDocument.Load(XmlPath).Root;
            if (root == null)
                return;

            if (TryParseBool(root.Element("EnableSwitchTarget")?.Value, out bool enable))
                ArcherRepositionConfig.EnableSwitchTarget = enable;
            if (TryParseBool(root.Element("OnlyPlayerTeam")?.Value, out bool onlyPlayerTeam))
                ArcherRepositionConfig.OnlyPlayerTeam = onlyPlayerTeam;
            if (TryParseBool(root.Element("OnlyLooseFormation")?.Value, out bool onlyLooseFormation))
                ArcherRepositionConfig.OnlyLooseFormation = onlyLooseFormation;
        }

        private static bool TryParseBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result))
                return true;
            if (value == "1") { result = true; return true; }
            if (value == "0") { result = false; return true; }
            result = default;
            return false;
        }

        // ---------- MCM 软依赖接入 ----------

        /// <summary>探测 MCMv5 是否已加载。纯 BCL 反射，绝不触碰 MCM 类型。</summary>
        public static bool CheckMcmAvailable()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = asm.FullName;
                    if (name == null)
                        continue;
                    if (!name.StartsWith("MCMv5", StringComparison.Ordinal)
                        && !name.StartsWith("Bannerlord.MBOptionScreen", StringComparison.Ordinal))
                        continue;
                    if (asm.GetType("MCM.Abstractions.Base.Global.AttributeGlobalSettings`1", false) != null)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>MCM 可用时注册独立设置页（XML 值为初始真相）。调用方须先 CheckMcmAvailable()。</summary>
        public static void RegisterMcmIfAvailable()
        {
            Load();
            if (!CheckMcmAvailable())
                return;
            try
            {
                RegisterMcmInternal();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ArcherReposition] MCM 设置页注册失败: " + ex.Message);
            }
        }

        /// <summary>
        /// MCM 集成入口：反射加载 Adapter DLL（ArcherReposition.MCM.dll）。
        ///
        /// 为什么必须双 DLL：1.5.0 游戏加载器在 Assembly.Load 阶段解析主 DLL 的全部引用程序集，
        /// 引用表只要出现 MCMv5，无 MCM 环境下整个模块直接加载失败（实测，JIT 惰性隔离无效）。
        /// 故主 DLL 零 MCM 引用，Adapter DLL 独立编译，仅在探测到 MCM 已加载后按需 LoadFrom。
        /// </summary>
        private static void RegisterMcmInternal()
        {
            string adapterPath = Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory, "../../Modules/ArcherReposition/bin/Win64_Shipping_Client/ArcherReposition.MCM.dll"));
            if (!File.Exists(adapterPath))
            {
                Console.WriteLine("[ArcherReposition] MCM Adapter DLL 不存在，跳过 MCM 集成: " + adapterPath);
                return;
            }
            Assembly adapter = Assembly.LoadFrom(adapterPath);
            Type? adapterType = adapter.GetType("ArcherReposition.ArcherRepositionMcmAdapter", false);
            MethodInfo? register = adapterType?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
            if (adapterType == null || register == null)
            {
                Console.WriteLine("[ArcherReposition] MCM Adapter 入口缺失（ArcherRepositionMcmAdapter.Register），跳过 MCM 集成");
                return;
            }
            _mcmSyncLater = adapterType.GetMethod("TrySyncLater", BindingFlags.Public | BindingFlags.Static);

            object? synced = register.Invoke(null, null);   // false = MCM 尚未发现本设置页（Instance 为 null）
            _mcmPending = synced is not true;
            if (_mcmPending)
                Console.WriteLine("[ArcherReposition] MCM 尚未发现设置页，稍后自动重试同步（不影响 XML 配置生效）");
        }

        /// <summary>
        /// MCM 延迟同步：由 SubModule.OnApplicationTick 每帧调用一次。
        ///
        /// 为什么需要：Adapter DLL 是本模块在 OnBeforeInitialModuleScreenSetAsRoot 里按需 LoadFrom 的，
        /// 而 MCM 的设置发现晚于该时点，所以首次注册时 McmPage.Instance 通常为 null（此前会 NRE）。
        /// MCM 完成发现后再把 XML 的当前值推给 MCM 属性，保证 UI 显示与 XML 真相一致。
        /// 成功或超时后自动停止，不再产生开销。
        /// </summary>
        public static void TickMcmSync()
        {
            if (!_mcmPending || _mcmSyncLater == null)
                return;
            if (++_mcmRetries > MaxMcmRetries)
            {
                _mcmPending = false;
                Console.WriteLine("[ArcherReposition] MCM 长时间未发现设置页，停止延迟同步（XML 配置仍然生效）");
                return;
            }
            try
            {
                if (_mcmSyncLater.Invoke(null, null) is true)
                {
                    _mcmPending = false;
                    Console.WriteLine("[ArcherReposition] MCM 设置页延迟同步完成");
                }
            }
            catch (Exception ex)
            {
                _mcmPending = false;
                Console.WriteLine("[ArcherReposition] MCM 设置页延迟同步失败: " + ex.Message);
            }
        }
    }
}
