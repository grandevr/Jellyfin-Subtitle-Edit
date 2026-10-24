#!/bin/sh
# Sourced by the other scripts. Settings for the THROWAWAY test host; nothing here has a default on purpose.
#   SQE_HOST_EXEC  command prefix that runs a command on the test host / container host,
#                  e.g. "ssh myhost" or "ssh myhost pct exec 103 --" (stdin is passed through)
#   SQE_TEST_ROOT  test server folder on that host (holds config/, media/, src/), e.g. /srv/jellyfin-test
#   JF_BASE        test server URL, e.g. http://test-host:8097 (used by testdata/*.py)
# Optional: SQE_CONTAINER (default jellyfin-test), SQE_DIR (synced project folder, default $SQE_TEST_ROOT/src/sqe).
: "${SQE_HOST_EXEC:?set SQE_HOST_EXEC, see devtools/env.sh}"
: "${SQE_TEST_ROOT:?set SQE_TEST_ROOT, see devtools/env.sh}"
SQE_CONTAINER="${SQE_CONTAINER:-jellyfin-test}"
SQE_DIR="${SQE_DIR:-$SQE_TEST_ROOT/src/sqe}"
