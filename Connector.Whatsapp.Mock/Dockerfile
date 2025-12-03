FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Connector.Whatsapp.Mock/Connector.Whatsapp.Mock.csproj", "Connector.Whatsapp.Mock/"]
RUN dotnet restore "Connector.Whatsapp.Mock/Connector.Whatsapp.Mock.csproj"
COPY . .
WORKDIR "/src/Connector.Whatsapp.Mock"
RUN dotnet build "Connector.Whatsapp.Mock.csproj" -c Release -o /app/build
FROM build AS publish
RUN dotnet publish "Connector.Whatsapp.Mock.csproj" -c Release -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Connector.Whatsapp.Mock.dll"]
