#!/bin/sh
# Copy the project (without bin/obj) to the build folder on the test host. Test folder only: $SQE_TEST_ROOT.
# Usage: devtools/sync.sh
set -e
. "$(dirname "$0")/env.sh"
cd "${SQE_SRC:-$(dirname "$0")/..}"
tar -c --owner=1000 --group=1000 \
  --exclude=./bin --exclude='*/bin' --exclude='*/obj' --exclude=./upstream-template \
  --exclude=./template-bump --exclude=./upstream-template.tar.gz --exclude=./devtools \
  --exclude=./testdata --exclude='./*.md' . \
  | $SQE_HOST_EXEC tar -x -C "$SQE_DIR"
