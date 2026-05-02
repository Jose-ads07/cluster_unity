using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

public class ClusterSimulationManager : MonoBehaviour
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
        public ClusterEntityStateCode stateCode;
        public byte flags;
        public EnemyView view;
        public int currentZoneId;
    }

    private sealed class WorkerRuntime
    {
        public WorkerEndpoint endpoint;
        public ClusterWorkerClient client;
        public bool connected;
        public float lastConnectAttemptTime;
        public float lastHeartbeatRequestTime;
        public float lastHeartbeatResponseTime;
        public float averageProcessingMs;
        public int timeoutCount;
        public int protocolErrorCount;
        public int zonesOwned;

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

    private sealed class ZoneRuntime
    {
        public int zoneId;
        public int gridX;
        public int gridY;
        public uint snapshotId;
        public int lastTick;
        public int lastMembershipHash;
        public bool snapshotDirty;
        public string ownerWorkerName;
        public ClusterObjectiveType objectiveType;
        public Vector2 targetPoint;
        public Vector2 auxiliary;
        public uint checksum;
        public int localFallbackCount;
    }

    private sealed class PendingZoneJob
    {
        public uint requestId;
        public int zoneId;
        public int tick;
        public uint snapshotId;
        public float deadlineTime;
        public WorkerRuntime worker;
        public List<int> entityIds;
    }

    [Header("Workers")]
    [SerializeField] private List<WorkerEndpoint> workerEndpoints = new List<WorkerEndpoint>();
    [SerializeField] private int connectTimeoutMs = 700;
    [SerializeField] private float reconnectInterval = 2.0f;
    [SerializeField] private float heartbeatInterval = 1.0f;
    [SerializeField] private float requestTimeoutSeconds = 0.4f;
    [SerializeField] private bool verboseConnectionLogging = true;
    [SerializeField] private bool preferDeltaResults = true;

    [Header("Simulation")]
    [SerializeField] private int enemyCount = 500;
    [SerializeField] private float simulationTickInterval = 0.05f;
    [SerializeField] private float maxSimulationBacklog = 0.2f;
    [SerializeField] private float minSpeed = 1.5f;
    [SerializeField] private float maxSpeed = 3.6f;
    [SerializeField] private Vector2 worldMin = new Vector2(-24f, -24f);
    [SerializeField] private Vector2 worldMax = new Vector2(24f, 24f);

    [Header("Grid")]
    [SerializeField] private int gridColumns = 2;
    [SerializeField] private int gridRows = 2;

    [Header("Gameplay")]
    [SerializeField] private ClusterPlayerController playerController;
    [SerializeField] private bool autoSpawnPlayer = true;
    [SerializeField] private Vector2 playerSpawnPoint = Vector2.zero;
    [SerializeField] private float playerMoveSpeed = 8f;
    [SerializeField] private float defeatDistance = 1.0f;
    [SerializeField] private float restartDelay = 0.5f;
    [SerializeField] private KeyCode restartKey = KeyCode.R;
    [SerializeField] [Range(0f, 1f)] private float avoiderRatio = 0.25f;
    [SerializeField] private bool autoCreateHud = true;

    [Header("Visuals")]
    [SerializeField] private EnemyView enemyPrefab;
    [SerializeField] private Transform visualParent;
    [SerializeField] private float renderSmoothing = 12f;
    [SerializeField] private bool showDebugOverlay = true;

    private readonly ConcurrentQueue<ClusterWorkerEvent> inbox = new ConcurrentQueue<ClusterWorkerEvent>();
    private readonly List<WorkerRuntime> workers = new List<WorkerRuntime>();
    private readonly Dictionary<int, EntityRuntime> entities = new Dictionary<int, EntityRuntime>();
    private readonly Dictionary<int, ZoneRuntime> zones = new Dictionary<int, ZoneRuntime>();
    private readonly Dictionary<uint, PendingZoneJob> pendingJobs = new Dictionary<uint, PendingZoneJob>();

    private float simulationAccumulator;
    private int currentTick;
    private int lastCompletedTick;
    private uint nextRequestId = 1;
    private int lastTopologyHash;
    private long lastSentBytes;
    private long lastReceivedBytes;
    private float overlayNetworkRateSent;
    private float overlayNetworkRateReceived;
    private bool gameOver;
    private float gameOverTimestamp;
    private float survivalSeconds;
    private int fullResultFrames;
    private int deltaResultFrames;

    public bool IsGameOver => gameOver;
    public float SurvivalSeconds => survivalSeconds;
    public int Score => Mathf.FloorToInt(survivalSeconds * 10f);
    public int EnemyCount => entities.Count;
    public int OnlineWorkerCount => GetActiveWorkers().Count;
    public int TotalWorkerCount => workers.Count;
    public float NetworkTxRate => overlayNetworkRateSent;
    public float NetworkRxRate => overlayNetworkRateReceived;
    public KeyCode RestartKey => restartKey;
    public bool RestartReady => gameOver && (Time.unscaledTime - gameOverTimestamp) >= restartDelay;
    public float RestartDelayRemaining => gameOver ? Mathf.Max(0f, restartDelay - (Time.unscaledTime - gameOverTimestamp)) : 0f;

    private void Start()
    {
        if (verboseConnectionLogging)
        {
            Debug.Log(string.Format("[ClusterNet] ClusterSimulationManager starting. configuredWorkers={0} grid={1}x{2} enemyCount={3}", workerEndpoints.Count, gridColumns, gridRows, enemyCount));
        }

        CreateZones();
        CreateOrConfigurePlayer();
        CreateEntities();
        EnsureHudExists();
        CreateWorkers();
        RefreshConnections(true);
        StartNewRound();
    }

    private void Update()
    {
        using (ClusterProfilerMarkers.Update.Auto())
        {
            HandleRestartInput();

            if (!gameOver)
            {
                survivalSeconds += Time.deltaTime;
            }

            simulationAccumulator += Time.deltaTime;
            simulationAccumulator = Mathf.Min(simulationAccumulator, Mathf.Max(simulationTickInterval, maxSimulationBacklog));

            DrainIncomingMessages();
            RefreshConnections(false);
            SendHeartbeatsWhenIdle();
            CheckTimedOutJobs();

            if (!gameOver && pendingJobs.Count == 0)
            {
                while (simulationAccumulator >= simulationTickInterval)
                {
                    simulationAccumulator -= simulationTickInterval;
                    RunSimulationTick();
                }
            }

            UpdateNetworkRateOverlay();
            UpdateVisuals(Time.deltaTime);

            if (!gameOver)
            {
                EvaluateDefeatCondition();
            }
        }
    }

    private void OnDestroy()
    {
        for (int index = 0; index < workers.Count; index++)
        {
            workers[index].client?.Dispose();
        }
    }

    private void CreateZones()
    {
        zones.Clear();
        int zoneId = 0;
        Vector2 cellSize = GetCellSize();

        for (int y = 0; y < gridRows; y++)
        {
            for (int x = 0; x < gridColumns; x++)
            {
                ZoneRuntime zone = new ZoneRuntime();
                zone.zoneId = zoneId;
                zone.gridX = x;
                zone.gridY = y;
                zone.snapshotId = 0u;
                zone.lastTick = 0;
                zone.lastMembershipHash = int.MinValue;
                zone.snapshotDirty = true;
                zone.ownerWorkerName = string.Empty;
                zone.objectiveType = (ClusterObjectiveType)(zoneId % 3);
                zone.targetPoint = GetZoneCenter(x, y);
                zone.auxiliary = new Vector2(cellSize.x * 0.35f, zoneId % 2 == 0 ? -1f : 1f);
                zone.checksum = 0u;
                zone.localFallbackCount = 0;
                zones.Add(zoneId, zone);
                zoneId++;
            }
        }
    }

    private void CreateOrConfigurePlayer()
    {
        if (playerController == null && autoSpawnPlayer)
        {
            GameObject playerObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            playerObject.name = "ClusterPlayer";
            playerObject.transform.position = new Vector3(playerSpawnPoint.x, 0.9f, playerSpawnPoint.y);
            Collider collider = playerObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }
            Renderer renderer = playerObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(0.15f, 0.85f, 0.35f);
            }

            playerController = playerObject.AddComponent<ClusterPlayerController>();
        }

        if (playerController != null)
        {
            playerController.Configure(playerMoveSpeed, worldMin, worldMax);
            playerController.ResetTo(playerSpawnPoint);
            playerController.SetInputEnabled(true);
        }
    }

    private void EnsureHudExists()
    {
        if (!autoCreateHud)
        {
            return;
        }

        ClusterGameHUD hud = FindObjectOfType<ClusterGameHUD>();
        if (hud == null)
        {
            GameObject hudObject = new GameObject("ClusterGameHUD");
            hud = hudObject.AddComponent<ClusterGameHUD>();
        }

        hud.Initialize(this);
    }

    private void CreateEntities()
    {
        entities.Clear();
        Transform parent = visualParent != null ? visualParent : transform;

        for (int id = 0; id < enemyCount; id++)
        {
            EntityRuntime entity = new EntityRuntime();
            entity.id = id;
            entity.speed = UnityEngine.Random.Range(minSpeed, maxSpeed);
            entity.flags = UnityEngine.Random.value < avoiderRatio ? (byte)1 : (byte)0;
            entity.stateCode = ClusterEntityStateCode.Chase;
            entity.previousPosition = GetRandomEnemySpawnPosition();
            entity.currentPosition = entity.previousPosition;
            entity.renderPosition = entity.currentPosition;
            entity.velocity = Vector2.zero;
            entity.currentZoneId = GetZoneIdForPosition(entity.currentPosition);

            entity.view = CreateEnemyView(entity.currentPosition, parent);
            if (entity.view != null)
            {
                entity.view.Initialize(id);
                entity.view.ConfigureAppearance(entity.flags);
            }

            entities.Add(id, entity);
        }
    }

    private EnemyView CreateEnemyView(Vector2 position, Transform parent)
    {
        if (enemyPrefab != null)
        {
            return Instantiate(enemyPrefab, new Vector3(position.x, 0f, position.y), Quaternion.identity, parent);
        }

        GameObject enemyObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        enemyObject.name = "enemy";
        enemyObject.transform.SetParent(parent, true);
        enemyObject.transform.position = new Vector3(position.x, 0f, position.y);
        EnemyView view = enemyObject.GetComponent<EnemyView>();
        if (view == null)
        {
            view = enemyObject.AddComponent<EnemyView>();
        }
        return view;
    }

    private void CreateWorkers()
    {
        workers.Clear();

        if (workerEndpoints.Count == 0)
        {
            Debug.LogWarning("[ClusterNet] No worker endpoints configured in ClusterSimulationManager.");
        }

        for (int index = 0; index < workerEndpoints.Count; index++)
        {
            WorkerRuntime worker = new WorkerRuntime();
            worker.endpoint = workerEndpoints[index];
            worker.client = new ClusterWorkerClient(worker.endpoint.host, worker.endpoint.port, "unity-master", (ushort)(gridColumns * gridRows), inbox, verboseConnectionLogging, preferDeltaResults);
            worker.connected = false;
            worker.lastConnectAttemptTime = -999f;
            worker.lastHeartbeatRequestTime = -999f;
            worker.lastHeartbeatResponseTime = -999f;
            worker.averageProcessingMs = 0f;
            worker.timeoutCount = 0;
            worker.protocolErrorCount = 0;
            worker.zonesOwned = 0;
            workers.Add(worker);

            if (verboseConnectionLogging)
            {
                Debug.Log(string.Format("[ClusterNet] Worker endpoint configured. index={0} label={1} host={2} port={3}", index, worker.DisplayName, worker.endpoint.host, worker.endpoint.port));
            }
        }
    }

    private void RefreshConnections(bool forceAttempt)
    {
        float now = Time.unscaledTime;

        for (int index = 0; index < workers.Count; index++)
        {
            WorkerRuntime worker = workers[index];
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
            if (verboseConnectionLogging)
            {
                Debug.Log(string.Format("[ClusterNet] Attempting worker connect. worker={0} forceAttempt={1} time={2:0.000}", worker.DisplayName, forceAttempt, now));
            }

            bool connected = worker.client != null && worker.client.Connect(connectTimeoutMs);
            worker.connected = connected;
            if (connected)
            {
                worker.lastHeartbeatResponseTime = now;
                MarkAllZonesDirty();
                Debug.Log(string.Format("Connected cluster worker {0}", worker.DisplayName));
            }
            else if (verboseConnectionLogging)
            {
                Debug.LogWarning(string.Format("[ClusterNet] Worker connect attempt failed. worker={0}", worker.DisplayName));
            }
        }
    }

    private void SendHeartbeatsWhenIdle()
    {
        if (pendingJobs.Count > 0)
        {
            return;
        }

        float now = Time.unscaledTime;

        for (int index = 0; index < workers.Count; index++)
        {
            WorkerRuntime worker = workers[index];
            if (!worker.connected || worker.client == null || !worker.client.IsConnected)
            {
                continue;
            }

            if (now - worker.lastHeartbeatRequestTime < heartbeatInterval)
            {
                continue;
            }

            worker.lastHeartbeatRequestTime = now;
            worker.client.SendHeartbeat(lastCompletedTick);
        }
    }

    private void DrainIncomingMessages()
    {
        using (ClusterProfilerMarkers.DrainNetwork.Auto())
        {
            while (inbox.TryDequeue(out ClusterWorkerEvent workerEvent))
            {
                WorkerRuntime worker = FindWorker(workerEvent.Source);
                if (worker == null)
                {
                    continue;
                }

                if (workerEvent.Message is ClusterResultMessage)
                {
                    ApplyResult(worker, (ClusterResultMessage)workerEvent.Message);
                    continue;
                }

                if (workerEvent.Message is ClusterResultDeltaMessage)
                {
                    ApplyDeltaResult(worker, (ClusterResultDeltaMessage)workerEvent.Message);
                    continue;
                }

                if (workerEvent.Message is ClusterHeartbeatMessage)
                {
                    ApplyHeartbeat(worker, (ClusterHeartbeatMessage)workerEvent.Message);
                    continue;
                }

                if (workerEvent.Message is ClusterErrorMessage)
                {
                    ApplyError(worker, (ClusterErrorMessage)workerEvent.Message);
                    continue;
                }
            }
        }
    }

    private void RunSimulationTick()
    {
        using (ClusterProfilerMarkers.Tick.Auto())
        {
            currentTick++;
            List<WorkerRuntime> activeWorkers = GetActiveWorkers();
            Dictionary<int, List<int>> zoneMemberships = BuildZoneMemberships();

            if (activeWorkers.Count == 0)
            {
                SimulateAllZonesLocally(zoneMemberships);
                lastCompletedTick = currentTick;
                return;
            }

            int topologyHash = ComputeTopologyHash(activeWorkers);
            if (topologyHash != lastTopologyHash)
            {
                lastTopologyHash = topologyHash;
                MarkAllZonesDirty();
            }

            AssignZoneOwners(activeWorkers);
            Vector2 playerPosition = GetPlayerPosition();
            int dispatched = 0;

            for (int zoneId = 0; zoneId < zones.Count; zoneId++)
            {
                ZoneRuntime zone = zones[zoneId];
                WorkerRuntime owner = FindActiveWorkerByName(activeWorkers, zone.ownerWorkerName);
                List<int> entityIds = zoneMemberships[zoneId];

                if (owner == null || owner.client == null || !owner.client.IsConnected)
                {
                    SimulateZoneLocally(zone, entityIds, currentTick, playerPosition);
                    continue;
                }

                int membershipHash = ComputeMembershipHash(entityIds);
                if (zone.snapshotDirty || zone.lastMembershipHash != membershipHash)
                {
                    zone.snapshotId++;
                    zone.lastMembershipHash = membershipHash;
                    zone.snapshotDirty = false;
                    ClusterZoneSnapshotFrame snapshot = BuildZoneSnapshot(zone, entityIds);
                    if (!owner.client.SendSnapshot(snapshot))
                    {
                        owner.connected = false;
                        zone.snapshotDirty = true;
                        SimulateZoneLocally(zone, entityIds, currentTick, playerPosition);
                        continue;
                    }
                    zone.checksum = snapshot.Checksum;
                }

                ClusterSimulateRequestFrame request = new ClusterSimulateRequestFrame();
                request.RequestId = nextRequestId++;
                request.ZoneId = zone.zoneId;
                request.SnapshotId = zone.snapshotId;
                request.Tick = currentTick;
                request.DeltaTime = simulationTickInterval;
                request.PlayerPosition = playerPosition;

                if (!owner.client.SendSimulate(request))
                {
                    owner.connected = false;
                    zone.snapshotDirty = true;
                    SimulateZoneLocally(zone, entityIds, currentTick, playerPosition);
                    continue;
                }

                PendingZoneJob job = new PendingZoneJob();
                job.requestId = request.RequestId;
                job.zoneId = zone.zoneId;
                job.tick = currentTick;
                job.snapshotId = zone.snapshotId;
                job.deadlineTime = Time.unscaledTime + requestTimeoutSeconds;
                job.worker = owner;
                job.entityIds = entityIds;
                pendingJobs.Add(job.requestId, job);
                dispatched++;
            }

            if (dispatched == 0)
            {
                lastCompletedTick = currentTick;
            }
        }
    }

    private void ApplyResult(WorkerRuntime worker, ClusterResultMessage result)
    {
        using (ClusterProfilerMarkers.ApplyResult.Auto())
        {
            fullResultFrames++;
            PendingZoneJob job;
            if (!pendingJobs.TryGetValue(result.RequestId, out job))
            {
                return;
            }

            pendingJobs.Remove(result.RequestId);
            worker.lastHeartbeatResponseTime = Time.unscaledTime;
            worker.averageProcessingMs = Mathf.Lerp(worker.averageProcessingMs, result.ProcessingUs / 1000f, worker.averageProcessingMs <= 0.001f ? 1f : 0.25f);

            ZoneRuntime zone = zones[job.zoneId];
            bool valid = result.ZoneId == job.zoneId
                && result.Tick == job.tick
                && result.SnapshotId == job.snapshotId;

            uint computedChecksum = ClusterBinaryProtocol.ComputeChecksum(result.ZoneId, result.Tick, result.Entities);
            valid &= computedChecksum == result.Checksum;

            if (!valid || result.Status != ClusterResultStatus.Ok)
            {
                zone.snapshotDirty = true;
                worker.protocolErrorCount++;
                SimulateZoneLocally(zone, job.entityIds, job.tick, GetPlayerPosition());
            }
            else
            {
                for (int index = 0; index < result.Entities.Count; index++)
                {
                    ClusterProtocolEntityState protocolEntity = result.Entities[index];
                    EntityRuntime entity;
                    if (!entities.TryGetValue(protocolEntity.Id, out entity))
                    {
                        continue;
                    }

                    byte previousFlags = entity.flags;
                    entity.previousPosition = entity.currentPosition;
                    entity.currentPosition = protocolEntity.Position;
                    entity.velocity = protocolEntity.Velocity;
                    entity.speed = protocolEntity.Speed;
                    entity.stateCode = protocolEntity.StateCode;
                    entity.flags = protocolEntity.Flags;
                    entity.currentZoneId = zone.zoneId;
                    if (entity.view != null && previousFlags != entity.flags)
                    {
                        entity.view.ConfigureAppearance(entity.flags);
                    }
                }

                zone.lastTick = result.Tick;
                zone.checksum = result.Checksum;
            }

            if (pendingJobs.Count == 0)
            {
                lastCompletedTick = currentTick;
            }
        }
    }

    private void ApplyDeltaResult(WorkerRuntime worker, ClusterResultDeltaMessage result)
    {
        ClusterResultMessage materialized = MaterializeDeltaResult(result);
        if (materialized == null)
        {
            worker.protocolErrorCount++;
            MarkAllZonesDirty();
            return;
        }

        deltaResultFrames++;
        ApplyResult(worker, materialized);
        fullResultFrames--;
    }

    private ClusterResultMessage MaterializeDeltaResult(ClusterResultDeltaMessage deltaResult)
    {
        ClusterResultMessage result = new ClusterResultMessage();
        result.MessageType = ClusterMessageType.Result;
        result.FrameSizeBytes = deltaResult.FrameSizeBytes;
        result.RequestId = deltaResult.RequestId;
        result.ZoneId = deltaResult.ZoneId;
        result.SnapshotId = deltaResult.SnapshotId;
        result.Tick = deltaResult.Tick;
        result.Status = deltaResult.Status;
        result.ProcessingUs = deltaResult.ProcessingUs;
        result.Checksum = deltaResult.Checksum;

        for (int index = 0; index < deltaResult.Entities.Count; index++)
        {
            ClusterDeltaEntityState deltaEntity = deltaResult.Entities[index];
            EntityRuntime baseEntity;
            if (!entities.TryGetValue(deltaEntity.Id, out baseEntity))
            {
                Debug.LogWarning(string.Format("[ClusterNet] Delta result referenced unknown entity id={0} zone={1} tick={2}", deltaEntity.Id, deltaResult.ZoneId, deltaResult.Tick));
                return null;
            }

            ClusterProtocolEntityState absoluteEntity = new ClusterProtocolEntityState();
            absoluteEntity.Id = deltaEntity.Id;
            int basePosX = Mathf.RoundToInt(baseEntity.currentPosition.x * ClusterBinaryProtocol.DeltaScale);
            int basePosY = Mathf.RoundToInt(baseEntity.currentPosition.y * ClusterBinaryProtocol.DeltaScale);
            int baseVelX = Mathf.RoundToInt(baseEntity.velocity.x * ClusterBinaryProtocol.DeltaScale);
            int baseVelY = Mathf.RoundToInt(baseEntity.velocity.y * ClusterBinaryProtocol.DeltaScale);
            absoluteEntity.Position = new Vector2(
                (basePosX + deltaEntity.PositionDeltaX) / ClusterBinaryProtocol.DeltaScale,
                (basePosY + deltaEntity.PositionDeltaY) / ClusterBinaryProtocol.DeltaScale
            );
            absoluteEntity.Velocity = new Vector2(
                (baseVelX + deltaEntity.VelocityDeltaX) / ClusterBinaryProtocol.DeltaScale,
                (baseVelY + deltaEntity.VelocityDeltaY) / ClusterBinaryProtocol.DeltaScale
            );
            absoluteEntity.Speed = baseEntity.speed;
            absoluteEntity.StateCode = deltaEntity.StateCode;
            absoluteEntity.Flags = deltaEntity.Flags;
            result.Entities.Add(absoluteEntity);
        }

        return result;
    }

    private void ApplyHeartbeat(WorkerRuntime worker, ClusterHeartbeatMessage heartbeat)
    {
        worker.lastHeartbeatResponseTime = Time.unscaledTime;
        worker.averageProcessingMs = Mathf.Lerp(worker.averageProcessingMs, heartbeat.AverageProcessingUs / 1000f, worker.averageProcessingMs <= 0.001f ? 1f : 0.15f);
    }

    private void ApplyError(WorkerRuntime worker, ClusterErrorMessage error)
    {
        worker.protocolErrorCount++;
        worker.connected = false;
        MarkAllZonesDirty();
        Debug.LogWarning(string.Format("Cluster worker {0} error {1}: {2}", worker.DisplayName, error.Code, error.Message));
    }

    private void CheckTimedOutJobs()
    {
        if (pendingJobs.Count == 0)
        {
            return;
        }

        float now = Time.unscaledTime;
        List<uint> timedOutRequestIds = null;

        foreach (KeyValuePair<uint, PendingZoneJob> pair in pendingJobs)
        {
            if (now <= pair.Value.deadlineTime)
            {
                continue;
            }

            if (timedOutRequestIds == null)
            {
                timedOutRequestIds = new List<uint>();
            }

            timedOutRequestIds.Add(pair.Key);
        }

        if (timedOutRequestIds == null)
        {
            return;
        }

        using (ClusterProfilerMarkers.LocalFallback.Auto())
        {
            Vector2 playerPosition = GetPlayerPosition();

            for (int index = 0; index < timedOutRequestIds.Count; index++)
            {
                PendingZoneJob job = pendingJobs[timedOutRequestIds[index]];
                pendingJobs.Remove(job.requestId);

                ZoneRuntime zone = zones[job.zoneId];
                zone.snapshotDirty = true;
                zone.localFallbackCount++;

                if (job.worker != null)
                {
                    job.worker.timeoutCount++;
                    job.worker.connected = false;
                    job.worker.client?.Dispose();
                }

                SimulateZoneLocally(zone, job.entityIds, job.tick, playerPosition);
            }
        }

        if (pendingJobs.Count == 0)
        {
            lastCompletedTick = currentTick;
        }
    }

    private Dictionary<int, List<int>> BuildZoneMemberships()
    {
        Dictionary<int, List<int>> memberships = new Dictionary<int, List<int>>();

        for (int zoneId = 0; zoneId < zones.Count; zoneId++)
        {
            memberships[zoneId] = new List<int>();
        }

        foreach (KeyValuePair<int, EntityRuntime> pair in entities)
        {
            EntityRuntime entity = pair.Value;
            int zoneId = GetZoneIdForPosition(entity.currentPosition);
            entity.currentZoneId = zoneId;
            memberships[zoneId].Add(entity.id);
        }

        return memberships;
    }

    private void SimulateAllZonesLocally(Dictionary<int, List<int>> zoneMemberships)
    {
        Vector2 playerPosition = GetPlayerPosition();

        for (int zoneId = 0; zoneId < zones.Count; zoneId++)
        {
            SimulateZoneLocally(zones[zoneId], zoneMemberships[zoneId], currentTick, playerPosition);
        }
    }

    private void SimulateZoneLocally(ZoneRuntime zone, List<int> entityIds, int targetTick, Vector2 playerPosition)
    {
        using (ClusterProfilerMarkers.LocalFallback.Auto())
        {
            for (int index = 0; index < entityIds.Count; index++)
            {
                EntityRuntime entity = entities[entityIds[index]];
                Vector2 desiredVelocity = ComputeDesiredVelocity(zone, entity, playerPosition, targetTick);
                Vector2 nextVelocity = Vector2.Lerp(entity.velocity, desiredVelocity, 0.45f);
                Vector2 nextPosition = entity.currentPosition + (nextVelocity * simulationTickInterval);

                entity.previousPosition = entity.currentPosition;
                entity.currentPosition = nextPosition;
                entity.velocity = nextVelocity;
                entity.stateCode = GetStateForObjective(zone.objectiveType);
                entity.currentZoneId = zone.zoneId;
            }

            zone.lastTick = targetTick;
            zone.checksum = ComputeZoneChecksum(zone, entityIds, targetTick);
        }
    }

    private ClusterZoneSnapshotFrame BuildZoneSnapshot(ZoneRuntime zone, List<int> entityIds)
    {
        ClusterZoneSnapshotFrame snapshot = new ClusterZoneSnapshotFrame();
        snapshot.ZoneId = zone.zoneId;
        snapshot.SnapshotId = zone.snapshotId;
        snapshot.Tick = lastCompletedTick;
        snapshot.GridX = (ushort)zone.gridX;
        snapshot.GridY = (ushort)zone.gridY;
        snapshot.ObjectiveType = zone.objectiveType;
        snapshot.TargetPoint = zone.targetPoint;
        snapshot.Auxiliary = zone.auxiliary;

        for (int index = 0; index < entityIds.Count; index++)
        {
            EntityRuntime entity = entities[entityIds[index]];
            ClusterProtocolEntityState protocolEntity = new ClusterProtocolEntityState();
            protocolEntity.Id = entity.id;
            protocolEntity.Position = entity.currentPosition;
            protocolEntity.Velocity = entity.velocity;
            protocolEntity.Speed = entity.speed;
            protocolEntity.StateCode = entity.stateCode;
            protocolEntity.Flags = entity.flags;
            snapshot.Entities.Add(protocolEntity);
        }

        snapshot.Checksum = ClusterBinaryProtocol.ComputeChecksum(snapshot.ZoneId, snapshot.Tick, snapshot.Entities);
        return snapshot;
    }

    private Vector2 ComputeDesiredVelocity(ZoneRuntime zone, EntityRuntime entity, Vector2 playerPosition, int tick)
    {
        Vector2 toPlayer = playerPosition - entity.currentPosition;
        float distance = toPlayer.magnitude;
        Vector2 normalized = distance > 0.0001f ? toPlayer / distance : Vector2.zero;
        float directionScale = (entity.flags & 0x1) != 0 ? -1f : 1f;

        if (zone.objectiveType == ClusterObjectiveType.Seek)
        {
            return normalized * entity.speed * directionScale;
        }

        if (zone.objectiveType == ClusterObjectiveType.Orbit)
        {
            Vector2 tangent = new Vector2(-normalized.y, normalized.x);
            float desiredRadius = Mathf.Max(1f, zone.auxiliary.x);
            float radialError = distance - desiredRadius;
            float radialStrength = Mathf.Clamp(radialError * 0.35f, -1f, 1f);
            float orbitBias = Mathf.Clamp(zone.auxiliary.y, -1f, 1f);
            return ((tangent * orbitBias) + (normalized * radialStrength * directionScale)) * entity.speed;
        }

        float angle = (tick * 0.17f) + (entity.id * 0.37f) + (zone.auxiliary.y * 0.1f);
        Vector2 wander = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        return ((normalized * 0.45f * directionScale) + (wander * 0.55f)) * entity.speed;
    }

    private ClusterEntityStateCode GetStateForObjective(ClusterObjectiveType objectiveType)
    {
        if (objectiveType == ClusterObjectiveType.Seek)
        {
            return ClusterEntityStateCode.Chase;
        }

        if (objectiveType == ClusterObjectiveType.Orbit)
        {
            return ClusterEntityStateCode.Orbit;
        }

        return ClusterEntityStateCode.Wander;
    }

    private void AssignZoneOwners(List<WorkerRuntime> activeWorkers)
    {
        for (int index = 0; index < workers.Count; index++)
        {
            workers[index].zonesOwned = 0;
        }

        if (activeWorkers.Count == 0)
        {
            for (int zoneId = 0; zoneId < zones.Count; zoneId++)
            {
                zones[zoneId].ownerWorkerName = string.Empty;
            }
            return;
        }

        for (int zoneId = 0; zoneId < zones.Count; zoneId++)
        {
            ZoneRuntime zone = zones[zoneId];
            WorkerRuntime owner = activeWorkers[zoneId % activeWorkers.Count];
            if (zone.ownerWorkerName != owner.DisplayName)
            {
                zone.ownerWorkerName = owner.DisplayName;
                zone.snapshotDirty = true;
            }
            owner.zonesOwned++;
        }
    }

    private List<WorkerRuntime> GetActiveWorkers()
    {
        List<WorkerRuntime> activeWorkers = new List<WorkerRuntime>();

        for (int index = 0; index < workers.Count; index++)
        {
            WorkerRuntime worker = workers[index];
            if (worker.connected && worker.client != null && worker.client.IsConnected)
            {
                activeWorkers.Add(worker);
            }
        }

        return activeWorkers;
    }

    private WorkerRuntime FindActiveWorkerByName(List<WorkerRuntime> activeWorkers, string workerName)
    {
        for (int index = 0; index < activeWorkers.Count; index++)
        {
            if (activeWorkers[index].DisplayName == workerName)
            {
                return activeWorkers[index];
            }
        }

        return null;
    }

    private WorkerRuntime FindWorker(ClusterWorkerClient client)
    {
        for (int index = 0; index < workers.Count; index++)
        {
            if (workers[index].client == client)
            {
                return workers[index];
            }
        }

        return null;
    }

    private int GetZoneIdForPosition(Vector2 position)
    {
        Vector2 size = worldMax - worldMin;
        float normalizedX = size.x <= Mathf.Epsilon ? 0f : Mathf.InverseLerp(worldMin.x, worldMax.x, position.x);
        float normalizedY = size.y <= Mathf.Epsilon ? 0f : Mathf.InverseLerp(worldMin.y, worldMax.y, position.y);
        int gridX = Mathf.Clamp(Mathf.FloorToInt(normalizedX * gridColumns), 0, gridColumns - 1);
        int gridY = Mathf.Clamp(Mathf.FloorToInt(normalizedY * gridRows), 0, gridRows - 1);
        return (gridY * gridColumns) + gridX;
    }

    private Vector2 GetCellSize()
    {
        return new Vector2(
            (worldMax.x - worldMin.x) / Mathf.Max(1, gridColumns),
            (worldMax.y - worldMin.y) / Mathf.Max(1, gridRows)
        );
    }

    private Vector2 GetZoneCenter(int gridX, int gridY)
    {
        Vector2 cellSize = GetCellSize();
        return new Vector2(
            worldMin.x + ((gridX + 0.5f) * cellSize.x),
            worldMin.y + ((gridY + 0.5f) * cellSize.y)
        );
    }

    private int ComputeMembershipHash(List<int> entityIds)
    {
        unchecked
        {
            int hash = 17;
            for (int index = 0; index < entityIds.Count; index++)
            {
                hash = (hash * 31) + entityIds[index];
            }
            return hash;
        }
    }

    private int ComputeTopologyHash(List<WorkerRuntime> activeWorkers)
    {
        unchecked
        {
            int hash = 23;
            for (int index = 0; index < activeWorkers.Count; index++)
            {
                hash = (hash * 31) + activeWorkers[index].DisplayName.GetHashCode();
            }
            return hash;
        }
    }

    private uint ComputeZoneChecksum(ZoneRuntime zone, List<int> entityIds, int tick)
    {
        List<ClusterProtocolEntityState> protocolEntities = new List<ClusterProtocolEntityState>(entityIds.Count);

        for (int index = 0; index < entityIds.Count; index++)
        {
            EntityRuntime entity = entities[entityIds[index]];
            ClusterProtocolEntityState protocolEntity = new ClusterProtocolEntityState();
            protocolEntity.Id = entity.id;
            protocolEntity.Position = entity.currentPosition;
            protocolEntity.Velocity = entity.velocity;
            protocolEntity.Speed = entity.speed;
            protocolEntity.StateCode = entity.stateCode;
            protocolEntity.Flags = entity.flags;
            protocolEntities.Add(protocolEntity);
        }

        return ClusterBinaryProtocol.ComputeChecksum(zone.zoneId, tick, protocolEntities);
    }

    private void MarkAllZonesDirty()
    {
        foreach (KeyValuePair<int, ZoneRuntime> pair in zones)
        {
            pair.Value.snapshotDirty = true;
        }
    }

    private void UpdateVisuals(float deltaTime)
    {
        using (ClusterProfilerMarkers.VisualUpdate.Auto())
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
    }

    private void UpdateNetworkRateOverlay()
    {
        long totalSent = 0;
        long totalReceived = 0;

        for (int index = 0; index < workers.Count; index++)
        {
            if (workers[index].client == null)
            {
                continue;
            }

            totalSent += workers[index].client.BytesSent;
            totalReceived += workers[index].client.BytesReceived;
        }

        overlayNetworkRateSent = Mathf.Lerp(overlayNetworkRateSent, (totalSent - lastSentBytes) / Mathf.Max(Time.deltaTime, 0.0001f), 0.25f);
        overlayNetworkRateReceived = Mathf.Lerp(overlayNetworkRateReceived, (totalReceived - lastReceivedBytes) / Mathf.Max(Time.deltaTime, 0.0001f), 0.25f);
        lastSentBytes = totalSent;
        lastReceivedBytes = totalReceived;
    }

    private void EvaluateDefeatCondition()
    {
        Vector2 playerPosition = GetPlayerPosition();
        float defeatDistanceSquared = defeatDistance * defeatDistance;

        foreach (KeyValuePair<int, EntityRuntime> pair in entities)
        {
            if ((pair.Value.currentPosition - playerPosition).sqrMagnitude <= defeatDistanceSquared)
            {
                gameOver = true;
                gameOverTimestamp = Time.unscaledTime;
                if (playerController != null)
                {
                    playerController.SetInputEnabled(false);
                }
                break;
            }
        }
    }

    private void HandleRestartInput()
    {
        if (!gameOver)
        {
            return;
        }

        if (Time.unscaledTime - gameOverTimestamp < restartDelay)
        {
            return;
        }

        if (Input.GetKeyDown(restartKey))
        {
            StartNewRound();
        }
    }

    private void StartNewRound()
    {
        gameOver = false;
        survivalSeconds = 0f;
        gameOverTimestamp = 0f;
        simulationAccumulator = 0f;
        pendingJobs.Clear();
        fullResultFrames = 0;
        deltaResultFrames = 0;

        if (playerController != null)
        {
            playerController.Configure(playerMoveSpeed, worldMin, worldMax);
            playerController.ResetTo(playerSpawnPoint);
            playerController.SetInputEnabled(true);
        }

        foreach (KeyValuePair<int, EntityRuntime> pair in entities)
        {
            EntityRuntime entity = pair.Value;
            entity.flags = UnityEngine.Random.value < avoiderRatio ? (byte)1 : (byte)0;
            entity.speed = UnityEngine.Random.Range(minSpeed, maxSpeed);
            entity.previousPosition = GetRandomEnemySpawnPosition();
            entity.currentPosition = entity.previousPosition;
            entity.renderPosition = entity.currentPosition;
            entity.velocity = Vector2.zero;
            entity.stateCode = ClusterEntityStateCode.Chase;
            entity.currentZoneId = GetZoneIdForPosition(entity.currentPosition);

            if (entity.view != null)
            {
                entity.view.ConfigureAppearance(entity.flags);
                entity.view.ApplyPosition(entity.currentPosition);
            }
        }

        foreach (KeyValuePair<int, ZoneRuntime> pair in zones)
        {
            pair.Value.snapshotDirty = true;
            pair.Value.lastMembershipHash = int.MinValue;
            pair.Value.localFallbackCount = 0;
        }
    }

    private Vector2 GetPlayerPosition()
    {
        if (playerController != null)
        {
            return playerController.Position2D;
        }

        return playerSpawnPoint;
    }

    private Vector2 GetRandomEnemySpawnPosition()
    {
        Vector2 position = playerSpawnPoint;
        int guard = 0;

        while (guard < 32)
        {
            position = new Vector2(
                UnityEngine.Random.Range(worldMin.x, worldMax.x),
                UnityEngine.Random.Range(worldMin.y, worldMax.y)
            );

            if ((position - playerSpawnPoint).sqrMagnitude >= 9f)
            {
                break;
            }

            guard++;
        }

        return position;
    }

    private void OnGUI()
    {
        if (!showDebugOverlay)
        {
            return;
        }

        Rect area = new Rect(12f, 120f, 580f, 300f);
        GUILayout.BeginArea(area, GUI.skin.box);
        GUILayout.Label("Cluster Simulation Manager");
        GUILayout.Label(string.Format("State: {0}", gameOver ? "Game Over" : "Running"));
        GUILayout.Label(string.Format("Survival: {0:0.0}s", survivalSeconds));
        GUILayout.Label(string.Format("Entities: {0}", entities.Count));
        GUILayout.Label(string.Format("Grid: {0} x {1}", gridColumns, gridRows));
        GUILayout.Label(string.Format("Current Tick: {0}", currentTick));
        GUILayout.Label(string.Format("Last Completed Tick: {0}", lastCompletedTick));
        GUILayout.Label(string.Format("Pending Jobs: {0}", pendingJobs.Count));
        GUILayout.Label(string.Format("TX Rate: {0:0.0} B/s", overlayNetworkRateSent));
        GUILayout.Label(string.Format("RX Rate: {0:0.0} B/s", overlayNetworkRateReceived));
        GUILayout.Label(string.Format("Result Frames: full={0} delta={1}", fullResultFrames, deltaResultFrames));

        for (int index = 0; index < workers.Count; index++)
        {
            WorkerRuntime worker = workers[index];
            string status = worker.connected && worker.client != null && worker.client.IsConnected ? "online" : "offline";
            GUILayout.Label(string.Format(
                "{0} [{1}] zones={2} avg_proc={3:0.00}ms timeouts={4} errors={5} sent={6}B recv={7}B",
                worker.DisplayName,
                status,
                worker.zonesOwned,
                worker.averageProcessingMs,
                worker.timeoutCount,
                worker.protocolErrorCount,
                worker.client != null ? worker.client.BytesSent : 0,
                worker.client != null ? worker.client.BytesReceived : 0
            ));
        }

        GUILayout.EndArea();
    }
}
