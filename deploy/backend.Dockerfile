# syntax=docker/dockerfile:1
FROM eclipse-temurin:25-jdk-noble AS java
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY MusicRecognition.Domain/ MusicRecognition.Domain/
COPY MusicRecognition.Application/ MusicRecognition.Application/
COPY MusicRecognition.Infrastructure/ MusicRecognition.Infrastructure/
COPY MusicRecognition.ServiceDefaults/ MusicRecognition.ServiceDefaults/
COPY MusicRecognition.Api/ MusicRecognition.Api/
COPY MusicRecognition.Worker/ MusicRecognition.Worker/
RUN dotnet publish MusicRecognition.Api -c Release -o /out/api /p:UseAppHost=false \
 && dotnet publish MusicRecognition.Worker -c Release -o /out/worker /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
 python3 python3-venv poppler-utils tesseract-ocr tesseract-ocr-eng \
 fonts-dejavu-core fonts-noto-cjk libasound2t64 libgl1 libegl1 libopengl0 \
 libxkbcommon-x11-0 libxcb-cursor0 libxcb-icccm4 libxcb-keysyms1 libxcb-shape0 \
 libxcb-xinerama0 libxcb-render-util0 libnss3 libdbus-1-3 libgomp1 \
 libgtk-3-0 libxi6 libxtst6 libxrender1 libfontconfig1 \
 && rm -rf /var/lib/apt/lists/*
COPY --from=java /opt/java/openjdk /opt/java/openjdk
ENV JAVA_HOME=/opt/java/openjdk PATH=/opt/java/openjdk/bin:/opt/recognition/.tools/poc-python/bin:$PATH
WORKDIR /opt/recognition
COPY scripts/runtime/ scripts/runtime/
COPY scripts/setup/requirements*.txt scripts/setup/
COPY deploy/toolchain.lock.json deploy/install-toolchain.py deploy/
RUN python3 -m venv .tools/poc-python \
 && .tools/poc-python/bin/pip install --no-cache-dir -r scripts/setup/requirements-composition.txt -r scripts/setup/requirements-opencv.txt \
 && python3 deploy/install-toolchain.py \
 && mkdir -p .tools/poc-classes /data /home/app/.cache \
 && javac --release 25 -encoding UTF-8 -cp '.tools/audiveris/Audiveris/app/*' -d .tools/poc-classes scripts/runtime/ExportNotes.java \
 && chown -R 1654:1654 /data /home/app
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production \
 Recognition__RepositoryRoot=/opt/recognition Recognition__DataRoot=/data \
 Recognition__Python=/opt/recognition/.tools/poc-python/bin/python \
 Recognition__Pdftoppm=/usr/bin/pdftoppm Recognition__Font=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf \
 Recognition__RequireOpenCv=true Recognition__RequireComposition=true \
 MUSICRECOGNITION_MUSESCORE=/usr/local/bin/musescore-headless \
 QT_QPA_PLATFORM=offscreen HOME=/home/app
COPY deploy/musescore-headless.sh /usr/local/bin/musescore-headless
RUN apt-get update && apt-get install -y --no-install-recommends xvfb xauth \
 && chmod 755 /usr/local/bin/musescore-headless && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
USER 1654
EXPOSE 8080
FROM runtime AS api
WORKDIR /app/api
ENTRYPOINT ["dotnet", "/app/api/MusicRecognition.Api.dll"]
FROM runtime AS worker
WORKDIR /app/worker
ENTRYPOINT ["dotnet", "/app/worker/MusicRecognition.Worker.dll"]
