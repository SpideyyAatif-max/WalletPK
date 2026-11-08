#!/bin/bash
# Creates WalletPK Kafka topics. Safe to run repeatedly (--if-not-exists).
# Partitions are keyed by wallet ID, so all events of one wallet stay in order.
set -euo pipefail

BOOTSTRAP="${BOOTSTRAP:-kafka:9092}"
KT=/opt/kafka/bin/kafka-topics.sh

create() {
  local topic=$1 partitions=$2 retention_ms=$3
  $KT --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
      --topic "$topic" --partitions "$partitions" --replication-factor 1 \
      --config retention.ms="$retention_ms" --config min.insync.replicas=1
  echo "  ok  $topic ($partitions partitions)"
}

SEVEN_DAYS=604800000
THIRTY_DAYS=2592000000
ONE_DAY=86400000

echo "Creating WalletPK topics on $BOOTSTRAP ..."
# Domain events
create identity.events      6 $SEVEN_DAYS
create ledger.events       12 $THIRTY_DAYS
create payments.events      6 $SEVEN_DAYS
create bills.events         6 $SEVEN_DAYS
create topup.events         6 $SEVEN_DAYS
create agents.events        3 $SEVEN_DAYS
create fraud.alerts         3 $THIRTY_DAYS
create notifications.events 6 $SEVEN_DAYS
# Dead-letter queues
create payments.dlq         3 $THIRTY_DAYS
create bills.dlq            3 $THIRTY_DAYS
create topup.dlq            3 $THIRTY_DAYS
# Simulator live metrics (short-lived)
create sim.metrics          3 $ONE_DAY

echo "Topics now on the broker:"
$KT --bootstrap-server "$BOOTSTRAP" --list
