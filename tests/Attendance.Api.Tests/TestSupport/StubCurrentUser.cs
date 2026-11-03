using Attendance.Api.Infrastructure;

namespace Attendance.Api.Tests.TestSupport;

public sealed class StubCurrentUser(string name = "test-user") : ICurrentUser
{
    public string Name => name;
}
