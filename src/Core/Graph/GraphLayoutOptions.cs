namespace GroupWeaver.Core.Graph;

/// <summary>
/// Layout constants for <see cref="GraphBuilder"/> (ADR-004): node diameter D,
/// same-ring node margin m, radial ring gap g.
/// </summary>
public sealed class GraphLayoutOptions
{
    /// <summary>Creates layout options; throws <see cref="ArgumentException"/> when
    /// <paramref name="ringGap"/> is smaller than
    /// <paramref name="nodeDiameter"/> + <paramref name="nodeMargin"/>.</summary>
    public GraphLayoutOptions(double nodeDiameter = 44, double nodeMargin = 16, double ringGap = 150)
    {
        if (ringGap < nodeDiameter + nodeMargin)
        {
            // g >= D + m keeps the cross-ring distance at least as large as the
            // same-ring chord guarantee — the ADR-004 no-overlap proof needs both.
            throw new ArgumentException(
                $"ring gap {ringGap} must be at least node diameter {nodeDiameter} + node margin {nodeMargin}.",
                nameof(ringGap));
        }

        NodeDiameter = nodeDiameter;
        NodeMargin = nodeMargin;
        RingGap = ringGap;
    }

    /// <summary>Node diameter D in model units.</summary>
    public double NodeDiameter { get; }

    /// <summary>Minimum free space m between node borders on the same ring.</summary>
    public double NodeMargin { get; }

    /// <summary>Radial gap g between consecutive occupied rings.</summary>
    public double RingGap { get; }
}
