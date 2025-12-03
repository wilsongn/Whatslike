FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar arquivos de projeto
COPY ["Chat.StatusWorker/Chat.StatusWorker.csproj", "Chat.StatusWorker/"]
COPY ["Chat.Persistence/Chat.Persistence.csproj", "Chat.Persistence/"]
RUN dotnet restore "Chat.StatusWorker/Chat.StatusWorker.csproj"

# Copiar código fonte e compilar
COPY . .
WORKDIR "/src/Chat.StatusWorker"
RUN dotnet build "Chat.StatusWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Chat.StatusWorker.csproj" -c Release -o /app/publish

# Imagem final
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Chat.StatusWorker.dll"]
