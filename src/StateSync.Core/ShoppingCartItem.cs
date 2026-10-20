using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StateSync.Core;

/// <summary>
/// Giao diện hỗ trợ các phần tử có định danh khóa (Key Identity) tách biệt với nội dung thuộc tính (Payload Value),
/// giúp cấu trúc CRDT vừa tra cứu theo khóa định danh vừa hợp nhất và kiểm tra tính nhất quán đầy đủ của Payload.
/// </summary>
public interface IPayloadElement<T>
{
    bool PayloadEquals(T other);
    string GetCanonicalPayloadString();
    int GetEstimatedPayloadBytes();
}

/// <summary>
/// Đại diện cho một mặt hàng trong Giỏ hàng mua sắm dùng chung (Collaborative Shopping Cart).
/// Equals/GetHashCode so sánh theo khóa định danh `ProductId`, trong khi `PayloadEquals` kiểm tra
/// toàn bộ thuộc tính (`ProductId`, `Name`, `Price`, `Quantity`).
/// </summary>
public record ShoppingCartItem(string ProductId, string Name, decimal Price, int Quantity = 1)
    : IPayloadElement<ShoppingCartItem>
{
    // So sánh định danh mặt hàng theo ProductId để quản lý khóa trong tập hợp giỏ hàng
    public virtual bool Equals(ShoppingCartItem? other)
    {
        if (other is null) return false;
        return ProductId == other.ProductId;
    }

    public override int GetHashCode()
    {
        return ProductId.GetHashCode(StringComparison.Ordinal);
    }

    public bool PayloadEquals(ShoppingCartItem other)
    {
        if (other is null) return false;
        return ProductId == other.ProductId &&
               Name == other.Name &&
               Price == other.Price &&
               Quantity == other.Quantity;
    }

    public string GetCanonicalPayloadString()
    {
        // Escape boundaries and retain every decimal digit for a deterministic tie-break.
        return JsonSerializer.Serialize(new[] { ProductId, Name,
            Price.ToString("G29", CultureInfo.InvariantCulture), Quantity.ToString(CultureInfo.InvariantCulture) });
    }

    public int GetEstimatedPayloadBytes()
    {
        return Encoding.UTF8.GetByteCount(ProductId ?? string.Empty) +
               Encoding.UTF8.GetByteCount(Name ?? string.Empty) +
               sizeof(decimal) +
               sizeof(int);
    }
}
