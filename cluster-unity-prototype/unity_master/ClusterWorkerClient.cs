using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public sealed class ClusterWorkerEvent
{
    public ClusterWorkerClient Source;
    public ClusterIncomingMessage Message;
}

public sealed class ClusterWorkerClient : IDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly string localLabel;
    private readonly ushort maxZones;
    private readonly ConcurrentQueue<ClusterWorkerEvent> inbox;
    private readonly object sendLock = new object();
    private readonly bool verboseLogging;
    private readonly bool preferDeltaResults;

    private TcpClient tcpClient;
    private NetworkStream stream;
    private Thread receiveThread;
    private volatile bool running;

    public ClusterWelcomeMessage Welcome { get; private set; }
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    public string Name
    {
        get
        {
            return string.Format("{0}:{1}", host, port);
        }
    }

    public bool IsConnected
    {
        get
        {
            return running && tcpClient != null && tcpClient.Connected && stream != null;
        }
    }

    public ClusterWorkerClient(string host, int port, string localLabel, ushort maxZones, ConcurrentQueue<ClusterWorkerEvent> inbox, bool verboseLogging, bool preferDeltaResults)
    {
        this.host = host != null ? host.Trim() : string.Empty;
        this.port = port;
        this.localLabel = localLabel;
        this.maxZones = maxZones;
        this.inbox = inbox;
        this.verboseLogging = verboseLogging;
        this.preferDeltaResults = preferDeltaResults;
    }

    public bool Connect(int timeoutMs)
    {
        Dispose();

        try
        {
            LogInfo(string.Format("Connect requested. target={0} timeoutMs={1}", Name, timeoutMs));

            IPAddress parsedAddress;
            bool isLiteralIp = IPAddress.TryParse(host, out parsedAddress);
            TcpClient client = isLiteralIp
                ? new TcpClient(parsedAddress.AddressFamily)
                : new TcpClient();

            IAsyncResult connectResult;
            if (isLiteralIp)
            {
                LogInfo(string.Format("Connecting using literal IP. target={0} addressFamily={1}", Name, parsedAddress.AddressFamily));
                connectResult = client.BeginConnect(parsedAddress, port, null, null);
            }
            else
            {
                LogInfo(string.Format("Connecting using DNS hostname resolution. target={0}", Name));
                connectResult = client.BeginConnect(host, port, null, null);
            }
            bool signaled = connectResult.AsyncWaitHandle.WaitOne(timeoutMs);
            connectResult.AsyncWaitHandle.Close();

            if (!signaled)
            {
                LogError(string.Format("Connect timed out before completion. target={0}", Name));
                client.Close();
                return false;
            }

            client.EndConnect(connectResult);
            if (!client.Connected)
            {
                LogError(string.Format("Connect completed but TcpClient.Connected is false. target={0}", Name));
                client.Close();
                return false;
            }

            client.NoDelay = true;
            client.SendTimeout = timeoutMs;
            client.ReceiveTimeout = timeoutMs;
            tcpClient = client;
            stream = tcpClient.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            LogInfo(string.Format("TCP connection established. target={0} local={1} remote={2}", Name, SafeEndpoint(client.Client.LocalEndPoint), SafeEndpoint(client.Client.RemoteEndPoint)));

            uint capabilities = preferDeltaResults ? ClusterBinaryProtocol.CapabilityDeltaResults : 0u;
            byte[] helloPayload = ClusterBinaryProtocol.BuildHello(localLabel, capabilities, maxZones, 1);
            LogInfo(string.Format("Sending HELLO. target={0} payloadBytes={1} label={2} maxZones={3} capabilities=0x{4:X}", Name, helloPayload.Length, localLabel, maxZones, capabilities));

            if (!SendRaw(ClusterMessageType.Hello, helloPayload))
            {
                LogError(string.Format("Failed to send HELLO. target={0}", Name));
                Dispose();
                return false;
            }

            LogInfo(string.Format("Waiting for WELCOME. target={0}", Name));

            ClusterIncomingMessage message;
            int frameSizeBytes;
            if (!ClusterBinaryProtocol.TryReadMessage(stream, out message, out frameSizeBytes))
            {
                LogError(string.Format("Socket closed while waiting for WELCOME. target={0}", Name));
                Dispose();
                return false;
            }

            BytesReceived += frameSizeBytes;
            LogInfo(string.Format("Received handshake frame. target={0} type={1} frameBytes={2}", Name, message.MessageType, frameSizeBytes));

            ClusterWelcomeMessage welcome = message as ClusterWelcomeMessage;
            if (welcome == null)
            {
                LogError(string.Format("Expected WELCOME but received {0}. target={1}", message.MessageType, Name));
                Dispose();
                return false;
            }

            Welcome = welcome;
            stream.ReadTimeout = Timeout.Infinite;
            client.ReceiveTimeout = 0;
            running = true;
            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Name = string.Format("ClusterWorker-{0}", Name);
            receiveThread.Start();

            LogInfo(string.Format("Handshake complete. target={0} workerLabel={1} sessionId={2} heartbeatMs={3}", Name, welcome.WorkerLabel, welcome.SessionId, welcome.HeartbeatMs));
            return true;
        }
        catch (Exception ex)
        {
            LogError(string.Format("Connect failed for {0}. Exception:\n{1}", Name, ex));
            Dispose();
            return false;
        }
    }

    public bool SendSnapshot(ClusterZoneSnapshotFrame snapshot)
    {
        using (ClusterProfilerMarkers.SnapshotSerialize.Auto())
        {
            return SendRaw(ClusterMessageType.Snapshot, ClusterBinaryProtocol.BuildSnapshot(snapshot));
        }
    }

    public bool SendSnapshotPatch(ClusterZoneSnapshotPatchFrame patch)
    {
        using (ClusterProfilerMarkers.SnapshotSerialize.Auto())
        {
            return SendRaw(ClusterMessageType.SnapshotPatch, ClusterBinaryProtocol.BuildSnapshotPatch(patch));
        }
    }

    public bool SendSimulate(ClusterSimulateRequestFrame request)
    {
        using (ClusterProfilerMarkers.SimulateSerialize.Auto())
        {
            return SendRaw(ClusterMessageType.Simulate, ClusterBinaryProtocol.BuildSimulate(request));
        }
    }

    public bool SendHeartbeat(int currentTick)
    {
        return SendRaw(ClusterMessageType.Heartbeat, ClusterBinaryProtocol.BuildHeartbeatRequest(currentTick));
    }

    public bool RequestStats()
    {
        return SendRaw(ClusterMessageType.StatsRequest, ClusterBinaryProtocol.BuildStatsRequest());
    }

    public bool RequestZoneHistory(int zoneId, ushort maxEntries)
    {
        return SendRaw(ClusterMessageType.ZoneHistoryRequest, ClusterBinaryProtocol.BuildZoneHistoryRequest(zoneId, maxEntries));
    }

    private bool SendRaw(ClusterMessageType messageType, byte[] payload)
    {
        if (stream == null)
        {
            LogError(string.Format("SendRaw aborted because stream is null. target={0} type={1}", Name, messageType));
            return false;
        }

        try
        {
            lock (sendLock)
            {
                int written = ClusterBinaryProtocol.WriteFrame(stream, messageType, payload);
                BytesSent += written;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogError(string.Format("Worker send failed for {0}. type={1} exception:\n{2}", Name, messageType, ex));
            running = false;
            return false;
        }
    }

    private void ReceiveLoop()
    {
        try
        {
            LogInfo(string.Format("Receive loop started. target={0}", Name));

            while (running && stream != null)
            {
                ClusterIncomingMessage message;
                int frameSizeBytes;
                if (!ClusterBinaryProtocol.TryReadMessage(stream, out message, out frameSizeBytes))
                {
                    LogInfo(string.Format("Receive loop ended because TryReadMessage returned false. target={0}", Name));
                    break;
                }

                BytesReceived += frameSizeBytes;

                ClusterWorkerEvent workerEvent = new ClusterWorkerEvent();
                workerEvent.Source = this;
                workerEvent.Message = message;
                inbox.Enqueue(workerEvent);
            }
        }
        catch (Exception ex)
        {
            if (running)
            {
                LogError(string.Format("Worker receive loop stopped for {0}. Exception:\n{1}", Name, ex));
            }
        }
        finally
        {
            running = false;
            LogInfo(string.Format("Receive loop finished. target={0}", Name));
        }
    }

    public void Dispose()
    {
        bool hadResources = running || tcpClient != null || stream != null || receiveThread != null;

        running = false;
        Welcome = null;

        try
        {
            stream?.Dispose();
        }
        catch
        {
        }

        try
        {
            tcpClient?.Close();
        }
        catch
        {
        }

        stream = null;
        tcpClient = null;
        receiveThread = null;

        if (hadResources)
        {
            LogInfo(string.Format("Disposed client resources. target={0}", Name));
        }
    }

    private void LogInfo(string message)
    {
        if (verboseLogging)
        {
            Debug.Log(string.Format("[ClusterNet] {0}", message));
        }
    }

    private static void LogError(string message)
    {
        Debug.LogError(string.Format("[ClusterNet] {0}", message));
    }

    private static string SafeEndpoint(EndPoint endpoint)
    {
        return endpoint != null ? endpoint.ToString() : "<null>";
    }
}
