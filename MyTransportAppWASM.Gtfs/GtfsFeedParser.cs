using System.Buffers.Binary;
using System.Text;

namespace MyTransportAppWASM.Gtfs;

/// <summary>
/// Selectively projects the GTFS vehicle fields used by the map behind a primitive callback
/// boundary so the main application can load before this route-specific assembly is downloaded.
/// </summary>
public static class GtfsFeedParser
{
  private const uint FeedEntityTag = 18;
  private const uint EntityVehicleTag = 34;
  private const uint VehicleTripTag = 10;
  private const uint VehiclePositionTag = 18;
  private const uint VehicleTimestampTag = 40;
  private const uint VehicleDescriptorTag = 66;
  private const uint TripRouteIdTag = 42;
  private const uint DescriptorIdTag = 10;
  private const uint PositionLatitudeTag = 13;
  private const uint PositionLongitudeTag = 21;
  private const uint PositionBearingTag = 29;
  private const uint PositionSpeedTag = 45;

  public static void ParseVehicles(
    ReadOnlyMemory<byte> data,
    Action<int> setCapacity,
    Action<string?, string?, float, float, float, float, ulong> addVehicle,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(setCapacity);
    ArgumentNullException.ThrowIfNull(addVehicle);
    if (data.IsEmpty)
    {
      throw new GtfsFeedParsingException("Cannot parse empty GTFS data.");
    }

    cancellationToken.ThrowIfCancellationRequested();

    // Vehicle entities are typically around 100 bytes. The estimate avoids the
    // backing-list growth without allowing a large payload to preallocate without bound.
    setCapacity(Math.Clamp(data.Length / 96, 4, 32_768));

    try
    {
      var input = new ProtoReader(data.Span);
      var entityIndex = 0;
      while (input.ReadTag() is var tag && tag != 0)
      {
        if (tag != FeedEntityTag)
        {
          input.SkipField(tag);
          continue;
        }

        if ((entityIndex++ & 255) == 0)
        {
          cancellationToken.ThrowIfCancellationRequested();
        }

        var entity = input.ReadSubReader();
        ParseEntity(ref entity, addVehicle);
      }
    }
    catch (Exception ex) when (ex is not OperationCanceledException and not GtfsFeedParsingException)
    {
      throw new GtfsFeedParsingException("Failed to parse GTFS vehicle positions.", ex);
    }
  }

  private static void ParseEntity(
    ref ProtoReader input,
    Action<string?, string?, float, float, float, float, ulong> addVehicle)
  {
    var vehicle = new ParsedVehicle();
    while (input.ReadTag() is var tag && tag != 0)
    {
      if (tag != EntityVehicleTag)
      {
        input.SkipField(tag);
        continue;
      }

      var vehicleMessage = input.ReadSubReader();
      ParseVehicle(ref vehicleMessage, ref vehicle);
    }

    if (vehicle.HasPosition)
    {
      addVehicle(
        vehicle.HasTrip ? vehicle.RouteId ?? string.Empty : null,
        vehicle.HasVehicleDescriptor ? vehicle.VehicleId ?? string.Empty : null,
        vehicle.Latitude,
        vehicle.Longitude,
        vehicle.Bearing,
        vehicle.Speed,
        vehicle.Timestamp);
    }
  }

  private static void ParseVehicle(ref ProtoReader input, ref ParsedVehicle vehicle)
  {
    while (input.ReadTag() is var tag && tag != 0)
    {
      switch (tag)
      {
        case VehicleTripTag:
          vehicle.HasTrip = true;
          var trip = input.ReadSubReader();
          ParseTrip(ref trip, ref vehicle);
          break;
        case VehiclePositionTag:
          vehicle.HasPosition = true;
          var position = input.ReadSubReader();
          ParsePosition(ref position, ref vehicle);
          break;
        case VehicleTimestampTag:
          vehicle.Timestamp = input.ReadUInt64();
          break;
        case VehicleDescriptorTag:
          vehicle.HasVehicleDescriptor = true;
          var descriptor = input.ReadSubReader();
          ParseVehicleDescriptor(ref descriptor, ref vehicle);
          break;
        default:
          input.SkipField(tag);
          break;
      }
    }
  }

  private static void ParseTrip(ref ProtoReader input, ref ParsedVehicle vehicle)
  {
    while (input.ReadTag() is var tag && tag != 0)
    {
      if (tag == TripRouteIdTag)
      {
        vehicle.RouteId = input.ReadString();
      }
      else
      {
        input.SkipField(tag);
      }
    }
  }

  private static void ParseVehicleDescriptor(ref ProtoReader input, ref ParsedVehicle vehicle)
  {
    while (input.ReadTag() is var tag && tag != 0)
    {
      if (tag == DescriptorIdTag)
      {
        vehicle.VehicleId = input.ReadString();
      }
      else
      {
        input.SkipField(tag);
      }
    }
  }

  private static void ParsePosition(ref ProtoReader input, ref ParsedVehicle vehicle)
  {
    while (input.ReadTag() is var tag && tag != 0)
    {
      switch (tag)
      {
        case PositionLatitudeTag:
          vehicle.Latitude = input.ReadFloat();
          break;
        case PositionLongitudeTag:
          vehicle.Longitude = input.ReadFloat();
          break;
        case PositionBearingTag:
          vehicle.Bearing = input.ReadFloat();
          break;
        case PositionSpeedTag:
          vehicle.Speed = input.ReadFloat();
          break;
        default:
          input.SkipField(tag);
          break;
      }
    }
  }

  private struct ParsedVehicle
  {
    public string? RouteId;
    public string? VehicleId;
    public float Latitude;
    public float Longitude;
    public float Bearing;
    public float Speed;
    public ulong Timestamp;
    public bool HasTrip;
    public bool HasVehicleDescriptor;
    public bool HasPosition;
  }

  private ref struct ProtoReader
  {
    private const int MaximumGroupDepth = 10;
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
      _data = data;
    }

    public uint ReadTag()
    {
      if (_position == _data.Length)
      {
        return 0;
      }

      var rawTag = ReadVarint();
      if (rawTag == 0 || rawTag > uint.MaxValue || (rawTag >> 3) == 0)
      {
        throw new InvalidDataException("The GTFS payload contains an invalid protobuf tag.");
      }

      return (uint)rawTag;
    }

    public ProtoReader ReadSubReader()
    {
      var length = ReadLength();
      EnsureAvailable(length);
      var nested = new ProtoReader(_data.Slice(_position, length));
      _position += length;
      return nested;
    }

    public ulong ReadUInt64() => ReadVarint();

    public float ReadFloat()
    {
      EnsureAvailable(sizeof(int));
      var bits = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, sizeof(int)));
      _position += sizeof(int);
      return BitConverter.Int32BitsToSingle(bits);
    }

    public string ReadString()
    {
      var length = ReadLength();
      EnsureAvailable(length);
      var value = Encoding.UTF8.GetString(_data.Slice(_position, length));
      _position += length;
      return value;
    }

    public void SkipField(uint tag, int depth = 0)
    {
      switch (tag & 7)
      {
        case 0:
          ReadVarint();
          return;
        case 1:
          Skip(sizeof(long));
          return;
        case 2:
          Skip(ReadLength());
          return;
        case 3:
          if (depth >= MaximumGroupDepth)
          {
            throw new InvalidDataException("The GTFS payload exceeds the protobuf group depth limit.");
          }

          var expectedEndTag = (tag & ~7u) | 4u;
          while (true)
          {
            var nestedTag = ReadTag();
            if ((nestedTag & 7) == 4)
            {
              if (nestedTag != expectedEndTag)
              {
                throw new InvalidDataException("The GTFS payload contains a mismatched protobuf group.");
              }
              return;
            }

            SkipField(nestedTag, depth + 1);
          }
        case 5:
          Skip(sizeof(int));
          return;
        default:
          throw new InvalidDataException("The GTFS payload contains an unsupported protobuf wire type.");
      }
    }

    private int ReadLength()
    {
      var length = ReadVarint();
      if (length > int.MaxValue)
      {
        throw new InvalidDataException("A GTFS protobuf field exceeds the supported size.");
      }
      return (int)length;
    }

    private ulong ReadVarint()
    {
      ulong value = 0;
      for (var shift = 0; shift < 64; shift += 7)
      {
        EnsureAvailable(1);
        var current = _data[_position++];
        if (shift == 63 && current > 1)
        {
          throw new InvalidDataException("The GTFS payload contains an oversized protobuf varint.");
        }

        value |= (ulong)(current & 0x7f) << shift;
        if ((current & 0x80) == 0)
        {
          return value;
        }
      }

      throw new InvalidDataException("The GTFS payload contains an unterminated protobuf varint.");
    }

    private void Skip(int length)
    {
      EnsureAvailable(length);
      _position += length;
    }

    private void EnsureAvailable(int length)
    {
      if (length < 0 || length > _data.Length - _position)
      {
        throw new EndOfStreamException("The GTFS payload ended inside a protobuf field.");
      }
    }
  }
}

public sealed class GtfsFeedParsingException : Exception
{
  public GtfsFeedParsingException(string message) : base(message)
  {
  }

  public GtfsFeedParsingException(string message, Exception innerException)
    : base(message, innerException)
  {
  }
}
