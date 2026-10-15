namespace StateSync.Core;

/// <summary>
/// Đại diện cho một mặt hàng trong Giỏ hàng mua sắm dùng chung (Collaborative Shopping Cart).
/// </summary>
public record ShoppingCartItem(string ProductId, string Name, decimal Price, int Quantity = 1)
{
    // So sánh định danh mặt hàng theo ProductId để quản lý sự tồn tại trong giỏ hàng
    public virtual bool Equals(ShoppingCartItem? other)
    {
        if (other is null) return false;
        return ProductId == other.ProductId;
    }

    public override int GetHashCode()
    {
        return ProductId.GetHashCode();
    }
}
