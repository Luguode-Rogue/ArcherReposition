using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace ArcherReposition
{
    /// <summary>
    /// Mission 层驱动器（全主线程）。
    ///
    /// 历史教训（实测）：
    /// - v1 曾用 Harmony postfix 挂在 HumanAIComponent.ParallelUpdateFormationMovement（TWParallel
    ///   工作线程）上做检测与 formation frame 覆写 —— 工作线程打补丁 + worker 上的 native 调用
    ///   引发原生 AccessViolation 闪退与整局冻结，已彻底移除；
    /// - 现架构：OnAgentCreated 登记 → OnMissionTick 主线程轮询检测（错峰节流）→ 同线程状态机决策。
    /// </summary>
    public class ArcherRepositionBehavior : MissionLogic
    {
        public override void OnAgentCreated(Agent agent)
        {
            base.OnAgentCreated(agent);
            ArcherRepositionStateStore.Register(agent);
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            ArcherRepositionStateStore.Unregister(affectedAgent);
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            ArcherRepositionLogic.TickMain(Mission);
        }

        public override void OnEndMissionInternal()
        {
            base.OnEndMissionInternal();
            ArcherRepositionStateStore.ResetAll();
        }
    }
}
