using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Graph;

/// <summary>
/// A positioned node of the rendered graph (ADR-004): identity is the DN
/// (compared via <see cref="Dn.Comparer"/>), placed on a concentric ring with
/// coordinates rounded to 0.1 model units.
/// </summary>
public sealed record GraphNode(
    string Dn,
    string Label,
    AdObjectKind Kind,
    double X,
    double Y,
    int Ring,
    bool IsRoot);
