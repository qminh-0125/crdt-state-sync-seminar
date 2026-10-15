using System;

namespace StateSync.Core;

/// <summary>
/// Dấu thời gian kết hợp giữa thời gian logic (hoặc vật lý) và định danh client
/// để đảm bảo quan hệ thứ tự toàn phần (Total Order) không bao giờ bị trùng lặp.
/// </summary>
public readonly struct Timestamp : IComparable<Timestamp>, IEquatable<Timestamp>
{
    public long Ticks { get; }
    public string ClientId { get; }

    public Timestamp(long ticks, string clientId)
    {
        Ticks = ticks;
        ClientId = clientId ?? string.Empty;
    }

    public static Timestamp Now(string clientId)
    {
        return new Timestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), clientId);
    }

    public int CompareTo(Timestamp other)
    {
        int cmp = Ticks.CompareTo(other.Ticks);
        if (cmp != 0) return cmp;
        return string.Compare(ClientId, other.ClientId, StringComparison.Ordinal);
    }

    public bool Equals(Timestamp other)
    {
        return Ticks == other.Ticks && ClientId == other.ClientId;
    }

    public override bool Equals(object? obj)
    {
        return obj is Timestamp other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Ticks, ClientId);
    }

    public static bool operator <(Timestamp left, Timestamp right) => left.CompareTo(right) < 0;
    public static bool operator <=(Timestamp left, Timestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >(Timestamp left, Timestamp right) => left.CompareTo(right) > 0;
    public static bool operator >=(Timestamp left, Timestamp right) => left.CompareTo(right) >= 0;
    public static bool operator ==(Timestamp left, Timestamp right) => left.Equals(right);
    public static bool operator !=(Timestamp left, Timestamp right) => !left.Equals(right);

    public override string ToString() => $"({Ticks}, {ClientId})";
}
