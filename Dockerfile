FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/FlashSale.Domain/FlashSale.Domain.csproj", "src/FlashSale.Domain/"]
COPY ["src/FlashSale.Application/FlashSale.Application.csproj", "src/FlashSale.Application/"]
COPY ["src/FlashSale.Infrastructure/FlashSale.Infrastructure.csproj", "src/FlashSale.Infrastructure/"]
COPY ["src/Order.Api/Order.Api.csproj", "src/Order.Api/"]
COPY ["src/HealthProbe/HealthProbe.csproj", "src/HealthProbe/"]
RUN dotnet restore "src/Order.Api/Order.Api.csproj"
COPY . .
WORKDIR "/src/src/Order.Api"
RUN dotnet publish "Order.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false
WORKDIR "/src/src/HealthProbe"
RUN dotnet publish "HealthProbe.csproj" -c Release -o /app/healthprobe /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
# Install krb5 library for Kerberos/SSPI support
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
# Non-root runtime (ADR-007): the base image ships an `app` user (uid 1654).
# Published output is root-owned, so grant the app user read+execute and a
# writable logs/ dir for the file DLQ before dropping privileges.
COPY --from=build /app/publish .
COPY --from=build /app/healthprobe ./healthprobe
RUN mkdir -p /app/logs && chown -R app:app /app
USER app
ENTRYPOINT ["dotnet", "Order.Api.dll"]
