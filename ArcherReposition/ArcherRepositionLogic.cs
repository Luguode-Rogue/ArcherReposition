using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Path = System.IO.Path;

namespace ArcherReposition
{
    /// <summary>
    /// 主线程决策逻辑。仅由 ArcherRepositionBehavior.OnMissionTick 驱动。
    ///
    /// 职责：
    /// 1. 主线程错峰检测（DetectMain）—— 无任何 Harmony 补丁、无工作线程
    /// 2. ManualTarget 生命周期自管（托管侧不清 target —— 必须自己恢复 SetAutomaticTargetSelection(true)）
    /// 3. 消费 Dirty 请求：换目标（射线验证，加权预算限流）
    /// 4. 射线结果缓存（非对称 TTL：挡=0.5s / 通=0.1s）
    /// 5. 异常熔断：连续异常达到上限 → 自动禁用，行为回退原版
    /// 6. 无锁日志队列，主线程批量落盘
    /// </summary>
    internal static class ArcherRepositionLogic
    {
        /// <summary>熔断开关（volatile）。</summary>
        public static volatile bool Disabled;

        private static int _faultCount;
        private static int _frameBudget;
        private static bool _resetPending;
        private static bool _announced;

        // ---------- 射线结果缓存（直接索引哈希，碰撞覆盖可接受） ----------

        private struct RayCacheEntry
        {
            public int Key;
            public float Time;
            public bool Blocked;
        }

        private static readonly RayCacheEntry[] RayCache = new RayCacheEntry[ArcherRepositionConfig.CacheSlots];

        // ---------- 主线程入口 ----------

        public static void TickMain(Mission? mission)
        {
            if (Disabled)
                return;

            if (!_announced)
            {
                // 版本戳：进战斗时一眼确认跑的是哪个版本（防"改了没部署"的排查黑洞）
                _announced = true;
                InformationManager.DisplayMessage(new InformationMessage(
                    "[ArcherReposition] v5 主线程版 已加载", Colors.White));
            }

            if (mission == null || mission.MissionEnded || mission.IsMissionEnding)
            {
                if (!_resetPending)
                {
                    _resetPending = true;
                    ArcherRepositionStateStore.ResetAll();
                    Array.Clear(RayCache, 0, RayCache.Length);
                }
                return;
            }
            _resetPending = false;
            FlushLog();

            if (GameNetwork.IsClientOrReplay)
                return;

            float now = mission.CurrentTime;
            _frameBudget = ArcherRepositionConfig.MaxRayUnitsPerFrame;

            // 主线程错峰检测
            DetectMain(mission, now);

            for (int i = 0; i < ArcherRepositionStateStore.Capacity; i++)
            {
                // 快速跳过：绝大多数槽位零 native 调用
                ref ArcherAgentState s = ref ArcherRepositionStateStore.States[i];
                if (s.Stage == ArcherStage.Idle && !s.Dirty && !s.RequestExit)
                    continue;

                Agent agent = ArcherRepositionStateStore.Owners[i];
                if (agent == null || !agent.IsActive() || agent.Mission != mission)
                {
                    // 槽位失效（agent 已移除 / 战斗切换）—— 清理
                    s = default;
                    ArcherRepositionStateStore.Owners[i] = null;
                    continue;
                }

                // ---- ManualTarget 生命周期自管 ----
                if (s.Stage == ArcherStage.ManualTarget)
                {
                    bool restore = s.RequestExit;
                    if (!restore)
                    {
                        Agent target = agent.GetTargetAgent();
                        restore = target == null || !target.IsActive()
                            || agent.GetLastTargetVisibilityState() == AITargetVisibilityState.TargetIsClear;
                    }
                    if (!restore && s.Dirty && s.BadStreak >= ArcherRepositionConfig.DetectionStreak)
                    {
                        restore = true;   // 手动目标也被挡了 → 放弃手动指定，回到常规流程
                    }
                    if (restore)
                    {
                        SafeRestoreAutomaticTarget(agent);
                        EnterCooldown(ref s, now);
                    }
                    continue;
                }

                // ---- Strafe 收尾（预留：主线程侧移实现前不会进入） ----
                if (s.Stage == ArcherStage.Strafe)
                {
                    if (s.RequestExit || s.StageStartTime + ArcherRepositionConfig.StrafeTimeout < now)
                    {
                        EnterCooldown(ref s, now);
                    }
                    continue;
                }

                // ---- Idle / Cooldown：处理检测请求 ----
                if (!s.Dirty)
                    continue;
                s.Dirty = false;
                if (s.Stage == ArcherStage.Cooldown && now < s.NextAllowedTime)
                    continue;
                if (ArcherRepositionConfig.OnlyPlayerTeam && !IsPlayerSide(mission, agent))
                    continue;

                ProcessBlockedAgent(mission, agent, ref s, now);
            }
        }

        // ---------- 主线程检测 ----------

        private static int _detectCursor;

        /// <summary>
        /// 主线程错峰检测：每帧扫描一小批槽位，全部 agent 约 0.5s 轮完一遍。
        /// 所有引擎调用（可见性缓存读 / 射程 / 装备）均为引擎自身在主线程的常态用法。
        /// </summary>
        private static void DetectMain(Mission mission, float now)
        {
            int capacity = ArcherRepositionStateStore.Capacity;
            int scanned = 0;
            for (int n = 0; n < capacity && scanned < ArcherRepositionConfig.DetectBatchPerFrame; n++)
            {
                int i = _detectCursor;
                _detectCursor = _detectCursor + 1 >= capacity ? 0 : _detectCursor + 1;

                Agent agent = ArcherRepositionStateStore.Owners[i];
                if (agent == null)
                    continue;
                scanned++;
                if (!agent.IsActive() || agent.Mission != mission)
                    continue;

                ref ArcherAgentState s = ref ArcherRepositionStateStore.States[i];

                // per-agent 错峰节流（~0.5s/agent）
                if (now < s.NextDetectTime)
                    continue;
                s.NextDetectTime = now + 0.45f + (agent.Index * 0.017f % 0.1f);

                if (!agent.IsAIControlled || agent.Controller != AgentControllerType.AI)
                    continue;
                if (agent.MountAgent != null || agent.IsMount || !agent.IsHuman)
                    continue;

                // "是否远程"缓存刷新（约 8 个检测周期一次）
                if (++s.RefreshCounter >= ArcherRepositionConfig.IsRangedRefreshCycles)
                {
                    s.RefreshCounter = 0;
                    s.IsRanged = ComputeIsRanged(agent);
                }
                if (!s.IsRanged)
                    continue;

                Formation formation = agent.Formation;
                if (formation == null || agent.IsDetachedFromFormation
                    || formation.Arrangement is ColumnFormation
                    || (ArcherRepositionConfig.OnlyLooseFormation
                        && formation.ArrangementOrder != ArrangementOrder.ArrangementOrderLoose)
                    || agent.IsRetreating())
                {
                    if (s.Stage != ArcherStage.Idle && s.Stage != ArcherStage.Cooldown)
                        s.RequestExit = true;
                    s.BadStreak = 0;
                    s.ClearStreak = 0;
                    continue;
                }

                AITargetVisibilityState vis = agent.GetLastTargetVisibilityState();
                if (vis == AITargetVisibilityState.TargetIsClear)
                {
                    s.ClearStreak++;
                    s.BadStreak = 0;
                    if (s.Stage != ArcherStage.Idle && s.Stage != ArcherStage.Cooldown
                        && s.ClearStreak >= ArcherRepositionConfig.ClearStreakToExit)
                    {
                        s.RequestExit = true;
                    }
                    continue;
                }
                s.ClearStreak = 0;

                if (s.Stage == ArcherStage.Cooldown && now < s.NextAllowedTime)
                    continue;
                if (++s.BadStreak < ArcherRepositionConfig.DetectionStreak)
                    continue;

                // 二次确认：目标有效 + 射程内（超射程不是遮挡问题，交给阵型层）
                Agent target = agent.GetTargetAgent();
                if (target == null || !target.IsActive() || !agent.IsEnemyOf(target))
                    continue;
                float range = agent.GetMissileRange();
                if (range <= 0f)
                    continue;
                float maxDistSq = range * range
                    * ArcherRepositionConfig.TargetRangeFactor * ArcherRepositionConfig.TargetRangeFactor;
                if (target.Position.DistanceSquared(agent.Position) > maxDistSq)
                {
                    s.BadStreak = 0;
                    continue;
                }

                s.Dirty = true;   // 下方主循环同帧消费
            }
        }

        private static bool ComputeIsRanged(Agent agent)
        {
            EquipmentIndex slot = agent.GetPrimaryWieldedItemIndex();
            if (slot == EquipmentIndex.None)
                return false;
            WeaponComponentData weapon = agent.Equipment[slot].CurrentUsageItem;
            return weapon != null && weapon.IsRangedWeapon;
        }

        // ---------- 决策 ----------

        private static void ProcessBlockedAgent(Mission mission, Agent agent, ref ArcherAgentState s, float now)
        {
            Team enemy = FindEnemyTeam(mission, agent.Team);
            if (enemy != null && enemy.ActiveAgents.Count > 0)
            {
                Agent candidate = FindClearTarget(mission, agent, enemy, ref s, now);
                if (candidate != null)
                {
                    SafeTakeManualTarget(agent, candidate);
                    s.Stage = ArcherStage.ManualTarget;
                    s.ManualTargetIndex = candidate.Index;
                    s.StageStartTime = now;
                    s.RequestExit = false;
                    Log($"t={now:F1} agent#{agent.Index} 换目标 -> #{candidate.Index}");
                    return;
                }
            }

            // 侧移路径预留（主线程实现前不批准，直接冷却重试）
            EnterCooldown(ref s, now);
        }

        private static Team? FindEnemyTeam(Mission mission, Team? ownTeam)
        {
            if (ownTeam == null)
                return null;
            foreach (Team team in mission.Teams)
            {
                if (team != ownTeam && team.IsEnemyOf(ownTeam) && team.ActiveAgents.Count > 0)
                    return team;
            }
            return null;
        }

        private static Agent? FindClearTarget(Mission mission, Agent agent, Team enemy, ref ArcherAgentState s, float now)
        {
            MBReadOnlyList<Agent> list = enemy.ActiveAgents;
            int count = list.Count;
            if (count <= 0)
                return null;

            float range = agent.GetMissileRange() * ArcherRepositionConfig.TargetRangeFactor;
            float maxSq = range * range;
            float minSq = ArcherRepositionConfig.MinTargetDistance * ArcherRepositionConfig.MinTargetDistance;

            s.SamplePhase++;
            int seed = agent.Index * 31 + s.SamplePhase * 97;   // Index 哈希伪随机（采样去相关）

            for (int k = 0; k < ArcherRepositionConfig.TargetSampleCount; k++)
            {
                int idx = (seed + k * 257) % count;
                if (idx < 0)
                    idx += count;
                Agent candidate = list[idx];
                if (candidate == null || !candidate.IsActive() || candidate.IsMount || !candidate.IsEnemyOf(agent))
                    continue;
                float distSq = candidate.Position.DistanceSquared(agent.Position);
                if (distSq < minSq || distSq > maxSq)
                    continue;
                if (IsLineBlocked(mission, agent, candidate, now))
                    continue;
                return candidate;
            }
            return null;
        }

        // ---------- 射线 ----------

        private static bool IsLineBlocked(Mission mission, Agent shooter, Agent target, float now)
        {
            return IsLineBlockedInternal(mission,
                shooter.GetEyeGlobalPosition(), target.CollisionCapsuleCenter,
                shooter.Index, target.Index, now, cacheKeyExtra: 0);
        }

        private static bool IsLineBlockedInternal(Mission mission, Vec3 from, Vec3 to,
            int shooterIndex, int targetIndex, float now, int cacheKeyExtra)
        {
            int key = 0;
            if (ArcherRepositionConfig.CacheEnabled)
            {
                key = MakeCacheKey(from, to, cacheKeyExtra);
                int slot = key & (ArcherRepositionConfig.CacheSlots - 1);
                if (slot < 0)
                    slot += ArcherRepositionConfig.CacheSlots;
                ref RayCacheEntry entry = ref RayCache[slot];
                if (entry.Key == key)
                {
                    float ttl = entry.Blocked
                        ? ArcherRepositionConfig.CacheTtlBlocked
                        : ArcherRepositionConfig.CacheTtlClear;
                    if (now - entry.Time < ttl)
                        return entry.Blocked;
                }
            }

            bool blocked = false;
            float dist = from.Distance(to);

            // 预算不足：直接放弃本次验证且不写缓存 —— 防止"未验证"被当"畅通"污染缓存
            if (!SpendBudget(ArcherRepositionConfig.AgentRayCost))
                return false;

            // 1) agent 射线（Mission 级胶囊遍历，O(活跃agent)，最贵 → 2 单位）
            try
            {
                Agent hit = mission.RayCastForClosestAgent(
                    from, to, shooterIndex, ArcherRepositionConfig.RayThickness, out float _);
                if (hit != null && hit.Index != targetIndex)
                    blocked = true;                                    // 友军（或别的单位）挡在弹道上
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }

            // 2) 地形/杂物射线（物理 broadphase，较便宜 → 1 单位）
            if (!blocked && SpendBudget(ArcherRepositionConfig.SceneRayCost))
            {
                try
                {
                    bool hitWorld = mission.Scene.RayCastForClosestEntityOrTerrain(
                        from, to, out float collisionDist, out WeakGameEntity _,
                        ArcherRepositionConfig.RayThickness, BodyFlags.CommonCollisionExcludeFlagsForMissile);
                    if (hitWorld && collisionDist < dist - ArcherRepositionConfig.TerrainTolerance)
                        blocked = true;
                }
                catch (Exception ex)
                {
                    ReportFault(ex);
                }
            }

            if (ArcherRepositionConfig.CacheEnabled)
            {
                int slot = key & (ArcherRepositionConfig.CacheSlots - 1);
                if (slot < 0)
                    slot += ArcherRepositionConfig.CacheSlots;
                RayCache[slot] = new RayCacheEntry { Key = key, Time = now, Blocked = blocked };
            }
            return blocked;
        }

        private static int MakeCacheKey(Vec3 from, Vec3 to, int extra)
        {
            // 1m 量化 + 二元组(射手格, 目标格)
            unchecked
            {
                int k = 17;
                k = k * 31 + (int)from.x;
                k = k * 31 + (int)from.y;
                k = k * 31 + (int)to.x;
                k = k * 31 + (int)to.y;
                k = k * 31 + extra;
                return k;
            }
        }

        private static bool SpendBudget(int cost)
        {
            if (_frameBudget < cost)
                return false;
            _frameBudget -= cost;
            return true;
        }

        // ---------- 状态迁移 ----------

        private static void EnterCooldown(ref ArcherAgentState s, float now)
        {
            s.Stage = ArcherStage.Cooldown;
            s.NextAllowedTime = now + ArcherRepositionConfig.ExitCooldown;
            s.StrafeApproved = false;
            s.RequestExit = false;
            s.Dirty = false;
            s.BadStreak = 0;
            s.ClearStreak = 0;
        }

        private static void SafeRestoreAutomaticTarget(Agent agent)
        {
            try
            {
                agent.SetAutomaticTargetSelection(true);
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }
        }

        private static void SafeTakeManualTarget(Agent agent, Agent candidate)
        {
            try
            {
                agent.SetAutomaticTargetSelection(false);   // 官方模式：TaskForceDetachment.cs:95-96
                agent.SetTargetAgent(candidate);
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }
        }

        private static bool IsPlayerSide(Mission mission, Agent agent)
        {
            Team team = agent.Team;
            if (team == null)
                return false;
            if (team.IsPlayerTeam)
                return true;
            Team playerTeam = mission.PlayerTeam;
            return playerTeam != null && !playerTeam.IsEnemyOf(team);   // 含友方队伍
        }

        // ---------- 熔断与日志 ----------

        public static void ReportFault(Exception ex)
        {
            int count = Interlocked.Increment(ref _faultCount);
            Log("FAULT #" + count + " " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            if (count == 1)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "[ArcherReposition] 发生异常，详见 Logs/ArcherReposition.log", Colors.Red));
            }
            if (count >= ArcherRepositionConfig.MaxFaults)
            {
                Disabled = true;
                InformationManager.DisplayMessage(new InformationMessage(
                    "[ArcherReposition] 连续异常已熔断禁用，行为回退原版。", Colors.Red));
            }
        }

        private static readonly ConcurrentQueue<string> LogQueue = new ConcurrentQueue<string>();

        /// <summary>
        /// 线程安全：仅入队（ConcurrentQueue 无锁），绝不在此写文件。
        /// 落盘统一由主线程 FlushLog() 完成（同步文件 IO 在引擎并行区会拖垮整帧，实测）。
        /// </summary>
        private static void Log(string message)
        {
            LogQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
        }

        /// <summary>主线程批量落盘（TickMain 每帧调用一次；单次写盘、无锁竞争）。</summary>
        private static void FlushLog()
        {
            if (LogQueue.IsEmpty)
                return;
            try
            {
                StringBuilder buffer = new StringBuilder();
                while (LogQueue.TryDequeue(out string line))
                    buffer.Append(line).Append('\n');
                if (buffer.Length == 0)
                    return;
                string path = Path.GetFullPath(Path.Combine(
                    Environment.CurrentDirectory, "../../Modules/ArcherReposition/Logs/ArcherReposition.log"));
                string? dir = Path.GetDirectoryName(path);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, buffer.ToString());
            }
            catch
            {
                // 日志失败静默 —— 绝不影响战斗；封顶丢弃保护防内存增长
                if (LogQueue.Count > 4096)
                    while (LogQueue.Count > 2048) LogQueue.TryDequeue(out _);
            }
        }
    }
}
