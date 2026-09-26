using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace ArcherReposition
{
    /// <summary>
    /// MCM 独立选项页（v5 AttributeGlobalSettings）。
    ///
    /// 本类型只允许存在于 Adapter DLL（ArcherReposition.MCM.dll）：
    /// 主 DLL 的引用表一旦出现 MCMv5，游戏在无 MCM 时会拒绝加载整个模块
    /// （1.5.0 AssemblyLoader 在 Assembly.Load 阶段解析全部引用，JIT 隔离无效 —— 实测）。
    /// 主 DLL 通过反射加载本程序集（见 ArcherRepositionSettingsManager.RegisterMcmInternal）。
    /// </summary>
    public sealed class ArcherRepositionMcmPage : AttributeGlobalSettings<ArcherRepositionMcmPage>
    {
        public override string Id => "ArcherReposition_v1";
        public override string FolderName => "ArcherReposition";
        public override string DisplayName => "射手AI防发呆重定位";
        // v5 默认 FormatType = "none" 不会落盘，必须显式指定
        public override string FormatType => "json2";

        // ---------- 总控 ----------

        [SettingPropertyBool("启用防发呆重定位",
            HintText = "总开关。关闭后本功能完全休眠（检测与换目标全部关闭），行为与原版一致",
            RequireRestart = false)]
        [SettingPropertyGroup("总控", GroupOrder = 0)]
        public bool EnableSwitchTarget { get; set; } = ArcherRepositionConfig.EnableSwitchTarget;

        // ---------- 生效范围 ----------

        [SettingPropertyBool("仅玩家队伍生效",
            HintText = "开启：只对玩家方（含友方队伍）的射手生效；关闭：敌我双方都生效",
            RequireRestart = false)]
        [SettingPropertyGroup("生效范围", GroupOrder = 1)]
        public bool OnlyPlayerTeam { get; set; } = ArcherRepositionConfig.OnlyPlayerTeam;

        [SettingPropertyBool("仅散阵阵型生效",
            HintText = "开启：只有射手所在阵型为散阵（Loose）时本功能才生效；关闭：任意阵型都生效",
            RequireRestart = false)]
        [SettingPropertyGroup("生效范围", GroupOrder = 1)]
        public bool OnlyLooseFormation { get; set; } = ArcherRepositionConfig.OnlyLooseFormation;

        /// <summary>MCM 注册后由 Adapter 调用：Config/XML → 属性（XML 为持久真相）。</summary>
        internal void SyncFromConfig()
        {
            EnableSwitchTarget = ArcherRepositionConfig.EnableSwitchTarget;
            OnlyPlayerTeam = ArcherRepositionConfig.OnlyPlayerTeam;
            OnlyLooseFormation = ArcherRepositionConfig.OnlyLooseFormation;
        }

        public override void OnPropertyChanged(string propertyName)
        {
            base.OnPropertyChanged(propertyName);
            bool changed = false;
            if (propertyName == nameof(EnableSwitchTarget))
            {
                ArcherRepositionConfig.EnableSwitchTarget = EnableSwitchTarget;
                changed = true;
            }
            else if (propertyName == nameof(OnlyPlayerTeam))
            {
                ArcherRepositionConfig.OnlyPlayerTeam = OnlyPlayerTeam;
                changed = true;
            }
            else if (propertyName == nameof(OnlyLooseFormation))
            {
                ArcherRepositionConfig.OnlyLooseFormation = OnlyLooseFormation;
                changed = true;
            }

            if (changed)
                ArcherRepositionSettingsManager.Save();   // 持久真相回写
        }
    }
}
