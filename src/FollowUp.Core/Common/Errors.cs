namespace FollowUp.Core.Common;

/// <summary>Requested record does not exist or is not visible to the caller (HTTP 404 — never reveals existence).</summary>
public sealed class NotFoundException(string what) : Exception($"{what} not found.");

/// <summary>The caller may see the record but not perform the action (HTTP 403).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>Invalid input (HTTP 400) with a stable code.</summary>
public sealed class ValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>State conflict (HTTP 409), e.g. a concurrent edit or an invalid transition.</summary>
public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
