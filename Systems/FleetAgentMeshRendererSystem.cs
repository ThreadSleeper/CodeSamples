using System.Collections.Generic;
using Agent;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Assertions;


//-----------------------------------------------------------------------------
// this is a copy of MeshInstanceRendererSystem with some required changes
// ReSharper disable once RequiredBaseTypesIsNotInherited
[DisableAutoCreation]
[UpdateInGroup(typeof(RenderingGroup))]
[UpdateAfter(typeof(UnityEngine.PlayerLoop.PreLateUpdate.ParticleSystemBeginUpdateAll))]
[ExecuteInEditMode]
public partial class FleetAgentMeshInstanceRendererSystem : SystemBase
{
    private const int PreallocatedLod0BufferSize = 4 * 1024;
    private const int PreallocatedLod1BufferSize = 8 * 1024;
    private const int PreallocatedLod2BufferSize = 16 * 1024;
    private const int PreallocatedLod3BufferSize = 32 * 1024;
    private JobHandle CullAndComputeJobHandle;

    private NativeList<LocalToWorld> m_Lod0Transforms;
    private NativeList<LocalToWorld> m_Lod1Transforms;
    private NativeList<LocalToWorld> m_Lod2Transforms;
    private NativeList<LocalToWorld> m_Lod3Transforms;

    private NativeList<PlanetarySelection> m_Lod0Selections;
    private NativeList<PlanetarySelection> m_Lod1Selections;
    private NativeList<PlanetarySelection> m_Lod2Selections;
    private NativeList<PlanetarySelection> m_Lod3Selections;

    // Instance renderer takes only batches of 1023
    private Matrix4x4[] m_MatricesArray = new Matrix4x4[512];
    private NativeArray<float3> m_Colors;
    private List<RenderingData> m_CacheduniqueRendererTypes = new List<RenderingData>(10);
    private List<ComputeBuffer> m_ComputeBuffers = new List<ComputeBuffer>();
    private List<Material> m_Materials = new List<Material>();
    EntityQuery m_Query;

    //-----------------------------------------------------------------------------
    public unsafe static void CopyMatrices(NativeArray<LocalToWorld> transforms, int beginIndex, int length, Matrix4x4[] outMatrices)
    {
        fixed (Matrix4x4* matricesPtr = outMatrices)
        {
            Assert.AreEqual(sizeof(Matrix4x4), sizeof(LocalToWorld));
            var matricesSlice = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<LocalToWorld>(matricesPtr, sizeof(Matrix4x4), length);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeSliceUnsafeUtility.SetAtomicSafetyHandle(ref matricesSlice, AtomicSafetyHandle.GetTempUnsafePtrSliceHandle());
#endif
            NativeSlice<LocalToWorld> copyFrom = new NativeSlice<LocalToWorld>(transforms, beginIndex, length);

            matricesSlice.CopyFrom(copyFrom);
        }
    }

    //-----------------------------------------------------------------------------
    protected override void OnCreate()
    {
        // We want to find all AgentMeshInstanceRenderer & TransformMatrix combinations and render them

        m_Colors = new NativeArray<float3>(512, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        m_Lod0Transforms = new NativeList<LocalToWorld>(PreallocatedLod0BufferSize, Allocator.Persistent);
        m_Lod1Transforms = new NativeList<LocalToWorld>(PreallocatedLod1BufferSize, Allocator.Persistent);
        m_Lod2Transforms = new NativeList<LocalToWorld>(PreallocatedLod2BufferSize, Allocator.Persistent);
        m_Lod3Transforms = new NativeList<LocalToWorld>(PreallocatedLod3BufferSize, Allocator.Persistent);
        m_Lod0Selections = new NativeList<PlanetarySelection>(PreallocatedLod0BufferSize, Allocator.Persistent);
        m_Lod1Selections = new NativeList<PlanetarySelection>(PreallocatedLod1BufferSize, Allocator.Persistent);
        m_Lod2Selections = new NativeList<PlanetarySelection>(PreallocatedLod2BufferSize, Allocator.Persistent);
        m_Lod3Selections = new NativeList<PlanetarySelection>(PreallocatedLod3BufferSize, Allocator.Persistent);

        var queryDesc = new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(RenderingData), ComponentType.ReadOnly<UnitTransformData>(),
                ComponentType.ReadOnly<PlanetarySelection>(), ComponentType.ReadOnly<FleetComponent>() }
            };

        m_Query = GetEntityQuery(queryDesc);
    }

    //-----------------------------------------------------------------------------
    protected override void OnDestroy()
    {
        foreach (var buffer in m_ComputeBuffers)
            buffer.Release();

        m_Colors.Dispose();
        m_Lod0Transforms.Dispose();
        m_Lod1Transforms.Dispose();
        m_Lod2Transforms.Dispose();
        m_Lod3Transforms.Dispose();
        m_Lod0Selections.Dispose();
        m_Lod1Selections.Dispose();
        m_Lod2Selections.Dispose();
        m_Lod3Selections.Dispose();
    }

    //-----------------------------------------------------------------------------
    protected override void OnUpdate()
    {
        // We want to iterate over all unique MeshInstanceRenderer shared component data,
        // that are attached to any entities in the world
        EntityManager.GetAllUniqueSharedComponentsManaged(m_CacheduniqueRendererTypes);

        var cameraPosition = Camera.main.transform.position;

        for (int i = 1; i != m_CacheduniqueRendererTypes.Count; i++)
        {
            int drawIdx = 0;
            int beginIndex = 0;
            // For each unique MeshInstanceRenderer data, we want to get all entities with a TransformMatrix
            // SharedComponentData gurantees that all those entities are packed togehter in a chunk with linear memory layout.
            // As a result the copy of the matrices out is internally done via memcpy.
            var renderer = m_CacheduniqueRendererTypes[i];
            m_Query.AddSharedComponentFilterManaged(renderer);

            var entityCount = m_Query.CalculateEntityCount();
            var transforms = m_Query.ToComponentDataArray<UnitTransformData>(Allocator.TempJob);
            var selections = m_Query.ToComponentDataArray<PlanetarySelection>(Allocator.TempJob);

            var cullAndComputeJob = new CullAndComputeParametersSafe()
            {
                unitTransformData = transforms,
                selections = selections,
                CameraPosition = cameraPosition,
                DistanceMaxLod0 = 50,
                DistanceMaxLod1 = 100,
                DistanceMaxLod2 = 200,
                Lod0Transforms = m_Lod0Transforms,
                Lod1Transforms = m_Lod1Transforms,
                Lod2Transforms = m_Lod2Transforms,
                Lod3Transforms = m_Lod3Transforms,
                Lod0selections = m_Lod0Selections,
                Lod1selections = m_Lod1Selections,
                Lod2selections = m_Lod2Selections,
                Lod3selections = m_Lod3Selections
            };

            Dependency = cullAndComputeJob.Schedule(Dependency);
            Dependency.Complete();

            DrawMeshInstanced(m_Lod0Transforms, ref drawIdx, renderer, m_Lod0Selections, renderer.LodData.Lod1Mesh, beginIndex);
            DrawMeshInstanced(m_Lod1Transforms, ref drawIdx, renderer, m_Lod1Selections, renderer.LodData.Lod1Mesh, beginIndex);
            DrawMeshInstanced(m_Lod2Transforms, ref drawIdx, renderer, m_Lod2Selections, renderer.LodData.Lod2Mesh, beginIndex);
            DrawMeshInstanced(m_Lod3Transforms, ref drawIdx, renderer, m_Lod3Selections, renderer.LodData.Lod3Mesh, beginIndex);

            beginIndex += entityCount;

            Dependency = transforms.Dispose(Dependency);
            Dependency = selections.Dispose(Dependency);

            m_Query.ResetFilter();

            m_Lod0Transforms.Clear();
            m_Lod1Transforms.Clear();
            m_Lod2Transforms.Clear();
            m_Lod3Transforms.Clear();
        }

        Dependency.Complete();

        m_Lod0Selections.Clear();
        m_Lod1Selections.Clear();
        m_Lod2Selections.Clear();
        m_Lod3Selections.Clear();

        m_CacheduniqueRendererTypes.Clear();
    }

    private void DrawMeshInstanced(NativeList<LocalToWorld> transforms, ref int drawIdx,
        RenderingData renderer, NativeList<PlanetarySelection> selection, Mesh mesh, int beginIndex = 0)
    {
        // For now, we have to copy our data into Matrix4x4[] with a specific upper limit of how many instances we can render in one batch.
        // So we just have a for loop here, representing each Graphics.DrawMeshInstanced batch

        while (beginIndex < transforms.Length)
        {
            // Copy Matrices
            int length = math.min(m_MatricesArray.Length, transforms.Length - beginIndex);
            CopyMatrices(transforms, beginIndex, length, m_MatricesArray);

            if (drawIdx + 1 >= m_ComputeBuffers.Count)
            {
                var computeBuffer = new ComputeBuffer(512, 3 * sizeof(float));
                m_ComputeBuffers.Add(computeBuffer);
                var material = new Material(renderer.Material);
                m_Materials.Add(material);
            }

            for (int x = 0; x < length; ++x)
                m_Colors[x] = selection[beginIndex + x].Value == 1 ? new Vector3(0.5f, 1f, 0.5f) : new Vector3(0.1f, 0.1f, 0.1f);

            m_ComputeBuffers[drawIdx].SetData(m_Colors, 0, 0, length);
            m_Materials[drawIdx].SetBuffer("velocityBuffer", m_ComputeBuffers[drawIdx]);

            // !!! This will draw all meshes using the last material.  Probably need an array of materials.
            Graphics.DrawMeshInstanced(mesh, 0, m_Materials[drawIdx], m_MatricesArray, length, null, UnityEngine.Rendering.ShadowCastingMode.On, false);
            drawIdx++;
            beginIndex += length;
        }
    }

    [BurstCompile]
    struct CullAndComputeParametersSafe : IJob
    {

        [ReadOnly]
        public NativeArray<UnitTransformData> unitTransformData;

        [ReadOnly]
        public NativeArray<PlanetarySelection> selections;

        [ReadOnly]
        public float DistanceMaxLod0;

        [ReadOnly]
        public float DistanceMaxLod1;

        [ReadOnly]
        public float DistanceMaxLod2;

        [ReadOnly]
        public float3 CameraPosition;

        public NativeList<LocalToWorld> Lod0Transforms;

        public NativeList<LocalToWorld> Lod1Transforms;

        public NativeList<LocalToWorld> Lod2Transforms;

        public NativeList<LocalToWorld> Lod3Transforms;

        public NativeList<PlanetarySelection> Lod0selections;

        public NativeList<PlanetarySelection> Lod1selections;

        public NativeList<PlanetarySelection> Lod2selections;

        public NativeList<PlanetarySelection> Lod3selections;

        public void Execute()
        {
            for (int i = 0; i < unitTransformData.Length; i++)
            {
                var unitTransform = unitTransformData[i];

                if ((Vector3)unitTransform.Forward == Vector3.zero || (Vector3)unitTransform.Position == Vector3.zero)
                {
                    continue;
                }

                var selection = selections[i];
                float distance = math.length(CameraPosition - unitTransform.Position);

                float3 f = math.normalize(unitTransform.Forward);
                float3 r = math.cross(f, new float3(0, -1, 0));
                float3 u = math.cross(f, r);
                float3 p = unitTransform.Position;

                // Just add some scale to the minions, later remove this

                var transformMatrix = new Matrix4x4(new Vector4(r.x, r.y, r.z, 0),
                                                new Vector4(u.x, u.y, u.z, 0),
                                                new Vector4(f.x, f.y, f.z, 0),
                                                new Vector4(p.x, p.y, p.z, 1f));

                transformMatrix = transformMatrix * GetScaleMatrix(unitTransform.Scale); // todo remove after getting right scaled mesh to get rid of this unnecessary calculations

                var localToworld = new LocalToWorld();
                localToworld.Value = transformMatrix;

                if (distance < DistanceMaxLod0)
                {
                    Lod0Transforms.Add(localToworld);
                    Lod0selections.Add(selection);
                }
                else if (distance < DistanceMaxLod1)
                {
                    Lod1Transforms.Add(localToworld);
                    Lod1selections.Add(selection);
                }
                else if (distance < DistanceMaxLod2)
                {
                    Lod2Transforms.Add(localToworld);
                    Lod2selections.Add(selection);
                }
                else
                {
                    Lod3Transforms.Add(localToworld);
                    Lod3selections.Add(selection);
                }
            }
        }

        public static Matrix4x4 GetScaleMatrix(float scale)
        {
            return new Matrix4x4(new Vector4(scale, 0, 0, 0),
                                 new Vector4(0, scale, 0, 0),
                                 new Vector4(0, 0, scale, 0),
                                 new Vector4(0, 0, 0, 1));
        }
    }
}