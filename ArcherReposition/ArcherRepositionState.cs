using TaleWorlds.MountAndBlade;

namespace ArcherReposition
{
    /// <summary>单兵状态机的阶段。</summary>
    internal static class ArcherStage
    {
        public const byte Idle = 0;          // 未介入
        public const byte ManualTarget = 1;  // 已手动指定目标（SetAutomaticTargetSelection(false) 生效中）
        public const byte Strafe = 2;        // 侧移中（预留：主线程侧移实现前不会进入）
        public const byte Cooldown = 3;      // 冷却
    }

    /// <summary>
    /// 单兵热状态（struct，纯标量字段，零堆分配）。全主线程读写，无锁。
    /// </summary>
    internal struct ArcherAgentState
    {
        public byte Stage;
        public byte BadStreak;        // 连续非 Clear 计数
        public byte ClearStreak;      // 连续 Clear 计数
        public byte RefreshCounter;   // IsRanged 缓存刷新计数
        public byte SamplePhase;      // 采样游标（按 agent 自增，多 agent 采样去相关）

        public bool IsRanged;         // 主手是否远程（缓存，定期刷新）
        public bool Dirty;            // 检测 → 主循环的处理请求
        public bool RequestExit;      // 请求主循环恢复引擎状态/退出当前阶段

        public bool StrafeApproved;   // 预留
        public sbyte StrafeDir;       // 预留：+1 / -1
        public float StrafeOffset;    // 预留：当前批准的侧移幅度（米）

        public int ManualTargetIndex; // 手动目标 Agent.Index（调试用）
        public float NextAllowedTime; // 冷却截止时间（Mission.CurrentTime）
        public float StageStartTime;  // 进入当前阶段的时间
        public float NextDetectTime;  // 下次允许检测的时间（主线程错峰节流）
    }

    /// <summary>
    /// 状态存储：按 Agent.Index 对齐的定长 struct 数组 + Owner 数组。
    /// 登记/注销由 MissionBehavior 的 OnAgentCreated/OnAgentRemoved（主线程）驱动。
    /// </summary>
    internal static class ArcherRepositionStateStore
    {
        public static readonly int Capacity = ArcherRepositionConfig.StateCapacity;
        public static readonly ArcherAgentState[] States = new ArcherAgentState[Capacity];
        public static readonly Agent[] Owners = new Agent[Capacity];

        /// <summary>战斗结束时整体重置（防止跨战斗的 stale 引用拖内存）。</summary>
        public static void ResetAll()
        {
            for (int i = 0; i < Capacity; i++)
            {
                States[i] = default;
                Owners[i] = null;
            }
        }

        /// <summary>主线程登记（MissionBehavior.OnAgentCreated）。</summary>
        public static void Register(Agent agent)
        {
            int idx = agent.Index;
            if (idx < 0 || idx >= Capacity)
                return;
            States[idx] = default;
            Owners[idx] = agent;
        }

        /// <summary>主线程注销（MissionBehavior.OnAgentRemoved）。</summary>
        public static void Unregister(Agent agent)
        {
            int idx = agent.Index;
            if (idx < 0 || idx >= Capacity)
                return;
            if (Owners[idx] == agent)
            {
                States[idx] = default;
                Owners[idx] = null;
            }
        }
    }
}
