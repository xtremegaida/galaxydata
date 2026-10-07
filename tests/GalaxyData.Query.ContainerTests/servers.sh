#!/bin/sh
# Starts (up) or removes (down) the database servers the container tests run against: PostgreSQL on port 55432,
# SQL Server (Developer edition) on port 51433 and ClickHouse (HTTP) on port 58123, with the logins ContainerServers
# expects. Run it where Docker is, e.g. from Windows with Docker in WSL:
#    wsl -d Alpine -u root sh tests/GalaxyData.Query.ContainerTests/servers.sh up
# Other servers: set GDQ_TEST_POSTGRES, GDQ_TEST_SQLSERVER and GDQ_TEST_CLICKHOUSE to connection strings of logins
# that may create databases.
set -e

PASSWORD=GdqTest2026
POSTGRES_IMAGE=${GDQ_POSTGRES_IMAGE:-postgres:18-alpine}
SQLSERVER_IMAGE=${GDQ_SQLSERVER_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}
CLICKHOUSE_IMAGE=${GDQ_CLICKHOUSE_IMAGE:-clickhouse/clickhouse-server:26.8}

start() {
   name=$1
   shift
   if [ -n "$(docker ps -q -f name="^$name\$")" ]; then return; fi
   if [ -n "$(docker ps -aq -f name="^$name\$")" ]; then docker start "$name" > /dev/null; return; fi
   docker run -d --name "$name" "$@" > /dev/null
}

wait_for() {
   name=$1
   shift
   for _ in $(seq 1 90); do
      if "$@" > /dev/null 2>&1; then echo "$name is ready"; return; fi
      sleep 2
   done
   echo "$name didn't start; see: docker logs $name" >&2
   exit 1
}

case "${1:-up}" in
   up)
      start gdq-postgres -p 55432:5432 -e POSTGRES_PASSWORD=$PASSWORD --tmpfs /var/lib/postgresql \
         "$POSTGRES_IMAGE" -c fsync=off -c synchronous_commit=off -c full_page_writes=off
      start gdq-sqlserver -p 51433:1433 -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD=$PASSWORD "$SQLSERVER_IMAGE"
      start gdq-clickhouse -p 58123:8123 -e CLICKHOUSE_USER=gdq -e CLICKHOUSE_PASSWORD=$PASSWORD -e CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT=1 \
         --ulimit nofile=262144:262144 --tmpfs /var/lib/clickhouse "$CLICKHOUSE_IMAGE"
      wait_for gdq-postgres docker exec gdq-postgres pg_isready -U postgres -h 127.0.0.1
      wait_for gdq-sqlserver docker exec gdq-sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $PASSWORD -Q "SELECT 1"
      wait_for gdq-clickhouse docker exec gdq-clickhouse clickhouse-client --user gdq --password $PASSWORD --query "SELECT 1"
      ;;
   down)
      docker rm -f gdq-postgres gdq-sqlserver gdq-clickhouse > /dev/null 2>&1 || true
      ;;
   *)
      echo "usage: servers.sh [up|down]" >&2
      exit 2
      ;;
esac
