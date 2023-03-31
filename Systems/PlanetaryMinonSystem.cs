using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Unity.Entities;
using UnityEngine.Experimental.AI;
using Assets.Scripts.Navigation;
using Pathfinding;
using Unity.Burst;
using Tiles;
using Agent;

[UpdateAfter(typeof(MinionAttackSystem))]
public partial class PlanetaryMinionSystem : SystemBase
{
    public struct Minions
    {
        public NativeArray<UnitTransformData> transforms;

        public int Length;
    }

    public struct PlanetaryMinions
    {
        public NativeArray<PlanetaryUnitTransformData> planetaryTransforms;

        public int Length;
    }

    private Minions minions;

    private PlanetaryMinions planetaryMinions;

    EntityQuery m_Query;
    EntityQuery m_PlanetaryQuery;

    protected override void OnCreate()
    {
        var queryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] {
            ComponentType.ReadOnly<UnitTransformData>(), ComponentType.ReadOnly<PlanetaryComponent>() }
        };

        m_Query = GetEntityQuery(queryDesc);

        var planetaryQueryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] {
            ComponentType.ReadWrite<PlanetaryUnitTransformData>() }
        };

        m_PlanetaryQuery = GetEntityQuery(planetaryQueryDesc);
    }
    protected override void OnDestroy()
    {
    }

    protected override void OnUpdate()
    {
        if (!Application.isPlaying)
            return;

        minions.Length = m_Query.CalculateEntityCount();
        planetaryMinions.Length = m_PlanetaryQuery.CalculateEntityCount();

        if (minions.Length == 0 || minions.Length != planetaryMinions.Length) return; // I still hate these initialization issues

        JobHandle jobHandle1, jobHandle2, jobHandle3;
        minions.transforms = m_Query.ToComponentDataArrayAsync<UnitTransformData>(Allocator.TempJob, out jobHandle1);
        planetaryMinions.planetaryTransforms = m_PlanetaryQuery.ToComponentDataArrayAsync<PlanetaryUnitTransformData>(Allocator.TempJob, out jobHandle2);
        Dependency = JobHandle.CombineDependencies(Dependency, jobHandle1, jobHandle2);

        // ============ JOB CREATION ===============
        var minionBehaviorJob = new PlanetaryPositionRotationJob
        {
            transforms = minions.transforms,
            planetaryTransforms = planetaryMinions.planetaryTransforms,
            gridSettings = GridUtilties.gridSettings,
            heightVariation = Main.ActiveInitParams.heightVariation,
            planetRadius = Main.ActiveInitParams.planetRadius,
            offset = new float3(0, 200, 0)
        };

        var minionBehaviorJobFence = minionBehaviorJob.Schedule(minions.Length, SimulationState.BigBatchSize,
            Dependency);

        minionBehaviorJobFence.Complete(); // todo get rid of sync point

        m_PlanetaryQuery.CopyFromComponentDataArrayAsync(planetaryMinions.planetaryTransforms, out jobHandle3);
        Dependency = JobHandle.CombineDependencies(minionBehaviorJobFence, jobHandle3);
        Dependency = planetaryMinions.planetaryTransforms.Dispose(Dependency);
    }

    //-----------------------------------------------------------------------------
    [BurstCompile]
    struct PlanetaryPositionRotationJob : IJobParallelFor
    {
        [ReadOnly, DeallocateOnJobCompletion]
        public NativeArray<UnitTransformData> transforms;

        public NativeArray<PlanetaryUnitTransformData> planetaryTransforms;

        public GridSettings gridSettings;
        public float3 offset;
        public float heightVariation;
        public float planetRadius;

        //-----------------------------------------------------------------------------
        public void Execute(int index)
        {
            var pos = transforms[index].Position;
            var forward = transforms[index].Forward;
            var forwardPos = pos + forward * 100;

            var planetaryTransform = planetaryTransforms[index];

            // clamp position
            float maxWorldX = gridSettings.worldSize.x * .5f;
            float maxWorldZ = gridSettings.worldSize.y * .5f;

            int gridIndex = (int)((pos.x + maxWorldX) / gridSettings.worldSize.y);

            float3 newPosition;
            float3 newForward;
            float3 newForwardPos;

            switch (gridIndex)
            {
                case 0:
                    newPosition = CalculatePlanetaryPosition(new float3((pos.z) / maxWorldZ, 1, (pos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ));
                    newForwardPos = CalculatePlanetaryPosition(new float3((forwardPos.z) / maxWorldZ, 1, (forwardPos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ));
                    break;

                case 1:
                    newPosition = CalculatePlanetaryPosition(new float3((pos.z) / maxWorldZ, -(pos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, 1));
                    newForwardPos = CalculatePlanetaryPosition(new float3((forwardPos.z) / maxWorldZ, -(forwardPos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, 1));
                    break;
                case 2:
                    newPosition = CalculatePlanetaryPosition(new float3((pos.z) / maxWorldZ, -1, -(pos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ));
                    newForwardPos = CalculatePlanetaryPosition(new float3((forwardPos.z) / maxWorldZ, -1, -(forwardPos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ));
                    break;

                case 3:
                    newPosition = CalculatePlanetaryPosition(new float3((pos.z) / maxWorldZ, (pos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, -1));
                    newForwardPos = CalculatePlanetaryPosition(new float3((forwardPos.z) / maxWorldZ, (forwardPos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, -1));
                    break;
                case 4:
                    newPosition = CalculatePlanetaryPosition(new float3(-1, (pos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, -(pos.z) / maxWorldZ));
                    newForwardPos = CalculatePlanetaryPosition(new float3(-1, (forwardPos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, -(forwardPos.z) / maxWorldZ));
                    break;

                case 5:
                    newPosition = CalculatePlanetaryPosition(new float3(1, (pos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, (pos.z) / maxWorldZ));
                    newForwardPos = CalculatePlanetaryPosition(new float3(1, (forwardPos.x - ((gridIndex - 3) * 2 + 1) * maxWorldZ) / maxWorldZ, (forwardPos.z) / maxWorldZ));
                    break;

                default:
                    Debug.Log("GridIndex is wrong!");
                    break;
            }

            float3 newPositionProjected = Vector3.Project(newForwardPos, newPosition);

            newForward = newForwardPos - newPositionProjected;

            newPosition.y -= offset.y;
            
            planetaryTransform.Position = newPosition;
            planetaryTransform.Forward = newForward;
            planetaryTransforms[index] = planetaryTransform;
        }

        private float3 CalculatePlanetaryPosition(float3 position3D)
        {

            float dX2, dY2, dZ2;

            dX2 = position3D.x * position3D.x;
            dY2 = position3D.y * position3D.y;
            dZ2 = position3D.z * position3D.z;

            if (position3D.x > 0)
                position3D.x = math.sqrt(1f / (1f + dY2 / dX2 + dZ2 / dX2));
            else
                position3D.x = -math.sqrt(1f / (1f + dY2 / dX2 + dZ2 / dX2));
            if (position3D.y > 0)
                position3D.y = math.sqrt(1f / (1f + dX2 / dY2 + dZ2 / dY2));
            else
                position3D.y = -math.sqrt(1f / (1f + dX2 / dY2 + dZ2 / dY2));
            if (position3D.z > 0)
                position3D.z = math.sqrt(1f / (1f + dY2 / dZ2 + dX2 / dZ2));
            else
                position3D.z = -math.sqrt(1f / (1f + dY2 / dZ2 + dX2 / dZ2));

            // calculate noise displacement
            float displacement = Main.Instance.Terrain.moduleStruct.GetValue(position3D);

            // displace vertex position
            position3D += position3D * displacement * heightVariation;

            // scale to planet radius
            position3D *= planetRadius;

            return position3D;
        }
    }
}
