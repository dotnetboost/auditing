namespace SampleApp.Dtos;

/// <summary>Body of <c>POST /api/products</c>.</summary>
public sealed record CreateProduct(string Name, decimal Price, string Category, decimal SupplierCost);

/// <summary>Body of <c>PUT /api/products/{id}</c>. Omitted fields are left alone.</summary>
public sealed record UpdateProduct(string? Name, decimal? Price, string? Category, decimal? SupplierCost);

/// <summary>A product as the endpoints return it.</summary>
public sealed record ProductResponse(
    int Id,
    string Name,
    decimal Price,
    string Category,
    decimal SupplierCost,
    int ViewCount);

/// <summary>Body of <c>POST /api/products/discount</c>.</summary>
public sealed record ApplyDiscount(string Category, decimal Percent);

/// <summary>What a bulk operation or a prune did.</summary>
public sealed record AffectedResponse(int Affected);

/// <summary>Body of <c>POST /api/stock-checks</c>.</summary>
public sealed record RecordStockCheck(int ProductId, int OnHand);
