FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Lsc.Inventory.Api/Lsc.Inventory.Api.csproj", "src/Lsc.Inventory.Api/"]
RUN dotnet restore "src/Lsc.Inventory.Api/Lsc.Inventory.Api.csproj"
COPY . .
RUN dotnet publish "src/Lsc.Inventory.Api/Lsc.Inventory.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
RUN adduser --disabled-password --gecos "" --uid 10001 appuser
COPY --from=build /app/publish .
USER appuser
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lsc.Inventory.Api.dll"]
