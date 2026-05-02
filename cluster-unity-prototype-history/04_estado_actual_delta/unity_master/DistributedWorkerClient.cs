using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[Serializable]
public class SimulationEntityState
{
    public int id;
    public float x;
    public float y;
    public float vx;
    public float vy;
    public float speed;
    public string state;
}

[Serializable]
public class SimulationRequest
{
    public string type;
    public int requestId;
    public int tick;
    public int zoneIndex;
    public float deltaTime;
    public float targetX;
    public float targetY;
    public float damping;
    public List<SimulationEntityState> entities;
}

[Serializable]
public class SimulationResultEntity
{
    public int id;
    public float x;
    public float y;
    public float vx;
    public float vy;
    public string state;
}

[Serializable]
public class SimulationResponse
{
    public string type;
    public int requestId;
    public int tick;
    public int zoneIndex;
    public int workerEntityCount;
    public int processingMs;
    public List<SimulationResultEntity> entities;
    public string message;
}

public sealed class DistributedWorkerClient : IDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly ConcurrentQueue<SimulationResponse> inbox;
    private readonly object writerLock = new object();

    private TcpClient tcpClient;
    private StreamWriter writer;
    private Thread receiveThread;
    private volatile bool running;

    public string Name => string.Format("{0}:{1}", host, port);
    public bool IsConnected => tcpClient != null && tcpClient.Connected && running;

    public DistributedWorkerClient(string host, int port, ConcurrentQueue<SimulationResponse> inbox)
    {
        this.host = host;
        this.port = port;
        this.inbox = inbox;
    }

    public bool Connect(int timeoutMs)
    {
        Dispose();

        try
        {
            TcpClient client = new TcpClient();
            IAsyncResult connectResult = client.BeginConnect(host, port, null, null);
            bool connected = connectResult.AsyncWaitHandle.WaitOne(timeoutMs);

            if (!connected || !client.Connected)
            {
                client.Close();
                return false;
            }

            client.EndConnect(connectResult);
            client.NoDelay = true;
            client.SendTimeout = timeoutMs;
            client.ReceiveTimeout = 0;

            tcpClient = client;
            NetworkStream stream = tcpClient.GetStream();
            writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.AutoFlush = true;

            running = true;
            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Name = string.Format("WorkerReceiver-{0}", Name);
            receiveThread.Start(stream);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(string.Format("Failed to connect to worker {0}: {1}", Name, ex.Message));
            Dispose();
            return false;
        }
    }

    public bool Send(SimulationRequest request)
    {
        if (!IsConnected || writer == null)
        {
            return false;
        }

        try
        {
            string json = JsonUtility.ToJson(request);
            lock (writerLock)
            {
                writer.WriteLine(json);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(string.Format("Send failed for worker {0}: {1}", Name, ex.Message));
            running = false;
            return false;
        }
    }

    private void ReceiveLoop(object state)
    {
        NetworkStream stream = (NetworkStream)state;

        try
        {
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (running)
                {
                    string line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    SimulationResponse response = JsonUtility.FromJson<SimulationResponse>(line);
                    if (response != null)
                    {
                        inbox.Enqueue(response);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (running)
            {
                Debug.LogWarning(string.Format("Worker {0} receive loop stopped: {1}", Name, ex.Message));
            }
        }
        finally
        {
            running = false;
        }
    }

    public void Dispose()
    {
        running = false;

        try
        {
            writer?.Dispose();
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

        writer = null;
        tcpClient = null;
        receiveThread = null;
    }
}
