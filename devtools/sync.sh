#!/bin/sh
# Copy the project (without bin/obj) to the build folder on the test host. Test folder only: $SQE_TEST_ROOT.
# The synced src/ and tests/ trees are removed first, so files deleted or moved in the repo do not linger there.
# Usage: devtools/sync.sh
set -e
. "$(dirname "$0")/env.sh"
case "$SQE_DIR" in "$SQE_TEST_ROOT"/*) ;; *) echo "SQE_DIR must be inside SQE_TEST_ROOT" >&2; exit 1 ;; esac
sqe_host rm -rf "$SQE_DIR/src" "$SQE_DIR/tests"
cd "${SQE_SRC:-$(dirname "$0")/..}"
tar -c --owner=1000 --group=1000 \
  --exclude=./bin --exclude='*/bin' --exclude='*/obj' --exclude=./upstream-template \
  --exclude=./template-bump --exclude=./upstream-template.tar.gz --exclude=./devtools \
  --exclude=./testdata --exclude='./*.md' . \
  | sqe_host tar -x -C "$SQE_DIR"
