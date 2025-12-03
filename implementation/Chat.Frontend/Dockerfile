FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar arquivos de projeto
COPY ["Chat.Frontend/Chat.Frontend.csproj", "Chat.Frontend/"]
RUN dotnet restore "Chat.Frontend/Chat.Frontend.csproj"

# Copiar código fonte e compilar
COPY . .
WORKDIR "/src/Chat.Frontend"
RUN dotnet build "Chat.Frontend.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Chat.Frontend.csproj" -c Release -o /app/publish

# Imagem final
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Chat.Frontend.dll"]
