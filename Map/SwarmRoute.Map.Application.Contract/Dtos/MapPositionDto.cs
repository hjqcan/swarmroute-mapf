namespace SwarmRoute.Map.Application.Contract.Dtos;

/// <summary>Transport shape for a <c>MapPosition</c> value object.</summary>
public sealed record MapPositionDto(double X, double Y, double Angle = 0);
