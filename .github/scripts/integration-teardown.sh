#!/usr/bin/env bash
# Remove o SQL Server descartável e, se foi criado, o segredo temporário do Key Vault (apagado e purgado; sem permissão
# de purga, ele expira sozinho em 1 dia). Chamado sempre no fim do job, mesmo com falha.
set -uo pipefail

if [ "${TEC_AZURE_LOGIN:-0}" = "1" ] && [ -n "${TEC_TESTES_VAULT_URI:-}" ]; then
  VAULT_NAME=$(printf '%s' "$TEC_TESTES_VAULT_URI" | sed -E 's#^https://([^.]+)\..*$#\1#')
  SECRET="tec-testes-sql-ci-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
  if az keyvault secret delete --vault-name "$VAULT_NAME" --name "$SECRET" --output none 2>/dev/null; then
    for _ in $(seq 1 10); do
      az keyvault secret purge --vault-name "$VAULT_NAME" --name "$SECRET" --output none 2>/dev/null && break
      sleep 3
    done
  fi
fi

docker rm -f tec-mssql-ci >/dev/null 2>&1 || true
