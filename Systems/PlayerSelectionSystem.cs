using Agent;
using Assets.Scripts.Components.Buildings;
using ECSInput;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

//-----------------------------------------------------------------------------
[DisableAutoCreation]
public partial class PlayerSelectionSystem : SystemBase
{
    private InputDataGroup m_Input;
    private SelectionGroup m_AgentSelection;
    private PlanetarySelectionGroup m_PlanetaryAgentSelection;
    private SelectionRect m_Selection;
    private float3 m_Start;
    private float3 m_Stop;

    public NativeArray<bool> isSingleTargetSelectedArray;
    public NativeArray<Entity> singleTargetSelectedEntityArray;
    public static bool isSingleTargetSelected;
    public static int singleTargetSelectedTypeId;

    EntityQuery m_Query;
    EntityQuery m_PlanetaryQuery;
    EntityQuery m_inputQuery;

    //-----------------------------------------------------------------------------
    protected override void OnCreate()
    {
        m_Input = new InputDataGroup();
        m_Input.Buttons = new List<InputButtons>();

        m_Selection = Object.FindObjectOfType<SelectionRect>();

        isSingleTargetSelectedArray = new NativeArray<bool>(1, Allocator.Persistent);
        singleTargetSelectedEntityArray = new NativeArray<Entity>(1, Allocator.Persistent);

        var queryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] { ComponentType.ReadOnly<Position>(),
            ComponentType.ReadWrite<Selection>() }
        };

        m_Query = GetEntityQuery(queryDesc);

        var planetaryQueryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] { ComponentType.ReadOnly<Position>(),
            ComponentType.ReadWrite<PlanetarySelection>() }
        };

        m_PlanetaryQuery = GetEntityQuery(planetaryQueryDesc);

        var inputQueryDesc = new EntityQueryDesc
        {
            All = new ComponentType[] { typeof(InputButtons), ComponentType.ReadOnly<MousePosition>() }
        };

        m_inputQuery = GetEntityQuery(inputQueryDesc);
    }

    protected override void OnDestroy()
    {
        isSingleTargetSelectedArray.Dispose();
        singleTargetSelectedEntityArray.Dispose();
    }

    //-----------------------------------------------------------------------------
    protected override void OnUpdate()
    {
        EntityManager.GetAllUniqueSharedComponentsManaged(m_Input.Buttons);

        var status = m_Input.Buttons[1].Values["SelectAgents"].Status;
        if (status == ECSInput.InputButtons.NONE)
            return;


        m_Input.MousePos = m_inputQuery.ToComponentDataArray<MousePosition>(Allocator.TempJob);
        m_AgentSelection.position = m_Query.ToComponentDataArray<Position>(Allocator.TempJob);
        m_AgentSelection.selection = m_Query.ToComponentDataArray<Selection>(Allocator.TempJob);
        m_AgentSelection.entities = m_Query.ToEntityArray(Allocator.TempJob);

        var building = GetSharedComponentTypeHandle<BuildingComponent>();

        m_AgentSelection.Length = m_Query.CalculateEntityCount();
        m_PlanetaryAgentSelection.position = m_PlanetaryQuery.ToComponentDataArray<Position>(Allocator.TempJob);
        m_PlanetaryAgentSelection.selection = m_PlanetaryQuery.ToComponentDataArray<PlanetarySelection>(Allocator.TempJob);

        // select all key?
        status = m_Input.Buttons[1].Values["SelectAll"].Status;
        if (status == ECSInput.InputButtons.UP)
        {
            var selectionJob = new SelectAllJob {
                AgentSelection = m_AgentSelection,
                PlanetaryAgentSelection = m_PlanetaryAgentSelection
            };
            Dependency = selectionJob.Schedule(m_AgentSelection.Length, 64, Dependency);
            Dependency.Complete();
        }

        status = m_Input.Buttons[1].Values["SelectAgents"].Status;
        if (status == ECSInput.InputButtons.NONE)
            return;


        // select by rectangle?
        if (status == ECSInput.InputButtons.DOWN)
        {
            m_Start = m_Input.MousePos[0].Value;
            m_Selection.Start = m_Start;
            m_Selection.Stop = m_Stop;
            m_Selection.enabled = true;
        }
        m_Stop = m_Input.MousePos[0].Value;
        m_Selection.Stop = m_Stop;

        if (status == ECSInput.InputButtons.UP)
        {
            m_Selection.enabled = false;
            float3 length = m_Start - m_Stop;

            if (math.dot(length, length) < 1)
            {
                //isSingleTargetSelected = isSingleTargetSelectedArray[0];
                isSingleTargetSelectedArray[0] = false;
                singleTargetSelectedEntityArray[0] = new Entity();

                var job = new SelectGoalJob
                {
                    Stop = Normalize(math.max(m_Start, m_Stop), Screen.width, Screen.height),
                    World2Clip = Camera.main.projectionMatrix * Camera.main.GetComponent<Transform>().worldToLocalMatrix,
                    AgentSelection = m_AgentSelection,
                    PlanetaryAgentSelection = m_PlanetaryAgentSelection,
                    SingleTargetSelectedEntityArray = singleTargetSelectedEntityArray,
                    IsSingleTargetSelected = isSingleTargetSelectedArray,
                };
                Dependency = job.Schedule(m_AgentSelection.Length, 64, Dependency);
                Dependency.Complete();

                isSingleTargetSelected = isSingleTargetSelectedArray[0];

                if (singleTargetSelectedEntityArray[0] != new Entity())
                {
                    var buildingComponent = EntityManager.GetSharedComponentManaged<BuildingComponent>(singleTargetSelectedEntityArray[0]); //todo

                    Debug.Log("building component: " + buildingComponent.buildingType.ToString());
                }
            }
        }

        if (m_Selection.enabled)
        {
            var job = new SelectionJob
            {
                Start = Normalize(math.min(m_Start, m_Stop), Screen.width, Screen.height),
                Stop = Normalize(math.max(m_Start, m_Stop), Screen.width, Screen.height),
                World2Clip = Camera.main.projectionMatrix * Camera.main.GetComponent<Transform>().worldToLocalMatrix,
                AgentSelection = m_AgentSelection,
                PlanetaryAgentSelection = m_PlanetaryAgentSelection
            };
            var selectionJobhandle = job.Schedule(m_AgentSelection.Length, 64, Dependency);

            selectionJobhandle.Complete();

            Dependency = selectionJobhandle;
        }

        m_Query.CopyFromComponentDataArray(m_AgentSelection.selection);
        m_PlanetaryQuery.CopyFromComponentDataArray(m_PlanetaryAgentSelection.selection);

        m_Input.MousePos.Dispose();
        m_AgentSelection.position.Dispose();
        m_AgentSelection.selection.Dispose();
        m_AgentSelection.entities.Dispose();
        m_PlanetaryAgentSelection.position.Dispose();
        m_PlanetaryAgentSelection.selection.Dispose();
        m_Input.Buttons.Clear();
    }

    //-----------------------------------------------------------------------------
    [BurstCompile]
    struct SelectionJob : IJobParallelFor
    {
        public float2 Start;
        public float2 Stop;
        public float4x4 World2Clip;
        public SelectionGroup AgentSelection; // todo no need position in this job, just pass only selection here
        public PlanetarySelectionGroup PlanetaryAgentSelection;
        public int AgentSelectionCount;

        //-----------------------------------------------------------------------------
        public void Execute(int index)
        {

            var position = PlanetaryAgentSelection.position[index].Value;
            float4 agentVector = math.mul(World2Clip, new float4(position.x, position.y, position.z, 1));
            float2 screenPoint = (agentVector / -agentVector.w).xy;

            var result = math.all(Start <= screenPoint) && math.all(screenPoint <= Stop);

            var selectionValue = math.select(0, 1, result);

            AgentSelection.selection[index] = new Selection { Value = (byte)selectionValue };
            PlanetaryAgentSelection.selection[index] = new PlanetarySelection { Value = (byte)selectionValue };
        }
    }

    //-----------------------------------------------------------------------------
    [BurstCompile]
    struct SelectAllJob : IJobParallelFor
    {
        public SelectionGroup AgentSelection;
        public PlanetarySelectionGroup PlanetaryAgentSelection;

        //-----------------------------------------------------------------------------
        public void Execute(int index)
        {
            AgentSelection.selection[index] = new Selection { Value = 1 };
            PlanetaryAgentSelection.selection[index] = new PlanetarySelection { Value = 1 };
        }
    }

    //-----------------------------------------------------------------------------
    [BurstCompile]
    struct SelectGoalJob : IJobParallelFor
    {
        public float2 Stop;
        public float4x4 World2Clip;
        public SelectionGroup AgentSelection;
        public PlanetarySelectionGroup PlanetaryAgentSelection;
        [NativeDisableContainerSafetyRestriction]
        public NativeArray<Entity> SingleTargetSelectedEntityArray;

        [NativeDisableContainerSafetyRestriction]
        public NativeArray<bool> IsSingleTargetSelected;

        //-----------------------------------------------------------------------------
        public void Execute(int index)
        {
            if (IsSingleTargetSelected[0]) return;

            var position = PlanetaryAgentSelection.position[index].Value;

            float4 agentVector = math.mul(World2Clip, new float4(position.x, position.y, position.z, 1));
            float2 screenPoint = (agentVector / -agentVector.w).xy;

            bool result;

            float2 length = screenPoint - Stop;
            if (!IsSingleTargetSelected[0] && math.dot(length, length) < 1 / (-agentVector.w * 10))
            {
                result = true;
                IsSingleTargetSelected[0] = true;
                SingleTargetSelectedEntityArray[0] = AgentSelection.entities[index];
            }
            else
            {
                result = false;
            }

            var selectionValue = math.select(0, 1, result);

            AgentSelection.selection[index] = new Selection { Value = (byte)selectionValue };
            PlanetaryAgentSelection.selection[index] = new PlanetarySelection { Value = (byte)selectionValue };
        }
    }

    //-----------------------------------------------------------------------------
    static float2 Normalize(float3 p, int width, int height)
    {
        return new float2(p.x / (width / 2) - 1, p.y / (height / 2) - 1);
    }

    static unsafe byte BoolToByte(bool input)
    {
        return *((byte*)(&input));
    }
}