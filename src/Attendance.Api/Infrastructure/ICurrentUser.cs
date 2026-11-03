namespace Attendance.Api.Infrastructure;

/// <summary>Supplies the audit name written to CreatedBy/ModifiedBy/SubmittedBy (the legacy procedure used SYSTEM_USER).</summary>
public interface ICurrentUser
{
    string Name { get; }
}

public sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    /// <summary>Written until authentication is added to the API.</summary>
    public const string AnonymousName = "api";

    public string Name =>
        httpContextAccessor.HttpContext?.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? name
            : AnonymousName;
}
