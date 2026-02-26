FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY thegamma-unified.fsproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# App binaries (dotnet publish output)
COPY --from=build /app .

# Static data baked into image (smlouvy XMLs excluded via .dockerignore)
COPY data/ data/
COPY templates/ templates/
COPY olympics-docs/ olympics-docs/
COPY olympics-templates/ olympics-templates/

# Seed data for volume initialisation (logs/cache excluded via .dockerignore)
COPY storage/uploads/      /seed/uploads/
COPY storage/snippets/     /seed/snippets/
COPY storage/datavizconfig/ /seed/datavizconfig/

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080
ENV THEGAMMA_BASE_URL=https://services.thegamma.net
ENV THEGAMMA_STORAGE_ROOT=/data/storage
EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
