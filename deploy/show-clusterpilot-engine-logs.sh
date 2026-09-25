#!/bin/sh
set -eu
echo "ClusterPilot Engine logları — yeni kayıt gelmezse ekran bekler. Çıkış: Ctrl+C."
exec journalctl --no-pager -u tronloop-clusterpilot-engine.service -n 100 -f "$@"
