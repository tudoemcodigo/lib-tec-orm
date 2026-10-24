<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

# TEC.ORM.EntityFrameworkCore

Motor de persistência base e agnóstico da família **TEC.ORM**, integrando **Entity Framework Core** e **Dapper**.

## O que fornece

- **Repositório Genérico (`OrmRepository<TEntity, TKey>`)**: Implementação completa das operações de CRUD, paginação, exclusão lógica propagada e exclusão física com auditoria.
- **Consultas Rápidas (`OrmQueryExecutor`)**: Execução de SQL de alta performance com Dapper, limite estrito de linhas (`MaxQueryRows`) e cancelamento de comandos.
- **Trilha Histórica de Auditoria (`HistoricalAuditInterceptor`)**: Gravação atômica de diffs (`OldValues` / `NewValues`) em JSON para entidades `[Auditable]`, com mascaramento `[SensitiveData]` e exclusão `[AuditIgnore]`.
- **Idempotência no Banco (`EfIdempotencyStore`)**: Armazenamento e concorrência para chaves de idempotência integradas ao `TEC.Cqrs`.
- **Segurança de Conexões (`OrmConnectionSecurity`)**: Resolução de credenciais via `TEC.Vault`, validação de TLS mandatória, sanitização de strings e Circuit Breaker.
- **Abstrações de Dialeto (`ISqlDialect`, `IReadOnlySqlValidator`, `IOrmExceptionTranslator`)**: Contratos estáveis para especialização por banco de dados.

## Instalação

```bash
dotnet add package TEC.ORM.EntityFrameworkCore --version 0.0.1
```

Este pacote é consumido automaticamente pelos provedores `TEC.ORM.SqlServer`, `TEC.ORM.PostgreSql`, `TEC.ORM.MySql` e `TEC.ORM.Oracle`.
