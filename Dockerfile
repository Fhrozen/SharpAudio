# wiki: docker-packaging#image-stages
FROM node:24-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package.json frontend/pnpm-workspace.yaml ./
RUN corepack enable pnpm && pnpm install --ignore-scripts --no-frozen-lockfile
COPY frontend/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS backend-build
WORKDIR /src
COPY SharpAudio.slnx ./
COPY src/SharpAudio.Api/SharpAudio.Api.csproj src/SharpAudio.Api/
COPY src/SharpAudio.Core/SharpAudio.Core.csproj src/SharpAudio.Core/
COPY src/SharpAudio.Worker.Tts/SharpAudio.Worker.Tts.csproj src/SharpAudio.Worker.Tts/
COPY src/SharpAudio.Worker.Asr/SharpAudio.Worker.Asr.csproj src/SharpAudio.Worker.Asr/
COPY src/SharpAudio.Worker.Music/SharpAudio.Worker.Music.csproj src/SharpAudio.Worker.Music/
COPY src/SharpAudio.Worker.Diar/SharpAudio.Worker.Diar.csproj src/SharpAudio.Worker.Diar/
RUN dotnet restore src/SharpAudio.Api/SharpAudio.Api.csproj
RUN dotnet restore src/SharpAudio.Worker.Tts/SharpAudio.Worker.Tts.csproj
RUN dotnet restore src/SharpAudio.Worker.Asr/SharpAudio.Worker.Asr.csproj
RUN dotnet restore src/SharpAudio.Worker.Music/SharpAudio.Worker.Music.csproj
RUN dotnet restore src/SharpAudio.Worker.Diar/SharpAudio.Worker.Diar.csproj
COPY src/SharpAudio.Core/ src/SharpAudio.Core/
COPY src/SharpAudio.Api/ src/SharpAudio.Api/
COPY src/SharpAudio.Worker.Tts/ src/SharpAudio.Worker.Tts/
COPY src/SharpAudio.Worker.Asr/ src/SharpAudio.Worker.Asr/
COPY src/SharpAudio.Worker.Music/ src/SharpAudio.Worker.Music/
COPY src/SharpAudio.Worker.Diar/ src/SharpAudio.Worker.Diar/
COPY --from=frontend-build /app/frontend/dist/ src/SharpAudio.Api/wwwroot/
RUN dotnet publish src/SharpAudio.Api/SharpAudio.Api.csproj -c Release -o /out/api -r linux-x64 --self-contained false /p:UseAppHost=false
RUN dotnet publish src/SharpAudio.Worker.Tts/SharpAudio.Worker.Tts.csproj -c Release -o /out/worker -r linux-x64 --self-contained false /p:UseAppHost=true
RUN dotnet publish src/SharpAudio.Worker.Asr/SharpAudio.Worker.Asr.csproj -c Release -o /out/worker-asr -r linux-x64 --self-contained false /p:UseAppHost=true
RUN dotnet publish src/SharpAudio.Worker.Music/SharpAudio.Worker.Music.csproj -c Release -o /out/worker-music -r linux-x64 --self-contained false /p:UseAppHost=true
RUN dotnet publish src/SharpAudio.Worker.Diar/SharpAudio.Worker.Diar.csproj -c Release -o /out/worker-diar -r linux-x64 --self-contained false /p:UseAppHost=true

# Shared base for all runtime image variants (TTS-only / ASR-only / both).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime-base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV MODEL_CACHE_DIR=/cache
ENV ESPEAK_DATA_DIR=/app/assets/espeak-ng-data

# espeak-ng (Kokoro TTS phonemization) + libgomp1 (Whisper.net's native ggml-cpu library needs OpenMP)
RUN apt-get update && \
    apt-get install -y --no-install-recommends libespeak-ng1 espeak-ng-data libgomp1 curl && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Note: /app/assets should be mounted as a volume with espeak-ng-data and tokens.txt
VOLUME ["/cache"]
COPY --from=backend-build /out/api/ ./

# --- TTS-only image: API + TTS worker only ---
# docker build --target runtime-tts -t sharp-audio:tts .
FROM runtime-base AS runtime-tts
COPY --from=backend-build /out/worker/ ./worker/
RUN chmod +x ./worker/SharpAudio.Worker.Tts
ENV WorkerOptions__ExecutablePath=/app/worker/SharpAudio.Worker.Tts
ENV SERVER_MODE=tts
ENTRYPOINT ["dotnet", "SharpAudio.Api.dll"]

# --- ASR-only image: API + ASR worker only ---
# docker build --target runtime-asr -t sharp-audio:asr .
FROM runtime-base AS runtime-asr
# ffmpeg normalizes any uploaded audio format (FLAC, MP3, OGG, WEBM, M4A, ...) to WAV before ASR.
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*
COPY --from=backend-build /out/worker-asr/ ./worker-asr/
RUN chmod +x ./worker-asr/SharpAudio.Worker.Asr
ENV AsrWorkerOptions__ExecutablePath=/app/worker-asr/SharpAudio.Worker.Asr
# Whisper.net.Runtime ships native libs under runtimes/<rid>/ (not the standard .../native/
# layout), so the dynamic linker won't find sibling .so dependencies without this on the path.
ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64:/app/worker-asr/runtimes/linux-x64
ENV SERVER_MODE=asr
ENTRYPOINT ["dotnet", "SharpAudio.Api.dll"]

# --- Music-only image: API + music worker only ---
# docker build --target runtime-music -t sharp-audio:music .
FROM runtime-base AS runtime-music
COPY --from=backend-build /out/worker-music/ ./worker-music/
RUN chmod +x ./worker-music/SharpAudio.Worker.Music
ENV MusicWorkerOptions__ExecutablePath=/app/worker-music/SharpAudio.Worker.Music
ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64:/app/worker-music/runtimes/linux-x64
ENV SERVER_MODE=music
ENTRYPOINT ["dotnet", "SharpAudio.Api.dll"]

# --- Diarization-only image: API + diarization worker only ---
# docker build --target runtime-diar -t sharp-audio:diar .
FROM runtime-base AS runtime-diar
COPY --from=backend-build /out/worker-diar/ ./worker-diar/
RUN chmod +x ./worker-diar/SharpAudio.Worker.Diar
ENV DiarWorkerOptions__ExecutablePath=/app/worker-diar/SharpAudio.Worker.Diar
ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64:/app/worker-diar/runtimes/linux-x64
ENV SERVER_MODE=diar
ENTRYPOINT ["dotnet", "SharpAudio.Api.dll"]

# --- Combined image: API + every task worker (default if no --target is given) ---
# docker build -t sharp-audio:all .   (equivalent to --target runtime-all)
FROM runtime-base AS runtime-all
# ffmpeg normalizes any uploaded audio format (FLAC, MP3, OGG, WEBM, M4A, ...) to WAV before ASR.
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*
COPY --from=backend-build /out/worker/ ./worker/
COPY --from=backend-build /out/worker-asr/ ./worker-asr/
COPY --from=backend-build /out/worker-music/ ./worker-music/
COPY --from=backend-build /out/worker-diar/ ./worker-diar/
RUN chmod +x ./worker/SharpAudio.Worker.Tts ./worker-asr/SharpAudio.Worker.Asr \
    ./worker-music/SharpAudio.Worker.Music ./worker-diar/SharpAudio.Worker.Diar
ENV WorkerOptions__ExecutablePath=/app/worker/SharpAudio.Worker.Tts
ENV AsrWorkerOptions__ExecutablePath=/app/worker-asr/SharpAudio.Worker.Asr
ENV MusicWorkerOptions__ExecutablePath=/app/worker-music/SharpAudio.Worker.Music
ENV DiarWorkerOptions__ExecutablePath=/app/worker-diar/SharpAudio.Worker.Diar
ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64:/app/worker-asr/runtimes/linux-x64:/app/worker-music/runtimes/linux-x64:/app/worker-diar/runtimes/linux-x64
ENV SERVER_MODE=all
ENTRYPOINT ["dotnet", "SharpAudio.Api.dll"]
