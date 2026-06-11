namespace GroupWeaver.Core.Providers;

/// <summary>Result of a successful connectivity probe.</summary>
/// <param name="Description">Human-readable description of the connected directory.</param>
/// <param name="GroupCount">Number of groups visible to the provider.</param>
public sealed record DirectoryConnection(string Description, int GroupCount);
