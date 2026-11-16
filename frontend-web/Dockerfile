FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY frontend-web/SafePassage.Web.csproj frontend-web/
RUN dotnet restore frontend-web/SafePassage.Web.csproj

COPY frontend-web/ frontend-web/
RUN dotnet publish frontend-web/SafePassage.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "SafePassage.Web.dll"]
