FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/SafePassage.Api.csproj backend/
RUN dotnet restore backend/SafePassage.Api.csproj

COPY backend/ backend/
RUN dotnet publish backend/SafePassage.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "SafePassage.Api.dll"]
