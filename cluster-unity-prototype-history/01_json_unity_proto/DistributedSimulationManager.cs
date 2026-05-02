using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

public class DistributedSimulationManager : MonoBehaviour
{
    [Serializable]
    public struct WorkerEndpoint
    {
        public string host;
        public int port;
        public string label;
    }

    private sealed class EntityRuntime
    {
        public int id;
        public Vector2 previousPosition;
        public Vector2 currentPosition;
        public Vector2 renderPosition;
        public Vector2 velocity;
        public float speed;
        public string state;
        public EnemyView view;
    }

    private sealed class WorkerRuntime
    {
        public WorkerEndpoint endpoint;
        public DistributedWorkerClient client;
        public bool connected;
        public float lastConnectAttemptTime;
        public float lastResponseTime;
        public float averageLatencyMs;
        public int completedJobs;
        public int timedOutJobs;
        public int zoneIndex;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(endpoint.label))
                {
                    return endpoint.label;
                }

                return string.Format("{0}:{1}", endpoint.host, endpoint.port);
            }
        }
    }

    private sealed class PendingJob
    {
        public int requestId;
        public int tick;
        public int zoneIndex;
        public float deadlineTime;
        public WorkerRuntime worker;
        public List<int> entityIds;
    }

    [Header("Workers")]
    [SerializeField] private List<WorkerEndpoint> workerEndpoints = new List<WorkerEndpoint>();
    [SerializeField] private float reconnectInterval = 2.0f;
    [SerializeField] private int connectTimeoutMs = 700;
    [SerializeField] private float requestTimeoutSeconds = 0.35f;

    [Header("Simulation")]
    [SerializeField] private int enemyCount = 500;
    [SerializeField] private float simulationTickInterval = 0.05f;
    [SerializeField] private float maxSimulationBacklog = 0.2f;
    [SerializeField] private float spawnRadius = 16f;
    [SerializeField] private float minSpeed = 1.5f;
    [SerializeField] private float maxSpeed = 3.5f;
    [SerializeField] private float velocityDamping = 10f;
    [SerializeField] private Vector2 targetPoint = Vector2.zero;

    [Header("Spatial Partition")]
    [SerializeField] private float worldMinX = -20f;
    [SerializeField] private float worldMaxX = 20f;

    [Header("Visuals")]
    [SerializeField] private EnemyView enemyPrefab;
    [SerializeField] private Transform visualParent;
    [SerializeField] private float renderSmoothing = 12f;
    [SerializeField] private bool showDebugOverlay = true;

    private readonly ConcurrentQueue<SimulationResponse> inbox = new ConcurrentQueue<SimulationResponse>();
    private readonly List<WorkerRuntime> workers = new List<WorkerRuntime>();
    private readonly Dictionary<int, EntityRuntime> entities = new Dictionary<int, EntityRuntime>();
    private readonly Dictionary<int, PendingJob> pendingJobs = new Dictionary<int, PendingJob>();

    private float simulationAccumulator;
    private int currentTick;
    private int nextRequestId = 1;
    private int lastCompletedTick;

    private void Start()
    {
        CreateEntities();
        CreateWorkerRuntimes();
        RefreshConnections(true);
    }

    private void Update()
    {
        simulationAccumulator += Time.deltaTime;
        simulationAccumulator = Mathf.Min(simulationAccumulator, Mathf.Max(simulationTickInterval, maxSimulationBacklog));

        DrainResponses();
        RefreshConnections(false);
        CheckForTimedOutJobs();

        if (pendingJobs.Count == 0)
        {
            while (simulationAccumulator >= simulationTickInterval)
            {
                simulationAccumulator -= simulationTickInterval;
                RunSimulationTick();
            }
        }

        UpdateVisuals(Time.deltaTime);
    }

    private void OnDestroy()
    {
        for (int i = 0; i < workers.Count; i++)
        {
            workers[i].client?.Dispose();
        }

        workers.Clear();
        pendingJobs.Clear();
    }

    private void CreateEntities()
    {
        Transform parent = visualParent != null ? visualParent : transform;

        for (int i = 0; i < enemyCount; i++)
        {
            Vector2 startPosition = UnityEngine.Random.insideUnitCircle * spawnRadius;
            float speed = UnityEngine.Random.Range(minSpeed, maxSpeed);

            EnemyView view = null;
            if (enemyPrefab != null)
            {
                view = Instantiate(enemyPrefab, new Vector3(startPosition.x, 0f, startPosition.y), Quaternion.identity, parent);
                view.Initialize(i);
            }

            EntityRuntime entity = new EntityRuntime();
            entity.id = i;
            entity.previousPosition = startPosition;
            entity.currentPosition = startPosition;
            entity.renderPosition = startPosition;
            entity.velocity = Vector2.zero;
            entity.speed = speed;
            entity.state = "chase";
            entity.view = view;

            entities[i] = entity;
        }
    }

    private void CreateWorkerRuntimes()
    {
        workers.Clear();

        for (int i = 0; i < workerEndpoints.Count; i++)
        {
            WorkerRuntime worker = new WorkerRuntime();
            worker.endpoint = workerEndpoints[i];
            worker.client = new DistributedWorkerClient(worker.endpoint.host, worker.endpoint.port, inbox);
            worker.connected = false;
            worker.lastConnectAttemptTime = -999f;
            worker.lastResponseTime = -999f;
            worker.averageLatencyMs = 0f;
            worker.completedJobs = 0;
            worker.timedOutJobs = 0;
            worker.zoneIndex = i;
            workers.Add(worker);
        }
    }

    private void RefreshConnections(bool forceAttempt)
    {
        float now = Time.unscaledTime;

        for (int i = 0; i < workers.Count; i++)
        {
            WorkerRuntime worker = workers[i];
            if (worker.connected && worker.client != null && worker.client.IsConnected)
            {
                continue;
            }

            worker.connected = false;

            if (!forceAttempt && now - worker.lastConnectAttemptTime < reconnectInterval)
            {
                continue;
            }

            worker.lastConnectAttemptTime = now;
            bool connected = worker.client != null && worker.client.Connect(connectTimeoutMs);
            worker.connected = connected;

            if (connected)
            {
                worker.lastResponseTime = now;
                Debug.Log(string.Format("Connected worker {0}", worker.DisplayName));
            }
        }
    }

    private void RunSimulationTick()
    {
        currentTick++;

        List<WorkerRuntime> activeWorkers = GetActiveWorkers();
        if (activeWorkers.Count == 0)
        {
            SimulateAllLocally();
            lastCompletedTick = currentTick;
            return;
        }

        Dictionary<int, List<int>> assignments = BuildZoneAssignments(activeWorkers.Count);
        int dispatchedJobs = 0;

        for (int zoneIndex = 0; zoneIndex < activeWorkers.Count; zoneIndex++)
        {
            List<int> assignedEntityIds = assignments[zoneIndex];
            if (assignedEntityIds.Count == 0)
            {
                continue;
            }

            WorkerRuntime worker = activeWorkers[zoneIndex];
            worker.zoneIndex = zoneIndex;
            SimulationRequest request = BuildRequest(zoneIndex, assignedEntityIds);
            bool sent = worker.client != null && worker.client.Send(request);

            if (!sent)
            {
                worker.connected = false;
                SimulateEntitiesLocally(assignedEntityIds);
                continue;
            }

            PendingJob job = new PendingJob();
            job.requestId = request.requestId;
            job.tick = currentTick;
            job.zoneIndex = zoneIndex;
            job.deadlineTime = Time.unscaledTime + requestTimeoutSeconds;
            job.worker = worker;
            job.entityIds = assignedEntityIds;
            pendingJobs[job.requestId] = job;
            dispatchedJobs++;
        }

        if (dispatchedJobs == 0)
        {
            lastCompletedTick = currentTick;
        }
    }

    private Dictionary<int, List<int>> BuildZoneAssignments(int activeWorkerCount)
    {
        Dictionary<int, List<int>> assignments = new Dictionary<int, List<int>>();

        for (int i = 0; i < activeWorkerCount; i++)
        {
            assignments[i] = new List<int>();
        }

        foreach (KeyValuePair<int, EntityRuntime> pair in entities)
        {
            EntityRuntime entity = pair.Value;
            float normalized = Mathf.InverseLerp(worldMinX, worldMaxX, entity.currentPosition.x);
            int zoneIndex = Mathf.Clamp(Mathf.FloorToInt(normalized * activeWorkerCount), 0, activeWorkerCount - 1);
            assignments[zoneIndex].Add(entity.id);
        }

        return assignments;
    }

    private SimulationRequest BuildRequest(int zoneIndex, List<int> entityIds)
    {
        List<SimulationEntityState> payload = new List<SimulationEntityState>(entityIds.Count);

        for (int i = 0; i < entityIds.Count; i++)
        {
            EntityRuntime entity = entities[entityIds[i]];
            SimulationEntityState state = new SimulationEntityState();
            state.id = entity.id;
            state.x = entity.currentPosition.x;
            state.y = entity.currentPosition.y;
            state.vx = entity.velocity.x;
            state.vy = entity.velocity.y;
            state.speed = entity.speed;
            state.state = entity.state;
            payload.Add(state);
        }

        SimulationRequest request = new SimulationRequest();
        request.type = "simulate";
        request.requestId = nextRequestId++;
        request.tick = currentTick;
        request.zoneIndex = zoneIndex;
        request.deltaTime = simulationTickInterval;
        request.targetX = targetPoint.x;
        request.targetY = targetPoint.y;
        request.damping = velocityDamping;
        request.entities = payload;
        return request;
    }

    private void DrainResponses()
    {
        while (inbox.TryDequeue(out SimulationResponse response))
        {
            ApplyResponse(response);
        }
    }

    private void ApplyResponse(SimulationResponse response)
    {
        if (response == null)
        {
            return;
        }

        if (response.type == "error")
        {
            Debug.LogWarning(string.Format("Worker error: {0}", response.message));
            return;
        }

        if (response.type != "result")
        {
            return;
        }

        PendingJob job;
        if (!pendingJobs.TryGetValue(response.requestId, out job))
        {
            return;
        }

        pendingJobs.Remove(response.requestId);

        WorkerRuntime worker = job.worker;
        worker.connected = true;
        worker.lastResponseTime = Time.unscaledTime;
        worker.completedJobs++;
        worker.averageLatencyMs = worker.completedJobs == 1
            ? response.processingMs
            : Mathf.Lerp(worker.averageLatencyMs, response.processingMs, 0.25f);

        if (response.entities != null)
        {
            for (int i = 0; i < response.entities.Count; i++)
            {
                SimulationResultEntity result = response.entities[i];
                EntityRuntime entity;
                if (!entities.TryGetValue(result.id, out entity))
                {
                    continue;
                }

                Vector2 nextPosition = new Vector2(result.x, result.y);
                Vector2 nextVelocity = new Vector2(result.vx, result.vy);
                ApplyEntityState(entity, nextPosition, nextVelocity, result.state);
            }
        }

        if (pendingJobs.Count == 0)
        {
            lastCompletedTick = response.tick;
        }
    }

    private void CheckForTimedOutJobs()
    {
        if (pendingJobs.Count == 0)
        {
            return;
        }

        float now = Time.unscaledTime;
        List<int> timedOutRequestIds = null;

        foreach (KeyValuePair<int, PendingJob> pair in pendingJobs)
        {
            if (now <= pair.Value.deadlineTime)
            {
                continue;
            }

            if (timedOutRequestIds == null)
            {
                timedOutRequestIds = new List<int>();
            }

            timedOutRequestIds.Add(pair.Key);
        }

        if (timedOutRequestIds == null)
        {
            return;
        }

        for (int i = 0; i < timedOutRequestIds.Count; i++)
        {
            PendingJob job = pendingJobs[timedOutRequestIds[i]];
            pendingJobs.Remove(job.requestId);

            if (job.worker != null)
            {
                job.worker.timedOutJobs++;
                job.worker.connected = false;
                job.worker.client?.Dispose();
            }

            Debug.LogWarning(string.Format("Worker timeout for zone {0}. Falling back to local simulation.", job.zoneIndex));
            SimulateEntitiesLocally(job.entityIds);
        }

        if (pendingJobs.Count == 0)
        {
            lastCompletedTick = currentTick;
        }
    }

    private void SimulateAllLocally()
    {
        List<int> allEntityIds = new List<int>(entities.Keys);
        SimulateEntitiesLocally(allEntityIds);
    }

    private void SimulateEntitiesLocally(List<int> entityIds)
    {
        for (int i = 0; i < entityIds.Count; i++)
        {
            EntityRuntime entity = entities[entityIds[i]];
            Vector2 nextVelocity = ComputeNextVelocity(entity.currentPosition, entity.velocity, entity.speed);
            Vector2 nextPosition = entity.currentPosition + (nextVelocity * simulationTickInterval);
            ApplyEntityState(entity, nextPosition, nextVelocity, entity.state);
        }
    }

    private Vector2 ComputeNextVelocity(Vector2 currentPosition, Vector2 currentVelocity, float speed)
    {
        Vector2 delta = targetPoint - currentPosition;
        Vector2 desiredVelocity = delta.sqrMagnitude > 0.0001f ? delta.normalized * speed : Vector2.zero;
        float smoothing = Mathf.Clamp01(velocityDamping * simulationTickInterval);
        return Vector2.Lerp(currentVelocity, desiredVelocity, smoothing);
    }

    private void ApplyEntityState(EntityRuntime entity, Vector2 nextPosition, Vector2 nextVelocity, string nextState)
    {
        entity.previousPosition = entity.currentPosition;
        entity.currentPosition = nextPosition;
        entity.velocity = nextVelocity;

        if (!string.IsNullOrEmpty(nextState))
        {
            entity.state = nextState;
        }
    }

    private void UpdateVisuals(float deltaTime)
    {
        float smoothing = 1f - Mathf.Exp(-renderSmoothing * deltaTime);
        float tickBlend = simulationTickInterval > Mathf.Epsilon
            ? Mathf.Clamp01(simulationAccumulator / simulationTickInterval)
            : 1f;

        if (pendingJobs.Count > 0)
        {
            tickBlend = 1f;
        }

        foreach (KeyValuePair<int, EntityRuntime> pair in entities)
        {
            EntityRuntime entity = pair.Value;
            Vector2 targetRenderPosition = Vector2.Lerp(entity.previousPosition, entity.currentPosition, tickBlend);
            entity.renderPosition = Vector2.Lerp(entity.renderPosition, targetRenderPosition, smoothing);

            if (entity.view != null)
            {
                entity.view.ApplyPosition(entity.renderPosition);
                entity.view.ApplyVelocity(entity.velocity);
            }
        }
    }

    private List<WorkerRuntime> GetActiveWorkers()
    {
        List<WorkerRuntime> activeWorkers = new List<WorkerRuntime>();

        for (int i = 0; i < workers.Count; i++)
        {
            WorkerRuntime worker = workers[i];
            if (worker.connected && worker.client != null && worker.client.IsConnected)
            {
                activeWorkers.Add(worker);
            }
        }

        return activeWorkers;
    }

    private void OnGUI()
    {
        if (!showDebugOverlay)
        {
            return;
        }

        Rect area = new Rect(12f, 12f, 460f, 260f);
        GUILayout.BeginArea(area, GUI.skin.box);
        GUILayout.Label("Distributed Simulation");
        GUILayout.Label(string.Format("Entities: {0}", entities.Count));
        GUILayout.Label(string.Format("Current Tick: {0}", currentTick));
        GUILayout.Label(string.Format("Last Completed Tick: {0}", lastCompletedTick));
        GUILayout.Label(string.Format("Pending Jobs: {0}", pendingJobs.Count));
        GUILayout.Label(string.Format("Simulation Step: {0:0.000}s", simulationTickInterval));

        for (int i = 0; i < workers.Count; i++)
        {
            WorkerRuntime worker = workers[i];
            string status = worker.connected && worker.client != null && worker.client.IsConnected ? "online" : "offline";
            GUILayout.Label(string.Format(
                "{0} [{1}] latency {2:0.0} ms jobs {3} timeouts {4}",
                worker.DisplayName,
                status,
                worker.averageLatencyMs,
                worker.completedJobs,
                worker.timedOutJobs));
        }

        GUILayout.EndArea();
    }
}
