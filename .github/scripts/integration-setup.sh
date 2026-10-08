#!/usr/bin/env bash
# Prepara a integração do TEC.ORM no CI (chamado pelo dotnet-test.yml do tec-workflows, antes do build).
#
# 1. SQL Server descartável em container, com senha aleatória por execução (mascarada no log e passada ao container por
#    variável de ambiente, fora da linha de comando) e banco tec_testes com READ_COMMITTED_SNAPSHOT ON (padrão do Azure
#    SQL: leitores não disputam travas com escritores na carga mista).
# 2. Conexão entregue aos testes em TEC_TESTES_ORM_SQL_CONEXAO. Quando houve login OIDC (main, fora de PR) e o cofre de
#    testes está configurado, a conexão também é gravada num segredo TEMPORÁRIO e exclusivo desta execução no Key Vault
#    (TEC_TESTES_ORM_SQL_SEGREDO), lido pelos testes pelo TEC.Vault, como em produção. O teardown apaga e purga o segredo.
set -euo pipefail

PASSWORD="Tec1!$(openssl rand -hex 16)"
echo "::add-mask::$PASSWORD"
export MSSQL_SA_PASSWORD="$PASSWORD"

docker run -d --name tec-mssql-ci -p 127.0.0.1:1433:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD \
  mcr.microsoft.com/mssql/server:2022-latest >/dev/null

# Log lido numa variável: com pipefail, "docker logs | grep -q" falha por SIGPIPE quando o grep encerra antes
ready=0
for _ in $(seq 1 90); do
  if grep -q 'SQL Server is now ready for client connections' <<<"$(docker logs tec-mssql-ci 2>&1)"; then
    ready=1
    break
  fi
  sleep 2
done
if [ "$ready" -ne 1 ]; then
  echo "::error::O SQL Server não ficou pronto em 3 minutos."
  docker logs tec-mssql-ci 2>&1 | tail -n 30
  exit 1
fi

# Recuperação dos bancos de sistema logo após o "ready": tenta algumas vezes. Senha por SQLCMDPASSWORD.
for attempt in $(seq 1 10); do
  if docker exec -e SQLCMDPASSWORD="$PASSWORD" tec-mssql-ci /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b \
      -Q "IF DB_ID(N'tec_testes') IS NULL CREATE DATABASE tec_testes; ALTER DATABASE tec_testes SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;" \
      >/dev/null; then
    break
  fi
  if [ "$attempt" -eq 10 ]; then
    echo "::error::Não foi possível criar o banco tec_testes."
    exit 1
  fi
  sleep 3
done

CONNECTION="Server=localhost,1433;Database=tec_testes;User ID=sa;Password=$PASSWORD;Encrypt=True;TrustServerCertificate=True"
echo "TEC_TESTES_ORM_SQL_CONEXAO=$CONNECTION" >> "$GITHUB_ENV"

if [ "${TEC_AZURE_LOGIN:-0}" = "1" ] && [ -n "${TEC_TESTES_VAULT_URI:-}" ]; then
  VAULT_NAME=$(printf '%s' "$TEC_TESTES_VAULT_URI" | sed -E 's#^https://([^.]+)\..*$#\1#')
  SECRET="tec-testes-sql-ci-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
  EXPIRES=$(date -u -d '+1 day' +%Y-%m-%dT%H:%M:%SZ)
  az keyvault secret set --vault-name "$VAULT_NAME" --name "$SECRET" --value "$CONNECTION" \
    --expires "$EXPIRES" --content-type text/plain --output none
  echo "TEC_TESTES_ORM_SQL_SEGREDO=$SECRET" >> "$GITHUB_ENV"
fi
