using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public enum ClusterMessageType : ushort
{
    Hello = 1,
    Welcome = 2,
    Snapshot = 3,
    Simulate = 4,
    Result = 5,
    Heartbeat = 6,
    Error = 7,
    StatsRequest = 8,
    StatsResponse = 9,
    ResultDelta = 10,
    SnapshotPatch = 11,
}

public enum ClusterObjectiveType : byte
{
    Seek = 0,
    Orbit = 1,
    Wander = 2,
}

public enum ClusterEntityStateCode : byte
{
    Idle = 0,
    Chase = 1,
    Orbit = 2,
    Wander = 3,
}

public enum ClusterErrorCode : ushort
{
    UnsupportedVersion = 1,
    InvalidMessage = 2,
    UnknownZone = 3,
    SnapshotMismatch = 4,
    TickOutOfOrder = 5,
    TooManyEntities = 6,
    InternalError = 100,
}

public enum ClusterResultStatus : byte
{
    Ok = 0,
    ResnapshotRequired = 1,
}

public sealed class ClusterProtocolEntityState
{
    public int Id;
    public Vector2 Position;
    public Vector2 Velocity;
    public float Speed;
    public ClusterEntityStateCode StateCode;
    public byte Flags;
    public ushort Reserved;
}

public sealed class ClusterDeltaEntityState
{
    public int Id;
    public short PositionDeltaX;
    public short PositionDeltaY;
    public short VelocityDeltaX;
    public short VelocityDeltaY;
    public ClusterEntityStateCode StateCode;
    public byte Flags;
}

public sealed class ClusterZoneSnapshotFrame
{
    public int ZoneId;
    public uint SnapshotId;
    public int Tick;
    public ushort GridX;
    public ushort GridY;
    public ClusterObjectiveType ObjectiveType;
    public Vector2 TargetPoint;
    public Vector2 Auxiliary;
    public uint Checksum;
    public List<ClusterProtocolEntityState> Entities = new List<ClusterProtocolEntityState>();
}

public sealed class ClusterZoneSnapshotPatchFrame
{
    public int ZoneId;
    public uint BaseSnapshotId;
    public uint SnapshotId;
    public int Tick;
    public ushort GridX;
    public ushort GridY;
    public ClusterObjectiveType ObjectiveType;
    public Vector2 TargetPoint;
    public Vector2 Auxiliary;
    public uint Checksum;
    public List<int> RemovedEntityIds = new List<int>();
    public List<ClusterProtocolEntityState> UpsertEntities = new List<ClusterProtocolEntityState>();
}

public sealed class ClusterSimulateRequestFrame
{
    public uint RequestId;
    public int ZoneId;
    public uint SnapshotId;
    public int Tick;
    public float DeltaTime;
    public Vector2 PlayerPosition;
}

public abstract class ClusterIncomingMessage
{
    public ClusterMessageType MessageType;
    public int FrameSizeBytes;
}

public sealed class ClusterWelcomeMessage : ClusterIncomingMessage
{
    public uint SessionId;
    public ushort HeartbeatMs;
    public ushort MaxZones;
    public string WorkerLabel;
}

public sealed class ClusterResultMessage : ClusterIncomingMessage
{
    public uint RequestId;
    public int ZoneId;
    public uint SnapshotId;
    public int Tick;
    public ClusterResultStatus Status;
    public uint ProcessingUs;
    public uint Checksum;
    public List<ClusterProtocolEntityState> Entities = new List<ClusterProtocolEntityState>();
}

public sealed class ClusterResultDeltaMessage : ClusterIncomingMessage
{
    public uint RequestId;
    public int ZoneId;
    public uint SnapshotId;
    public int Tick;
    public ClusterResultStatus Status;
    public uint ProcessingUs;
    public uint Checksum;
    public List<ClusterDeltaEntityState> Entities = new List<ClusterDeltaEntityState>();
}

public sealed class ClusterHeartbeatMessage : ClusterIncomingMessage
{
    public int LastTick;
    public ushort AssignedZones;
    public ushort PendingJobs;
    public uint AverageProcessingUs;
    public ulong UptimeMs;
}

public sealed class ClusterErrorMessage : ClusterIncomingMessage
{
    public ClusterErrorCode Code;
    public uint CorrelationId;
    public string Message;
}

public sealed class ClusterStatsResponseMessage : ClusterIncomingMessage
{
    public ulong TotalMessages;
    public ulong TotalSnapshots;
    public ulong TotalSimulations;
    public ulong TotalErrors;
    public uint AverageProcessingUs;
    public uint MaxProcessingUs;
    public uint KnownZones;
}

public static class ClusterBinaryProtocol
{
    private const uint Magic = 0x52434C55;
    private const ushort Version = 1;
    private const int HeaderSize = 12;

    public const uint CapabilityDeltaResults = 1u << 0;
    public const float DeltaScale = 1000f;

    public static byte[] BuildHello(string label, uint capabilities, ushort maxZones, byte role)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(role);
            writer.Write(capabilities);
            writer.Write(maxZones);
            WriteString(writer, label);
            return stream.ToArray();
        }
    }

    public static byte[] BuildSnapshot(ClusterZoneSnapshotFrame snapshot)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(snapshot.ZoneId);
            writer.Write(snapshot.SnapshotId);
            writer.Write(snapshot.Tick);
            writer.Write(snapshot.GridX);
            writer.Write(snapshot.GridY);
            writer.Write((byte)snapshot.ObjectiveType);
            writer.Write(new byte[3]);
            writer.Write(snapshot.TargetPoint.x);
            writer.Write(snapshot.TargetPoint.y);
            writer.Write(snapshot.Auxiliary.x);
            writer.Write(snapshot.Auxiliary.y);
            writer.Write(snapshot.Checksum);
            WriteEntities(writer, snapshot.Entities);
            return stream.ToArray();
        }
    }

    public static byte[] BuildSnapshotPatch(ClusterZoneSnapshotPatchFrame patch)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(patch.ZoneId);
            writer.Write(patch.BaseSnapshotId);
            writer.Write(patch.SnapshotId);
            writer.Write(patch.Tick);
            writer.Write(patch.GridX);
            writer.Write(patch.GridY);
            writer.Write((byte)patch.ObjectiveType);
            writer.Write(new byte[3]);
            writer.Write(patch.TargetPoint.x);
            writer.Write(patch.TargetPoint.y);
            writer.Write(patch.Auxiliary.x);
            writer.Write(patch.Auxiliary.y);
            writer.Write(patch.Checksum);
            writer.Write((ushort)patch.RemovedEntityIds.Count);
            for (int index = 0; index < patch.RemovedEntityIds.Count; index++)
            {
                writer.Write(patch.RemovedEntityIds[index]);
            }
            WriteEntities(writer, patch.UpsertEntities);
            return stream.ToArray();
        }
    }

    public static byte[] BuildSimulate(ClusterSimulateRequestFrame request)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(request.RequestId);
            writer.Write(request.ZoneId);
            writer.Write(request.SnapshotId);
            writer.Write(request.Tick);
            writer.Write(request.DeltaTime);
            writer.Write(request.PlayerPosition.x);
            writer.Write(request.PlayerPosition.y);
            return stream.ToArray();
        }
    }

    public static byte[] BuildHeartbeatRequest(int currentTick)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(currentTick);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((uint)0);
            writer.Write((ulong)0);
            return stream.ToArray();
        }
    }

    public static byte[] BuildStatsRequest()
    {
        return Array.Empty<byte>();
    }

    public static int WriteFrame(Stream stream, ClusterMessageType messageType, byte[] payload)
    {
        using (MemoryStream headerStream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(headerStream, Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((ushort)messageType);
            writer.Write(payload.Length);
            byte[] header = headerStream.ToArray();
            stream.Write(header, 0, header.Length);
            if (payload.Length > 0)
            {
                stream.Write(payload, 0, payload.Length);
            }
            stream.Flush();
            return header.Length + payload.Length;
        }
    }

    public static bool TryReadMessage(Stream stream, out ClusterIncomingMessage message, out int frameSizeBytes)
    {
        message = null;
        frameSizeBytes = 0;

        byte[] header = new byte[HeaderSize];
        if (!ReadExact(stream, header, HeaderSize))
        {
            return false;
        }

        uint magic = BitConverter.ToUInt32(header, 0);
        ushort version = BitConverter.ToUInt16(header, 4);
        ClusterMessageType messageType = (ClusterMessageType)BitConverter.ToUInt16(header, 6);
        int payloadLength = BitConverter.ToInt32(header, 8);

        if (magic != Magic)
        {
            throw new InvalidDataException("Invalid protocol magic.");
        }

        if (version != Version)
        {
            throw new InvalidDataException("Unsupported protocol version.");
        }

        byte[] payload = new byte[payloadLength];
        if (payloadLength > 0 && !ReadExact(stream, payload, payloadLength))
        {
            return false;
        }

        frameSizeBytes = HeaderSize + payloadLength;
        message = ParseMessage(messageType, payload, frameSizeBytes);
        return true;
    }

    public static uint ComputeChecksum(int zoneId, int tick, IList<ClusterProtocolEntityState> entities)
    {
        uint checksum = 2166136261u;
        checksum = Fnv1a(checksum, (uint)zoneId);
        checksum = Fnv1a(checksum, (uint)tick);
        checksum = Fnv1a(checksum, (uint)entities.Count);

        List<ClusterProtocolEntityState> ordered = new List<ClusterProtocolEntityState>(entities);
        ordered.Sort((left, right) => left.Id.CompareTo(right.Id));

        for (int index = 0; index < ordered.Count; index++)
        {
            ClusterProtocolEntityState entity = ordered[index];
            checksum = Fnv1a(checksum, (uint)entity.Id);
            checksum = Fnv1a(checksum, (uint)Mathf.RoundToInt(entity.Position.x * 1000f));
            checksum = Fnv1a(checksum, (uint)Mathf.RoundToInt(entity.Position.y * 1000f));
            checksum = Fnv1a(checksum, (uint)Mathf.RoundToInt(entity.Velocity.x * 1000f));
            checksum = Fnv1a(checksum, (uint)Mathf.RoundToInt(entity.Velocity.y * 1000f));
            checksum = Fnv1a(checksum, (uint)Mathf.RoundToInt(entity.Speed * 1000f));
            checksum = Fnv1a(checksum, (uint)entity.StateCode);
            checksum = Fnv1a(checksum, entity.Flags);
        }

        return checksum;
    }

    private static ClusterIncomingMessage ParseMessage(ClusterMessageType messageType, byte[] payload, int frameSizeBytes)
    {
        using (MemoryStream stream = new MemoryStream(payload))
        using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
        {
            switch (messageType)
            {
                case ClusterMessageType.Welcome:
                    {
                        ClusterWelcomeMessage message = new ClusterWelcomeMessage();
                        message.MessageType = messageType;
                        message.FrameSizeBytes = frameSizeBytes;
                        message.SessionId = reader.ReadUInt32();
                        message.HeartbeatMs = reader.ReadUInt16();
                        message.MaxZones = reader.ReadUInt16();
                        message.WorkerLabel = ReadString(reader);
                        return message;
                    }
                case ClusterMessageType.Result:
                    {
                        ClusterResultMessage message = new ClusterResultMessage();
                        message.MessageType = messageType;
                        message.FrameSizeBytes = frameSizeBytes;
                        message.RequestId = reader.ReadUInt32();
                        message.ZoneId = reader.ReadInt32();
                        message.SnapshotId = reader.ReadUInt32();
                        message.Tick = reader.ReadInt32();
                        message.Status = (ClusterResultStatus)reader.ReadByte();
                        reader.ReadBytes(3);
                        message.ProcessingUs = reader.ReadUInt32();
                        message.Checksum = reader.ReadUInt32();
                        message.Entities = ReadEntities(reader);
                        return message;
                    }
                case ClusterMessageType.ResultDelta:
                    {
                        ClusterResultDeltaMessage message = new ClusterResultDeltaMessage();
                        message.MessageType = messageType;
                        message.FrameSizeBytes = frameSizeBytes;
                        message.RequestId = reader.ReadUInt32();
                        message.ZoneId = reader.ReadInt32();
                        message.SnapshotId = reader.ReadUInt32();
                        message.Tick = reader.ReadInt32();
                        message.Status = (ClusterResultStatus)reader.ReadByte();
                        reader.ReadBytes(3);
                        message.ProcessingUs = reader.ReadUInt32();
                        message.Checksum = reader.ReadUInt32();
                        message.Entities = ReadDeltaEntities(reader);
                        return message;
                    }
                case ClusterMessageType.Heartbeat:
                    {
                        ClusterHeartbeatMessage message = new ClusterHeartbeatMessage();
                        message.MessageType = messageType;
                        message.FrameSizeBytes = frameSizeBytes;
                        message.LastTick = reader.ReadInt32();
                        message.AssignedZones = reader.ReadUInt16();
                        message.PendingJobs = reader.ReadUInt16();
                        message.AverageProcessingUs = reader.ReadUInt32();
                        message.UptimeMs = reader.ReadUInt64();
                        return message;
                    }
                case ClusterMessageType.Error:
                    {
                        ClusterErrorMessage message = new ClusterErrorMessage();
                        message.MessageType = messageType;
                        message.FrameSizeBytes = frameSizeBytes;
                        message.Code = (ClusterErrorCode)reader.ReadUInt16();
                        message.CorrelationId = reader.ReadUInt32();
                        message.Message = ReadString(reader);
                        return message;
                    }
                case ClusterMessageType.StatsResponse:
                    {
                        ClusterStatsResponseMessage message = new ClusterStatsResponseMessage();
                        message.MessageType = messageType;
                        message.FrameSizeBytes = frameSizeBytes;
                        message.TotalMessages = reader.ReadUInt64();
                        message.TotalSnapshots = reader.ReadUInt64();
                        message.TotalSimulations = reader.ReadUInt64();
                        message.TotalErrors = reader.ReadUInt64();
                        message.AverageProcessingUs = reader.ReadUInt32();
                        message.MaxProcessingUs = reader.ReadUInt32();
                        message.KnownZones = reader.ReadUInt32();
                        return message;
                    }
                default:
                    throw new InvalidDataException(string.Format("Unsupported incoming message type: {0}", messageType));
            }
        }
    }

    private static void WriteEntities(BinaryWriter writer, IList<ClusterProtocolEntityState> entities)
    {
        writer.Write((ushort)entities.Count);

        for (int index = 0; index < entities.Count; index++)
        {
            ClusterProtocolEntityState entity = entities[index];
            writer.Write(entity.Id);
            writer.Write(entity.Position.x);
            writer.Write(entity.Position.y);
            writer.Write(entity.Velocity.x);
            writer.Write(entity.Velocity.y);
            writer.Write(entity.Speed);
            writer.Write((byte)entity.StateCode);
            writer.Write(entity.Flags);
            writer.Write(entity.Reserved);
        }
    }

    private static List<ClusterProtocolEntityState> ReadEntities(BinaryReader reader)
    {
        ushort entityCount = reader.ReadUInt16();
        List<ClusterProtocolEntityState> entities = new List<ClusterProtocolEntityState>(entityCount);

        for (int index = 0; index < entityCount; index++)
        {
            ClusterProtocolEntityState entity = new ClusterProtocolEntityState();
            entity.Id = reader.ReadInt32();
            entity.Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            entity.Velocity = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            entity.Speed = reader.ReadSingle();
            entity.StateCode = (ClusterEntityStateCode)reader.ReadByte();
            entity.Flags = reader.ReadByte();
            entity.Reserved = reader.ReadUInt16();
            entities.Add(entity);
        }

        return entities;
    }

    private static List<ClusterDeltaEntityState> ReadDeltaEntities(BinaryReader reader)
    {
        ushort entityCount = reader.ReadUInt16();
        List<ClusterDeltaEntityState> entities = new List<ClusterDeltaEntityState>(entityCount);

        for (int index = 0; index < entityCount; index++)
        {
            ClusterDeltaEntityState entity = new ClusterDeltaEntityState();
            entity.Id = reader.ReadInt32();
            entity.PositionDeltaX = reader.ReadInt16();
            entity.PositionDeltaY = reader.ReadInt16();
            entity.VelocityDeltaX = reader.ReadInt16();
            entity.VelocityDeltaY = reader.ReadInt16();
            entity.StateCode = (ClusterEntityStateCode)reader.ReadByte();
            entity.Flags = reader.ReadByte();
            entities.Add(entity);
        }

        return entities;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        writer.Write((ushort)utf8.Length);
        writer.Write(utf8);
    }

    private static string ReadString(BinaryReader reader)
    {
        ushort length = reader.ReadUInt16();
        byte[] utf8 = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(utf8);
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int size)
    {
        int offset = 0;

        while (offset < size)
        {
            int read = stream.Read(buffer, offset, size - offset);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static uint Fnv1a(uint checksum, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            checksum ^= (value >> shift) & 0xFFu;
            checksum *= 16777619u;
        }

        return checksum;
    }
}
