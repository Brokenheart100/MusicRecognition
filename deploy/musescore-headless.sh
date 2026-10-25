#!/bin/sh
# The official Linux binary may select xcb even when QT_QPA_PLATFORM is set.
# Use an isolated virtual display; no host X socket or desktop is exposed.
exec xvfb-run -a -s '-screen 0 1280x1024x24' /opt/musescore/squashfs-root/AppRun "$@"
