# Nexora backend — production container image (ASP.NET Core 8 + workers)
# Build context = the Backend/ directory.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ERP_RFQ_Automation/ERP_RFQ_Automation.csproj ERP_RFQ_Automation/
RUN dotnet restore ERP_RFQ_Automation/ERP_RFQ_Automation.csproj
COPY ERP_RFQ_Automation/ ERP_RFQ_Automation/
RUN dotnet publish ERP_RFQ_Automation/ERP_RFQ_Automation.csproj -c Release -o /app /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Native libs for Tesseract/Leptonica OCR (the Tesseract nuget also ships linux-x64 natives).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libleptonica-dev libtesseract-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# Containers listen on 8080 by convention; the host maps it to 443/80.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Health probe target: GET /health
ENTRYPOINT ["dotnet", "ERP_RFQ_Automation.dll"]
