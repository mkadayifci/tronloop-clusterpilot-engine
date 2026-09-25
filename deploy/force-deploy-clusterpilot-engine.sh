#!/bin/sh
set -eu
echo "ClusterPilot Engine: main dalındaki son sürüm yeniden derlenip kurulacak."
exec sudo /bin/bash /opt/tronloop/clusterpilot-engine/deploy-clusterpilot-engine.sh --force
