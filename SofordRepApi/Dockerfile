FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SofordRepApi.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
RUN mkdir -p /app/App_Data && chown -R app:app /app/App_Data
VOLUME ["/app/App_Data"]
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "SofordRepApi.dll"]
