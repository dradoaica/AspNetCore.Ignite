#!/bin/sh
# Apache Ignite's control utility (cluster state, baseline, caches, snapshots, ...) on the jars of this image, e.g.:
#   control.sh --baseline
# It connects to the node of this container unless --host and --port say otherwise: as the ignite user with
# IGNITE_USER_PASSWORD (with authentication) unless --user is given, and with the node's key and trust stores (with
# USE_CLIENT_SSL) unless --keystore is given. It runs in a JVM of its own (heap up to 256 MB): where the memory of the
# node's container is tight, run it in another pod of this image.
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

case " $* " in
    *" --keystore "*) ;;
    *)
        if [ "$(lower "${USE_CLIENT_SSL:-false}")" = true ]; then
            set -- --keystore "$SSL_KEY_STORE_FILE_PATH" --keystore-password "$SSL_KEY_STORE_PASSWORD" \
                --keystore-type "${SSL_KEY_STORE_TYPE:-JKS}" --truststore "$SSL_TRUST_STORE_FILE_PATH" \
                --truststore-password "$SSL_TRUST_STORE_PASSWORD" --truststore-type "${SSL_TRUST_STORE_TYPE:-JKS}" "$@"
        fi
        ;;
esac

case " $* " in
    *" --user "*) ;;
    *)
        # the server keeps the default password without IGNITE_USER_PASSWORD
        if [ "$(lower "${ENABLE_AUTHENTICATION:-true}")" != false ]; then
            set -- --user ignite --password "${IGNITE_USER_PASSWORD:-ignite}" "$@"
        fi
        ;;
esac

exec "${JAVA_HOME:-/usr/lib/jvm/java-17}/bin/java" -Xms32m -Xmx256m -XX:+UseSerialGC -XX:TieredStopAtLevel=1 \
    -DIGNITE_HOME=/tmp/control-utility -DIGNITE_UPDATE_NOTIFIER=false \
    --add-opens=java.base/jdk.internal.access=ALL-UNNAMED --add-opens=java.base/jdk.internal.misc=ALL-UNNAMED \
    --add-opens=java.base/sun.nio.ch=ALL-UNNAMED --add-opens=java.base/sun.util.calendar=ALL-UNNAMED \
    --add-opens=java.management/com.sun.jmx.mbeanserver=ALL-UNNAMED \
    --add-opens=jdk.internal.jvmstat/sun.jvmstat.monitor=ALL-UNNAMED \
    --add-opens=java.base/sun.reflect.generics.reflectiveObjects=ALL-UNNAMED \
    --add-opens=jdk.management/com.sun.management.internal=ALL-UNNAMED \
    --add-opens=java.base/java.io=ALL-UNNAMED --add-opens=java.base/java.nio=ALL-UNNAMED \
    --add-opens=java.base/java.net=ALL-UNNAMED --add-opens=java.base/java.util=ALL-UNNAMED \
    --add-opens=java.base/java.util.concurrent=ALL-UNNAMED \
    --add-opens=java.base/java.util.concurrent.locks=ALL-UNNAMED \
    --add-opens=java.base/java.util.concurrent.atomic=ALL-UNNAMED --add-opens=java.base/java.lang=ALL-UNNAMED \
    --add-opens=java.base/java.lang.invoke=ALL-UNNAMED --add-opens=java.base/java.math=ALL-UNNAMED \
    --add-opens=java.sql/java.sql=ALL-UNNAMED --add-opens=java.base/java.lang.reflect=ALL-UNNAMED \
    --add-opens=java.base/java.time=ALL-UNNAMED --add-opens=java.base/java.text=ALL-UNNAMED \
    --add-opens=java.management/sun.management=ALL-UNNAMED \
    -cp "$(dirname "$(readlink -f "$0")")/libs/*" org.apache.ignite.internal.commandline.CommandHandler "$@"
