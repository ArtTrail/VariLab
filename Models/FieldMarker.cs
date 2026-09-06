namespace VariLab.Models;

/// <summary>A labeled pixel position on a reference frame — the target or a comp star,
/// annotated on the field image export.</summary>
public record FieldMarker(double X, double Y, string Label);
