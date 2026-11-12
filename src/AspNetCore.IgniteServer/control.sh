#!/bin/sh
# Apache Ignite's control utility (cluster state, baseline, caches, snapshots, ...) on the jars of this image, e.g.:
#   control.sh --baseline
# It connects to the node of this container unless --host and --port say otherwise: as the ignite user with
# IGNITE_USER_PASSWORD (with authentication) unless --user is given, and with the node's key and trust stores (with
# USE_CLIENT_SSL) unless --keystore is given. It runs in a JVM of its own (heap up to 256 MB): where the memory of the
# node's container is tight, run it in another pod of this image.
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
# an argument of a java argument file: quoted, with its backslashes and quotes escaped
quote() { printf '"%s"\n' "$(printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"; }

# The node's credentials go in a java argument file (readable by this user only, removed afterward) rather than on the
# command line, which any process can read (/proc/<pid>/cmdline). The file starts with the main class: java expands
# argument files only before it, so the arguments given to this script stay as they are.
# a writable directory for its files: TMPDIR or /tmp, else /dev/shm (in memory), e.g., on a read-only root file
# system without a /tmp volume
for tmp_dir in "${TMPDIR:-/tmp}" /dev/shm ""; do
    if [ -z "$tmp_dir" ]; then
        echo "control.sh: none of ${TMPDIR:-/tmp} and /dev/shm is writable" >&2
        exit 1
    fi
    [ -d "$tmp_dir" ] && [ -w "$tmp_dir" ] && break
done
args_file=$(mktemp "$tmp_dir/control-utility-args.XXXXXX") || exit 1
trap 'rm -f "$args_file"; exit 130' INT
trap 'rm -f "$args_file"; exit 143' TERM
{
    echo org.apache.ignite.internal.commandline.CommandHandler
    case " $* " in
        *" --keystore "*) ;;
        *)
            if [ "$(lower "${USE_CLIENT_SSL:-false}")" = true ]; then
                for arg in --keystore "$SSL_KEY_STORE_FILE_PATH" --keystore-password "$SSL_KEY_STORE_PASSWORD" \
                    --keystore-type "${SSL_KEY_STORE_TYPE:-JKS}" --truststore "$SSL_TRUST_STORE_FILE_PATH" \
                    --truststore-password "$SSL_TRUST_STORE_PASSWORD" --truststore-type "${SSL_TRUST_STORE_TYPE:-JKS}"; do
                    quote "$arg"
                done
            fi
            ;;
    esac
    case " $* " in
        *" --user "*) ;;
        *)
            # the server keeps the default password without IGNITE_USER_PASSWORD
            if [ "$(lower "${ENABLE_AUTHENTICATION:-true}")" != false ]; then
                for arg in --user ignite --password "${IGNITE_USER_PASSWORD:-ignite}"; do
                    quote "$arg"
                done
            fi
            ;;
    esac
} > "$args_file"

app_dir=$(dirname "$(readlink -f "$0")")
"${JAVA_HOME:-/usr/lib/jvm/java-17}/bin/java" -Xms32m -Xmx256m -XX:+UseSerialGC -XX:TieredStopAtLevel=1 \
    -DIGNITE_HOME="$tmp_dir/control-utility" -DIGNITE_UPDATE_NOTIFIER=false \
    "@$app_dir/config/jvm-add-opens.args" -cp "$app_dir/libs/*" "@$args_file" "$@"
status=$?
rm -f "$args_file"
exit $status
