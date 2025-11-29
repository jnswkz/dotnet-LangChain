# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Install native dependencies for Tesseract OCR and SkiaSharp
RUN apt-get update && apt-get install -y \
    libtesseract-dev \
    libleptonica-dev \
    tesseract-ocr \
    tesseract-ocr-vie \
    tesseract-ocr-eng \
    libfontconfig1 \
    libfreetype6 \
    && rm -rf /var/lib/apt/lists/*

# Copy project file and restore
COPY dotnet-LangChain.csproj .
RUN dotnet restore

# Copy source and build
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install runtime dependencies for Tesseract OCR and SkiaSharp
RUN apt-get update && apt-get install -y \
    curl \
    libtesseract5 \
    liblept5 \
    tesseract-ocr \
    tesseract-ocr-vie \
    tesseract-ocr-eng \
    libfontconfig1 \
    libfreetype6 \
    libgl1-mesa-glx \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN adduser --disabled-password --gecos '' appuser

# Create directories for documents
RUN mkdir -p /app/pdfs /app/docx

COPY --from=build /app/publish .

# Note: pdfs and docx folders should be mounted as volumes at runtime
# See docker-compose.yml for volume configuration

# Set ownership
RUN chown -R appuser:appuser /app
USER appuser

# Environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5001
ENV DOTNET_EnableDiagnostics=0

# These should be provided at runtime
# ENV GOOGLE_API_KEY=your-api-key
# ENV AZURE_POSTGRES_URL=your-postgres-connection-string

EXPOSE 5001

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:5001/health || exit 1

ENTRYPOINT ["dotnet", "dotnet-LangChain.dll"]
