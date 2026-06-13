namespace GroupWeaver.Core.Plan;

/// <summary>
/// Thrown when an authoring operation on a <see cref="PlanModel"/> would violate a
/// structural invariant: a duplicate DN, an edge endpoint that is not an authored
/// object, a non-group parent, a rename onto an existing DN, or a token carrying a
/// control character. Cycles and self-membership are NOT conflicts — they are
/// authorable findings (ADR-014).
/// </summary>
public sealed class PlanConflictException : Exception
{
    /// <summary>Creates the exception with a human-readable message.</summary>
    public PlanConflictException(string message)
        : base(message)
    {
    }
}
