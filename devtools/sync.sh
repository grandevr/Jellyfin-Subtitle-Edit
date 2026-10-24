#!/bin/sh
# Copy the project (without bin/obj) to the build folder on CT 103. Test folder only: /opt/jellyfin-test.
# Usage: devtools/sync.sh
set -e
cd "${SQE_SRC:-$(dirname "$0")/..}"
tar -c --owner=1000 --group=1000 \
  --exclude=./bin --exclude='*/bin' --exclude='*/obj' --exclude=./upstream-template \
  --exclude=./template-bump --exclude=./upstream-template.tar.gz --exclude=./devtools \
  --exclude=./testdata --exclude='./*.md' . \
  | ssh pve pct exec 103 -- tar -x -C "${SQE_DIR:-/opt/jellyfin-test/src/sqe}"
