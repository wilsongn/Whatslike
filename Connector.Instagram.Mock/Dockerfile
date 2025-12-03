FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Connector.Instagram.Mock/Connector.Instagram.Mock.csproj", "Connector.Instagram.Mock/"]
RUN dotnet restore "Connector.Instagram.Mock/Connector.Instagram.Mock.csproj"
COPY . .
WORKDIR "/src/Connector.Instagram.Mock"
RUN dotnet build "Connector.Instagram.Mock.csproj" -c Release -o /app/build
FROM build AS publish
RUN dotnet publish "Connector.Instagram.Mock.csproj" -c Release -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Connector.Instagram.Mock.dll"]
