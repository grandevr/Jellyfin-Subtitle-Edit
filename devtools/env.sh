#!/bin/sh
# Sourced by the other scripts. Settings for the THROWAWAY test host; nothing here has a default on purpose.
#   SQE_HOST_EXEC  command prefix that runs a command STRING on the test host, e.g. "ssh myhost" or
#                  "ssh myhost lxc-attach -n test --"; for a local test server use "sh -c". It receives the whole
#                  command as ONE final argument, already shell-quoted by sqe_host below, so arguments with
#                  spaces survive ssh's re-parsing. stdin is passed through.
#   SQE_TEST_ROOT  test server folder on that host (holds config/, media/, src/), e.g. /srv/jellyfin-test
#   JF_BASE        test server URL, e.g. http://test-host:8097 (used by testdata/*.py)
# Optional: SQE_CONTAINER (default jellyfin-test), SQE_DIR (synced project folder, default $SQE_TEST_ROOT/src/sqe).
: "${SQE_HOST_EXEC:?set SQE_HOST_EXEC, see devtools/env.sh}"
: "${SQE_TEST_ROOT:?set SQE_TEST_ROOT, see devtools/env.sh}"
SQE_CONTAINER="${SQE_CONTAINER:-jellyfin-test}"
SQE_DIR="${SQE_DIR:-$SQE_TEST_ROOT/src/sqe}"

# sqe_host CMD ARG...: run a command on the test host through SQE_HOST_EXEC, quoting every argument.
sqe_host() {
  sqe_cmd=
  for sqe_arg in "$@"; do
    sqe_cmd="$sqe_cmd '$(printf %s "$sqe_arg" | sed "s/'/'\\\\''/g")'"
  done
  $SQE_HOST_EXEC "$sqe_cmd"
}
