FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# Debug keeps local compose behaviour; CI passes --build-arg CONFIGURATION=Release for published images.
ARG CONFIGURATION=Debug
WORKDIR /src
COPY . .
RUN dotnet restore WorkContinuityEngine.sln
RUN dotnet publish src/App.Web/App.Web.csproj -c ${CONFIGURATION} -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "WorkContinuity.Web.dll"]
