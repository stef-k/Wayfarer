using System.Text.Json.Serialization;

namespace Wayfarer.Models.Dtos;

public record OrderDto
{
    [JsonConstructor]                 // make binder look at *properties*
    public OrderDto(Guid id, int order) =>
        (Id, Order) = (id, order);

    public Guid Id    { get; init; }
    public int  Order { get; init; }
}
