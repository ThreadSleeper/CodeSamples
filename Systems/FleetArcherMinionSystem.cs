using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using Unity.Entities;
using Agent;

[UpdateAfter(typeof(MinionCollisionSystem))]
public partial class FleetArcherMinionSystem : SystemBase
{
    public struct RangedMinions
    {
        [ReadOnly]
        public NativeArray<RangedUnitData> rangedMinionsFilter;
        [ReadOnly]
        public NativeArray<AliveMinionData> aliveMinionsFilter;
        [ReadOnly]
        public NativeArray<UnitTransformData> transforms;
        [ReadOnly]
        public NativeArray<MinionBitmask> bitmask;
        public NativeArray<MinionData> minions;

        public int Length;
    }

    private RangedMinions rangedMinions;

    private ComponentLookup<FormationClosestData> formationClosestDataFromEntity;

    private ComponentLookup<FormationData> formationsFromEntity;

    private UnitLifecycleManager lifeCycleManager;

    private JobHandle archerJobFence;

    public float archerAttackCycle = 0;

    EntityQuery m_Query;

    protected override void OnCreate()
    {
        var queryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] { ComponentType.ReadOnly<RangedUnitData>(), ComponentType.ReadOnly<AliveMinionData>(), 
            ComponentType.ReadOnly<UnitTransformData>(), ComponentType.ReadOnly<MinionBitmask>(), 
            ComponentType.ReadWrite<MinionData>(), ComponentType.ReadOnly<FleetComponent>() }
        };

        m_Query = GetEntityQuery(queryDesc);

        lifeCycleManager = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<UnitLifecycleManager>();
    }

    protected override void OnUpdate()
    {
        rangedMinions.Length = m_Query.CalculateEntityCount();

        if (rangedMinions.Length == 0 || !lifeCycleManager.createdArrows.IsCreated) return;

        // query for all needed data

        JobHandle jobHandle1, jobHandle2, jobHandle3, jobHandle4, jobHandle5;
        rangedMinions.minions = m_Query.ToComponentDataArrayAsync<MinionData>(Allocator.TempJob, out jobHandle1);
        rangedMinions.bitmask = m_Query.ToComponentDataArrayAsync<MinionBitmask>(Allocator.TempJob, out jobHandle2);
        rangedMinions.transforms = m_Query.ToComponentDataArrayAsync<UnitTransformData>(Allocator.TempJob, out jobHandle3);
        formationClosestDataFromEntity = GetComponentLookup<FormationClosestData>();
        formationsFromEntity = GetComponentLookup<FormationData>();

        Dependency = JobHandle.CombineDependencies(Dependency, jobHandle1, jobHandle2);
        Dependency = JobHandle.CombineDependencies(Dependency, jobHandle3);

        float prevArcherAttackCycle = archerAttackCycle;
        archerAttackCycle += World.Time.DeltaTime;
        if (archerAttackCycle > SimulationSettings.Instance.ArcherAttackTime)
        {
            archerAttackCycle -= SimulationSettings.Instance.ArcherAttackTime;
        }

        var archerJob = new FleetArcherJob
        {
            createdArrowsQueue = lifeCycleManager.createdFleetArrows.AsParallelWriter(),
            archers = rangedMinions.minions,
            transforms = rangedMinions.transforms,
            formations = formationsFromEntity,
            closestFormationsFromEntity = formationClosestDataFromEntity,
            minionConstData = rangedMinions.bitmask,
            randomizer = UnityEngine.Time.frameCount,
            archerHitTime = SimulationSettings.Instance.ArcherHitTime,
            archerAttackCycle = archerAttackCycle,
            prevArcherAttackCycle = prevArcherAttackCycle
        };

        archerJobFence = archerJob.Schedule(rangedMinions.Length, SimulationState.SmallBatchSize, Dependency);

        archerJobFence.Complete(); // todo get rid off sync point

        m_Query.CopyFromComponentDataArrayAsync(rangedMinions.minions, out jobHandle4);

        Dependency = JobHandle.CombineDependencies(archerJobFence, jobHandle4);
        Dependency = rangedMinions.minions.Dispose(Dependency);
    }
}
