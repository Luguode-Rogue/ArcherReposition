using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ArcherReposition
{
    /// <summary>
    /// 射手AI「防发呆」重定位 —— 独立测试模块。
    ///
    /// 与主 mod（New_ZZZF）同时启用会双份运行同一功能：
    /// 独立测试时请在启动器中停用 New_ZZZF（或仅用自定义战斗对照）。
    /// </summary>
    public class SubModule : MBSubModuleBase
    {
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            ArcherRepositionSettingsManager.Load();   // 读 ModuleData/ArcherRepositionSettings.xml（无 MCM 时的手动配置入口）
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            InformationManager.DisplayMessage(new InformationMessage("[ArcherReposition] 独立测试模块已启动"));
            ArcherRepositionSettingsManager.RegisterMcmIfAvailable();   // MCM 可用时注册独立设置页
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            // MCM 的设置页发现晚于本模块加载，这里做延迟同步（成功后自动停止，几乎无开销）
            ArcherRepositionSettingsManager.TickMcmSync();
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            mission.AddMissionBehavior(new ArcherRepositionBehavior());
        }
    }
}
