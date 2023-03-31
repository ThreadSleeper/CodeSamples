using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Unity.Entities;
using Unity.Burst;
using Agent;

[UpdateAfter(typeof(MinionSystem))]
public partial class FleetArrowSystem : SystemBase
{
    public struct Arrows
    {
        public NativeArray<ArrowData> data;
        public NativeArray<Entity> entities;

        public int Length;
    }

    public struct Minions
    {
        [ReadOnly]
        public NativeArray<AliveMinionData> aliveMinionsFilter;
        [ReadOnly]
        public NativeArray<MinionBitmask> constData;
        [ReadOnly]
        public NativeArray<UnitTransformData> transforms;
        public NativeArray<Entity> entities;

        public int Length;
    }

    private Arrows arrows;

    private Minions minions;

    private MinionSystem minionSystem;

    private NativeArray<RaycastHit> raycastHits;
    private NativeArray<RaycastCommand> raycastCommands;

    private UnitLifecycleManager lifecycleManager;

    EntityQuery m_Query;
    EntityQuery m_ArrowsQuery;

    protected override void OnCreate()
    {
        var queryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] { ComponentType.ReadOnly<AliveMinionData>(),
            ComponentType.ReadOnly<MinionBitmask>(),
            ComponentType.ReadOnly<UnitTransformData>(), ComponentType.ReadOnly<FleetComponent>() }
        };

        m_Query = GetEntityQuery(queryDesc);

        var arrowsQueryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] { ComponentType.ReadWrite<ArrowData>(), ComponentType.ReadOnly<FleetComponent>() }
        };

        m_ArrowsQuery = GetEntityQuery(arrowsQueryDesc);

        minionSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<MinionSystem>();
        lifecycleManager = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<UnitLifecycleManager>();
    }

    protected override void OnDestroy()
    {
        if (raycastHits.IsCreated) raycastHits.Dispose();
        if (raycastCommands.IsCreated) raycastCommands.Dispose();
    }

    protected override void OnUpdate()
    {
        if (minionSystem == null) return;

        arrows.Length = m_ArrowsQuery.CalculateEntityCount();
        minions.Length = m_Query.CalculateEntityCount();

        if (arrows.Length == 0) return;
        if (minions.Length == 0) return;

        JobHandle jobHandle1, jobHandle2, jobHandle3, jobHandle4, jobHandle5, jobHandle6;
        minions.constData = m_Query.ToComponentDataArrayAsync<MinionBitmask>(Allocator.TempJob, out jobHandle1);
        minions.transforms = m_Query.ToComponentDataArrayAsync<UnitTransformData>(Allocator.TempJob, out jobHandle2);
        minions.entities = m_Query.ToEntityArrayAsync(Allocator.TempJob, out jobHandle3);

        arrows.data = m_ArrowsQuery.ToComponentDataArrayAsync<ArrowData>(Allocator.TempJob, out jobHandle4);
        arrows.entities = m_ArrowsQuery.ToEntityArrayAsync(Allocator.TempJob, out jobHandle5);


        Dependency = JobHandle.CombineDependencies(Dependency, jobHandle1, jobHandle2);
        Dependency = JobHandle.CombineDependencies(Dependency, jobHandle3);
        Dependency = JobHandle.CombineDependencies(Dependency, jobHandle4, jobHandle5);

        // Update seems to be called after Play mode has been exited

        // ============ REALLOC ===============
        // todo fix nativearray
        NativeArrayExtensions.ResizeNativeArray(ref raycastHits, math.max(raycastHits.Length, arrows.Length));
        NativeArrayExtensions.ResizeNativeArray(ref raycastCommands, math.max(raycastCommands.Length, arrows.Length));

        // ============ JOB CREATION ===============

        var arrowJob = new ProgressFleetArrowJob
        {
            raycastCommands = raycastCommands,
            arrows = arrows.data,
            arrowEntities = arrows.entities,
            dt = World.Time.DeltaTime,
            allMinionTransforms = minions.transforms,
            buckets = minionSystem.CollisionBuckets,
            minionConstData = minions.constData,
            AttackCommands = CommandSystem.AttackCommandsConcurrent,
            minionEntities = minions.entities,
            queueForKillingEntities = lifecycleManager.queueForKillingEntities.AsParallelWriter()
        };

        var stopArrowJob = new StopArrowsJob
        {
            raycastHits = raycastHits,
            arrows = arrows.data,
            arrowEntities = arrows.entities,
            stoppedArrowsQueue = lifecycleManager.deathQueue.AsParallelWriter()
        };

        var arrowJobFence = arrowJob.Schedule(arrows.Length, SimulationState.SmallBatchSize, JobHandle.CombineDependencies(Dependency, CommandSystem.AttackCommandsFence));
        var raycastJobFence = RaycastCommand.ScheduleBatch(raycastCommands, raycastHits, SimulationState.SmallBatchSize, arrowJobFence);
        var stopArrowJobFence = stopArrowJob.Schedule(arrows.Length, SimulationState.SmallBatchSize, raycastJobFence);

        CommandSystem.AttackCommandsConcurrentFence = JobHandle.CombineDependencies(stopArrowJobFence, CommandSystem.AttackCommandsConcurrentFence);

        stopArrowJobFence.Complete(); // todo get rid off sync point

        m_ArrowsQuery.CopyFromComponentDataArrayAsync(arrows.data, out jobHandle6);
        Dependency = JobHandle.CombineDependencies(CommandSystem.AttackCommandsConcurrentFence, jobHandle6);
        Dependency = arrows.data.Dispose(Dependency);
    }

    [BurstCompile]
    public struct StopArrowsJob : IJobParallelFor
    {
        public NativeArray<ArrowData> arrows;
        [ReadOnly, DeallocateOnJobCompletion]
        public NativeArray<Entity> arrowEntities;

        [ReadOnly]
        public NativeArray<RaycastHit> raycastHits;

        public NativeQueue<Entity>.ParallelWriter stoppedArrowsQueue;

        public void Execute(int index)
        {
            if (arrows[index].active)
            {
                var arrow = arrows[index];

                if (arrow.position.y <= raycastHits[index].point.y)
                {
                    arrow.active = false;
                    arrows[index] = arrow;
                    stoppedArrowsQueue.Enqueue(arrowEntities[index]);
                }
            }
        }
    }
}