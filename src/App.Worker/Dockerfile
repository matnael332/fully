FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Debug keeps local compose behaviour; CI passes --build-arg CONFIGURATION=Release for published images.
ARG CONFIGURATION=Debug
WORKDIR /src
COPY . .
RUN dotnet restore WorkContinuityEngine.sln
RUN dotnet publish src/App.Worker/App.Worker.csproj -c ${CONFIGURATION} -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
# Npgsql probes the Kerberos GSSAPI library when it opens a connection and prints an error to stderr
# when the plain runtime image lacks it (the ASP.NET image ships it). Installing the library keeps
# job and worker logs free of a misleading error; password authentication is unaffected either way.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WorkContinuity.Worker.dll"]
