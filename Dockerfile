# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first so the restore layer is cached until a csproj changes.
COPY Attendance.sln ./
COPY src/Attendance.Api/Attendance.Api.csproj src/Attendance.Api/
COPY tests/Attendance.Api.Tests/Attendance.Api.Tests.csproj tests/Attendance.Api.Tests/
RUN dotnet restore Attendance.sln

COPY . .
RUN dotnet publish src/Attendance.Api -c Release -o /app/publish /p:UseAppHost=false --no-restore

# ---- test (opt-in: docker build --target test .) ------------------------------
FROM build AS test
RUN dotnet test tests/Attendance.Api.Tests -c Release --no-restore

# ---- runtime -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Attendance.Api.dll"]
